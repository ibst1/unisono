using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Unisono;

/// <summary>
/// The building blocks of the Cryptomator vault format that .NET does not have: AES-SIV (RFC 5297, with AES-CMAC from
/// RFC 4493), scrypt (RFC 7914) and AES Key Wrap (RFC 3394). Checked against the RFCs' test vectors.
/// </summary>
public static class CryptoPrimitives
{
    // ------------------------------------------------------------- AES-SIV

    /// <summary>AES-SIV: <paramref name="macKey"/> for S2V, <paramref name="ctrKey"/> for CTR. Returns V (16 bytes) + ciphertext.</summary>
    public static byte[] SivEncrypt(byte[] macKey, byte[] ctrKey, ReadOnlySpan<byte> plaintext, params byte[][] associatedData)
    {
        using var mac = NewAes(macKey);
        var v = S2V(mac, associatedData, plaintext);
        var output = new byte[16 + plaintext.Length];
        v.CopyTo(output, 0);
        using var ctr = NewAes(ctrKey);
        Ctr(ctr, SivCounter(v), plaintext, output.AsSpan(16));
        return output;
    }

    /// <summary>The plaintext, or null when the ciphertext or associated data were not made with these keys.</summary>
    public static byte[]? SivDecrypt(byte[] macKey, byte[] ctrKey, ReadOnlySpan<byte> ciphertext, params byte[][] associatedData)
    {
        if (ciphertext.Length < 16) return null;
        var v = ciphertext[..16].ToArray();
        var plain = new byte[ciphertext.Length - 16];
        using (var ctr = NewAes(ctrKey)) Ctr(ctr, SivCounter(v), ciphertext[16..], plain);
        using var mac = NewAes(macKey);
        return CryptographicOperations.FixedTimeEquals(S2V(mac, associatedData, plain), v) ? plain : null;
    }

    private static byte[] SivCounter(byte[] v)
    {
        var q = (byte[])v.Clone();
        q[8] &= 0x7F; q[12] &= 0x7F; // clear the 31st and 63rd bit (RFC 5297 2.6)
        return q;
    }

    private static byte[] S2V(Aes mac, byte[][] associatedData, ReadOnlySpan<byte> plaintext)
    {
        var d = Cmac(mac, new byte[16]);
        foreach (var ad in associatedData) Xor(Dbl(d), Cmac(mac, ad), d);
        byte[] t;
        if (plaintext.Length >= 16)
        {
            t = plaintext.ToArray();
            for (var i = 0; i < 16; i++) t[t.Length - 16 + i] ^= d[i];
        }
        else
        {
            t = Dbl(d);
            var padded = new byte[16];
            plaintext.CopyTo(padded);
            padded[plaintext.Length] = 0x80;
            for (var i = 0; i < 16; i++) t[i] ^= padded[i];
        }
        return Cmac(mac, t);
    }

    /// <summary>AES-CMAC (RFC 4493).</summary>
    public static byte[] Cmac(Aes aes, ReadOnlySpan<byte> message)
    {
        var l = aes.EncryptEcb(new byte[16], PaddingMode.None);
        var k1 = Dbl(l);
        var k2 = Dbl(k1);
        var n = (message.Length + 15) / 16;
        var complete = n > 0 && message.Length % 16 == 0;
        if (n == 0) n = 1;
        var x = new byte[16];
        var block = new byte[16];
        for (var i = 0; i < n - 1; i++)
        {
            for (var j = 0; j < 16; j++) block[j] = (byte)(x[j] ^ message[i * 16 + j]);
            x = aes.EncryptEcb(block, PaddingMode.None);
        }
        var last = new byte[16];
        var rest = message[((n - 1) * 16)..];
        rest.CopyTo(last);
        if (!complete) last[rest.Length] = 0x80;
        var k = complete ? k1 : k2;
        for (var j = 0; j < 16; j++) block[j] = (byte)(x[j] ^ last[j] ^ k[j]);
        return aes.EncryptEcb(block, PaddingMode.None);
    }

    private static byte[] Dbl(byte[] b)
    {
        var r = new byte[16];
        for (var i = 0; i < 15; i++) r[i] = (byte)((b[i] << 1) | (b[i + 1] >> 7));
        r[15] = (byte)(b[15] << 1);
        if ((b[0] & 0x80) != 0) r[15] ^= 0x87;
        return r;
    }

    private static void Xor(byte[] a, byte[] b, byte[] into)
    {
        for (var i = 0; i < 16; i++) into[i] = (byte)(a[i] ^ b[i]);
    }

    /// <summary>AES-CTR with a 128-bit big-endian counter.</summary>
    private static void Ctr(Aes aes, byte[] iv, ReadOnlySpan<byte> input, Span<byte> output)
    {
        var blocks = (input.Length + 15) / 16;
        var counters = new byte[blocks * 16];
        var counter = (byte[])iv.Clone();
        for (var b = 0; b < blocks; b++)
        {
            counter.CopyTo(counters, b * 16);
            for (var i = 15; i >= 0 && ++counter[i] == 0; i--) { }
        }
        var stream = aes.EncryptEcb(counters, PaddingMode.None);
        for (var i = 0; i < input.Length; i++) output[i] = (byte)(input[i] ^ stream[i]);
    }

    private static Aes NewAes(byte[] key)
    {
        var aes = Aes.Create();
        aes.Key = key;
        return aes;
    }

    // --------------------------------------------------------------- scrypt

