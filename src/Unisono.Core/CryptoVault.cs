using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Unisono;

/// <summary>
/// Client-side encryption for a pair (<see cref="SyncPair.Encrypted"/>): the other side only ever holds ciphertext.
///
/// Keys: a passphrase and a random salt give a 64-byte master key (PBKDF2-SHA256, 600 000 rounds), from which HKDF
/// derives the content key and two name keys. The salt, the round count and a check value (to recognise a wrong
/// passphrase) sit in the vault file <see cref="VaultFile"/> at the top of the remote folder, in plain text: none of it
/// is secret. Each computer keeps the master key DPAPI-protected for the Windows user, so the passphrase is typed once
/// per computer. Without the passphrase the files cannot be recovered by anyone.
///
/// Names: every path segment is encrypted deterministically (so items can be found again by name) with a synthetic IV:
/// iv = HMAC-SHA256(macKey, name)[..16], ciphertext = AES-256-CTR(encKey, iv, name), written as lowercase base32 of
/// iv + ciphertext (case-insensitive file systems stay safe). Decryption checks the IV, so foreign or damaged names are
/// recognised and left out. Equal names encrypt equally (in any folder), and the length of a name shows.
///
/// Content: "UNI1", an 8-byte random prefix, then chunks of 1 MiB plaintext, each AES-256-GCM with the nonce
/// prefix + chunk number and the chunk number + a last-chunk flag as associated data (so chunks cannot be reordered,
/// dropped or the file cut short). A file whose length is a multiple of the chunk size ends with an empty last chunk.
/// </summary>
public sealed class CryptoVault
{
    public const string VaultFile = ".unisono-crypt";
    private const int ChunkSize = 1 << 20;
    private const int TagSize = 16;
    private static readonly byte[] Magic = "UNI1"u8.ToArray();
    private const int HeaderSize = 4 + 8;

    private readonly byte[] _contentKey, _nameMacKey, _nameEncKey;

