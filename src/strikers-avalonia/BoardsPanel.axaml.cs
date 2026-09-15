using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Strikers.App;

public partial class BoardsPanel : UserControl
{
    public event Action? Changed;

    private readonly List<(Border Hit, Border Swatch, TextBlock Label, int Value)> chips = [];
    private BoardView view = null!;

    public BoardsPanel()
    {
        AvaloniaXamlLoader.Load(this);

        view = this.FindControl<BoardView>("View")!;

        view.Mirror = false;
        var eights = Library.Boards
            .Where(b => b.Rows.Length == StrikeBoard.Size && b.Rows[0].Length == StrikeBoard.Size)
            .ToList();
        var design = eights[rng.Next(eights.Count)];
        view.Board = Library.Build(design);

        BuildSwatches();
        WireMirror();
        WireShape();
        ShowShapeOf(view.Board);
        WireRandom();
        WireKeepRow();
        RedrawSaved();

        view.Changed += ShowStanding;
        ShowStanding();

        LayoutUpdated += (_, _) => FitShapeBar();
        LayoutUpdated += (_, _) => FitSwatches();
        LayoutUpdated += (_, _) => FitTightHeight();
    }

    private const double SavedListFloor = 44;

    private enum ShelfSort
    {
        File,
        Name,
        Tiles,
    }

    private ShelfSort sortBy = ShelfSort.File;
    private bool sortAscending = true;

    private TextBlock? nameCell;
    private TextBlock? boardCell;

    private const double ThumbSize = 70;
    private const double ThumbGap = 8;
    private const double ThumbColumn = ThumbSize + ThumbGap;

    private void BuildSavedHeader()
    {
        var header = this.FindControl<Grid>("SavedHeader")!;
        if (header.Children.Count > 0)
        {
            return;
        }

        header.ColumnDefinitions = new ColumnDefinitions($"{ThumbColumn},*");

        boardCell = ArmiesPanel.ShelfHeadingCell("Board", centre: false);
        nameCell = ArmiesPanel.ShelfHeadingCell("Name", centre: false);

        var boardHit = SortHit(boardCell, ShelfSort.Tiles);
        Grid.SetColumn(boardHit, 0);
        var nameHit = SortHit(nameCell, ShelfSort.Name);
        Grid.SetColumn(nameHit, 1);

        header.Children.Add(boardHit);
        header.Children.Add(nameHit);
    }

    private string SortArrow(ShelfSort column)
    {
        if (sortBy != column)
        {
            return "";
        }

        return sortAscending ? " ▲" : " ▼";
    }

    private Border SortHit(TextBlock cell, ShelfSort column)
    {
        var hit = new Border
        {
            Child = cell,
            Cursor = new Cursor(StandardCursorType.Hand),
            Height = ArmiesPanel.ShelfHeaderHeight,
        };
        hit.Classes.Add("headerCell");
        hit.PointerPressed += (_, _) =>
        {
            if (sortBy != column)
            {
                sortBy = column;
                sortAscending = true;
            }
            else if (sortAscending)
            {
                sortAscending = false;
            }
            else
            {
                sortBy = ShelfSort.File;
                sortAscending = true;
            }

            RedrawSaved();
        };

        return hit;
    }


    private double lastSectionFixed = 40;

    private double savedRowHigh = 84;
    private double sectionFixed;

    private const double SavedPromiseRows = 3.55;

    public static double ShelfFromBottom { get; private set; } = double.NaN;


    private double Promise()
    {
        return savedRowHigh * SavedPromiseRows;
    }

    private const string GhostTag = "ghost";

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

    private void FillGhosts(UniformGrid rows, double room, int columns)
    {
        var real = 0;
        var ghosts = 0;
        foreach (var child in rows.Children)
        {
            if ((child as Control)?.Tag as string == GhostTag)
            {
                ghosts++;
            }
            else
            {
                real++;
            }
        }

        var want = Math.Max(0, BoardStore.MaxBoards - real);

        if (want == ghosts)
        {
            return;
        }

        for (var i = rows.Children.Count - 1; i >= 0; i--)
        {
            if ((rows.Children[i] as Control)?.Tag as string == GhostTag)
            {
                rows.Children.RemoveAt(i);
            }
        }

        for (var i = 0; i < want; i++)
        {
            rows.Children.Add(MakeGhost(savedRowHigh - 6));
        }
    }

