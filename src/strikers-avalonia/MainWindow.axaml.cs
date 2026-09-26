using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;

namespace Strikers.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        var root = this.FindControl<Panel>("Root")!;
        root.Background = Palette.SlateDark.Window.ToBrush();
        KeepContentOnScreen(root);

        DressCaption();
        DressHome();
        DressVignette();
        ShowBackdrop();
        WireSettingsToggle();

        Closing += (_, _) => this.FindControl<PlayPanel>("PlayFlow")!.ShutDown();

        Activated += (_, _) => Attention.Settle(this);

        var play = this.FindControl<PlayPanel>("PlayFlow")!;
        var boards = this.FindControl<BoardsPanel>("BoardEditor")!;
        boards.Changed += play.RefreshBoards;

        var tools = this.FindControl<SettingsPanel>("Tools")!;
        tools.BoardsChanged += () =>
        {
            boards.RedrawSaved();
            play.RefreshBoards();
        };
        tools.ArmiesChanged += this.FindControl<ArmiesPanel>("ArmyBuilder")!.RefreshSaved;

        WireFocus(play);

        FitPlayBudget();

        tools.BackdropWanted += async () => await OfferBackdrop(fromSettings: true);
        tools.RefreshBackdropButton();

        IconFile.Apply(this);
        Opened += async (_, _) =>
        {
            var checking = CheckForUpdate(play);
            var forced = Environment.GetEnvironmentVariable("STRIKERS_SHOW_OFFER") is not null;
            if ((BackdropOffer.Missing().Count > 0 || forced) && Settings.Read().OfferBackdrop)
            {
                await OfferBackdrop(fromSettings: false);
            }

            await checking;
        };
    }

    private string? updateWaiting;

    private bool updateShowing;

    private async Task CheckForUpdate(PlayPanel play)
    {
        var (newest, note) = await UpdateCheck.RunAsync();
        if (note.Length > 0)
        {
            play.Note(note);
        }

        if (newest is null)
        {
            return;
        }

        this.FindControl<SettingsPanel>("Tools")!.RefreshVersionsQuietly();

        if (!Release.Offer(UpdateCheck.Ours(), newest))
        {
            return;
        }

        updateWaiting = newest;
        OfferUpdateWhenFree();
    }

    private void OfferUpdateWhenFree()
    {
        if (updateWaiting is not { } newest || updateShowing)
        {
            return;
        }

        if (offering || !this.FindControl<PlayPanel>("PlayFlow")!.Idle)
        {
            DispatcherTimer.RunOnce(OfferUpdateWhenFree, TimeSpan.FromSeconds(5));
            return;
        }

        updateWaiting = null;
        _ = OfferUpdate(newest);
    }

    private async Task OfferUpdate(string newest)
    {
        updateShowing = true;
        try
        {
            await new UpdateOffer(UpdateCheck.Ours() ?? "", newest).ShowDialog(this);
        }
        finally
        {
            updateShowing = false;
        }
    }

    private bool offering;

    private async Task OfferBackdrop(bool fromSettings)
    {
        if (offering)
        {
            return;
        }

        offering = true;
        try
        {
            var before = BackdropFile.Find();
            var offer = new BackdropOffer();
            if (fromSettings)
            {
                offer.FromSettings();
            }

            await offer.ShowDialog(this);

            if (BackdropFile.Find() != before)
            {
                ReloadBackdrop();
            }

            IconFile.Apply(this);
        }
        finally
        {
            offering = false;
            this.FindControl<SettingsPanel>("Tools")!.RefreshBackdropButton();

            RefitSettings();
        }
    }

    private void ReloadBackdrop()
    {
        this.FindControl<Border>("Hint")!.IsVisible = false;
        ShowBackdrop();
    }

    private bool focused;

    private const double FocusWidth = 640;

    private static readonly TimeSpan FocusGlide = TimeSpan.FromMilliseconds(320);

    private void WireFocus(PlayPanel play)
    {
        var home = this.FindControl<Grid>("Home")!;
        var playPanel = this.FindControl<Border>("PlayPanel")!;
        var column = (Grid)playPanel.Parent!;
        var tint = this.FindControl<Grid>("PlayTint")!;
        var settings = this.FindControl<Border>("SettingsPanel")!;
        var cog = this.FindControl<Button>("SettingsCog")!;
        var shelves = new[]
        {
            this.FindControl<Border>("BoardsPanel")!,
            this.FindControl<Border>("ArmiesPanel")!,
        };

        foreach (var shelf in shelves)
        {
            shelf.Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = FocusGlide },
            };
        }

        Transitions Gliding()
        {
            return new Transitions
            {
                new DoubleTransition { Property = WidthProperty, Duration = FocusGlide, Easing = new CubicEaseOut() },
                new DoubleTransition { Property = HeightProperty, Duration = FocusGlide, Easing = new CubicEaseOut() },
                new ThicknessTransition { Property = MarginProperty, Duration = FocusGlide, Easing = new CubicEaseOut() },
            };
        }

        Transitions Resting()
        {
            return new Transitions
            {
                new DoubleTransition { Property = HeightProperty, Duration = FocusGlide, Easing = new CubicEaseOut() },
            };
        }

        double ContentHeightAt(double width)
        {
            tint.Width = double.NaN;
            tint.Height = double.NaN;

            foreach (var layoutable in tint.GetVisualDescendants().OfType<Avalonia.Layout.Layoutable>())
            {
                layoutable.InvalidateMeasure();
            }

            tint.InvalidateMeasure();
            tint.Measure(new Size(width - 2, double.PositiveInfinity));
            return Math.Min(tint.DesiredSize.Height + 2, column.Bounds.Height - 12);
        }

        double ContentHeight()
        {
            return ContentHeightAt(FocusWidth);
        }

        void Freeze()
        {
            var at = playPanel.Bounds;
            playPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            playPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            playPanel.Width = at.Width;
            playPanel.Height = at.Height;
            playPanel.Margin = new Thickness(at.X, at.Y, 0, 0);
            playPanel.Transitions = Gliding();
        }

        var glides = 0;

        var centred = false;

        var gliding = false;

        void PinTint(double width, double height)
        {
            tint.Width = width - 2;
            tint.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            tint.Height = height - 2;
            tint.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        }

        void FreeTint()
        {
            tint.Width = double.NaN;
            tint.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            tint.Height = double.NaN;
            tint.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        }

        void GlideToCentre()
        {
            var glide = ++glides;
            if (!gliding)
            {
                Freeze();
                gliding = true;
            }

            var height = ContentHeight();

            PinTint(FocusWidth, height);
            playPanel.Width = FocusWidth;
            playPanel.Height = height;
            playPanel.Margin = new Thickness(Math.Max(6, (home.Bounds.Width - FocusWidth) / 2),
                                             Math.Max(6, (column.Bounds.Height - height) / 2), 0, 0);

            DispatcherTimer.RunOnce(() =>
            {
                if (!focused || glide != glides)
                {
                    return;
                }

                gliding = false;
                FreeTint();
                playPanel.Transitions = null;
                playPanel.Margin = new Thickness(6);
                playPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
                playPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                playPanel.Transitions = Resting();
                centred = true;
            }, FocusGlide + TimeSpan.FromMilliseconds(40));
        }

        void EaseTo(double height)
        {
            var glide = ++glides;
            tint.Height = height - 2;
            tint.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            if (double.IsNaN(playPanel.Height) || Math.Abs(playPanel.Height - height) > 0.5)
            {
                playPanel.Height = height;
            }

            DispatcherTimer.RunOnce(() =>
            {
                if (!focused || glide != glides)
                {
                    return;
                }

                tint.Height = double.NaN;
                tint.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            }, FocusGlide + TimeSpan.FromMilliseconds(40));
        }

        void Refit()
        {
            if (!focused)
            {
                return;
            }

            if (centred)
            {
                EaseTo(ContentHeight());
            }
            else
            {
                GlideToCentre();
            }
        }

        play.ContentChanged += Refit;

        column.PropertyChanged += (_, e) =>
        {
            if (e.Property != BoundsProperty || !focused || !centred)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (focused && centred)
                {
                    EaseTo(ContentHeight());
                }
            }, DispatcherPriority.Background);
        };

        play.FocusChanged += on =>
        {
            focused = on;
            if (on)
            {
                SetSettingsOpen(false);

                playPanel.MaxHeight = double.PositiveInfinity;

                column.RowDefinitions[0].Height = GridLength.Star;
                settings.IsEnabled = false;
                cog.IsVisible = false;
                centred = false;

                Grid.SetColumnSpan(column, 3);
                column.ZIndex = 1;

                foreach (var shelf in shelves)
                {
                    shelf.Opacity = 0;
                    shelf.IsHitTestVisible = false;
                    shelf.IsEnabled = false;
                }

                GlideToCentre();
                return;
            }

            var back = ++glides;
            centred = false;
            gliding = false;
            Freeze();

            var restWidth = (home.Bounds.Width * 0.25) - 12;
            var restHeight = ContentHeightAt(restWidth);
            PinTint(restWidth, restHeight);
            playPanel.Width = restWidth;
            playPanel.Height = restHeight;
            playPanel.Margin = new Thickness(6);

            foreach (var shelf in shelves)
            {
                shelf.Opacity = 1;
                shelf.IsHitTestVisible = true;
                shelf.IsEnabled = true;
            }

            DispatcherTimer.RunOnce(() =>
            {
                if (focused || back != glides)
                {
                    return;
                }

                FreeTint();
                playPanel.Transitions = null;
                playPanel.Width = double.NaN;
                playPanel.Height = double.NaN;
                playPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
                playPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
                Grid.SetColumnSpan(column, 1);
                column.ZIndex = 0;

                column.RowDefinitions[0].Height = GridLength.Auto;
                FitPlayBudget();

                settings.IsEnabled = true;
                cog.IsVisible = true;
            }, FocusGlide + TimeSpan.FromMilliseconds(40));
        };
    }

    private bool settingsOpen;

    private Action<bool> SetSettingsOpen = _ => { };

    private Action RefitSettings = () => { };

    private void WireSettingsToggle()
    {
        var panel = this.FindControl<Border>("SettingsPanel")!;
        var column = (Grid)panel.Parent!;

        panel.MaxHeight = 0;
        panel.Opacity = 0;
        panel.Margin = new Thickness(6, 0, 6, 0);

        panel.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = MaxHeightProperty,
                Duration = TimeSpan.FromMilliseconds(280),
                Easing = new CubicEaseOut(),
            },
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) },
            new ThicknessTransition
            {
                Property = MarginProperty,
                Duration = TimeSpan.FromMilliseconds(280),
                Easing = new CubicEaseOut(),
            },
        };

        var tint = this.FindControl<Grid>("SettingsTint")!;

        double Target()
        {
            var width = panel.Bounds.Width > 0 ? panel.Bounds.Width - 2 : column.Bounds.Width - 14;

            var tools = this.FindControl<SettingsPanel>("Tools")!;
            var pin = tools.LiftPin();
            tint.Measure(new Size(width, double.PositiveInfinity));
            var want = tint.DesiredSize.Height + 2;
            tools.RestorePin(pin);
            var playPanel = this.FindControl<Border>("PlayPanel")!;
            var margins = playPanel.Margin.Top + playPanel.Margin.Bottom;
            var cap = Math.Max(column.Bounds.Height - playPanel.Bounds.Height - (2 * margins),
                               SettingsFloor);
            return Math.Min(want, cap);
        }

        ScrollViewer? Bar()
        {
            return panel.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        }

        SetSettingsOpen = open =>
        {
            if (open == settingsOpen)
            {
                return;
            }

            settingsOpen = open;
            if (Bar() is { } bar)
            {
                bar.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            }

            if (settingsOpen)
            {
                settingsWant = Target();
                panel.MaxHeight = settingsWant;
                panel.Opacity = 1;
                panel.Margin = new Thickness(6);
                DispatcherTimer.RunOnce(() =>
                {
                    if (settingsOpen && Bar() is { } settled)
                    {
                        settled.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    }
                }, TimeSpan.FromMilliseconds(340));
            }
            else
            {
                panel.MaxHeight = 0;
                panel.Opacity = 0;
                panel.Margin = new Thickness(6, 0, 6, 0);
            }

            FitPlayBudget();
        };

        this.FindControl<Button>("SettingsCog")!.Click += (_, _) => SetSettingsOpen(!settingsOpen);

        if (Environment.GetEnvironmentVariable("STRIKERS_SHOW_SETTINGS") is not null)
        {
            Opened += (_, _) => Dispatcher.UIThread.Post(() => SetSettingsOpen(true),
                                                          DispatcherPriority.Background);
        }

        column.PropertyChanged += (_, e) =>
        {
            if (e.Property != BoundsProperty)
            {
                return;
            }

            if (settingsOpen)
            {
                settingsWant = Target();
                panel.MaxHeight = settingsWant;
            }

            FitPlayBudget();
        };

        RefitSettings = () =>
        {
            if (settingsOpen)
            {
                settingsWant = Target();
                panel.MaxHeight = settingsWant;
                FitPlayBudget();
            }
        };

        this.FindControl<Border>("PlayPanel")!.PropertyChanged += (_, e) =>
        {
            if (e.Property != BoundsProperty || !settingsOpen)
            {
                return;
            }

            var want = Target();
            if (Math.Abs(want - settingsWant) > 0.5)
            {
                settingsWant = want;
                panel.MaxHeight = settingsWant;
            }
        };
    }

    private const double SettingsFloor = 140;

    private void KeepContentOnScreen(Panel root)
    {
        void Fit()
        {
            var off = OffScreenMargin;
            var lift = WindowState == WindowState.Maximized ? 1 : 0;
            root.Margin = new Thickness(off.Left, off.Top - lift, off.Right, off.Bottom);
        }

        Fit();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == OffScreenMarginProperty || e.Property == WindowStateProperty)
            {
                Fit();
            }
        };
    }

    private void DressCaption()
    {
        WindowDecorations = WindowDecorations.BorderOnly;

        this.FindControl<Border>("CaptionScrim")!.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xB4, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0x66, 0, 0, 0), 0.55),
                new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1),
            },
        };

        var strip = this.FindControl<Border>("DragStrip")!;

        strip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };

        strip.DoubleTapped += (_, _) =>
        {
            ToggleMaximised();
        };

        this.FindControl<TextBlock>("WordMark")!.Foreground =
            new SolidColorBrush(Palette.SlateDark.Text.ToColor(), 0.72);

        var minimise = this.FindControl<Button>("MinButton")!;
        var maximise = this.FindControl<Button>("MaxButton")!;
        var close = this.FindControl<Button>("CloseButton")!;

        minimise.Click += (_, _) => WindowState = WindowState.Minimized;
        maximise.Click += (_, _) => ToggleMaximised();
        close.Click += (_, _) => Close();

        void ShowMaximiseGlyph()
        {
            maximise.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }

        ShowMaximiseGlyph();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                ShowMaximiseGlyph();
            }
        };
    }

    private static readonly string[] HomePanels = { "Play", "Settings", "Boards", "Armies" };

    private void DressHome()
    {
        var p = Palette.SlateDark.Panel;
        var tint = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x59, p.R, p.G, p.B), 0),
                new GradientStop(Color.FromArgb(0x26, p.R, p.G, p.B), 1),
            },
        };

        var edge = new SolidColorBrush(Colors.White, 0.10);
        var header = new SolidColorBrush(Palette.SlateDark.Text.ToColor(), 0.88);
        var rule = new SolidColorBrush(Palette.SlateDark.Accent.ToColor(), 0.85);

        foreach (var name in HomePanels)
        {
            this.FindControl<Border>($"{name}Panel")!.BorderBrush = edge;
            this.FindControl<Grid>($"{name}Tint")!.Background = tint;
            this.FindControl<TextBlock>($"{name}Header")!.Foreground = header;
            this.FindControl<Border>($"{name}Rule")!.Background = rule;
        }
    }

    private void DressVignette()
    {
        this.FindControl<Border>("Vignette")!.Background = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.62),
                new GradientStop(Color.FromArgb(0x4D, 0, 0, 0), 1),
            },
        };
    }

    private double settingsWant;

    private void FitPlayBudget()
    {
        if (focused)
        {
            return;
        }

        var panel = this.FindControl<Border>("PlayPanel")!;
        var column = (Grid)panel.Parent!;
        if (column.Bounds.Height < 1)
        {
            return;
        }

        var margins = panel.Margin.Top + panel.Margin.Bottom;
        var cap = Math.Max(1, column.Bounds.Height - margins
                              - (settingsOpen ? SettingsFloor + margins : 0));
        if (double.IsPositiveInfinity(panel.MaxHeight) || Math.Abs(panel.MaxHeight - cap) > 0.5)
        {
            panel.MaxHeight = cap;
        }

        if (!double.IsNaN(panel.Height))
        {
            panel.Height = double.NaN;
        }
    }

    private Bitmap? frost;

    private static Bitmap MakeFrost(string path, bool legible)
    {
        using var input = SKBitmap.Decode(path);

        var scale = Play.FrostScale(input.Width, input.Height);
        var w = Math.Max(1, (int)(input.Width * scale));
        var h = Math.Max(1, (int)(input.Height * scale));

        using var work = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(work))
        using (var paint = new SKPaint())
        {
            paint.ImageFilter = SKImageFilter.CreateBlur(5, 5);
            canvas.DrawBitmap(input, new SKRect(0, 0, w, h), paint);
        }

        if (legible)
        {
            var pixels = work.Pixels;
            for (var i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                var (r, g, b) = Legibility.Cap(c.Red, c.Green, c.Blue);
                pixels[i] = new SKColor(r, g, b, c.Alpha);
            }

            work.Pixels = pixels;
        }

        using var image = SKImage.FromBitmap(work);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        return new Bitmap(stream);
    }

    private bool frostWired;

    private void FrostPanels()
    {
        if (frost == null)
        {
            return;
        }

        foreach (var name in HomePanels)
        {
            this.FindControl<Image>($"{name}Frost")!.Source = frost;
        }

        if (!frostWired)
        {
            frostWired = true;
            LayoutUpdated += (_, _) => AlignFrost();
        }

        AlignFrost();
    }

    private void AlignFrost()
    {
        var sharp = this.FindControl<Image>("Backdrop")!;
        var source = sharp.Source;
        if (frost == null || source == null || sharp.Bounds.Width <= 0 || sharp.Bounds.Height <= 0)
        {
            return;
        }

        var winW = sharp.Bounds.Width;
        var winH = sharp.Bounds.Height;
        var fit = Math.Max(winW / source.Size.Width, winH / source.Size.Height);
        var dispW = source.Size.Width * fit;
        var dispH = source.Size.Height * fit;

        foreach (var name in HomePanels)
        {
            var pane = this.FindControl<Image>($"{name}Frost")!;
            if (pane.Parent is not Visual host)
            {
                continue;
            }

            var at = host.TranslatePoint(new Point(0, 0), sharp);
            if (at == null)
            {
                continue;
            }

            var margin = new Thickness((winW - dispW) / 2 - at.Value.X,
                                       (winH - dispH) / 2 - at.Value.Y, 0, 0);

            if (Math.Abs(pane.Width - dispW) > 0.1 || Math.Abs(pane.Height - dispH) > 0.1
                || !pane.Margin.Equals(margin))
            {
                pane.Width = dispW;
                pane.Height = dispH;
                pane.Margin = margin;
            }
        }
    }

    private void ToggleMaximised()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void ShowBackdrop()
    {
        var picture = this.FindControl<Image>("Backdrop")!;

        if (BackdropFile.Find() is { } found)
        {
            try
            {
                picture.Source = new Bitmap(found);
                frost = MakeFrost(found, legible: true);
                FrostPanels();
                return;
            }
            catch (Exception e)
            {
                var name = System.IO.Path.GetFileName(found);
                var why = e.Message.Replace(found, name);
                var folder = System.IO.Path.GetDirectoryName(found);
                if (!string.IsNullOrEmpty(folder))
                {
                    why = why.Replace(folder, "");
                }

                Tell($"That backdrop could not be read", $"{name}\n\n{why}");
                return;
            }
        }
    }

    private void Tell(string title, string body)
    {
        var hint = this.FindControl<Border>("Hint")!;
        hint.Background = new SolidColorBrush(Palette.SlateDark.Panel.ToColor(), 0.92);
        hint.IsVisible = true;

        this.FindControl<TextBlock>("HintTitle")!.Text = title;
        this.FindControl<TextBlock>("HintTitle")!.Foreground = Palette.SlateDark.Text.ToBrush();
        this.FindControl<TextBlock>("HintBody")!.Text = body;
        this.FindControl<TextBlock>("HintBody")!.Foreground = Palette.SlateDark.Muted.ToBrush();
    }
}

