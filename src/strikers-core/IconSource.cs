namespace Strikers.Core;

public static class IconSource
{
    public const string Url = "https://files.catbox.moe/hycg5v.png";

    public const string Host = "files.catbox.moe";

    public const string Sha256 = "5aa7da9d0880e17666a5a0df8bf811050139990acdb65e22c6fec264e6249d8a";

    public const long Bytes = 38928;

    public const string FileName = "strikers-icon.png";

    public static bool Pinned(string path)
    {
        return BackdropSource.Pinned(path, Bytes, Sha256);
    }

    public static bool Hosted()
    {
        return Url.Length > 0 && Sha256.Length == 64 && Bytes > 0;
    }
}