    private double SnapToRows(double room)
    {
        var peek = savedRowHigh * 0.55;
        if (room <= savedRowHigh + peek)
        {
            return room;
        }

        var wholeRows = Math.Floor((room - peek) / savedRowHigh);
        return Math.Max(SavedListFloor, (wholeRows * savedRowHigh) + peek);
    }

    private void FitKeepBoxes()
    {
        if (Bounds.Width < 1)
        {
            return;
        }

        var nameBox = this.FindControl<TextBox>("NameBox")!;
        var importBox = this.FindControl<TextBox>("ImportBox")!;
        var clear = this.FindControl<Button>("ResetButton")!;
        ArmiesPanel.FitClearButton(clear);
        var want = ArmiesPanel.KeepRowBoxWidth(nameBox,
                                               this.FindControl<Button>("SaveButton")!,
                                               this.FindControl<Button>("ImportButton")!,
                                               clear,
                                               Bounds.Width);

        if (double.IsNaN(nameBox.Width) || Math.Abs(nameBox.Width - want) > 0.5)
        {
            nameBox.Width = want;
            importBox.Width = want;
        }
    }

    private void FitTightHeight()
    {
        FitKeepBoxes();

        if (this.FindControl<ScrollViewer>("SavedScroll") is not { } scroll ||
            this.FindControl<StackPanel>("SavedSection") is not { } section ||
            Content is not Grid root || Bounds.Height < 1)
        {
            return;
        }

        var others = root.Margin.Top + root.Margin.Bottom;
        foreach (var child in root.Children)
        {
            if (child == view || child == section)
            {
                continue;
            }

            var asked = Math.Max(child.Bounds.Height, child.DesiredSize.Height);
            others += asked + child.Margin.Top + child.Margin.Bottom;
        }

        if (section.IsVisible)
        {
            sectionFixed = section.Bounds.Height + section.Margin.Top + section.Margin.Bottom -
                           scroll.Bounds.Height;
        }


        var free = Bounds.Height - others - view.MinimumHeight;
        var wanted = section.IsVisible && sectionFixed > 0
                     ? sectionFixed + SavedListFloor
                     : lastSectionFixed + SavedListFloor;

        var show = free >= wanted;
        if (section.IsVisible != show)
        {
            section.IsVisible = show;
        }

        if (!show)
        {
            var idle = Math.Max(view.MinimumHeight, Bounds.Height - others);
            if (Math.Abs(view.MaxHeight - idle) > 0.5 || double.IsPositiveInfinity(view.MaxHeight))
            {
                view.MaxHeight = idle;
            }

            ShelfFromBottom = 0;
            return;
        }

        if (sectionFixed > 0)
        {
            lastSectionFixed = sectionFixed;
        }

        var rows = this.FindControl<UniformGrid>("SavedRows")!;

        var columns = Bounds.Width >= 460 ? 2 : 1;
        if (rows.Columns != columns)
        {
            rows.Columns = columns;
        }

        if (rows.Children.Count > 0 && rows.Children[0].Bounds.Height > 1
            && (rows.Children[0] as Control)?.Tag as string != GhostTag)
        {
            var first = rows.Children[0];
            savedRowHigh = first.Bounds.Height + first.Margin.Top + first.Margin.Bottom;
        }

        var promise = Promise();

        var ceilingLeft = Math.Max(0, Bounds.Height - others - sectionFixed - BoardView.CeilingHeight);
        var entitled = Math.Max(promise, SnapToRows(ceilingLeft) + savedRowHigh);

        var budget = Math.Max(view.MinimumHeight, Bounds.Height - others - sectionFixed - entitled);
        if (double.IsPositiveInfinity(view.MaxHeight) || Math.Abs(view.MaxHeight - budget) > 0.5)
        {
            view.MaxHeight = budget;
        }

        var afterBoard = Bounds.Height - others - sectionFixed - BoardView.FloorHeight;
        var cap = Math.Max(SavedListFloor, Math.Min(entitled, afterBoard));

        cap = SnapToRows(cap);

        if (double.IsNaN(scroll.Height) || Math.Abs(scroll.Height - cap) > 0.5)
        {
            scroll.Height = cap;
        }

        FillGhosts(rows, cap, columns);

        ShelfFromBottom = root.Margin.Bottom + (sectionFixed - section.Margin.Top) + cap;
    }

