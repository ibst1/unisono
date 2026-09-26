using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Unisono;

/// <summary>
/// A Cryptomator vault (vault format 8, cipher combo SIV_GCM): what the Cryptomator apps for Windows, macOS, Linux,
/// Android and iOS open with the same passphrase. See docs.cryptomator.org, "Vault Format 8".
///
/// Top of the vault: vault.cryptomator (a JWT signed with the master keys, naming the format) and masterkey.cryptomator
/// (the 256-bit encryption and MAC master keys, each wrapped with AES Key Wrap under a key from scrypt(passphrase)).
/// Everything else lives under d/: every directory has a random id; its contents sit in d/XX/YYYY..., where XXYYYY... is
/// base32(SHA-1(AES-SIV(id))). A child's name is base64url(AES-SIV(name, parent id)) + ".c9r"; a subdirectory is a folder
/// of that name holding dir.c9r (its id). Encrypted names longer than 220 characters are replaced by
/// base64url(SHA-1(name)) + ".c9s", a folder holding name.c9s (the full name) and contents.c9r or dir.c9r.
/// File content: a 68-byte header (nonce, and the file's content key AES-GCM-encrypted with the master key), then
/// 32 KiB chunks, each AES-GCM with its own nonce and the chunk number and header nonce as associated data.
///
/// This computer keeps the two master keys DPAPI-protected for the Windows user (typed once per computer).
/// </summary>
public sealed class CryptomatorVault
{
    public const string VaultFile = "vault.cryptomator";
    public const string MasterkeyFile = "masterkey.cryptomator";
    public const string DataFolder = "d";
    public const int ShorteningThreshold = 220;
    private const int ChunkSize = 32 * 1024;
    private const int NonceSize = 12, TagSize = 16, HeaderSize = NonceSize + 40 + TagSize;

    private readonly byte[] _encKey, _macKey;

    /// <param name="keys">The encryption master key followed by the MAC master key (64 bytes).</param>
    public CryptomatorVault(byte[] keys)
    {
        if (keys.Length != 64) throw new ArgumentException("a vault has two 32-byte master keys");
        _encKey = keys[..32];
        _macKey = keys[32..];
    }

    // ----------------------------------------------------------- the vault

