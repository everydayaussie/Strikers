namespace Strikers.Core;

public sealed class MatchLog
{
    public const string Name = "strikers.log";
    public const string PreviousName = "strikers-previous.log";
    public const long MaxBytes = 1_000_000;

    public const int MaxLineChars = 4096;

    public static string Cut(string line)
    {
        return line.Length <= MaxLineChars ? line : line[..MaxLineChars] + " ... (cut)";
    }

    private readonly object _gate = new();

    public string Path { get; }

    public string PreviousPath { get; }

    public MatchLog(string folder)
    {
        Path = System.IO.Path.Combine(folder, Name);
        PreviousPath = System.IO.Path.Combine(folder, PreviousName);
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

        var stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {Cut(line)}";
        lock (_gate)
        {
            try
            {
                if (File.Exists(Path) && new FileInfo(Path).Length >= MaxBytes)
                {
                    File.Move(Path, PreviousPath, overwrite: true);
                }

                File.AppendAllText(Path, stamped + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
