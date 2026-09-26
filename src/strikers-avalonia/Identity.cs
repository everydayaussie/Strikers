using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Strikers.App;

public static class Identity
{
    private static Task<(string Netplay, string LiveProbe)>? tools;

    public static void Start()
    {
        tools ??= Task.Run(ReadTools);
    }

    public static void Refresh()
    {
        tools = Task.Run(ReadTools);
    }

    private static (string Netplay, string LiveProbe) ReadTools()
    {
        var netplay = NetplayTool.WithoutName("netplay", Ask(NetplayTool.Netplay(), "--version"));
        var liveProbe = NetplayTool.WithoutName("live-probe", Ask(NetplayTool.LiveProbe(), "--version"));
        return (netplay, liveProbe);
    }

    public static ReportFacts Facts(string why, bool? hosted)
    {
        var me = Assembly.GetExecutingAssembly();
        string? netplay = null;
        string? liveProbe = null;
        if (tools is { } pending && !pending.IsCompleted)
        {
            try
            {
                pending.Wait(2000);
            }
            catch (AggregateException)
            {
            }
        }

        if (tools is { IsCompletedSuccessfully: true } read)
        {
            (netplay, liveProbe) = read.Result;
        }

        return new ReportFacts(why, UpdateCheck.Ours(), Release.Commit(me), $"{me.ManifestModule.ModuleVersionId:N}",
                               netplay, liveProbe, hosted);
    }

    public static string? Ask(string? exe, string arg)
    {
        if (exe is null || !File.Exists(exe))
        {
            return null;
        }

        var text = NetplayTool.Run(exe, [arg], timeoutMs: 20000);
        if (text is null)
        {
            return null;
        }

        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                   .Select(l => l.Trim())
                   .FirstOrDefault(l => l.Length > 0);
    }

    public const string SendHint = "Drag the selected report into the page that opened.";

    public static string ExplorerPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
    }

    public static void Checks(Action<string, bool> check)
    {
        var explorer = ExplorerPath();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        check("the file manager is started from the Windows folder by full path, never by bare name",
              Path.IsPathRooted(explorer)
              && explorer.EndsWith(@"\explorer.exe", StringComparison.OrdinalIgnoreCase)
              && windows.Length > 3
              && explorer.StartsWith(windows, StringComparison.OrdinalIgnoreCase));
    }

    public static string? SendReport(string? zip, nint owner, string? title)
    {
        var folder = AppContext.BaseDirectory;
        var log = Path.Combine(folder, MatchLog.Name);
        var pick = zip is not null && File.Exists(zip) ? zip : File.Exists(log) ? log : null;
        var page = Release.IssuesUrl(Release.GitHubRepo, title);
        string? problem = null;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = page, UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            problem = $"Could not open the page. Go to {Release.IssuesUrl(Release.GitHubRepo)} in your browser.";
        }

        var before = ReportWindow.FolderWindows();
        try
        {
            var explorer = new ProcessStartInfo(ExplorerPath()) { UseShellExecute = false };
            if (pick is not null)
            {
                explorer.ArgumentList.Add("/select,");
                explorer.ArgumentList.Add(pick);
            }
            else
            {
                explorer.ArgumentList.Add(folder.TrimEnd('\\'));
            }

            Process.Start(explorer)?.Dispose();
            ReportWindow.FitWhenOpen(before, owner);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            problem = $"Could not open the folder. {MatchDriver.StartFailedReason(e)}".TrimEnd();
        }

        return problem;
    }
}
