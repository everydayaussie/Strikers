using System.Diagnostics;

namespace Strikers.Netplay;

internal static class ParentWatch
{
    public static void Start(int pid, Action onExit)
    {
        if (pid <= 0)
        {
            return;
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            onExit();
            return;
        }

        var watcher = new Thread(() =>
        {
            try
            {
                parent.WaitForExit();
            }
            catch
            {
            }
            finally
            {
                parent.Dispose();
            }

            onExit();
        })
        {
            IsBackground = true,
            Name = "parent-watch",
        };
        watcher.Start();
    }
}