    /// <summary>A new vault: its keys, the contents of its two files, and its id (the vault file's jti).</summary>
    public static (byte[] Keys, string Masterkey, string VaultConfig, string Id) Create(string passphrase)
    {
        var keys = RandomNumberGenerator.GetBytes(64);
        var salt = RandomNumberGenerator.GetBytes(8);
        const int n = 32768, r = 8;
        var kek = CryptoPrimitives.Scrypt(Passphrase(passphrase), salt, n, r, 1, 32);
        var mac = keys[32..];
        var versionMac = HMACSHA256.HashData(mac, BigEndian(999));
        var masterkey = new JsonObject
        {
            ["version"] = 999,
            ["scryptSalt"] = Convert.ToBase64String(salt),
            ["scryptCostParam"] = n,
            ["scryptBlockSize"] = r,
            ["primaryMasterKey"] = Convert.ToBase64String(CryptoPrimitives.KeyWrap(kek, keys[..32])),
            ["hmacMasterKey"] = Convert.ToBase64String(CryptoPrimitives.KeyWrap(kek, mac)),
            ["versionMac"] = Convert.ToBase64String(versionMac),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var id = Guid.NewGuid().ToString();
        var header = new JsonObject { ["kid"] = "masterkeyfile:" + MasterkeyFile, ["typ"] = "JWT", ["alg"] = "HS256" }.ToJsonString();
        var payload = new JsonObject { ["jti"] = id, ["format"] = 8, ["cipherCombo"] = "SIV_GCM", ["shorteningThreshold"] = ShorteningThreshold }.ToJsonString();
        var signed = Jwt(header) + "." + Jwt(payload);
        var config = signed + "." + Jwt(HMACSHA256.HashData(keys, Encoding.ASCII.GetBytes(signed)));
        return (keys, masterkey, config, id);
    }

    /// <summary>The master keys (64 bytes) from masterkey.cryptomator, or null when the passphrase is wrong.</summary>
    public static byte[]? Unlock(string masterkeyJson, string passphrase)
    {
        var m = JsonNode.Parse(masterkeyJson) ?? throw new InvalidDataException("masterkey.cryptomator is empty");
        var salt = Convert.FromBase64String((string)m["scryptSalt"]!);
        var kek = CryptoPrimitives.Scrypt(Passphrase(passphrase), salt, (int)m["scryptCostParam"]!, (int)m["scryptBlockSize"]!, 1, 32);
        var enc = CryptoPrimitives.KeyUnwrap(kek, Convert.FromBase64String((string)m["primaryMasterKey"]!));
        var mac = CryptoPrimitives.KeyUnwrap(kek, Convert.FromBase64String((string)m["hmacMasterKey"]!));
        return enc == null || mac == null ? null : enc.Concat(mac).ToArray();
    }

    /// <summary>Checks vault.cryptomator against the keys and returns the vault's id; throws when it is not a vault Unisono can use.</summary>
    public static string CheckConfig(string vaultConfig, byte[] keys)
    {
        var parts = vaultConfig.Trim().Split('.');
        if (parts.Length != 3) throw new InvalidDataException("vault.cryptomator is not a vault configuration");
        var expected = HMACSHA256.HashData(keys, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
        if (!CryptographicOperations.FixedTimeEquals(expected, FromJwt(parts[2]))) throw new InvalidDataException("vault.cryptomator does not belong to these keys");
        var payload = JsonNode.Parse(FromJwt(parts[1]))!;
        if ((int?)payload["format"] != 8 || (string?)payload["cipherCombo"] != "SIV_GCM")
            throw new InvalidDataException($"this Cryptomator vault has format {payload["format"]} / {payload["cipherCombo"]}; Unisono reads format 8 with SIV_GCM (made by Cryptomator 1.6 and later)");
        return (string)payload["jti"]!;
    }

    /// <summary>The vault's id from vault.cryptomator, read without checking it (to find the key stored for it).</summary>
    public static string IdOf(string vaultConfig)
    {
        var parts = vaultConfig.Trim().Split('.');
        if (parts.Length != 3) throw new InvalidDataException("vault.cryptomator is not a vault configuration");
        return (string)JsonNode.Parse(FromJwt(parts[1]))!["jti"]!;
    }

    private static byte[] Passphrase(string p) => Encoding.UTF8.GetBytes(p.Normalize(NormalizationForm.FormC));
    private static byte[] BigEndian(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }
    private static string Jwt(string json) => Jwt(Encoding.UTF8.GetBytes(json));
    private static string Jwt(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromJwt(string s) => FromBase64Url(s);

    private static byte[] FromBase64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        return Convert.FromBase64String(t + new string('=', (4 - t.Length % 4) % 4));
    }

    private static string ToBase64Url(byte[] data) => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_'); // with padding, as Cryptomator writes it

    // ---------------------------------------------------------- stored keys

    private static readonly byte[] Entropy = "Unisono Cryptomator vault keys"u8.ToArray();
    private static string KeyPath(string vaultId) => Path.Combine(AppPaths.AuthDir, "crypt", vaultId + ".key");

    public static void SaveKey(string vaultId, byte[] keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath(vaultId))!);
        File.WriteAllBytes(KeyPath(vaultId), ProtectedData.Protect(keys, Entropy, DataProtectionScope.CurrentUser));
    }

    public static byte[]? LoadKey(string? vaultId)
    {
        if (string.IsNullOrEmpty(vaultId) || !File.Exists(KeyPath(vaultId))) return null;
        try { return ProtectedData.Unprotect(File.ReadAllBytes(KeyPath(vaultId)), Entropy, DataProtectionScope.CurrentUser); }
        catch { return null; }
    }

    // --------------------------------------------------------------- names

    /// <summary>Where a directory's contents live: "d/XX/YYYY..." (the root directory's id is "").</summary>
    public string DirPath(string dirId)
    {
        var hash = SHA1.HashData(CryptoPrimitives.SivEncrypt(_macKey, _encKey, Encoding.UTF8.GetBytes(dirId)));
        var b32 = Base32(hash);
        return $"{DataFolder}/{b32[..2]}/{b32[2..]}";
    }

    /// <summary>The encrypted name of <paramref name="name"/> in the directory <paramref name="parentDirId"/>, with ".c9r".</summary>
    public string EncryptName(string name, string parentDirId) =>
        ToBase64Url(CryptoPrimitives.SivEncrypt(_macKey, _encKey, Encoding.UTF8.GetBytes(name.Normalize(NormalizationForm.FormC)),
            Encoding.UTF8.GetBytes(parentDirId))) + ".c9r";

