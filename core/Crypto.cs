using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BedReach;

/// <summary>
/// All cryptography for the BedReach protocol (v1). The Android app mirrors this
/// exactly in Crypto.kt — change both together. See PROTOCOL.md.
/// </summary>
public static class Crypto
{
    public static byte[] Random(int n) => RandomNumberGenerator.GetBytes(n);

    public static byte[] Sha256(params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in parts) h.AppendData(p);
        return h.GetHashAndReset();
    }

    public static byte[] Hmac(byte[] key, params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        foreach (var p in parts) h.AppendData(p);
        return h.GetHashAndReset();
    }

    public static byte[] Hkdf(byte[] ikm, byte[] salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, Encoding.UTF8.GetBytes(info));

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    public static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
        return r;
    }

    /// <summary>Result of the pairing key exchange (both sides compute the same values).</summary>
    public record PairResult(byte[] Secret, string Code);

    /// <summary>
    /// transcript = phonePub || phoneNonce || pcPub || pcNonce
    /// secret     = HKDF(ecdh, salt = SHA256(transcript), "bedreach-pair-v1")
    /// code       = uint32be(SHA256("bedreach-code-v1" || transcript || ecdh)[0..4]) mod 1,000,000
    /// </summary>
    public static PairResult DerivePairing(byte[] ecdhShared, byte[] phonePub, byte[] phoneNonce, byte[] pcPub, byte[] pcNonce)
    {
        var transcript = Concat(phonePub, phoneNonce, pcPub, pcNonce);
        var secret = Hkdf(ecdhShared, Sha256(transcript), "bedreach-pair-v1");
        var h = Sha256(Utf8("bedreach-code-v1"), transcript, ecdhShared);
        uint n = BinaryPrimitives.ReadUInt32BigEndian(h);
        return new PairResult(secret, (n % 1_000_000).ToString("D6"));
    }

    public static bool FixedEquals(byte[] a, byte[] b) => CryptographicOperations.FixedTimeEquals(a, b);
}

/// <summary>
/// AES-256-GCM channel. Each frame = counter(8, big-endian) || ciphertext || tag(16).
/// Nonce = direction byte (0 = phone→PC, 1 = PC→phone), 3 zero bytes, counter(8).
/// Counters must strictly increase, which blocks replayed or reordered frames.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    private readonly AesGcm _aes;
    private readonly byte _sendDir, _recvDir;
    private ulong _sendCounter;
    private ulong _lastRecv;
    private bool _anyRecv;

    public SecureChannel(byte[] key, bool isServer)
    {
        _aes = new AesGcm(key, 16);
        _sendDir = (byte)(isServer ? 1 : 0);
        _recvDir = (byte)(isServer ? 0 : 1);
    }

    private static byte[] Nonce(byte dir, ulong counter)
    {
        var n = new byte[12];
        n[0] = dir;
        BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), counter);
        return n;
    }

    public byte[] Seal(byte[] plain)
    {
        ulong c = ++_sendCounter;
        var outBuf = new byte[8 + plain.Length + 16];
        BinaryPrimitives.WriteUInt64BigEndian(outBuf, c);
        _aes.Encrypt(Nonce(_sendDir, c), plain, outBuf.AsSpan(8, plain.Length), outBuf.AsSpan(8 + plain.Length, 16));
        return outBuf;
    }

    public byte[] Open(byte[] frame)
    {
        if (frame.Length < 8 + 16) throw new CryptographicException("short frame");
        ulong c = BinaryPrimitives.ReadUInt64BigEndian(frame);
        if (_anyRecv && c <= _lastRecv) throw new CryptographicException("replayed frame");
        int len = frame.Length - 24;
        var plain = new byte[len];
        _aes.Decrypt(Nonce(_recvDir, c), frame.AsSpan(8, len), frame.AsSpan(8 + len, 16), plain);
        _lastRecv = c; _anyRecv = true;
        return plain;
    }

    public void Dispose() => _aes.Dispose();
}
