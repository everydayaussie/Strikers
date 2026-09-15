using System.Security.Cryptography;
using System.Text;

namespace Strikers.Netplay;

public static class RoomId
{
    public static string For(string code)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes("Strikers-room-v1\0" + Normalise(code)));
        return Convert.ToHexString(h)[..16];
    }

    public static string Random()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    }

    public static string Normalise(string code)
    {
        return (code ?? "").Trim().ToUpperInvariant();
    }
}

public sealed class SessionKeys
{
    public required byte[] Send { get; init; }
    public required byte[] Recv { get; init; }
    public required string Fingerprint { get; init; }

    public required byte[] Binding { get; init; }
}

public static class KeyExchange
{
    private const string Label = "Strikers-e2e-v2";

    public const int CommitmentLength = 32;
    public const int BindingLength = 32;

    public static ECDiffieHellman Begin()
    {
        return ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    }

    public static byte[] PublicBlob(ECDiffieHellman ours)
    {
        return ours.PublicKey.ExportSubjectPublicKeyInfo();
    }

    public static byte[] Commitment(byte[] publicBlob, bool bound)
    {
        return SHA256.HashData(Concat(Encoding.UTF8.GetBytes("Strikers-commit-v1"), [bound ? (byte)1 : (byte)0],
                                      publicBlob));
    }

    public static SessionKeys? Complete(string code, ECDiffieHellman ours, byte[] theirPublic, bool isHost,
                                        byte[]? binding = null)
    {
        byte[] shared;
        byte[] theirs;

        try
        {
            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(theirPublic, out _);
            if (peer.KeySize != 256)
            {
                return null;
            }

            shared = ours.DeriveRawSecretAgreement(peer.PublicKey);
            theirs = theirPublic;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }

        var mine = PublicBlob(ours);

        var (first, second) = isHost ? (mine, theirs) : (theirs, mine);

        var ikm = Concat(shared, SHA256.HashData(Encoding.UTF8.GetBytes(RoomId.Normalise(code))), binding ?? []);
        var mode = Encoding.UTF8.GetBytes(binding is null ? "open" : "bound");
        var context = Concat(Encoding.UTF8.GetBytes(Label), mode, first, second);

        var hostToGuest = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, context,
                                         Encoding.UTF8.GetBytes("host->guest"));
        var guestToHost = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, context,
                                         Encoding.UTF8.GetBytes("guest->host"));
        var fp = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 8, context,
                                Encoding.UTF8.GetBytes("fingerprint"));
        var sessionBinding = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, BindingLength, context,
                                            Encoding.UTF8.GetBytes("session-binding"));

        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(ikm);

        return new SessionKeys
        {
            Send = isHost ? hostToGuest : guestToHost,
            Recv = isHost ? guestToHost : hostToGuest,
            Fingerprint = Convert.ToHexString(fp),
            Binding = sessionBinding,
        };
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var all = new byte[total];
        var at = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, all, at, p.Length);
            at += p.Length;
        }

        return all;
    }
}

public sealed class SecureChannel
{
    private readonly byte[] _send;
    private readonly byte[] _recv;
    private long _sealed;
    private ulong _lastSeen;
    private bool _seenAny;

    public SecureChannel(SessionKeys keys)
    {
        _send = keys.Send;
        _recv = keys.Recv;
    }

    public string Seal(string line)
    {
        var counter = (ulong)Interlocked.Increment(ref _sealed) - 1;
        var nonce = NonceFor(counter);
        var plain = Encoding.UTF8.GetBytes(line);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];

        using var gcm = new AesGcm(_send, 16);
        gcm.Encrypt(nonce, plain, cipher, tag, BitConverter.GetBytes(counter));

        var wire = new byte[8 + 16 + cipher.Length];
        BitConverter.GetBytes(counter).CopyTo(wire, 0);
        tag.CopyTo(wire, 8);
        cipher.CopyTo(wire, 24);
        return Convert.ToBase64String(wire);
    }

    public string? Open(string wire)
    {
        byte[] raw;
        try { raw = Convert.FromBase64String(wire); }
        catch (FormatException) { return null; }

        if (raw.Length < 24)
        {
            return null;
        }

        var counter = BitConverter.ToUInt64(raw, 0);

        if (_seenAny && counter <= _lastSeen)
        {
            return null;
        }

        var tag = raw[8..24];
        var cipher = raw[24..];
        var plain = new byte[cipher.Length];

        try
        {
            using var gcm = new AesGcm(_recv, 16);
            gcm.Decrypt(NonceFor(counter), cipher, tag, plain, BitConverter.GetBytes(counter));
        }
        catch (CryptographicException)
        {
            return null;
        }

        _lastSeen = counter;
        _seenAny = true;
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] NonceFor(ulong counter)
    {
        var nonce = new byte[12];
        BitConverter.GetBytes(counter).CopyTo(nonce, 4);
        return nonce;
    }
}

