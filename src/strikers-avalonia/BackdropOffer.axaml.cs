using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace Strikers.App;

public partial class BackdropOffer : Window
{
    private CancellationTokenSource? cancel;

    private bool downloading;

    public BackdropOffer()
    {
        AvaloniaXamlLoader.Load(this);

        Background = new SolidColorBrush(Palette.SlateDark.Panel.ToColor());
        this.FindControl<TextBlock>("Headline")!.Foreground = Palette.SlateDark.Text.ToBrush();
        this.FindControl<TextBlock>("ProgressNote")!.Foreground = Palette.SlateDark.Muted.ToBrush();
        this.FindControl<TextBlock>("Trouble")!.Foreground = Palette.SlateDark.Bad.ToBrush();
        this.FindControl<ProgressBar>("Bar")!.Foreground = Palette.SlateDark.Accent.ToBrush();

        IconFile.Apply(this);
        pictures = Missing();
        var backdrop = pictures.Any(p => p.Target == BackdropDownload.Target());
        var icon = pictures.Any(p => p.Target == IconFile.Target());
        this.FindControl<TextBlock>("Headline")!.Text = backdrop && icon
            ? "Download the background image and icon?"
            : icon ? "Download the icon?" : "Download the background image?";

        this.FindControl<Button>("DownloadButton")!.Click += async (_, _) => await Download();
        this.FindControl<Button>("LaterButton")!.Click += (_, _) => Close();
        this.FindControl<Button>("NeverButton")!.Click += (_, _) =>
        {
            var settings = Settings.Read();
            settings.OfferBackdrop = false;
            if (settings.Write() is { } trouble)
            {
                var line = this.FindControl<TextBlock>("Trouble")!;
                line.Text = $"Could not remember that: {trouble}";
                line.IsVisible = true;
                return;
            }

            Close();
        };
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => cancel?.Cancel();

        Closing += (_, _) => cancel?.Cancel();
    }

    private async Task Download()
    {
        var download = this.FindControl<Button>("DownloadButton")!;
        var later = this.FindControl<Button>("LaterButton")!;
        var never = this.FindControl<Button>("NeverButton")!;
        var cancelButton = this.FindControl<Button>("CancelButton")!;
        var row = this.FindControl<StackPanel>("ProgressRow")!;
        var bar = this.FindControl<ProgressBar>("Bar")!;
        var note = this.FindControl<TextBlock>("ProgressNote")!;
        var trouble = this.FindControl<TextBlock>("Trouble")!;

        if (downloading)
        {
            return;
        }

        downloading = true;
        download.IsVisible = false;
        later.IsVisible = false;
        never.IsVisible = false;
        cancelButton.IsVisible = true;
        trouble.IsVisible = false;
        row.IsVisible = true;
        bar.IsIndeterminate = true;
        note.Text = "Connecting.";

        string? problem = null;
        using (cancel = new CancellationTokenSource())
        {
            var token = cancel.Token;
            try
            {
                var total = pictures.Sum(p => p.Bytes);
                long before = 0;
                foreach (var picture in pictures)
                {
                    var done = before;
                    problem = await Task.Run(() => BackdropDownload.FetchAsync(picture, got =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            bar.IsIndeterminate = false;
                            bar.Value = 100.0 * (done + got) / total;
                            note.Text = $"{(done + got) / 1_000_000.0:F1} of {total / 1_000_000.0:F1} MB";
                        });
                    }, token), token);
                    if (problem is not null)
                    {
                        break;
                    }

                    before += picture.Bytes;
                }
            }
            catch (Exception e)
            {
                problem = e.Message;
            }
        }

        cancel = null;
        downloading = false;

        if (problem is null)
        {
            Close();
            return;
        }

        row.IsVisible = false;
        cancelButton.IsVisible = false;
        download.IsVisible = true;
        later.IsVisible = true;
        never.IsVisible = neverAllowed;
        if (problem != "cancelled")
        {
            trouble.Text = $"Could not download it: {problem}";
            trouble.IsVisible = true;
        }
    }

    private List<Picture> pictures = [];

    internal static List<Picture> Missing()
    {
        var missing = new List<Picture>();
        if (BackdropFile.Find() is null)
        {
            missing.Add(BackdropDownload.Backdrop());
        }

        if (IconFile.Wanted())
        {
            missing.Add(BackdropDownload.Icon());
        }

        return missing;
    }

    private bool neverAllowed = true;

    public void FromSettings()
    {
        neverAllowed = false;
        this.FindControl<Button>("NeverButton")!.IsVisible = false;
    }
}