    /// <summary>The plain name, or null when <paramref name="encrypted"/> (with or without ".c9r") is not a name of this directory.</summary>
    public string? DecryptName(string encrypted, string parentDirId)
    {
        if (encrypted.EndsWith(".c9r", StringComparison.Ordinal)) encrypted = encrypted[..^4];
        byte[] data;
        try { data = FromBase64Url(encrypted); } catch (FormatException) { return null; }
        var plain = CryptoPrimitives.SivDecrypt(_macKey, _encKey, data, Encoding.UTF8.GetBytes(parentDirId));
        return plain == null ? null : Encoding.UTF8.GetString(plain);
    }

    /// <summary>The stand-in for an encrypted name longer than <see cref="ShorteningThreshold"/>: base64url(SHA-1) + ".c9s".</summary>
    public static string ShortName(string encryptedName) => ToBase64Url(SHA1.HashData(Encoding.UTF8.GetBytes(encryptedName))) + ".c9s";

    private static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = ((buffer << 8) | b) & 0xFFFF; bits += 8;
            while (bits >= 5) { sb.Append(alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    // ------------------------------------------------------------- content

    public void EncryptFile(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var headerNonce = RandomNumberGenerator.GetBytes(NonceSize);
        var contentKey = RandomNumberGenerator.GetBytes(32);
        var payload = new byte[40];
        payload.AsSpan(0, 8).Fill(0xFF); // reserved (-1)
        contentKey.CopyTo(payload, 8);
        var header = new byte[40];
        var tag = new byte[TagSize];
        using (var gcm = new AesGcm(_encKey, TagSize)) gcm.Encrypt(headerNonce, payload, header, tag);
        output.Write(headerNonce); output.Write(header); output.Write(tag);

        using var chunkGcm = new AesGcm(contentKey, TagSize);
        var plain = new byte[ChunkSize];
        var cipher = new byte[ChunkSize];
        long index = 0;
        int n;
        while ((n = ReadFull(input, plain)) > 0)
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            chunkGcm.Encrypt(nonce, plain.AsSpan(0, n), cipher.AsSpan(0, n), tag, ChunkAad(index++, headerNonce));
            output.Write(nonce); output.Write(cipher, 0, n); output.Write(tag);
            if (n < ChunkSize) break;
        }
        CryptographicOperations.ZeroMemory(contentKey);
    }

    public void DecryptFile(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var head = new byte[HeaderSize];
        if (ReadFull(input, head) != HeaderSize) throw new InvalidDataException("the encrypted file is too short for a Cryptomator file");
        var headerNonce = head[..NonceSize];
        var payload = new byte[40];
        try
        {
            using var gcm = new AesGcm(_encKey, TagSize);
            gcm.Decrypt(headerNonce, head.AsSpan(NonceSize, 40), head.AsSpan(NonceSize + 40, TagSize), payload);
        }
        catch (CryptographicException) { throw new InvalidDataException("the file header does not belong to this vault or is damaged"); }
        using var chunkGcm = new AesGcm(payload.AsSpan(8, 32), TagSize);
        CryptographicOperations.ZeroMemory(payload);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var block = new byte[NonceSize + ChunkSize + TagSize];
        var plain = new byte[ChunkSize];
        long index = 0;
        int n;
        while ((n = ReadFull(input, block)) > 0)
        {
            if (n < NonceSize + TagSize) throw new InvalidDataException("the encrypted file is cut short");
            var len = n - NonceSize - TagSize;
            try { chunkGcm.Decrypt(block.AsSpan(0, NonceSize), block.AsSpan(NonceSize, len), block.AsSpan(NonceSize + len, TagSize), plain.AsSpan(0, len), ChunkAad(index++, headerNonce)); }
            catch (CryptographicException) { throw new InvalidDataException("the encrypted file is damaged or was changed"); }
            output.Write(plain, 0, len);
            if (n < block.Length) break;
        }
    }

    private static byte[] ChunkAad(long index, byte[] headerNonce)
    {
        var aad = new byte[8 + NonceSize];
        BinaryPrimitives.WriteInt64BigEndian(aad, index);
        headerNonce.CopyTo(aad, 8);
        return aad;
    }

    /// <summary>The plaintext length of an encrypted file of <paramref name="cipherSize"/> bytes.</summary>
    public static long PlainSize(long cipherSize)
    {
        var c = cipherSize - HeaderSize;
        if (c <= 0) return 0;
        const int full = NonceSize + ChunkSize + TagSize;
        return c / full * ChunkSize + Math.Max(0, c % full - NonceSize - TagSize);
    }

    private static int ReadFull(Stream s, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = s.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
