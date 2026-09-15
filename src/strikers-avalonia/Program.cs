using Avalonia;

namespace Strikers.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            return Selftest.Run();
        }

        var (testFor, testExpires) = TestBuild.Read(System.Reflection.Assembly.GetExecutingAssembly());
        if (args.Contains("--version"))
        {
            var mvid = System.Reflection.Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId;
            var mark = TestBuild.Mark(testFor, testExpires);
            var commit = Release.Commit(System.Reflection.Assembly.GetExecutingAssembly());
            var stamp = commit is null ? "" : $"  commit {commit}";
            Console.WriteLine($"Strikers {mvid:N}" + stamp + (mark is null ? "" : $"  ({mark})"));
            Console.Out.Flush();
            return 0;
        }

        if (TestBuild.Expired(testExpires, DateOnly.FromDateTime(DateTime.Now)))
        {
            MessageBoxW(nint.Zero, TestBuild.ExpiredMessage(), "Strikers", 0x40);
            return 3;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
                         .UsePlatformDetect()
                         .LogToTrace();
    }
}