    /// <summary>What the vault file holds.</summary>
    public sealed class VaultInfo
    {
        public int Version { get; set; } = 1;
        public string Salt { get; set; } = "";
        public int Iterations { get; set; } = 600_000;
        public string Check { get; set; } = "";
        /// <summary>Names this vault on the computers that use it (for the stored key).</summary>
        public string Id => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(Salt)))[..16].ToLowerInvariant();
    }

    public CryptoVault(byte[] master)
    {
        if (master.Length != 64) throw new ArgumentException("the master key has 64 bytes");
        _contentKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "unisono content"u8.ToArray());
        _nameMacKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "unisono name mac"u8.ToArray());
        _nameEncKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: "unisono name enc"u8.ToArray());
    }

    // ------------------------------------------------------------------ keys

    public static (byte[] Master, VaultInfo Info) Create(string passphrase)
    {
        var info = new VaultInfo { Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        var master = Derive(passphrase, info);
        info.Check = CheckValue(master);
        return (master, info);
    }

    /// <summary>The master key, or null when the passphrase is wrong.</summary>
    public static byte[]? Unlock(VaultInfo info, string passphrase)
    {
        var master = Derive(passphrase, info);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(CheckValue(master)), Encoding.ASCII.GetBytes(info.Check)) ? master : null;
    }

    private static byte[] Derive(string passphrase, VaultInfo info) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC)), Convert.FromBase64String(info.Salt), info.Iterations, HashAlgorithmName.SHA256, 64);

    private static string CheckValue(byte[] master) => Convert.ToBase64String(HMACSHA256.HashData(master, "unisono passphrase check"u8));

    public static string Serialize(VaultInfo info) => JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
    public static VaultInfo Parse(string json) =>
        JsonSerializer.Deserialize<VaultInfo>(json) is { Salt.Length: > 0, Check.Length: > 0 } v ? v : throw new InvalidDataException("not an Unisono vault file");

    // The master key per vault, kept for the Windows user on this computer.
    private static readonly byte[] Entropy = "Unisono vault key"u8.ToArray();
    private static string KeyPath(string vaultId) => Path.Combine(AppPaths.AuthDir, "crypt", vaultId + ".key");

    public static void SaveKey(string vaultId, byte[] master)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath(vaultId))!);
        File.WriteAllBytes(KeyPath(vaultId), ProtectedData.Protect(master, Entropy, DataProtectionScope.CurrentUser));
    }

    public static byte[]? LoadKey(string? vaultId)
    {
        if (string.IsNullOrEmpty(vaultId) || !File.Exists(KeyPath(vaultId))) return null;
        try { return ProtectedData.Unprotect(File.ReadAllBytes(KeyPath(vaultId)), Entropy, DataProtectionScope.CurrentUser); }
        catch { return null; }
    }

    // ----------------------------------------------------------------- names

    public string EncryptName(string name)
    {
        var plain = Encoding.UTF8.GetBytes(name);
        var iv = HMACSHA256.HashData(_nameMacKey, plain)[..16];
        var output = new byte[16 + plain.Length];
        iv.CopyTo(output, 0);
        Ctr(iv, plain, output.AsSpan(16));
        return Base32.Encode(output);
    }

    /// <summary>The name, or null when <paramref name="encrypted"/> is not a name of this vault.</summary>
    public string? DecryptName(string encrypted)
    {
        var data = Base32.Decode(encrypted);
        if (data == null || data.Length < 17) return null;
        var iv = data[..16];
        var plain = new byte[data.Length - 16];
        Ctr(iv, data.AsSpan(16), plain);
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_nameMacKey, plain)[..16], iv)) return null;
        return Encoding.UTF8.GetString(plain);
    }

    public string EncryptPath(string rel) => rel.Length == 0 ? "" : string.Join("/", rel.Split('/').Select(EncryptName));

    private void Ctr(byte[] iv, ReadOnlySpan<byte> input, Span<byte> output)
    {
        using var aes = Aes.Create();
        aes.Key = _nameEncKey;
        var blocks = (input.Length + 15) / 16;
        var counters = new byte[blocks * 16];
        var counter = (byte[])iv.Clone();
        for (var b = 0; b < blocks; b++)
        {
            counter.CopyTo(counters, b * 16);
            for (var i = 15; i >= 0 && ++counter[i] == 0; i--) { } // big-endian increment
        }
        var stream = aes.EncryptEcb(counters, PaddingMode.None);
        for (var i = 0; i < input.Length; i++) output[i] = (byte)(input[i] ^ stream[i]);
    }

    // --------------------------------------------------------------- content

    public void EncryptFile(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var prefix = RandomNumberGenerator.GetBytes(8);
        output.Write(Magic); output.Write(prefix);
        using var gcm = new AesGcm(_contentKey, TagSize);
        var plain = new byte[ChunkSize];
        var cipher = new byte[ChunkSize];
        var tag = new byte[TagSize];
        uint index = 0;
        while (true)
        {
            var n = ReadFull(input, plain);
            var last = n < ChunkSize; // a full chunk is followed by another one, possibly empty
            gcm.Encrypt(Nonce(prefix, index), plain.AsSpan(0, n), cipher.AsSpan(0, n), tag, Aad(index, last));
            output.Write(cipher, 0, n); output.Write(tag);
            if (last) break;
            index++;
        }
    }

    public void DecryptFile(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var header = new byte[HeaderSize];
        if (ReadFull(input, header) != HeaderSize || !header.AsSpan(0, 4).SequenceEqual(Magic)) throw new InvalidDataException("not a file of this encrypted pair");
        var prefix = header[4..];
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var gcm = new AesGcm(_contentKey, TagSize);
        var block = new byte[ChunkSize + TagSize];
        var plain = new byte[ChunkSize];
        uint index = 0;
        while (true)
        {
            var n = ReadFull(input, block);
            if (n < TagSize) throw new InvalidDataException("the encrypted file is cut short");
            var last = n < block.Length;
            var len = n - TagSize;
            try { gcm.Decrypt(Nonce(prefix, index), block.AsSpan(0, len), block.AsSpan(len, TagSize), plain.AsSpan(0, len), Aad(index, last)); }
            catch (CryptographicException) { throw new InvalidDataException("the encrypted file is damaged or was changed"); }
            output.Write(plain, 0, len);
            if (last) break;
            index++;
        }
    }

    /// <summary>The plaintext length of an encrypted file of <paramref name="cipherSize"/> bytes.</summary>
    public static long PlainSize(long cipherSize)
    {
        var c = cipherSize - HeaderSize;
        if (c < TagSize) return 0;
        var full = c / (ChunkSize + TagSize);
        var rest = c % (ChunkSize + TagSize);
        return full * ChunkSize + Math.Max(0, rest - TagSize);
    }

    private static byte[] Nonce(byte[] prefix, uint index)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), index);
        return nonce;
    }

    private static byte[] Aad(uint index, bool last)
    {
        var aad = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(aad, index);
        aad[4] = last ? (byte)1 : (byte)0;
        return aad;
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

    /// <summary>RFC 4648 base32 in lowercase, without padding.</summary>
    private static class Base32
    {
        private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

        public static string Encode(byte[] data)
        {
            var sb = new StringBuilder((data.Length * 8 + 4) / 5);
            int buffer = 0, bits = 0;
            foreach (var b in data)
            {
                buffer = ((buffer << 8) | b) & 0xFFFF; bits += 8;
                while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
            }
            if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
            return sb.ToString();
        }

        public static byte[]? Decode(string text)
        {
            var output = new List<byte>(text.Length * 5 / 8);
            int buffer = 0, bits = 0;
            foreach (var ch in text.ToLowerInvariant())
            {
                var v = Alphabet.IndexOf(ch);
                if (v < 0) return null;
                buffer = ((buffer << 5) | v) & 0xFFFF; bits += 5;
                if (bits >= 8) { output.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
            }
            return output.ToArray();
        }
    }
}
