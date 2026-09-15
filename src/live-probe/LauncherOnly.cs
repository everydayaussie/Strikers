using System.Reflection;
using System.Runtime.InteropServices;

namespace Strikers.LiveProbe;

internal static class LauncherOnly
{
    public const string Key = "LauncherOnly";
    public const int ExitCode = 5;

    public static readonly string[] Parents = ["Strikers.exe", "netplay.exe"];

    private const uint QueryLimitedInformation = 0x1000;
    private const uint IconInformation = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int infoClass, ref BasicInformation info,
                                                        int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(nint process, int flags, char[] name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel,
                                               out long user);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] list, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint window, string text, string caption, uint type);

    public static bool Locked(Assembly assembly)
    {
        foreach (var a in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (a.Key == Key && string.Equals(a.Value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool Allows(string[] args, string ownPath, DateTime ownStart, string? parentPath,
                              DateTime? parentStart, IReadOnlyList<string> parents)
    {
        if (args is ["--version"] or ["--selftest"])
        {
            return true;
        }

        if (parentPath is null || parentStart is not { } started || started > ownStart)
        {
            return false;
        }

        var ownFolder = Path.GetDirectoryName(Path.GetFullPath(ownPath));
        var parentFolder = Path.GetDirectoryName(Path.GetFullPath(parentPath));
        if (!string.Equals(ownFolder, parentFolder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = Path.GetFileName(parentPath);
        foreach (var p in parents)
        {
            if (string.Equals(p, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string Message()
    {
        return "This file is part of Strikers. Open Strikers.exe to play.";
    }

    public static bool Refuses(string[] args)
    {
        if (!Locked(typeof(LauncherOnly).Assembly))
        {
            return false;
        }

        var ownPath = Environment.ProcessPath;
        var ownStart = StartOf(GetCurrentProcess());
        var (parentPath, parentStart) = Parent();
        if (ownPath is not null && ownStart is { } own && Allows(args, ownPath, own, parentPath, parentStart, Parents))
        {
            return false;
        }

        Console.Error.WriteLine(Message());
        var consoles = new uint[2];
        if (GetConsoleProcessList(consoles, (uint)consoles.Length) == 1)
        {
            MessageBoxW(0, Message(), "Strikers", IconInformation);
        }

        return true;
    }

    private static (string? Path, DateTime? Start) Parent()
    {
        var info = new BasicInformation();
        if (NtQueryInformationProcess(GetCurrentProcess(), 0, ref info, Marshal.SizeOf<BasicInformation>(), out _) != 0)
        {
            return (null, null);
        }

        var handle = OpenProcess(QueryLimitedInformation, false, (int)info.InheritedFromUniqueProcessId);
        if (handle == 0)
        {
            return (null, null);
        }

        try
        {
            var buffer = new char[32768];
            var size = buffer.Length;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size))
            {
                return (null, null);
            }

            return (new string(buffer, 0, size), StartOf(handle));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static DateTime? StartOf(nint process)
    {
        if (!GetProcessTimes(process, out var creation, out _, out _, out _))
        {
            return null;
        }

        return DateTime.FromFileTimeUtc(creation);
    }
}