    private void ShowBoard(StrikeBoard board)
    {
        view.Board = board;
        ShowShapeOf(board);
        ShowMirrorRule();
        this.FindControl<TextBox>("NameBox")!.Text = board.Name;
        ShowStanding();
    }

    internal static WriteableBitmap ThumbBitmap(StrikeBoard board, int cell = 8)
    {
        var pixels = BoardArt.Tiles(board, cell);
        var size = new PixelSize(board.Width * cell, board.Height * cell);
        var bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (var frame = bitmap.Lock())
        {
            var rowBytes = size.Width * 4;
            for (var y = 0; y < size.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    pixels, y * rowBytes, frame.Address + (y * frame.RowBytes), rowBytes);
            }
        }

        return bitmap;
    }

    internal const int ShareCodeLength = 24;

    private void WireKeepRow()
    {
        var nameBox = this.FindControl<TextBox>("NameBox")!;
        nameBox.MaxLength = Army.MaxName;

        var importBox = this.FindControl<TextBox>("ImportBox")!;

        importBox.MaxLength = StrikeBoard.MaxShareLength;
        var save = this.FindControl<Button>("SaveButton")!;
        var import = this.FindControl<Button>("ImportButton")!;

        nameBox.Width = ArmiesPanel.KeepRowBoxWidth(nameBox, save, import);
        importBox.Width = ArmiesPanel.KeepRowBoxWidth(importBox, save, import);

        save.Click += (_, _) => SaveBoard();
        nameBox.TextChanged += (_, _) => ShowStanding();

        importBox.TextChanged += (_, _) => ShowStanding();
        import.Click += (_, _) => ImportBoard();

        this.FindControl<Button>("ResetButton")!.Content = ArmiesPanel.ClearBoardLabel;

        var heading = new SolidColorBrush(Palette.SlateDark.Text.ToColor(), 0.88);
        this.FindControl<TextBlock>("SavedHeading")!.Foreground = heading;
    }

    private void SaveBoard()
    {
        var nameBox = this.FindControl<TextBox>("NameBox")!;
        var typed = Army.CleanName(nameBox.Text);

        if (typed.Length == 0)
        {
            ToastRail.Show(ToastKind.Bad, "Name the board before saving it.");
            return;
        }

        var all = BoardStore.ReadAll(out var unreadable);
        if (unreadable is not null)
        {
            ToastRail.Show(ToastKind.Bad, unreadable);
            return;
        }

        var name = BoardStore.FreeName(typed, all);

        view.Board.Name = name;
        var copy = Clone(view.Board);
        all.Add(copy);

        if (BoardStore.WriteAll(all) is { } failed)
        {
            ToastRail.Show(ToastKind.Bad, failed);
            return;
        }

        Changed?.Invoke();

        ToastRail.Show(ToastKind.Good, $"Saved '{name}'.");

        nameBox.Text = name;
        RedrawSaved();
        ShowStanding();
    }

    private void ImportBoard()
    {
        var box = this.FindControl<TextBox>("ImportBox")!;

        var text = box.Text ?? "";
        if (text.Length > StrikeBoard.MaxShareLength)
        {
            text = text[..StrikeBoard.MaxShareLength];
        }

        var board = StrikeBoard.FromShareString(text, out var problem);
        if (board is null)
        {
            if (problem is not null)
            {
                ToastRail.Show(ToastKind.Bad, problem);
            }

            return;
        }

        board.Name = "Imported";
        ShowBoard(board);
        box.Text = "";
    }

    private readonly List<WriteableBitmap> savedThumbs = [];

    public void RedrawSaved()
    {
        var section = this.FindControl<StackPanel>("SavedSection")!;
        var rows = this.FindControl<UniformGrid>("SavedRows")!;
        rows.Children.Clear();
        foreach (var thumb in savedThumbs)
        {
            thumb.Dispose();
        }

        savedThumbs.Clear();

        var all = BoardStore.ReadAll();

        ShowStanding();

        BuildSavedHeader();
        if (nameCell is not null && boardCell is not null)
        {
            nameCell.Text = "NAME" + SortArrow(ShelfSort.Name);
            boardCell.Text = "BOARD" + SortArrow(ShelfSort.Tiles);
        }

        all = sortBy switch
        {
            ShelfSort.Name => sortAscending
                ? [.. all.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)]
                : [.. all.OrderByDescending(b => b.Name, StringComparer.OrdinalIgnoreCase)],
            ShelfSort.Tiles => sortAscending
                ? [.. all.OrderBy(b => b.Width * b.Height)]
                : [.. all.OrderByDescending(b => b.Width * b.Height)],
            _ => all,
        };

        var text = Palette.SlateDark.Text.ToBrush();
        foreach (var board in all)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,44") };

            var thumb = ThumbBitmap(board);
            savedThumbs.Add(thumb);
            var holder = new Border
            {
                Width = ThumbSize,
                Height = ThumbSize,
                Margin = new Thickness(0, 0, ThumbGap, 0),
                Child = new Image
                {
                    Source = thumb,
                    Width = board.Width * 8,
                    Height = board.Height * 8,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                },
            };
            Grid.SetColumn(holder, 0);
            grid.Children.Add(holder);

            var name = new TextBlock
            {
                Text = board.Name,
                FontSize = 13,
                Foreground = text,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            var stored = board;
            var export = ExportHit(stored);
            Grid.SetColumn(export, 2);
            grid.Children.Add(export);

            var delete = ArmiesPanel.DeleteCross(() =>
            {
                var kept = BoardStore.ReadAll(out var unreadable);
                if (unreadable is not null)
                {
                    ToastRail.Show(ToastKind.Bad, unreadable);
                    return;
                }

                kept.RemoveAll(b => string.Equals(b.Name, stored.Name, StringComparison.OrdinalIgnoreCase));
                if (BoardStore.WriteAll(kept) is { } failed)
                {
                    return;
                }

                Changed?.Invoke();

                RedrawSaved();
            });
            Grid.SetColumn(delete, 3);
            grid.Children.Add(delete);

            var row = new Border { Child = grid };
            row.Classes.Add("row");
            row.Classes.Add("slot");
            row.Cursor = new Cursor(StandardCursorType.Hand);
            row.PointerPressed += (_, _) =>
            {
                ShowBoard(Clone(stored));
            };

            rows.Children.Add(row);
        }
    }

    private Border ExportHit(StrikeBoard stored)
    {
        var muted = Palette.SlateDark.Muted.ToBrush();
        var bright = Palette.SlateDark.Text.ToBrush();

        var label = new TextBlock
        {
            Text = "\uE72D",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = muted,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var hit = new Border
        {
            Child = label,
            Background = Brushes.Transparent,
            Padding = new Thickness(10, 4),
            Cursor = new Cursor(StandardCursorType.Hand),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        hit.PointerEntered += (_, _) => label.Foreground = bright;
        hit.PointerExited += (_, _) => label.Foreground = muted;
        hit.PointerPressed += async (_, e) =>
        {
            e.Handled = true;
            if (stored.Problem() is not null)
            {
                return;
            }

            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is { } clipboard)
            {
                try
                {
                    await clipboard.SetValueAsync(DataFormat.Text, stored.ToShareString());
                    ToastRail.Show(ToastKind.Good, $"Copied the board code for '{stored.Name}'.");
                }
                catch (Exception)
                {
                }
            }
        };

        return hit;
    }

    private static StrikeBoard Clone(StrikeBoard board)
    {
        return board.Reshaped(board.Width, board.Height, board.PlacementRows);
    }

    private bool showingShape;

    private void WireShape()
    {
        var ink = Palette.SlateDark.Text.ToBrush();
        foreach (var name in new[] { "SizeLabel", "ByLabel", "DepthLabel" })
        {
            this.FindControl<TextBlock>(name)!.Foreground = ink;
        }

        var sides = Enumerable.Range(1, StrikeBoard.MaxSide).Select(n => n.ToString()).ToArray();
        foreach (var (name, start) in new[] { ("WidthBox", 7), ("HeightBox", 7), ("DepthBox", 1) })
        {
            var box = this.FindControl<ComboBox>(name)!;
            box.ItemsSource = sides;
            box.SelectedIndex = start;
            box.SelectionChanged += (_, _) => Reshape();
        }
    }

    private void Reshape()
    {
        if (showingShape)
        {
            return;
        }

        var width = this.FindControl<ComboBox>("WidthBox")!.SelectedIndex + 1;
        var height = this.FindControl<ComboBox>("HeightBox")!.SelectedIndex + 1;
        var depth = this.FindControl<ComboBox>("DepthBox")!.SelectedIndex + 1;
        view.Board = view.Board.Reshaped(width, height, depth);
        ShowMirrorRule();
        ShowStanding();
    }

    private void ShowShapeOf(StrikeBoard board)
    {
        showingShape = true;
        this.FindControl<ComboBox>("WidthBox")!.SelectedIndex = Math.Clamp(board.Width, 1, StrikeBoard.MaxSide) - 1;
        this.FindControl<ComboBox>("HeightBox")!.SelectedIndex = Math.Clamp(board.Height, 1, StrikeBoard.MaxSide) - 1;
        this.FindControl<ComboBox>("DepthBox")!.SelectedIndex = Math.Clamp(board.PlacementRows, 1, StrikeBoard.MaxSide) - 1;
        showingShape = false;
    }

    private void BuildSwatches()
    {
        var strip = this.FindControl<WrapPanel>("Swatches")!;

        var face = new Typeface(new FontFamily("Consolas"));
        double widest = 0;
        foreach (var value in Terrain.Cycle)
        {
            var probe = new FormattedText($"{value} {Terrain.Name(value)}",
                                          System.Globalization.CultureInfo.InvariantCulture,
                                          FlowDirection.LeftToRight, face, 11, Brushes.White);
            widest = Math.Max(widest, probe.Width);
        }

        var chipWidth = Math.Ceiling(widest) + 16 + 6 + 16;
        chipFull = chipWidth;

        double widestNumber = 0;
        foreach (var value in Terrain.Cycle)
        {
            var probe = new FormattedText($"{value}", System.Globalization.CultureInfo.InvariantCulture,
                                          FlowDirection.LeftToRight, face, 11, Brushes.White);
            widestNumber = Math.Max(widestNumber, probe.Width);
        }

        chipCompact = Math.Ceiling(widestNumber) + 16 + 6 + 16;
        foreach (var value in Terrain.Cycle)
        {
            var swatch = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new Avalonia.CornerRadius(3),
                Background = BoardArt.MutedShade(value).ToBrush(),
                BorderThickness = new Avalonia.Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.White, 0.2),
                Margin = new Avalonia.Thickness(0, 0, 6, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            var label = new TextBlock
            {
                Text = $"{value} {Terrain.Name(value)}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = Palette.SlateDark.Muted.ToBrush(),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            var hit = new Border
            {
                Width = chipWidth,
                Padding = new Avalonia.Thickness(8, 4),
                Margin = new Avalonia.Thickness(0, 0, 4, 0),
                CornerRadius = new Avalonia.CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Children = { swatch, label },
                },
            };

            var picked = value;
            hit.PointerEntered += (_, _) => hit.Background = new SolidColorBrush(Colors.White, 0.08);
            hit.PointerExited += (_, _) => hit.Background = Brushes.Transparent;
            hit.PointerPressed += (_, _) =>
            {
                view.Brush = picked;
                ShowBrush();
            };

            chips.Add((hit, swatch, label, value));
            strip.Children.Add(hit);
        }

        ShowBrush();
        FitSwatches();
    }

    private bool shapeBarCompact;

    private void FitShapeBar()
    {
        var bar = this.FindControl<Border>("ShapeBar")!;
        var row = this.FindControl<WrapPanel>("ShapeRow")!;
        var sizeLabel = this.FindControl<TextBlock>("SizeLabel")!;
        if (Bounds.Width < 1 || row.Children.Count == 0)
        {
            return;
        }

        double Text(string s)
        {
            var probe = new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture,
                                          FlowDirection.LeftToRight, Typeface.Default, 13,
                                          Brushes.White);
            return probe.Width;
        }

        var dividers = new List<Border>();
        foreach (var child in row.Children)
        {
            if (child is StackPanel group)
            {
                foreach (var part in group.Children)
                {
                    if (part is Border b && b.Classes.Contains("optionDivider"))
                    {
                        dividers.Add(b);
                    }
                }
            }
        }

        var saving = (Text("Board size") - Text("Size"))
                     + (dividers.Count * 12) + 8;

        double oneLine = 0;
        foreach (var child in row.Children)
        {
            oneLine += child.Bounds.Width;
        }

        var avail = Bounds.Width - bar.Padding.Left - bar.Padding.Right;
        var fullLine = oneLine + (shapeBarCompact ? saving : 0);
        var compact = fullLine > avail;
        if (compact == shapeBarCompact)
        {
            return;
        }

        shapeBarCompact = compact;
        sizeLabel.Text = compact ? "Size" : "Board size";
        bar.Padding = compact ? new Thickness(10, 6) : new Thickness(14, 6);
        foreach (var divider in dividers)
        {
            divider.Margin = compact ? new Thickness(8, 0) : new Thickness(14, 0);
        }
    }

    private double chipFull;
    private double chipCompact;
    private bool chipsCompact;

    private void FitSwatches()
    {
        if (chips.Count == 0 || chipFull < 1 || Bounds.Width < 1)
        {
            return;
        }

        var bar = this.FindControl<Border>("SwatchBar")!;
        var avail = Bounds.Width - 28 - bar.Padding.Left - bar.Padding.Right;
        var compact = (chips.Count * (chipFull + 4)) > avail;
        if (compact == chipsCompact)
        {
            return;
        }

        chipsCompact = compact;
        foreach (var (hit, _, label, value) in chips)
        {
            label.Text = compact ? $"{value}" : $"{value} {Terrain.Name(value)}";
            hit.Width = compact ? chipCompact : chipFull;
        }
    }

    private void ShowBrush()
    {
        var ring = new SolidColorBrush(Colors.White, 0.9);
        var faint = new SolidColorBrush(Colors.White, 0.2);
        var bright = Palette.SlateDark.Text.ToBrush();
        var muted = Palette.SlateDark.Muted.ToBrush();
        foreach (var (_, swatch, label, value) in chips)
        {
            var on = value == view.Brush;
            swatch.BorderBrush = on ? ring : faint;
            label.Foreground = on ? bright : muted;
        }
    }

    private void WireMirror()
    {
        var box = this.FindControl<CheckBox>("MirrorBox")!;
        box.IsCheckedChanged += (_, _) =>
        {
            view.Mirror = box.IsChecked == true;
            if (box.IsEnabled)
            {
                mirrorWanted = box.IsChecked == true;
            }
        };

        ShowMirrorRule();
    }

    private bool mirrorWanted;

    private void ShowMirrorRule()
    {
        var box = this.FindControl<CheckBox>("MirrorBox")!;
        var allowed = view.Board.Height % 2 == 0;
        if (box.IsEnabled == allowed)
        {
            return;
        }

        box.IsEnabled = allowed;
        if (!allowed)
        {
            if (box.IsChecked == true)
            {
                box.IsChecked = false;
                ToastRail.Show(ToastKind.Info,
                    "Mirror is off on a board with an odd number of rows: the middle row has no other half.");
            }

            return;
        }

        box.IsChecked = mirrorWanted;
    }

    private readonly Random rng = new();

    private void WireRandom()
    {
        this.FindControl<Button>("RandomButton")!.Click += (_, _) => RandomBoard();
        this.FindControl<Button>("ResetButton")!.Click += (_, _) => ResetBoard();
    }

    private void ResetBoard()
    {
        var fresh = new StrikeBoard
        {
            Name = view.Board.Name,
            Width = StrikeBoard.Size,
            Height = StrikeBoard.Size,
            PlacementRows = 2,
        };
        ShowBoard(fresh);
    }

    private void RandomBoard()
    {
        var symmetric = this.FindControl<CheckBox>("MirrorBox")!.IsChecked == true;

        int width;
        int height;
        int depth;
        if (this.FindControl<CheckBox>("RandomSizeBox")!.IsChecked == true)
        {
            (width, height, depth) = StrikeBoard.RandomShape(rng);
        }
        else
        {
            width = this.FindControl<ComboBox>("WidthBox")!.SelectedIndex + 1;
            height = this.FindControl<ComboBox>("HeightBox")!.SelectedIndex + 1;
            depth = this.FindControl<ComboBox>("DepthBox")!.SelectedIndex + 1;
        }

        var board = StrikeBoard.Random(width, height, depth, symmetric, rng);
        board.Name = "Random board";
        ShowBoard(board);
    }

    private void ShowStanding()
    {
        var problem = view.Board.Problem();
        var save = this.FindControl<Button>("SaveButton")!;

        var full = !BoardStore.HasRoom(BoardStore.ReadAll().Count);
        var named = Army.CleanName(this.FindControl<TextBox>("NameBox")!.Text).Length > 0;
        save.IsEnabled = problem is null && !full && named;
        save.Content = full ? "List full" : problem is not null ? "Cannot save" : "Save";

        var pasted = this.FindControl<TextBox>("ImportBox")!.Text ?? "";
        this.FindControl<Button>("ImportButton")!.IsEnabled = pasted.Trim().Length > 0;
    }
}
