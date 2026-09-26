using Avalonia;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace Strikers.App;

public partial class ArmiesPanel : UserControl
{
    private static readonly (string Title, int Width, bool Number)[] Columns =
    [
        ("Machine", 128, false),
        ("Cost", 48, true),
        ("HP", 48, true),
        ("Move", 48, true),
        ("Range", 48, true),
        ("Power", 48, true),
        ("Attack", 50, false),
        ("Ability", 82, false),
    ];

    public ArmiesPanel()
    {
        AvaloniaXamlLoader.Load(this);

        BuildHeader();
        WireArmy();
        LoadRoster();
    }

    private readonly List<MachineBridge.Machine> army = [];

    private void WireArmy()
    {
        var heading = new SolidColorBrush(Palette.SlateDark.Text.ToColor(), 0.88);
        this.FindControl<TextBlock>("ArmyHeading")!.Foreground = heading;
        RedrawArmy();
        WireSaved();
    }

    internal const string NameSample = "The quick brown fox jumps over the lazy dog";

    internal const string CodeSample = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    internal static double InlineActionWidth(Button button)
    {
        var label = button.Content as string ?? "";
        var probe = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                      Typeface.Default, 12, null);
        return Math.Ceiling(probe.Width) + 20 + 7;
    }

    internal static double KeepRowBoxWidth(TextBox probe, Button save, Button import)
    {
        var name = BoxWidth(probe, Army.MaxName, NameSample) + InlineActionWidth(save);
        var boardCode = BoxWidth(probe, BoardsPanel.ShareCodeLength, CodeSample) + InlineActionWidth(import);
        var armyCode = BoxWidth(probe, ArmyShare.MaxCodeLength, CodeSample) + InlineActionWidth(import);
        return Math.Max(name, Math.Max(boardCode, armyCode));
    }

    internal const string ClearBoardLabel = "Clear board";
    internal const string ClearArmyLabel = "Clear army";

    internal static double ClearButtonWidth(Button clear)
    {
        var face = new Typeface(clear.FontFamily, clear.FontStyle, clear.FontWeight);
        var board = LabelWidth(ClearBoardLabel, face, clear.FontSize);
        var army = LabelWidth(ClearArmyLabel, face, clear.FontSize);

        return Math.Ceiling(Math.Max(board, army)) + 24 + 2;
    }

    private static double LabelWidth(string label, Typeface face, double fontSize)
    {
        var probe = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                      face, fontSize, null);
        return probe.Width;
    }

    internal static double KeepRowMiddleWidth(Button clear)
    {
        return ClearButtonWidth(clear) + 42;
    }

    internal static double KeepRowBoxWidth(TextBox probe, Button save, Button import, Button clear,
                                           double panelWidth)
    {
        var wanted = KeepRowBoxWidth(probe, save, import);

        var usable = panelWidth - 28 - 28;
        var room = (usable - KeepRowMiddleWidth(clear)) / 2;

        return Math.Max(150, Math.Min(wanted, room));
    }

    internal static double BoxWidth(TextBox box, int cap, string sample)
    {
        var probe = new FormattedText(sample, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                      Typeface.Default, box.FontSize, null);
        var perCharacter = probe.Width / sample.Length;
        return Math.Ceiling((perCharacter * cap)
                            + box.Padding.Left + box.Padding.Right
                            + box.BorderThickness.Left + box.BorderThickness.Right);
    }

    internal static Border DeleteCross(Action onDelete)
    {
        var muted = Palette.SlateDark.Muted.ToBrush();
        var bad = Palette.SlateDark.Bad.ToBrush();

        var cross = new TextBlock
        {
            Text = "\uE711",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 15,
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var hit = new Border
        {
            Child = cross,
            Background = Brushes.Transparent,
            Padding = new Avalonia.Thickness(12, 4),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        hit.PointerEntered += (_, _) => cross.Foreground = bad;
        hit.PointerExited += (_, _) => cross.Foreground = muted;
        hit.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            onDelete();
        };

        return hit;
    }

    private void FitTableHeight()
    {
        var panel = this.FindControl<Grid>("PanelRoot")!;
        var table = this.FindControl<ScrollViewer>("TableScroll")!;
        var header = this.FindControl<Grid>("Header")!;
        var rows = this.FindControl<StackPanel>("Rows")!;
        var newArmy = this.FindControl<Grid>("NewArmyGrid")!;
        var savedGrid = this.FindControl<Grid>("SavedGrid")!;
        var status = this.FindControl<TextBlock>("ArmiesStatus")!;

        if (rows.Children.Count == 0 || panel.Bounds.Height < 1)
        {
            return;
        }

        var rowHeight = rows.Children[0].Bounds.Height;
        if (rowHeight < 1)
        {
            return;
        }

        var headerSpace = header.Bounds.Height + header.Margin.Bottom;
        var newArmyBlock = newArmy.Bounds.Height + newArmy.Margin.Top;
        var savedBlock = savedGrid.IsVisible
                         ? savedGrid.Bounds.Height + savedGrid.Margin.Top
                         : 0;

        var want = Math.Max(headerSpace + (1.5 * rowHeight),
                            panel.Bounds.Height - newArmyBlock - savedBlock
                            - status.Bounds.Height);
        if (double.IsPositiveInfinity(table.MaxHeight) || Math.Abs(table.MaxHeight - want) > 0.5)
        {
            table.MaxHeight = want;
        }

    }

    private const double SavedShelfFloor = 24;

    private void FitSavedHeight()
    {
        var root = this.FindControl<Grid>("PanelRoot")!;
        var savedGrid = this.FindControl<Grid>("SavedGrid")!;
        var savedScroll = this.FindControl<ScrollViewer>("SavedScroll")!;
        var heading = this.FindControl<TextBlock>("SavedHeading")!;
        var savedHeader = this.FindControl<Border>("SavedHeaderRule")!;
        var status = this.FindControl<TextBlock>("ArmiesStatus")!;

        var shelf = BoardsPanel.ShelfFromBottom;
        if (root.Bounds.Height < 1 || double.IsNaN(shelf))
        {
            return;
        }

        var headBlock = heading.Bounds.Height + savedScroll.Margin.Top
                        + savedHeader.Bounds.Height + savedHeader.Margin.Top;
        var want = shelf - root.Margin.Bottom - status.Bounds.Height - headBlock;
        var show = shelf > 0 && want >= SavedShelfFloor;
        if (savedGrid.IsVisible != show)
        {
            savedGrid.IsVisible = show;
        }

        if (!show)
        {
            return;
        }

        if (double.IsNaN(savedScroll.Height) || Math.Abs(savedScroll.Height - want) > 0.5)
        {
            savedScroll.Height = want;
        }

        FillGhosts();
    }

    private Border ExportArmyHit(Army saved)
    {
        var muted = Palette.SlateDark.Muted.ToBrush();
        var bright = Palette.SlateDark.Text.ToBrush();

        var label = new TextBlock
        {
            Text = "\uE72D",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var hit = new Border
        {
            Child = label,
            Background = Brushes.Transparent,
            Padding = new Thickness(10, 4),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };

        hit.PointerEntered += (_, _) => label.Foreground = bright;
        hit.PointerExited += (_, _) => label.Foreground = muted;
        hit.PointerPressed += async (_, e) =>
        {
            e.Handled = true;

            var names = new List<string>();
            foreach (var uuid in saved.Machines)
            {
                if (roster.FirstOrDefault(m => m.Uuid == uuid) is not { } known)
                {
                    return;
                }

                names.Add(known.Name);
            }

            if (ArmyShare.ToShareString(names, roster.Select(m => m.Name)) is not { } code)
            {
                return;
            }

            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is { } clipboard)
            {
                try
                {
                    await clipboard.SetValueAsync(DataFormat.Text, code);
                    ToastRail.Show(ToastKind.Good, "Army code copied.");
                }
                catch (Exception)
                {
                }
            }
        };

        return hit;
    }

    private void ImportArmy()
    {
        var box = this.FindControl<TextBox>("ImportArmyBox")!;
        var text = box.Text ?? "";
        if (text.Length > ArmyShare.MaxShareLength)
        {
            text = text[..ArmyShare.MaxShareLength];
        }

        if (roster.Count == 0)
        {
            ToastRail.Show(ToastKind.Bad, "The machine list is still loading. Try again in a moment.");
            return;
        }

        var names = ArmyShare.FromShareString(text, roster.Select(m => m.Name), out var problem);
        if (names is null)
        {
            ToastRail.Show(ToastKind.Bad, problem ?? ArmyShare.NotACode);
            return;
        }

        var picks = new List<MachineBridge.Machine>();
        foreach (var name in names)
        {
            if (roster.FirstOrDefault(m => m.Name == name) is not { } found)
            {
                ToastRail.Show(ToastKind.Bad, $"This save has no {name}, so that army cannot be built.");
                return;
            }

            picks.Add(found);
        }

        army.Clear();
        army.AddRange(picks);
        box.Text = "";
        RedrawArmy();
        ToastRail.Show(ToastKind.Good, Play.ImportedText(picks.Count));
    }

    private const string GhostTag = "ghost";

    private double pickedRowHigh;

    private const double RowFurniture = 7;

    private Control MakePickedGhost()
    {
        if (pickedRowHigh < 1)
        {
            var line = ColumnGrid();
            var name = new TextBlock { Text = "Ag", FontSize = 13 };
            var cross = DeleteCross(() => { });
            Grid.SetColumn(name, 0);
            Grid.SetColumn(cross, Columns.Length);
            line.Children.Add(name);
            line.Children.Add(cross);

            var probe = new Border { Child = line };
            probe.Classes.Add("row");
            probe.Measure(Size.Infinity);

            pickedRowHigh = probe.DesiredSize.Height + RowFurniture;
        }

        var ghost = new Border
        {
            Tag = GhostTag,
            Height = Math.Max(1, pickedRowHigh),
            IsHitTestVisible = false,
        };
        ghost.Classes.Add("row");
        return ghost;
    }

    private Control MakeGhost(double height)
    {
        var ghost = new Border
        {
            Tag = GhostTag,
            Height = height,
            IsHitTestVisible = false,
        };
        ghost.Classes.Add("slot");
        return ghost;
    }

    private double savedRowHigh;

    private const double SlotFurniture = 14;

    private double MeasureSavedRow()
    {
        if (savedRowHigh > 1)
        {
            return savedRowHigh;
        }

        var line = ColumnGrid();
        var name = new TextBlock { Text = "Ag", FontSize = 13 };
        var cross = DeleteCross(() => { });
        Grid.SetColumn(name, 0);
        Grid.SetColumn(cross, Columns.Length);
        line.Children.Add(name);
        line.Children.Add(cross);

        var block = new StackPanel();
        block.Children.Add(line);
        block.Children.Add(new TextBlock
        {
            Text = "Ag",
            FontSize = 12,
            Margin = new Thickness(14, 1, 0, 2),
        });

        var probe = new Border { Child = block };
        probe.Classes.Add("row");
        probe.Classes.Add("slot");

        probe.Measure(Size.Infinity);
        var measured = probe.DesiredSize.Height + probe.Margin.Top + probe.Margin.Bottom;

        if (probe.BorderThickness.Top <= 0)
        {
            measured += SlotFurniture;
        }

        if (measured > 1)
        {
            savedRowHigh = measured;
        }

        return savedRowHigh > 1 ? savedRowHigh : 56;
    }

    private void FillGhosts()
    {
        var list = this.FindControl<StackPanel>("SavedList")!;
        var real = 0;
        var ghosts = 0;
        var rowHigh = MeasureSavedRow();
        foreach (var child in list.Children)
        {
            if ((child as Control)?.Tag as string == GhostTag)
            {
                ghosts++;
            }
            else
            {
                real++;
                if (real == 1 && child.Bounds.Height > 1)
                {
                    rowHigh = child.Bounds.Height + child.Margin.Top + child.Margin.Bottom;
                    savedRowHigh = rowHigh;
                }
            }
        }

        var want = Math.Max(0, ArmyStore.MaxArmies - real);

        if (want == ghosts)
        {
            return;
        }

        for (var i = list.Children.Count - 1; i >= 0; i--)
        {
            if ((list.Children[i] as Control)?.Tag as string == GhostTag)
            {
                list.Children.RemoveAt(i);
            }
        }

        for (var i = 0; i < want; i++)
        {
            list.Children.Add(MakeGhost(rowHigh - 6));
        }
    }

    private const int TableRowsBeforeReserve = 8;

    private void FillPickedGhosts(int want)
    {
        var picked = this.FindControl<StackPanel>("Picked")!;
        var ghosts = 0;
        foreach (var child in picked.Children)
        {
            if ((child as Control)?.Tag as string == GhostTag)
            {
                ghosts++;
            }
        }

        if (want == ghosts)
        {
            return;
        }

        for (var i = picked.Children.Count - 1; i >= 0; i--)
        {
            if ((picked.Children[i] as Control)?.Tag as string == GhostTag)
            {
                picked.Children.RemoveAt(i);
            }
        }

        for (var i = 0; i < want; i++)
        {
            picked.Children.Insert(army.Count + i, MakePickedGhost());
        }
    }

    internal static void FitClearButton(Button clear)
    {
        var want = ClearButtonWidth(clear);
        if (double.IsNaN(clear.Width) || Math.Abs(clear.Width - want) > 0.5)
        {
            clear.Width = want;
        }
    }

    private void FitKeepRow()
    {
        var room = this.FindControl<Grid>("PanelRoot")!.Bounds.Width;
        if (room < 1)
        {
            return;
        }

        var nameBox = this.FindControl<TextBox>("ArmyNameBox")!;
        var importBox = this.FindControl<TextBox>("ImportArmyBox")!;
        var clear = this.FindControl<Button>("ClearArmyButton")!;
        FitClearButton(clear);
        var want = KeepRowBoxWidth(nameBox,
                                   this.FindControl<Button>("SaveArmyButton")!,
                                   this.FindControl<Button>("ImportArmyButton")!,
                                   clear,
                                   room);

        if (double.IsNaN(nameBox.Width) || Math.Abs(nameBox.Width - want) > 0.5)
        {
            nameBox.Width = want;
            importBox.Width = want;
        }
    }

    private void FitPickedHeight()
    {
        var panel = this.FindControl<Grid>("PanelRoot")!;
        var pickedScroll = this.FindControl<ScrollViewer>("PickedScroll")!;
        var picked = this.FindControl<StackPanel>("Picked")!;
        var newArmy = this.FindControl<Grid>("NewArmyGrid")!;
        var savedGrid = this.FindControl<Grid>("SavedGrid")!;
        var header = this.FindControl<Grid>("Header")!;
        var rows = this.FindControl<StackPanel>("Rows")!;
        var status = this.FindControl<TextBlock>("ArmiesStatus")!;

        if (picked.Children.Count == 0 || rows.Children.Count == 0 || panel.Bounds.Height < 1)
        {
            pickedScroll.MaxHeight = double.PositiveInfinity;
            FillPickedGhosts(Play.MaxArmy - army.Count);
            return;
        }

        var pickedRow = picked.Children[0].Bounds.Height;
        var tableRow = rows.Children[0].Bounds.Height;
        if (pickedRow < 1 || tableRow < 1)
        {
            return;
        }

        var pickerFixed = newArmy.Bounds.Height + newArmy.Margin.Top - pickedScroll.Bounds.Height;
        var savedFixed = savedGrid.IsVisible
                         ? savedGrid.Bounds.Height + savedGrid.Margin.Top
                         : 0;
        var shelfRoom = panel.Bounds.Height
                        - (header.Bounds.Height + header.Margin.Bottom
                           + (TableRowsBeforeReserve * tableRow))
                        - pickerFixed - savedFixed - status.Bounds.Height;
        var shelfRows = Math.Clamp((int)Math.Floor(shelfRoom / pickedRow),
                                   PickedShelfFloorRows, Play.MaxArmy);
        FillPickedGhosts(Math.Max(0, shelfRows - army.Count));

        var want = double.PositiveInfinity;
        if (army.Count > shelfRows)
        {
            want = ((shelfRows - 1) * pickedRow) + (pickedRow / 2);
        }

        if (double.IsInfinity(want) != double.IsInfinity(pickedScroll.MaxHeight)
            || (!double.IsInfinity(want) && Math.Abs(pickedScroll.MaxHeight - want) > 0.5))
        {
            pickedScroll.MaxHeight = want;
        }
    }

    private const int PickedShelfFloorRows = 3;

    private void WireSaved()
    {
        this.FindControl<TextBlock>("SavedHeading")!.Foreground =
            new SolidColorBrush(Palette.SlateDark.Text.ToColor(), 0.88);

        var nameBox = this.FindControl<TextBox>("ArmyNameBox")!;
        nameBox.MaxLength = Army.MaxName;

        var saveButton = this.FindControl<Button>("SaveArmyButton")!;
        var importButton = this.FindControl<Button>("ImportArmyButton")!;

        nameBox.Width = KeepRowBoxWidth(nameBox, saveButton, importButton);

        LayoutUpdated += (_, _) => FitKeepRow();
        LayoutUpdated += (_, _) => FitPickedHeight();
        LayoutUpdated += (_, _) => FitTableHeight();
        LayoutUpdated += (_, _) => FitSavedHeight();
        this.FindControl<Button>("SaveArmyButton")!.Click += (_, _) => SaveArmy();
        nameBox.TextChanged += (_, _) => ShowArmyStanding();

        var clearButton = this.FindControl<Button>("ClearArmyButton")!;
        clearButton.Content = ClearArmyLabel;
        clearButton.Click += (_, _) =>
        {
            army.Clear();
            RedrawArmy();
        };
        this.FindControl<Button>("ImportArmyButton")!.Click += (_, _) => ImportArmy();

        var importBox = this.FindControl<TextBox>("ImportArmyBox")!;
        importBox.Width = KeepRowBoxWidth(importBox, saveButton, importButton);
        importBox.MaxLength = ArmyShare.MaxShareLength;

        importBox.TextChanged += (_, _) => ShowArmyStanding();
        RefreshSaved();
    }

    private void SaveArmy()
    {
        var box = this.FindControl<TextBox>("ArmyNameBox")!;
        var name = Army.CleanName(box.Text);

        if (name.Length == 0)
        {
            ToastRail.Show(ToastKind.Bad, "Name the army first.");
            return;
        }

        var saved = new Army
        {
            Name = ArmyStore.FreeName(name, ArmyStore.ReadAll()),
            Machines = [.. army.Select(m => m.Uuid)],
        };

        if (ArmyStore.Save(saved) is { } failed)
        {
            ToastRail.Show(ToastKind.Bad, failed);
            return;
        }

        ToastRail.Show(ToastKind.Good, $"Saved '{saved.Name}'.");

        army.Clear();
        box.Text = "";
        RedrawArmy();
        RefreshSaved();
    }

    private void LoadArmy(Army saved)
    {
        if (roster.Count == 0)
        {
            ToastRail.Show(ToastKind.Bad, "The machine list is still loading. Try again in a moment.");
            return;
        }

        var unknown = saved.Machines.Count(uuid => roster.All(m => m.Uuid != uuid));
        if (unknown > 0)
        {
            ToastRail.Show(ToastKind.Bad, $"'{saved.Name}' has a machine Strikers does not know, so it cannot be edited.");
            return;
        }

        army.Clear();
        foreach (var uuid in saved.Machines)
        {
            if (roster.FirstOrDefault(m => m.Uuid == uuid) is { } m)
            {
                army.Add(m);
            }
        }

        this.FindControl<TextBox>("ArmyNameBox")!.Text = saved.Name;
        RedrawArmy();
    }

    private int savedSortColumn = -1;
    private bool savedSortAscending = true;

    private void SavedSortBy(int column)
    {
        if (savedSortColumn == column && savedSortAscending)
        {
            savedSortAscending = false;
        }
        else if (savedSortColumn == column)
        {
            savedSortColumn = -1;
            savedSortAscending = true;
        }
        else
        {
            savedSortColumn = column;
            savedSortAscending = true;
        }

        RefreshSaved();
    }

    public void RefreshSaved()
    {
        var list = this.FindControl<StackPanel>("SavedList")!;
        var text = Palette.SlateDark.Text.ToBrush();
        var muted = Palette.SlateDark.Muted.ToBrush();
        var bad = Palette.SlateDark.Bad.ToBrush();

        list.Children.Clear();
        var all = ArmyStore.ReadAll();

        ShowArmyStanding();

        var infos = all.Select(saved =>
        {
            var cost = 0;
            var known = roster.Count > 0;
            var names = new List<string>();
            foreach (var uuid in saved.Machines)
            {
                if (roster.FirstOrDefault(m => m.Uuid == uuid) is { } m)
                {
                    cost += m.Cost;
                    names.Add(m.Name);
                }
                else
                {
                    known = false;
                }
            }

            return (Army: saved, Cost: cost, Known: known, Names: names);
        }).ToList();

        var ordered = savedSortColumn switch
        {
            0 => infos.OrderBy(i => i.Army.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            1 => infos.OrderBy(i => i.Known ? i.Cost : int.MaxValue).ToList(),
            _ => infos,
        };
        if (!savedSortAscending)
        {
            ordered.Reverse();
        }

        var header = this.FindControl<Grid>("SavedHeader")!;
        header.Children.Clear();

        {
            var headerGrid = ColumnGrid();
            (string Title, int Column, bool Centre)[] headings = [("Army", 0, false), ("Cost", 1, true)];
            foreach (var (title, column, centre) in headings)
            {
                var arrow = column == savedSortColumn ? (savedSortAscending ? " ▲" : " ▼") : "";
                var cell = ShelfHeadingCell(title, centre);
                cell.Text = cell.Text + arrow;

                var wrap = new Border
                {
                    Child = cell,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Height = ShelfHeaderHeight,
                };
                wrap.Classes.Add("headerCell");

                var target = column;
                wrap.PointerPressed += (_, _) => SavedSortBy(target);

                Grid.SetColumn(wrap, column);
                headerGrid.Children.Add(wrap);
            }

            header.Children.Add(headerGrid);
        }

        foreach (var (saved, cost, costKnown, names) in ordered)
        {
            var name = new TextBlock
            {
                Text = saved.Name,
                FontSize = 13,
                Foreground = text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var costCell = new TextBlock
            {
                Text = costKnown ? $"{cost}" : "",
                FontSize = 13,
                FontFamily = new FontFamily("Consolas"),
                Foreground = text,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var machines = new TextBlock
            {
                Text = string.Join(", ", names),
                FontSize = 12,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(14, 1, 0, 2),
            };

            var deleteHit = DeleteCross(() =>
            {
                if (ArmyStore.Delete(saved.Name) is { } failed)
                {
                    ToastRail.Show(ToastKind.Bad, failed);
                    return;
                }

                RefreshSaved();
            });

            var line = ColumnGrid();
            Grid.SetColumn(name, 0);
            Grid.SetColumn(costCell, 1);
            line.Children.Add(name);
            line.Children.Add(costCell);

            var exportHit = ExportArmyHit(saved);

            var glyphs = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            glyphs.Children.Add(exportHit);
            glyphs.Children.Add(deleteHit);

            var overlay = new Grid();
            overlay.Children.Add(line);
            overlay.Children.Add(glyphs);

            var block = new StackPanel();
            block.Children.Add(overlay);
            if (names.Count > 0)
            {
                block.Children.Add(machines);
            }

            var row = new Border { Child = block };
            row.Classes.Add("row");
            row.Classes.Add("slot");
            row.Cursor = new Cursor(StandardCursorType.Hand);
            row.PointerPressed += (_, _) => LoadArmy(saved);

            list.Children.Add(row);
        }
    }

    private void AddToArmy(MachineBridge.Machine m)
    {
        if (army.Count >= Play.MaxArmy)
        {
            return;
        }

        army.Add(m);
        RedrawArmy();
    }

    private static string[] CellsFor(MachineBridge.Machine m)
    {
        return [m.Name, $"{m.Cost}", $"{m.Health}", $"{m.Move}", $"{m.Range}", $"{m.Power}",
                Glossary.Attack(m.Pattern).Display, Glossary.Ability(m.Ability).Display];
    }

    private void DrawPickedHeader()
    {
        var header = this.FindControl<Grid>("PickedHeader")!;
        header.Children.Clear();

        var grid = ColumnGrid();
        for (var i = 0; i < Columns.Length; i++)
        {
            var cell = new TextBlock
            {
                Text = Columns[i].Title,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = Palette.SlateDark.Muted.ToBrush(),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = Columns[i].Number ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            };

            var wrap = new Border { Child = cell, Height = 22 };
            Grid.SetColumn(wrap, i);
            grid.Children.Add(wrap);
        }

        header.Children.Add(grid);
    }

    private void RedrawArmy()
    {
        var picked = this.FindControl<StackPanel>("Picked")!;
        var text = Palette.SlateDark.Text.ToBrush();
        var muted = Palette.SlateDark.Muted.ToBrush();

        picked.Children.Clear();
        DrawPickedHeader();

        for (var i = 0; i < army.Count; i++)
        {
            var m = army[i];
            var index = i;

            var cells = CellsFor(m);
            cells[0] = $"{i + 1}.  {cells[0]}";

            var line = ColumnGrid();
            for (var c = 0; c < cells.Length - 1; c++)
            {
                var cell = new TextBlock
                {
                    Text = cells[c],
                    FontSize = 13,
                    FontFamily = Columns[c].Number ? new FontFamily("Consolas") : FontFamily.Default,
                    Foreground = text,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = Columns[c].Number ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };

                Grid.SetColumn(cell, c);
                line.Children.Add(cell);
            }

            var ability = new TextBlock
            {
                Text = cells[^1],
                FontSize = 13,
                Foreground = text,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(ability, Columns.Length - 1);
            line.Children.Add(ability);

            var remove = DeleteCross(() =>
            {
                army.RemoveAt(index);
                RedrawArmy();
            });
            Grid.SetColumn(remove, Columns.Length);
            line.Children.Add(remove);

            var row = new Border { Child = line };
            row.Classes.Add("row");
            ToolTip.SetTip(row, MachineTip(m));

            picked.Children.Add(row);
        }

        ShowArmyStanding();

        this.FindControl<Button>("ClearArmyButton")!.IsEnabled = army.Count > 0;
    }

    private void ShowArmyStanding()
    {
        var save = this.FindControl<Button>("SaveArmyButton")!;
        var named = Army.CleanName(this.FindControl<TextBox>("ArmyNameBox")!.Text).Length > 0;
        var full = !ArmyStore.HasRoom(ArmyStore.ReadAll().Count, replacing: false);

        save.IsEnabled = army.Count > 0 && named && !full;
        save.Content = full ? "List full" : "Save";

        var pasted = this.FindControl<TextBox>("ImportArmyBox")!.Text ?? "";
        this.FindControl<Button>("ImportArmyButton")!.IsEnabled = pasted.Trim().Length > 0;
    }

    private const double TailWidth = 40;

    internal const double ShelfHeaderHeight = 20;

    internal static TextBlock ShelfHeadingCell(string text, bool centre)
    {
        return new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 0.8,
            Foreground = new SolidColorBrush(Palette.SlateDark.Muted.ToColor(), 0.75),
            HorizontalAlignment = centre ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static Grid ColumnGrid()
    {
        var grid = new Grid();
        foreach (var (_, width, _) in Columns)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(width, GridUnitType.Star)
            {
                MinWidth = width,
            });
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition(TailWidth, GridUnitType.Pixel));
        return grid;
    }

    private List<MachineBridge.Machine> roster = [];

    private int sortColumn = -1;
    private bool sortAscending = true;
    private readonly List<TextBlock> headerCells = [];

    private void BuildHeader()
    {
        var header = this.FindControl<Grid>("Header")!;
        var muted = Palette.SlateDark.Muted.ToBrush();

        var grid = ColumnGrid();
        for (var i = 0; i < Columns.Length; i++)
        {
            var cell = new TextBlock
            {
                Text = Columns[i].Title,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = muted,
                HorizontalAlignment = Columns[i].Number ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            headerCells.Add(cell);

            var wrap = new Border
            {
                Child = cell,
                Cursor = new Cursor(StandardCursorType.Hand),
                Height = 22,
            };
            wrap.Classes.Add("headerCell");

            var column = i;
            wrap.PointerPressed += (_, _) => SortBy(column);

            Grid.SetColumn(wrap, i);
            grid.Children.Add(wrap);
        }

        header.Children.Add(grid);
    }

    private void SortBy(int column)
    {
        if (sortColumn == column && sortAscending)
        {
            sortAscending = false;
        }
        else if (sortColumn == column)
        {
            sortColumn = -1;
            sortAscending = true;
        }
        else
        {
            sortColumn = column;
            sortAscending = true;
        }

        for (var i = 0; i < headerCells.Count; i++)
        {
            var arrow = i == sortColumn ? (sortAscending ? " ▲" : " ▼") : "";
            headerCells[i].Text = Columns[i].Title + arrow;
        }

        BuildRows();
    }

    private List<MachineBridge.Machine> Sorted()
    {
        if (sortColumn < 0)
        {
            return roster;
        }

        IOrderedEnumerable<MachineBridge.Machine> ordered = sortColumn switch
        {
            1 => roster.OrderBy(m => m.Cost),
            2 => roster.OrderBy(m => m.Health),
            3 => roster.OrderBy(m => m.Move),
            4 => roster.OrderBy(m => m.Range),
            5 => roster.OrderBy(m => m.Power),
            6 => roster.OrderBy(m => Glossary.Attack(m.Pattern).Display,
                                StringComparer.OrdinalIgnoreCase),
            7 => roster.OrderBy(m => Glossary.Ability(m.Ability).Display is "" or "-" ? 1 : 0)
                       .ThenBy(m => Glossary.Ability(m.Ability).Display,
                               StringComparer.OrdinalIgnoreCase),
            _ => roster.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
        };

        var sorted = ordered.ToList();
        if (!sortAscending)
        {
            sorted.Reverse();
        }

        return sorted;
    }

    private void LoadRoster()
    {
        Task.Run(() =>
        {
            var machines = MachineBridge.All(NetplayTool.Netplay());
            Dispatcher.UIThread.Post(() =>
            {
                ShowRoster(machines);
                LoadArmyForCapture();
            });
        });
    }

    private void LoadArmyForCapture()
    {
        var wanted = Environment.GetEnvironmentVariable("STRIKERS_LOAD_ARMY");
        if (wanted is null)
        {
            return;
        }

        if (ArmyStore.ReadAll().FirstOrDefault(a => a.Name == wanted) is { } saved)
        {
            LoadArmy(saved);
        }
    }

    internal static Control MachineTip(MachineBridge.Machine m)
    {
        var text = Palette.SlateDark.Text.ToBrush();
        var muted = Palette.SlateDark.Muted.ToBrush();

        var block = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        };

        void AddLine(Glossary.Meaning meaning, bool first)
        {
            if (!first)
            {
                block.Inlines!.Add(new Run(Environment.NewLine));
            }

            var rule = meaning.Rule.Length > 0 ? meaning.Rule : "no rule recorded.";
            block.Inlines!.Add(new Run($"{meaning.Display}: ")
            {
                Foreground = text,
                FontWeight = FontWeight.SemiBold,
            });
            block.Inlines!.Add(new Run(rule) { Foreground = muted });
        }

        AddLine(Glossary.Attack(m.Pattern), first: true);

        var ability = Glossary.Ability(m.Ability);
        if (ability.Display.Length > 0 && ability.Display != "-")
        {
            AddLine(ability, first: false);
        }

        return block;
    }

    private void ShowRoster(List<MachineBridge.Machine> machines)
    {
        if (machines.Count == 0)
        {
            var status = this.FindControl<TextBlock>("ArmiesStatus")!;
            status.Foreground = Palette.SlateDark.Bad.ToBrush();
            status.Text = "netplay.exe did not answer. It should be in the Strikers folder.";
            status.IsVisible = true;
            return;
        }

        roster = machines;

        BuildRows();
        CutLastRowInHalf();

        RefreshSaved();

    }

    private void BuildRows()
    {
        var rows = this.FindControl<StackPanel>("Rows")!;
        var text = Palette.SlateDark.Text.ToBrush();
        var muted = Palette.SlateDark.Muted.ToBrush();

        rows.Children.Clear();
        foreach (var m in Sorted())
        {
            var grid = ColumnGrid();
            var cells = CellsFor(m);
            for (var i = 0; i < cells.Length; i++)
            {
                var cell = new TextBlock
                {
                    Text = cells[i],
                    FontSize = 13,
                    FontFamily = Columns[i].Number ? new FontFamily("Consolas") : FontFamily.Default,
                    Foreground = text,
                    HorizontalAlignment = Columns[i].Number ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };

                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
            }

            var row = new Border { Child = grid };
            row.Classes.Add("row");
            row.Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(row, MachineTip(m));
            row.PointerPressed += (_, _) => AddToArmy(m);

            rows.Children.Add(row);
        }
    }

    private void CutLastRowInHalf()
    {
        var outer = this.FindControl<ScrollViewer>("TableScroll")!;
        var rowsScroll = this.FindControl<ScrollViewer>("RowsScroll")!;
        var header = this.FindControl<Grid>("Header")!;
        var rows = this.FindControl<StackPanel>("Rows")!;

        void Fit()
        {
            if (rows.Children.Count == 0)
            {
                return;
            }

            var rowHeight = rows.Children[0].Bounds.Height;
            if (rowHeight < 1)
            {
                return;
            }

            var headerSpace = header.Bounds.Height + header.Margin.Bottom;
            var avail = (double.IsPositiveInfinity(outer.MaxHeight)
                         ? outer.Bounds.Height
                         : outer.MaxHeight) - headerSpace;
            var wholeRows = Math.Max(0, Math.Floor((avail - (rowHeight / 2)) / rowHeight));
            var want = (wholeRows * rowHeight) + (rowHeight / 2);
            if (Math.Abs(rowsScroll.MaxHeight - want) > 0.5)
            {
                rowsScroll.MaxHeight = want;
            }

            FadeTheCut(rowsScroll, rows, want, rowHeight);
        }

        rows.LayoutUpdated += (_, _) => Fit();
        Fit();
    }

    private static void FadeTheCut(ScrollViewer rowsScroll, StackPanel rows, double want,
                                   double rowHeight)
    {
        if (rows.Bounds.Height <= want + 0.5 || want < 1)
        {
            rowsScroll.OpacityMask = null;
            return;
        }

        var solid = Math.Clamp(1 - ((rowHeight / 2) / want), 0, 1);
        rowsScroll.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(Colors.White, solid),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            },
        };
    }
}