    public static byte[] Scrypt(byte[] password, byte[] salt, int n, int r, int p, int length)
    {
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("scrypt N must be a power of 2");
        var b = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, p * 128 * r);
        var x = new uint[32 * r];
        var v = new uint[32 * r * n];
        var y = new uint[32 * r];
        for (var i = 0; i < p; i++)
        {
            var offset = i * 128 * r;
            for (var k = 0; k < x.Length; k++) x[k] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset + k * 4));
            for (var k = 0; k < n; k++)
            {
                Array.Copy(x, 0, v, k * x.Length, x.Length);
                BlockMix(x, y, r);
            }
            for (var k = 0; k < n; k++)
            {
                var j = (int)(x[(2 * r - 1) * 16] & (uint)(n - 1));
                for (var m = 0; m < x.Length; m++) x[m] ^= v[j * x.Length + m];
                BlockMix(x, y, r);
            }
            for (var k = 0; k < x.Length; k++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset + k * 4), x[k]);
        }
        return Rfc2898DeriveBytes.Pbkdf2(password, b, 1, HashAlgorithmName.SHA256, length);
    }

    private static void BlockMix(uint[] b, uint[] y, int r)
    {
        var x = new uint[16];
        Array.Copy(b, (2 * r - 1) * 16, x, 0, 16);
        for (var i = 0; i < 2 * r; i++)
        {
            for (var k = 0; k < 16; k++) x[k] ^= b[i * 16 + k];
            Salsa208(x);
            // Even blocks to the first half, odd ones to the second.
            Array.Copy(x, 0, y, ((i & 1) == 0 ? i / 2 : r + i / 2) * 16, 16);
        }
        Array.Copy(y, b, y.Length);
    }

    private static void Salsa208(uint[] b)
    {
        var x = (uint[])b.Clone();
        static uint R(uint a, int c) => (a << c) | (a >> (32 - c));
        for (var i = 0; i < 8; i += 2)
        {
            x[4] ^= R(x[0] + x[12], 7); x[8] ^= R(x[4] + x[0], 9); x[12] ^= R(x[8] + x[4], 13); x[0] ^= R(x[12] + x[8], 18);
            x[9] ^= R(x[5] + x[1], 7); x[13] ^= R(x[9] + x[5], 9); x[1] ^= R(x[13] + x[9], 13); x[5] ^= R(x[1] + x[13], 18);
            x[14] ^= R(x[10] + x[6], 7); x[2] ^= R(x[14] + x[10], 9); x[6] ^= R(x[2] + x[14], 13); x[10] ^= R(x[6] + x[2], 18);
            x[3] ^= R(x[15] + x[11], 7); x[7] ^= R(x[3] + x[15], 9); x[11] ^= R(x[7] + x[3], 13); x[15] ^= R(x[11] + x[7], 18);
            x[1] ^= R(x[0] + x[3], 7); x[2] ^= R(x[1] + x[0], 9); x[3] ^= R(x[2] + x[1], 13); x[0] ^= R(x[3] + x[2], 18);
            x[6] ^= R(x[5] + x[4], 7); x[7] ^= R(x[6] + x[5], 9); x[4] ^= R(x[7] + x[6], 13); x[5] ^= R(x[4] + x[7], 18);
            x[11] ^= R(x[10] + x[9], 7); x[8] ^= R(x[11] + x[10], 9); x[9] ^= R(x[8] + x[11], 13); x[10] ^= R(x[9] + x[8], 18);
            x[12] ^= R(x[15] + x[14], 7); x[13] ^= R(x[12] + x[15], 9); x[14] ^= R(x[13] + x[12], 13); x[15] ^= R(x[14] + x[13], 18);
        }
        for (var i = 0; i < 16; i++) b[i] += x[i];
    }

    // -------------------------------------------------------- AES Key Wrap

    private static readonly byte[] WrapIv = { 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6 };

    public static byte[] KeyWrap(byte[] kek, byte[] key)
    {
        var n = key.Length / 8;
        var a = (byte[])WrapIv.Clone();
        var r = new byte[n][];
        for (var i = 0; i < n; i++) r[i] = key[(i * 8)..(i * 8 + 8)];
        using var aes = NewAes(kek);
        var block = new byte[16];
        for (var j = 0; j <= 5; j++)
            for (var i = 0; i < n; i++)
            {
                a.CopyTo(block, 0); r[i].CopyTo(block, 8);
                var b = aes.EncryptEcb(block, PaddingMode.None);
                a = b[..8];
                var t = (ulong)(n * j + i + 1);
                for (var k = 0; k < 8; k++) a[7 - k] ^= (byte)(t >> (8 * k));
                r[i] = b[8..];
            }
        return a.Concat(r.SelectMany(x => x)).ToArray();
    }

    /// <summary>The unwrapped key, or null when the KEK is wrong (the integrity check fails).</summary>
    public static byte[]? KeyUnwrap(byte[] kek, byte[] wrapped)
    {
        var n = wrapped.Length / 8 - 1;
        if (n < 1) return null;
        var a = wrapped[..8];
        var r = new byte[n][];
        for (var i = 0; i < n; i++) r[i] = wrapped[((i + 1) * 8)..((i + 2) * 8)];
        using var aes = NewAes(kek);
        var block = new byte[16];
        for (var j = 5; j >= 0; j--)
            for (var i = n - 1; i >= 0; i--)
            {
                var t = (ulong)(n * j + i + 1);
                var at = (byte[])a.Clone();
                for (var k = 0; k < 8; k++) at[7 - k] ^= (byte)(t >> (8 * k));
                at.CopyTo(block, 0); r[i].CopyTo(block, 8);
                var b = aes.DecryptEcb(block, PaddingMode.None);
                a = b[..8];
                r[i] = b[8..];
            }
        return CryptographicOperations.FixedTimeEquals(a, WrapIv) ? r.SelectMany(x => x).ToArray() : null;
    }
}
