using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Strikers.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        GuardClipboard();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Strikers.Core.Report.Tidy(System.AppContext.BaseDirectory);
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void GuardClipboard()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            if (!Play.ClipboardBusy(e.Exception))
            {
                return;
            }

            e.Handled = true;
            ToastRail.Show(ToastKind.Bad, "Could not use the clipboard. Try again in a moment.");
        };
    }
}
