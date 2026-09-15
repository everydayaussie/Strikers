using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Layout;
using System.Globalization;
using System.Reflection;

namespace Strikers.App;

public partial class SettingsPanel : UserControl
{
    private TextBox Name_ = null!;
    private Grid Versions_ = null!;
    private TextBlock Status_ = null!;

    private string versionsText = "";

    public SettingsPanel()
    {
        AvaloniaXamlLoader.Load(this);

        Name_ = this.FindControl<TextBox>("NameBox")!;
        Versions_ = this.FindControl<Grid>("VersionsGrid")!;
        Status_ = this.FindControl<TextBlock>("StatusLine")!;
        Status_.IsVisible = false;

        Dress();
        WireName();
        WireLogFolder();
        WireReport();
        WireTools();
        WireVersions();
        SpreadToFill();
        RefreshVersions();
    }

    public event Action? BoardsChanged;

    public event Action? ArmiesChanged;

    public event Action? BackdropWanted;

    public void RefreshBackdropButton()
    {
        var missing = BackdropOffer.Missing();
        var button = this.FindControl<Button>("BackdropButton")!;
        var backdrop = missing.Any(p => p.Target == BackdropDownload.Target());
        var icon = missing.Any(p => p.Target == IconFile.Target());
        ToolTip.SetTip(button, backdrop && icon ? "Downloads the background image and the window icon."
                               : icon ? "Downloads the window icon."
                               : "Downloads the background image.");
        button.IsVisible = missing.Count > 0;
    }

    private void WireTools()
    {
        var unlock = this.FindControl<Button>("UnlockButton")!;
        var randomBoard = this.FindControl<Button>("RandomBoardButton")!;
        var randomArmy = this.FindControl<Button>("RandomArmyButton")!;

        unlock.Click += (_, _) => UnlockChallenges();
        this.FindControl<Button>("BackdropButton")!.Click += (_, _) => BackdropWanted?.Invoke();
        randomBoard.Click += (_, _) => RandomBoard();
        randomArmy.Click += (_, _) => RandomArmy();

        ToolTip.SetTip(unlock,
                       "Close Salma's challenge list before pressing this, then open it again. The "
                       + "unlock is written into the running game, and the list only re-reads it when "
                       + "it is opened. It is memory only, so it is gone when the game restarts.");
        ToolTip.SetTip(randomBoard,
                       $"Saves one of the {Library.Boards.Count} built-in boards to the Boards shelf, "
                       + "one the shelf does not already hold. They run from plain fields to boards "
                       + "that exist to be broken.");
        ToolTip.SetTip(randomArmy,
                       $"Saves one of the {Library.Armies.Count} built-in armies to the Armies shelf, "
                       + "one the shelf does not already hold. Most cost more than the stock 10 "
                       + "points. The max army cost on the Play panel is what lets those play.");
    }

    private void UnlockChallenges()
    {
        if (NetplayTool.LiveProbe() is not { } probe)
        {
            ToastRail.Show(ToastKind.Bad, "live-probe.exe is not beside the launcher.");
            return;
        }

        var button = this.FindControl<Button>("UnlockButton")!;
        button.IsEnabled = false;
        button.Content = "Unlocking...";

        Task.Run(() =>
        {
            var output = NetplayTool.Run(probe, ["--unlock-challenges", "--type", "BoardGame", "--yes"]);
            Dispatcher.UIThread.Post(() =>
            {
                button.IsEnabled = true;
                button.Content = "Unlock challenges";

                switch (Play.ReadUnlock(output))
                {
                    case Play.UnlockOutcome.Cleared:
                        ToastRail.Show(ToastKind.Good, "Unlocked challenges. Re-open the challenge list to see it.");
                        break;

                    case Play.UnlockOutcome.AlreadyClear:
                        ToastRail.Show(ToastKind.Info, "Every challenge is already unlocked.");
                        break;

                    default:
                        ToastRail.Show(ToastKind.Bad, "Could not reach the game. Start it and get past the main menu.");
                        break;
                }
            });
        });
    }

    private void RandomBoard()
    {
        var all = BoardStore.ReadAll(out var unreadable);
        if (unreadable is not null)
        {
            ToastRail.Show(ToastKind.Bad, unreadable);
            return;
        }

        if (!BoardStore.HasRoom(all.Count))
        {
            ToastRail.Show(ToastKind.Bad, $"{BoardStore.MaxBoards} saved boards is the limit. Delete one to add another.");
            return;
        }

        var design = Library.PickBoard(all.Select(b => b.Name), Random.Shared);
        if (design is null)
        {
            return;
        }

        var board = Library.Build(design);
        board.Name = BoardStore.FreeName(design.Name, all);
        all.Add(board);
        if (BoardStore.WriteAll(all) is { } failed)
        {
            ToastRail.Show(ToastKind.Bad, failed);
            return;
        }

        BoardsChanged?.Invoke();
        ToastRail.Show(ToastKind.Good, $"Added the board '{board.Name}'.");
    }

