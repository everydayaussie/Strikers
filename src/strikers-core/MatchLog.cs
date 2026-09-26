namespace Strikers.Core;

public sealed class MatchLog
{
    public const string Name = "strikers.log";
    public const string PreviousName = "strikers-previous.log";
    public const string UnmaskedName = "strikers-unmasked.log";
    public const string PreviousUnmaskedName = "strikers-previous-unmasked.log";
    public const string Marker = "masked log, mask set 2";
    public const long MaxBytes = 1_000_000;

    public const int MaxLineChars = 4096;

    public static string Cut(string line)
    {
        return line.Length <= MaxLineChars ? line : line[..MaxLineChars] + " ... (cut)";
    }

    private readonly object _gate = new();

    private bool _swept;

    public string Path { get; }

    public string PreviousPath { get; }

    public string UnmaskedPath { get; }

    public string PreviousUnmaskedPath { get; }

    public MatchLog(string folder)
    {
        Path = System.IO.Path.Combine(folder, Name);
        PreviousPath = System.IO.Path.Combine(folder, PreviousName);
        UnmaskedPath = System.IO.Path.Combine(folder, UnmaskedName);
        PreviousUnmaskedPath = System.IO.Path.Combine(folder, PreviousUnmaskedName);
    }

    public static MatchLog BesideTheExe()
    {
        return new MatchLog(AppContext.BaseDirectory);
    }

    public void Write(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var stamped = Stamp(line);
        lock (_gate)
        {
            try
            {
                if (!_swept)
                {
                    _swept = true;
                    SweepUnmasked();
                }

                if (File.Exists(Path) && new FileInfo(Path).Length >= MaxBytes)
                {
                    File.Move(Path, PreviousPath, overwrite: true);
                }

                if (!File.Exists(Path))
                {
                    File.AppendAllText(Path, Stamp(Marker) + Environment.NewLine);
                }

                File.AppendAllText(Path, stamped + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Stamp(string line)
    {
        return $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {Cut(line)}";
    }

    private void SweepUnmasked()
    {
        if (!KeepInPlace(Path))
        {
            MoveAside(Path, UnmaskedPath);
        }

        if (!KeepInPlace(PreviousPath))
        {
            MoveAside(PreviousPath, PreviousUnmaskedPath);
        }
    }

    private static bool KeepInPlace(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var first = reader.ReadLine();
            return first is not null && first.EndsWith(Marker, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void MoveAside(string from, string to)
    {
        try
        {
            if (File.Exists(from))
            {
                File.Move(from, to, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
