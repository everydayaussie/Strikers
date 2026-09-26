using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Strikers.App;

public static class ReportWindow
{
    private const string FolderClass = "CabinetWClass";

    private const uint NearestMonitor = 2;
    private const int Restore = 9;
    private const uint NoActivate = 0x0010;
    private const uint AsyncWindowPos = 0x4000;
    private const int Tries = 50;
    private const int TryEveryMs = 100;
    private const int SecondLookMs = 700;
    private const uint QueryLimited = 0x1000;

    private delegate bool EachWindow(nint window, nint state);

    [StructLayout(LayoutKind.Sequential)]
    private struct Box
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Box Monitor;
        public Box Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern nint OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] name, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EachWindow each, nint state);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, [Out] char[] name, int room);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint window, int how);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint fallback);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    public static List<nint> FolderWindows()
    {
        var found = new List<nint>();
        var name = new char[64];
        var explorer = Identity.ExplorerPath();
        EachWindow each = (window, _) =>
        {
            var length = GetClassNameW(window, name, name.Length);
            if (length > 0 && new string(name, 0, length) == FolderClass && Report.IsExplorerImage(ImageOf(window), explorer))
            {
                found.Add(window);
            }

            return true;
        };

        try
        {
            EnumWindows(each, 0);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }

        GC.KeepAlive(each);
        return found;
    }

    private static string? ImageOf(nint window)
    {
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return null;
        }

        var process = OpenProcess(QueryLimited, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var path = new char[1024];
            var size = (uint)path.Length;
            return QueryFullProcessImageNameW(process, 0, path, ref size) ? new string(path, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public static void FitWhenOpen(IReadOnlyCollection<nint> before, nint owner)
    {
        _ = Task.Run(() => Fit(before, owner));
    }

    private static async Task Fit(IReadOnlyCollection<nint> before, nint owner)
    {
        try
        {
            for (var attempt = 0; attempt < Tries; attempt++)
            {
                await Task.Delay(TryEveryMs);
                var shown = FolderWindows().Where(IsWindowVisible).ToList();
                if (Report.NewWindow(before, shown) is not { } window)
                {
                    continue;
                }

                Place(window, owner);
                await Task.Delay(SecondLookMs);
                Place(window, owner);
                return;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private static void Place(nint window, nint owner)
    {
        var anchor = owner != 0 ? owner : window;
        var monitor = MonitorFromWindow(anchor, NearestMonitor);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info))
        {
            return;
        }

        var dpi = GetDpiForWindow(anchor);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var (x, y, width, height) = Report.FolderWindowBounds(info.Work.Left, info.Work.Top, info.Work.Right,
                                                              info.Work.Bottom, scale);

        if (IsZoomed(window))
        {
            ShowWindowAsync(window, Restore);
        }

        SetWindowPos(window, 0, x, y, width, height, NoActivate | AsyncWindowPos);
        SetForegroundWindow(window);
    }
}