    private void RandomArmy()
    {
        var existing = ArmyStore.ReadAll();
        if (!ArmyStore.HasRoom(existing.Count, replacing: false))
        {
            ToastRail.Show(ToastKind.Bad, $"{ArmyStore.MaxArmies} saved armies is the limit. Delete one to add another.");
            return;
        }

        var design = Library.PickArmy(existing.Select(a => a.Name), Random.Shared);
        if (design is null)
        {
            return;
        }

        var button = this.FindControl<Button>("RandomArmyButton")!;
        button.IsEnabled = false;
        button.Content = "Building...";

        Task.Run(() =>
        {
            var roster = MachineBridge.All(NetplayTool.Netplay());
            Dispatcher.UIThread.Post(() =>
            {
                button.IsEnabled = true;
                button.Content = "Random army";
                if (roster.Count == 0)
                {
                    ToastRail.Show(ToastKind.Bad, "Could not reach netplay, so no machines to build from.");
                    return;
                }

                var army = new Army { Name = ArmyStore.FreeName(design.Name, ArmyStore.ReadAll()) };
                foreach (var machineName in design.Machines)
                {
                    var found = roster.FirstOrDefault(m => m.Name == machineName);
                    if (found is null)
                    {
                        ToastRail.Show(ToastKind.Bad, $"This save has no {machineName}, so '{design.Name}' cannot be built.");
                        return;
                    }

                    army.Machines.Add(found.Uuid);
                }

                if (ArmyStore.Save(army) is { } failed)
                {
                    ToastRail.Show(ToastKind.Bad, failed);
                    return;
                }

                ArmiesChanged?.Invoke();
                ToastRail.Show(ToastKind.Good, $"Added the army '{army.Name}'.");
            });
        });
    }

    public double LiftPin()
    {
        var spread = this.FindControl<Grid>("Spread")!;
        var pinned = spread.MinHeight;
        spread.MinHeight = 0;

        foreach (var ancestor in spread.GetVisualAncestors().OfType<Layoutable>())
        {
            ancestor.InvalidateMeasure();
        }

        foreach (var descendant in spread.GetVisualDescendants().OfType<Layoutable>())
        {
            descendant.InvalidateMeasure();
        }

        return pinned;
    }

    public void RestorePin(double pinned)
    {
        this.FindControl<Grid>("Spread")!.MinHeight = pinned;
    }

