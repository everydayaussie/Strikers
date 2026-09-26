using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Strikers.App;

public partial class UpdateOffer : Window
{
    public UpdateOffer()
        : this("", "")
    {
    }

    public UpdateOffer(string ours, string newest)
    {
        AvaloniaXamlLoader.Load(this);

        Background = new SolidColorBrush(Palette.SlateDark.Panel.ToColor());
        this.FindControl<TextBlock>("Headline")!.Foreground = Palette.SlateDark.Text.ToBrush();
        this.FindControl<TextBlock>("Versions")!.Foreground = Palette.SlateDark.Muted.ToBrush();
        this.FindControl<TextBlock>("Trouble")!.Foreground = Palette.SlateDark.Bad.ToBrush();
        IconFile.Apply(this);

        this.FindControl<TextBlock>("Versions")!.Text = Release.OfferText(ours, newest);

        this.FindControl<Button>("OpenButton")!.Click += (_, _) => OpenPage();
        this.FindControl<Button>("LaterButton")!.Click += (_, _) => Close();
    }

    private void OpenPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = UpdateCheck.Page(),
                UseShellExecute = true,
            });
            Close();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            Tell($"Could not open the page. Go to {UpdateCheck.Page()} in your browser.");
        }
    }

    private void Tell(string text)
    {
        var line = this.FindControl<TextBlock>("Trouble")!;
        line.Text = text;
        line.IsVisible = true;
    }
}
