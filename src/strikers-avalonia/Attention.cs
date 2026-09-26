using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Strikers.App;

public static class Attention
{
    private const uint FlashTray = 0x00000002;
    private const uint FlashTimerUntilForeground = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Rate;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    public static void Raise(Window? window)
    {
        if (window is null)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Maximized;
        }

        window.Topmost = true;
        Flash(window);
    }

    public static void Settle(Window? window)
    {
        if (window is not null)
        {
            window.Topmost = false;
        }
    }

    private static void Flash(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (handle == nint.Zero)
        {
            return;
        }

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = handle,
            Flags = FlashTray | FlashTimerUntilForeground,
            Count = 0,
            Rate = 0,
        };

        try
        {
            FlashWindowEx(ref info);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