    private void SpreadToFill()
    {
        var scroll = this.FindControl<ScrollViewer>("Scroll")!;
        var spread = this.FindControl<Grid>("Spread")!;

        void Fit()
        {
            var margins = spread.Margin.Top + spread.Margin.Bottom;
            var want = Math.Max(0, scroll.Bounds.Height - margins);
            if (Math.Abs(spread.MinHeight - want) > 0.5)
            {
                spread.MinHeight = want;
            }
        }

        scroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                Fit();
            }
        };

        Fit();
    }

    private void Dress()
    {
        var heading = new SolidColorBrush(Palette.SlateDark.Text.ToColor(), 0.88);
        var muted = Palette.SlateDark.Muted.ToBrush();

        this.FindControl<TextBlock>("NameHeading")!.Foreground = Palette.SlateDark.Muted.ToBrush();
        this.FindControl<TextBlock>("NameLabel")!.Foreground = Palette.SlateDark.Text.ToBrush();
    }

    private void WireName()
    {
        Name_.MaxLength = Settings.MaxName;

        Name_.AttachedToVisualTree += (_, _) =>
        {
            Dispatcher.UIThread.Post(SizeNameBox, DispatcherPriority.Loaded);
        };

        Name_.Text = Settings.Read().Name;
        Name_.TextChanged += (_, _) => ForceUpper();
        this.FindControl<Button>("SaveNameButton")!.Click += (_, _) => SaveName();
    }

    private void WireLogFolder()
    {
        var button = this.FindControl<Button>("LogFolderButton")!;
        button.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = AppContext.BaseDirectory,
                    UseShellExecute = true,
                });
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
            {
                ToastRail.Show(ToastKind.Bad, $"Could not open the folder. {MatchDriver.StartFailedReason(e)}".TrimEnd());
            }
        };

        ToolTip.SetTip(button,
                       $"Opens the folder beside Strikers.exe: {MatchLog.Name}, the record of every "
                       + $"match, the {Report.RecordingsFolder} netplay keeps, and the {Report.Folder}.");
    }

    private void WireReport()
    {
        var button = this.FindControl<Button>("ReportButton")!;
        button.Click += (_, _) =>
        {
            var zip = Report.TrySave(AppContext.BaseDirectory, DateTime.Now, out var problem);
            if (zip is not null)
            {
                ToastRail.Show(ToastKind.Good,
                               $"Saved {Report.Folder}\\{System.IO.Path.GetFileName(zip)} beside Strikers.");
                return;
            }

            ToastRail.Show(ToastKind.Info, $"No report saved: {problem}.");
        };

        ToolTip.SetTip(button,
                       $"Zips {MatchLog.Name}, its rolled predecessor and the newest match recording "
                       + $"into the {Report.Folder} folder beside Strikers.exe. Send that one file when "
                       + "asking for help. A halted match saves one by itself.");
    }

    private void SizeNameBox()
    {
        var save = this.FindControl<Button>("SaveNameButton")!;
        save.Measure(Avalonia.Size.Infinity);
        var want = ArmiesPanel.BoxWidth(Name_, Settings.MaxName + 1, "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
                   + save.DesiredSize.Width + save.Margin.Left + save.Margin.Right;

        if (double.IsNaN(Name_.Width) || Math.Abs(Name_.Width - want) > 0.5)
        {
            Name_.Width = want;
        }
    }

    private void ForceUpper()
    {
        var raw = Name_.Text ?? "";
        var upper = raw.ToUpperInvariant();
        if (upper != raw)
        {
            var caret = Name_.CaretIndex;
            Name_.Text = upper;
            Name_.CaretIndex = caret;
        }
    }

    private void SaveName()
    {
        var settings = Settings.Read();
        var cleaned = Settings.CleanName(Name_.Text);
        settings.Name = cleaned.Length == 0 ? "ALOY" : cleaned;
        if (settings.Write() is { } failed)
        {
            Status_.Foreground = Palette.SlateDark.Bad.ToBrush();
            Status_.Text = failed;
            Status_.IsVisible = true;
            return;
        }

        Name_.Text = settings.Name;
        Status_.Text = "";
        Status_.IsVisible = false;
        ToastRail.Show(ToastKind.Good, $"Saved the name '{settings.Name}'.");
    }

    private void WireVersions()
    {
        this.FindControl<Button>("CheckAgainButton")!.Click += (_, _) => RefreshVersions(announce: true);

        this.FindControl<Button>("CopyVersionsButton")!.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is { } clipboard)
            {
                var failed = await Play.Caught(() => clipboard.SetValueAsync(DataFormat.Text, versionsText));
                if (failed is not null)
                {
                    ToastRail.Show(ToastKind.Bad, "Could not copy the versions. Press Copy again.");
                    return;
                }

                ToastRail.Show(ToastKind.Good, "Versions copied.");
            }
        };
    }

    public void RefreshVersionsQuietly()
    {
        RefreshVersions();
    }

    private void RefreshVersions(bool announce = false)
    {
        Status_.Text = "";
        Status_.IsVisible = false;

        Task.Run(() =>
        {
            var me = Assembly.GetExecutingAssembly();

            var label = me.GetName().Name ?? "Strikers";
            var mvid = me.ManifestModule.ModuleVersionId;

            var rows = new List<(string Label, string Value)>
            {
                ("version", UpdateCheck.Ours() ?? "not set"),
            };

            if (Release.Commit(me) is { } commit)
            {
                rows.Add(("commit", commit));
            }

            if (UpdateCheck.Newest is { } newest)
            {
                rows.Add(("newest on Nexus", newest));
            }

            rows.AddRange(new List<(string Label, string Value)>
            {
                (label, $"{mvid:N}"),
                ("netplay", NetplayTool.WithoutName("netplay", Ask(NetplayTool.Netplay(), "--version"))),
                ("live-probe", NetplayTool.WithoutName("live-probe", Ask(NetplayTool.LiveProbe(), "--version"))),
            });

            var build = Ask(NetplayTool.LiveProbe(), "--build");
            rows.Add(("game build", build is null or "" ? "the game is not running" : build));

            var (testFor, testExpires) = TestBuild.Read(me);
            if (TestBuild.Mark(testFor, testExpires) is { } testMark)
            {
                rows.Add(("test build", testMark));
            }

            Dispatcher.UIThread.Post(() =>
            {
                ShowVersions(rows);
                if (announce)
                {
                    ToastRail.Show(ToastKind.Good, "Versions refreshed.");
                }
            });
        });
    }

    private void ShowVersions(List<(string Label, string Value)> rows)
    {
        Versions_.RowDefinitions.Clear();
        Versions_.Children.Clear();

        var ink = Palette.SlateDark.Text.ToBrush();
        var lines = new List<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            Versions_.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            lines.Add($"{rows[i].Label,-17} {rows[i].Value}");

            var rule = new Border();
            rule.Classes.Add("formRule");
            Grid.SetRow(rule, i);
            Grid.SetColumnSpan(rule, 2);
            Versions_.Children.Add(rule);

            var name = new TextBlock
            {
                Text = rows[i].Label,
                FontSize = 13,
                Foreground = ink,
                Margin = new Avalonia.Thickness(0, 4, 12, 4),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            };
            Grid.SetRow(name, i);
            Grid.SetColumn(name, 0);
            Versions_.Children.Add(name);

            var value = new SelectableTextBlock
            {
                Text = rows[i].Value,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Foreground = ink,
                Margin = new Avalonia.Thickness(0, 4, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            Versions_.Children.Add(value);
        }

        versionsText = string.Join('\n', lines);
    }

    private static string? Ask(string? exe, string arg)
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
}
