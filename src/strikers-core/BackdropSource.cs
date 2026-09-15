using System.Security.Cryptography;

namespace Strikers.Core;

public static class BackdropSource
{
    public const string Url = "https://files.catbox.moe/c5biwo.png";

    public const string Host = "files.catbox.moe";

    public const string Sha256 = "15ab9854cfaea518191ca707ba1669467b02105de6db820da1a46425b7141e87";

    public const long Bytes = 17407324;

    public const string FileName = "backdrop.png";

    public static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static readonly TimeSpan QuietWithin = TimeSpan.FromSeconds(30);

    public static List<string> Places(string folder)
    {
        var places = new List<string> { folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) };
        if (NetplayTool.InRepoBuild(folder))
        {
            places.Add(Path.GetFullPath(Path.Combine(folder, @"..\..\..\Assets")));
        }

        return places;
    }

    public static bool Pinned(string path, long expectedBytes, string expectedSha256)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expectedBytes)
            {
                return false;
            }

            var bytes = File.ReadAllBytes(path);
            return Problem(bytes.Length, HexSha256(bytes), bytes, expectedBytes, expectedSha256) is null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string? Problem(long length, string sha256Hex, ReadOnlySpan<byte> head,
                                  long expectedBytes, string expectedSha256)
    {
        if (length != expectedBytes)
        {
            return $"the file is {length} bytes, not the {expectedBytes} expected";
        }

        if (head.Length < PngSignature.Length || !head[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return "the file does not start like a PNG";
        }

        if (!string.Equals(sha256Hex, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return "the file's fingerprint is not the picture's";
        }

        return null;
    }

    public static string? Problem(long length, string sha256Hex, ReadOnlySpan<byte> head)
    {
        return Problem(length, sha256Hex, head, Bytes, Sha256);
    }

    public static string HexOf(byte[] hash)
    {
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string HexSha256(byte[] body)
    {
        return HexOf(SHA256.HashData(body));
    }
}
