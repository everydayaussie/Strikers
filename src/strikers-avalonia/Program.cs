using Avalonia;

namespace Strikers.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            return Selftest.Run(Identity.Checks);
        }

        if (args.Contains("--version"))
        {
            var mvid = System.Reflection.Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId;
            var commit = Release.Commit(System.Reflection.Assembly.GetExecutingAssembly());
            var stamp = commit is null ? "" : $"  commit {commit}";
            Console.WriteLine($"Strikers {mvid:N}" + stamp);
            Console.Out.Flush();
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
                         .UsePlatformDetect()
                         .LogToTrace();
    }
}
