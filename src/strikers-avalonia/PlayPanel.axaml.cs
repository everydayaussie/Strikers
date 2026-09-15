using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Strikers.App;

public partial class PlayPanel : UserControl
{
    private readonly Flow flow = new();
    private readonly MatchDriver driver;
    private readonly ReportOnce haltReport = new();
    private readonly Dictionary<Stage, Control> pages = [];

    private ChallengeBridge.Challenge? chosen;
    private List<ChallengeBridge.Challenge> challenges = [];
    private List<int>? hostBoard;
    private (int Width, int Height, int PlacementRows) hostShape =
        (StrikeBoard.Size, StrikeBoard.Size, -1);

    private List<string> army = [];
    private List<MachineBridge.Machine> machines = [];

    private bool machinesAsked;
    private int budget = -1;

    private string firstPlayer = Play.FirstHost;

    private List<string> opponentArmy = [];

    private readonly Play.SafetyCode fingerprint = new();

    private string roomId = "";
    private string serverAddress = "";

    private string? tunnelServer;

    private bool playStarted;
    private bool playSpawned;
    private bool peerHere;

    public PlayPanel()
    {
        AvaloniaXamlLoader.Load(this);

        driver = new MatchDriver(a => Dispatcher.UIThread.Post(a), MatchLog.BesideTheExe());
        driver.Output += line => Interpret(line.Trim());

        var me = System.Reflection.Assembly.GetExecutingAssembly();
        var (testFor, testExpires) = TestBuild.Read(me);
        var testMark = TestBuild.Mark(testFor, testExpires);
        driver.Say($"Strikers {me.ManifestModule.ModuleVersionId:N} started" + (testMark is null ? "" : $" ({testMark})"));
        driver.Trouble += (headline, detail) =>
        {
            driver.CancelStart();
            SayBad(headline, detail);
        };
        driver.Started += () =>
        {
            Find<Button>("StopButton").IsVisible = true;
        };
        driver.LobbyEnded += StartPlay;

        pages[Stage.Start] = Find<Control>("StartPage");
        pages[Stage.HostChoose] = Find<Control>("StartPage");
        pages[Stage.HostInvite] = Find<Control>("HostInvitePage");
        pages[Stage.JoinPaste] = Find<Control>("StartPage");
        pages[Stage.JoinWait] = Find<Control>("JoinWaitPage");
        pages[Stage.ChooseArmy] = Find<Control>("ChooseArmyPage");
        pages[Stage.Safety] = Find<Control>("SafetyPage");
        pages[Stage.SetUp] = Find<Control>("SetUpPage");
        pages[Stage.Playing] = Find<Control>("PlayingPage");

        Dress();
        WireButtons();
        WirePickers();
        WireFit();
        WireReflow();
        WireTicker();

        RefreshBoards();
        LoadMatches();
        StockPoints();
        ShowStage(Stage.Start);
        Announce(Stage.Start);
        ShowStageForCapture();
        PickBoardForCapture();
        FocusForCapture();
    }

    private void FocusForCapture()
    {
        var wanted = Environment.GetEnvironmentVariable("STRIKERS_SHOW_FOCUS");
        if (wanted is null)
        {
            return;
        }

        var target = Enum.TryParse<Stage>(wanted, ignoreCase: true, out var parsed)
                     && Flow.PathFor(hosting: true).Contains(parsed)
            ? parsed
            : Stage.HostInvite;

        var once = false;
        AttachedToVisualTree += (_, _) =>
        {
            if (once)
            {
                return;
            }

            once = true;
            DispatcherTimer.RunOnce(() =>
            {
                flow.Begin(hosting: true);
                pages[Stage.Start].IsVisible = false;
                Find<Button>("StopButton").IsVisible = true;
                TellFocus(true);
                StartWaiting(HostingHeadline(), "Reaching the relay.");
                DispatcherTimer.RunOnce(() => ShowFocusDoorScreen(target), TimeSpan.FromMilliseconds(1500));
            }, TimeSpan.FromMilliseconds(600));
        };
    }

    private void ShowFocusDoorScreen(Stage target)
    {
        switch (target)
        {
            case Stage.ChooseArmy:
                Go(Stage.ChooseArmy);
                DressArmyForCapture();
                Say("Choose your army", "");
                break;

            case Stage.Safety:
                fingerprint.Take("D5FC31D86A71485C");
                ShowSafetyCode(fingerprint.Code, warning: false, flow.Hosting);
                Go(Stage.Safety);
                CheckTypedCode();
                break;

            case Stage.SetUp:
                Go(Stage.SetUp);
                ShowClock(Play.SetupWindow);
                break;

            case Stage.Playing:
                Go(Stage.Playing);
                break;

            default:
                Find<TextBox>("ShareBox").Text = "bore.pub:12345 0123456789abcdef01234567 GATE01";
                Find<Button>("CopyInviteButton").IsEnabled = true;
                Go(Stage.HostInvite);
                WaitForJoin();
                break;
        }
    }

    private void ShowStageForCapture()
    {
        var wanted = Environment.GetEnvironmentVariable("STRIKERS_SHOW_STAGE");
        if (wanted is null || !Enum.TryParse<Stage>(wanted, ignoreCase: true, out var stage))
        {
            return;
        }

        captureStage = stage;
        flow.Begin(hosting: Flow.PathFor(hosting: true).Contains(stage));
        flow.GoTo(stage);
        ShowStage(stage);
        Announce(stage);
        EnterArmyScreen();

        if (Environment.GetEnvironmentVariable("STRIKERS_SHOW_STOP") is not null)
        {
            Find<Button>("StopButton").IsVisible = true;
            if (stage == Stage.SetUp)
            {
                ShowClock(Play.SetupWindow);
            }
        }
    }

    private Stage? captureStage;

    private void PickBoardForCapture()
    {
        var wanted = Environment.GetEnvironmentVariable("STRIKERS_PICK_BOARD");
        if (wanted is null || !int.TryParse(wanted, out var index))
        {
            return;
        }

        var box = Find<ComboBox>("BoardBox");
        if (index >= 0 && index < box.ItemCount)
        {
            box.SelectedIndex = index;
        }
    }

    private string SaveReport()
    {
        var zip = Report.TrySave(AppContext.BaseDirectory, DateTime.Now, out var problem);
        if (zip is not null)
        {
            driver.Say($"report saved: {Report.Folder}\\{System.IO.Path.GetFileName(zip)}");
            return $"A report is in the {Report.Folder} folder beside Strikers. Please send it to the mod's creator.";
        }

        driver.Say($"report not saved: {problem}");
        return $"The report could not be saved. Please send {MatchLog.Name} to the mod's creator instead.";
    }

    private T Find<T>(string name) where T : Control
    {
        return this.FindControl<T>(name)!;
    }

    private void Dress()
    {
        var heading = new SolidColorBrush(Palette.SlateDark.Text.ToColor(), 0.88);
        foreach (var name in new[]
                 {
                     "HostHeading", "JoinHeading", "ShareLabel", "SafetyLabel",
                     "StepsHeading", "MatchHeading",
                 })
        {
            Find<Control>(name).SetValue(ForegroundProperty, heading);
        }

        foreach (var name in new[]
                 {
                     "StepLine", "BoardLabel", "VictoryLabel", "DraftLabel",
                     "ShareBox", "PasteBox", "TypedCode", "BoardBox",
                     "ArmyBox", "VictoryBox", "DraftBox",
                 })
        {
            Find<Control>(name).SetValue(ForegroundProperty, Palette.SlateDark.Text.ToBrush());
        }

        foreach (var name in new[]
                 {
                     "DetailLine",
                     "SetUpClock", "PlayStatus",
                 })
        {
            Find<Control>(name).SetValue(ForegroundProperty, Palette.SlateDark.Muted.ToBrush());
        }

        Find<TextBlock>("SafetyCode").Foreground = Palette.SlateDark.Accent.ToBrush();
    }

    private void Ink(string name, Rgb colour)
    {
        Find<Control>(name).SetValue(ForegroundProperty, colour.ToBrush());
    }

    private void ShowStage(Stage stage)
    {
        if (stage == Stage.HostChoose)
        {
            RefreshBoards();
        }

        var wanted = pages[stage];
        foreach (var page in pages.Values.Distinct())
        {
            page.IsVisible = page == wanted;
        }

        ShowStageStrip();
        Find<Button>("StopButton").IsVisible = driver.AnythingRunning;
        if (stage != Stage.ChooseArmy)
        {
            Find<Button>("UseArmyButton").IsVisible = false;
        }

        Find<Button>("CodeMatchesButton").IsVisible = stage == Stage.Safety;
        Find<Button>("WriteButton").IsVisible = stage == Stage.SetUp;

        Reflow();
    }

    public event Action? ContentChanged;

    private void Reflow()
    {
        ContentChanged?.Invoke();
    }

    private bool reflowPosted;

    private void WireReflow()
    {
        Find<Panel>("Stages").SizeChanged += (_, _) => ReflowLater();
        Find<StackPanel>("Headline").SizeChanged += (_, _) => ReflowLater();
    }

    private void ReflowLater()
    {
        if (reflowPosted)
        {
            return;
        }

        reflowPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            reflowPosted = false;
            Reflow();
        }, DispatcherPriority.Background);
    }

    public bool Idle
    {
        get
        {
            return flow.Current == Stage.Start && !driver.AnythingRunning;
        }
    }

    public void Note(string line)
    {
        driver.Say($"  {line}");
    }

    public event Action<bool>? FocusChanged;

    private bool toldFocus;

    private void TellFocus(bool on)
    {
        if (on == toldFocus)
        {
            return;
        }

        toldFocus = on;
        FocusChanged?.Invoke(on);
    }


    private void Go(Stage stage)
    {
        TryGo(stage);
    }

    private bool TryGo(Stage stage)
    {
        if (!flow.GoTo(stage))
        {
            return false;
        }

        ShowStage(stage);
        Announce(stage);
        return true;
    }

    private void Announce(Stage stage)
    {
        switch (stage)
        {
            case Stage.Start:
            case Stage.HostChoose:
            case Stage.JoinPaste:
                Say("", "");
                break;

            case Stage.ChooseArmy:
                Say("Choose your army", "");
                break;

            case Stage.Safety:
                Say("Compare safety codes",
                    "Read the four amber characters out to your opponent, then type the four they read to you.");
                break;

            case Stage.SetUp:
                ShowSetUpSteps();
                Say("Set the match up in game", "");
                break;

            case Stage.Playing:
                Say("Playing", "Place your machines, then take your turns in game.");
                break;

            default:
                break;
        }
    }

    private void Say(string headline, string detail)
    {
        StopWaiting();
        Ink("StepLine", Palette.SlateDark.Text);
        Show("StepLine", headline);
        Ink("DetailLine", Palette.SlateDark.Muted);
        Show("DetailLine", detail);
        LogScreen(headline, detail);
    }

    private void Show(string name, string text)
    {
        var block = Find<TextBlock>(name);
        block.Text = text;
        var shown = text.Length > 0;
        if (block.IsVisible != shown)
        {
            block.IsVisible = shown;
            Reflow();
        }
    }

    private ChallengeBridge.Challenge? hostChallenge;

    private void ChooseHosting()
    {
        if (!HaveAnArmy())
        {
            return;
        }

        var picked = challenges.FirstOrDefault(c => c.Slots == 0);
        if (picked is null)
        {
            ToastRail.Show(ToastKind.Bad,
                           "That challenge is not available. The challenge list has not loaded. "
                           + "If this keeps up, netplay could not be reached.");
            return;
        }

        hostChallenge = picked;
        StartHosting();
    }

    private void ChooseJoining()
    {
        if (!HaveAnArmy())
        {
            return;
        }

        StartJoining();
    }

    private void BackToStart()
    {
        flow.Reset();
        TellFocus(false);
        Find<TextBox>("ShareBox").Text = "";
        Find<TextBox>("PasteBox").Text = "";
        MaskInvite();
        MaskPaste();
        Find<TextBlock>("SetUpClock").IsVisible = false;
        ShowStage(Stage.Start);
        Announce(Stage.Start);
    }

    private void WireButtons()
    {
        var joinHead = Find<Border>("JoinHead");
        joinHead.Margin = new Thickness(0, SectionGap - HeadingLead, 0, joinHead.Margin.Bottom);

        Find<Button>("CreateLobbyButton").Click += (_, _) => ChooseHosting();

        Find<NumericUpDown>("DraftBox").PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                FitThumb();
            }
        };

        Find<ComboBox>("FirstBox").PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                FitCreateLobby();
            }
        };

        Find<Grid>("HostForm").PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                FitThumb();
            }
        };
        Find<Button>("JoinGoButton").Click += (_, _) => ChooseJoining();

        var pasteBox = Find<TextBox>("PasteBox");
        pasteBox.TextChanged += (_, _) =>
        {
            Find<Button>("JoinGoButton").IsEnabled = (pasteBox.Text ?? "").Trim().Length > 0;
        };

        Find<Button>("UseArmyButton").Click += (_, _) => SendArmy();
        Find<Button>("WriteButton").Click += (_, _) => WriteSetup();
        Find<Button>("StopButton").Click += (_, _) => StopAll();
        Find<Button>("CodeMatchesButton").Click += (_, _) => ConfirmCode();
        Find<ScrollViewer>("ArmyScroll").PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                FitArmyTable();
            }
        };

        Find<Button>("CopyInviteButton").Click += async (_, _) =>
        {
            var failed = await Play.Caught(() => Copy(Find<TextBox>("ShareBox").Text ?? ""));
            if (failed is not null)
            {
                ToastRail.Show(ToastKind.Bad, "Could not copy the invite. Press Copy again.");
                return;
            }

            ToastRail.Show(ToastKind.Good, "Invite copied. Send it to your opponent.");
        };

        Find<Button>("PasteButton").Click += async (_, _) => await PasteInvite();

        Find<TextBox>("TypedCode").TextChanged += (_, _) =>
        {
            ForceUpper();
            CheckTypedCode();
        };
    }

    private bool forcingUpper;

    private void ForceUpper()
    {
        if (forcingUpper)
        {
            return;
        }

        var box = Find<TextBox>("TypedCode");
        var upper = (box.Text ?? "").ToUpperInvariant();
        if (upper == box.Text)
        {
            return;
        }

        forcingUpper = true;
        var caret = box.CaretIndex;
        box.Text = upper;
        box.CaretIndex = caret;
        forcingUpper = false;
    }

    private void CheckTypedCode()
    {
        var state = Play.SafetyTypedState(fingerprint.Code, Find<TextBox>("TypedCode").Text ?? "", flow.Hosting);
        Find<Button>("CodeMatchesButton").IsEnabled = state.Continue;
        SetContinueLabel(state.Press);
    }

    private void SetContinueLabel(string label)
    {
        Find<TextBlock>("ContinueLabel").Text = label;
    }

    private void WirePickers()
    {
        var armyBox = Find<ComboBox>("ArmyBox");
        armyBox.SelectionChanged += (_, _) => ArmyChosen();

        armyBox.ContainerPrepared += (_, e) =>
        {
            if (e.Container is not ComboBoxItem row || !hoverWired.Add(row))
            {
                return;
            }

            row.PointerEntered += (_, _) =>
            {
                var index = armyBox.IndexFromContainer(row);
                if (index < 0 || armyBox.Items[index] is not string name)
                {
                    return;
                }

                var hovered = ArmyStore.ReadAll().FirstOrDefault(a => a.Name == name)?.Machines;
                if (hovered is not null)
                {
                    BuildArmyRows(hovered);
                }
            };
        };
        armyBox.DropDownClosed += (_, _) => BuildArmyRows();

        var boardBox = Find<ComboBox>("BoardBox");
        boardBox.SelectionChanged += (_, _) => ShowBoardThumb();

        boardBox.ContainerPrepared += (_, e) =>
        {
            if (e.Container is not ComboBoxItem row || !hoverWired.Add(row))
            {
                return;
            }

            row.PointerEntered += (_, _) =>
            {
                var index = boardBox.IndexFromContainer(row);
                if (index >= 0)
                {
                    ShowBoardThumb(index);
                }
            };
        };
        boardBox.DropDownClosed += (_, _) => ShowBoardThumb();
    }

    private void StockPoints()
    {
        var (victory, draft) = Play.StockRules(0);
        Find<NumericUpDown>("VictoryBox").Value = victory;
        Find<NumericUpDown>("DraftBox").Value = draft;
    }

    private void LoadMatches()
    {
        Task.Run(() =>
        {
            var all = ChallengeBridge.All(NetplayTool.Netplay());
            Dispatcher.UIThread.Post(() =>
            {
                ShowMatches(all);
            });
        });
    }

    private void ShowMatches(List<ChallengeBridge.Challenge> all)
    {
        challenges = all;

        var attemptUnderWay = flow.Current != Stage.Start || driver.AnythingRunning;
        if (Play.MatchListNote(challenges.Count, attemptUnderWay) is { } note)
        {
            SayBad(note.Headline, note.Detail);
        }

        if (challenges.Count == 0)
        {
            return;
        }

        if (captureStage == Stage.ChooseArmy)
        {
            DressArmyForCapture();
        }
    }

    private void DressArmyForCapture()
    {
        captureStage = Stage.ChooseArmy;
        if (challenges.Count == 0)
        {
            return;
        }

        if (chosen is null)
        {
            chosen = challenges.FirstOrDefault(c => c.Slots == 0);
            budget = Play.StockRules(0).Draft;
        }

        EnterArmyScreen();
    }

    public void RefreshBoards()
    {
        var box = Find<ComboBox>("BoardBox");
        var picked = box.SelectedItem?.ToString();

        var names = new List<string> { Play.StockBoard };
        foreach (var b in BoardStore.ReadAll())
        {
            names.Add(b.Name);
        }

        box.ItemsSource = names;
        var back = picked is not null ? names.IndexOf(picked) : -1;
        box.SelectedIndex = back >= 0 ? back : 0;

        ShowBoardThumb();
    }

    private WriteableBitmap? boardThumb;
    private readonly HashSet<ComboBoxItem> hoverWired = new();

    private void ShowBoardThumb()
    {
        ShowBoardThumb(Find<ComboBox>("BoardBox").SelectedIndex);
    }

    private void ShowBoardThumb(int index)
    {
        var image = Find<Image>("BoardThumb");
        image.Source = null;
        boardThumb?.Dispose();
        boardThumb = null;

        var box = Find<ComboBox>("BoardBox");
        var names = box.ItemsSource as IList<string>;
        if (index < 0 || names is null || index >= names.Count)
        {
            return;
        }

        StrikeBoard? found;
        if (index == 0)
        {
            found = StrikeBoard.Stock();
        }
        else
        {
            var (picked, problem) = Play.PickedBoard(index, names[index], BoardStore.ReadAll());
            if (picked is null || problem is not null)
            {
                return;
            }

            found = picked;
        }

        boardThumb = BoardsPanel.ThumbBitmap(found, previewCell);
        image.Width = found.Width * previewCell;
        image.Height = found.Height * previewCell;
        image.Source = boardThumb;
    }

    private const int PreviewMaxCell = 16;
    private const int PreviewMinCell = 6;
    private const int PreviewTiles = 8;
    private const double PickerMinWidth = 80;
    private const double NumberBoxWidth = 52;
    private const double PreviewGap = 12;
    private int previewCell = 8;

    private const double CreateLobbyNeed = 126;

    private const double FirstBoxNeed = 110;

    private void FitThumb()
    {
        var form = Find<Grid>("HostForm");
        var boxRight = Find<NumericUpDown>("DraftBox").Bounds.Right;
        if (form.Bounds.Width < 1 || boxRight < 1)
        {
            return;
        }

        var labelRight = boxRight - NumberBoxWidth;
        var avail = form.Bounds.Width - Math.Max(boxRight, labelRight + PickerMinWidth) - PreviewGap;
        var cell = (int)Math.Floor(avail / PreviewTiles);

        var holder = Find<Border>("BoardThumbHolder");
        var fits = cell >= PreviewMinCell;
        if (holder.IsVisible != fits)
        {
            holder.IsVisible = fits;
        }
        if (!fits)
        {
            FitCreateLobby();
            return;
        }

        cell = Math.Min(cell, PreviewMaxCell);
        var side = (cell * PreviewTiles) + 2;
        if (double.IsNaN(holder.Width) || Math.Abs(holder.Width - side) > 0.5)
        {
            holder.Width = side;
            holder.Height = side;
        }

        FitCreateLobby();

        if (cell != previewCell)
        {
            previewCell = cell;
            ShowBoardThumb();
        }
    }

    private void ShowStageStrip()
    {
        var strip = Find<UniformGrid>("StageStrip");
        strip.Children.Clear();

        var number = flow.Number;
        if (number <= 1)
        {
            strip.IsVisible = false;
            return;
        }

        strip.IsVisible = true;
        var path = flow.Path;
        for (var i = 1; i < path.Length; i++)
        {
            var word = path[i] switch
            {
                Stage.HostInvite => "Invite",
                Stage.JoinWait => "Connect",
                Stage.ChooseArmy => "Army",
                Stage.Safety => "Code",
                Stage.SetUp => "Set up",
                Stage.Playing => "Play",
                _ => "",
            };

            var bar = new Border();
            bar.Classes.Add("stageBar");
            var label = new TextBlock { Text = word };
            label.Classes.Add("stageLabel");

            if (i + 1 <= number)
            {
                bar.Classes.Add("lit");
                label.Classes.Add("lit");
            }

            strip.Children.Add(new StackPanel { Children = { bar, label } });
        }
    }

    private void MaskInvite()
    {
        Find<TextBox>("ShareBox").PasswordChar = '*';
    }

    private void MaskPaste()
    {
        Find<TextBox>("PasteBox").PasswordChar = '*';
    }

    private (List<int>? Board, string? Problem) ChosenBoard()
    {
        hostShape = (StrikeBoard.Size, StrikeBoard.Size, -1);

        var box = Find<ComboBox>("BoardBox");
        var (found, problem) = Play.PickedBoard(box.SelectedIndex, box.SelectedItem?.ToString(),
                                                BoardStore.ReadAll());
        if (problem is not null)
        {
            return (null, problem);
        }

        if (found is null)
        {
            return (null, null);
        }

        hostShape = (found.Width, found.Height, found.PlacementRows);
        return (Play.PlayableBoard(found.Cells, found.Width, found.Height), null);
    }

    private int ChosenVictoryPoints()
    {
        return (int)(Find<NumericUpDown>("VictoryBox").Value ?? Play.StockRules(0).Victory);
    }

    private int ChosenDraftPoints()
    {
        return (int)(Find<NumericUpDown>("DraftBox").Value ?? Play.StockRules(0).Draft);
    }

    private int Budget()
    {
        return budget > 0 ? budget : 10;
    }

    private void EnterArmyScreen()
    {
        var pick = Find<ComboBox>("ArmyBox");
        if (chosen is not { } c)
        {
            Find<StackPanel>("ArmyRows").Children.Clear();
            pick.ItemsSource = Array.Empty<string>();
            pick.IsVisible = false;
            Find<ScrollViewer>("ArmyScroll").IsVisible = false;
            Find<TextBlock>("ArmyCostLine").IsVisible = false;
            Find<Button>("UseArmyButton").IsVisible = false;
            Reflow();
            return;
        }

        pick.IsVisible = true;
        pick.IsEnabled = true;
        Find<ScrollViewer>("ArmyScroll").IsVisible = true;
        Find<Button>("UseArmyButton").IsVisible = true;
        Reflow();

        if (machines.Count == 0 && !machinesAsked)
        {
            machinesAsked = true;
            machines = MachineBridge.All(NetplayTool.Netplay());
        }

        var offered = OffersOpponentArmy();
        var hadOpponent = offered && pick.SelectedIndex == 0 && pick.ItemCount > 0
                          && pick.SelectedItem?.ToString() == Play.OpponentArmyEntry;
        var picked = pick.SelectedItem?.ToString();
        var names = new List<string>();

        if (offered)
        {
            names.Add(Play.OpponentArmyEntry);
        }

        foreach (var a in ArmyStore.ReadAll())
        {
            names.Add(a.Name);
        }

        pick.ItemsSource = names;
        var first = offered ? 1 : 0;
        var back = hadOpponent ? 0 : picked is not null ? names.IndexOf(picked, first) : -1;
        pick.SelectedIndex = back >= 0 ? back : Math.Min(first, names.Count - 1);
    }

    private bool OffersOpponentArmy()
    {
        return !flow.Hosting && opponentArmy.Count > 0;
    }

    private void ArmyChosen()
    {
        if (chosen is not { } c)
        {
            return;
        }

        var pick = Find<ComboBox>("ArmyBox");
        if (OffersOpponentArmy() && pick.SelectedIndex == 0)
        {
            army = [.. opponentArmy];
        }
        else
        {
            var name = pick.SelectedItem?.ToString();
            army = ArmyStore.ReadAll().FirstOrDefault(a => a.Name == name)?.Machines ?? [];
        }

        BuildArmyRows();
    }

    private bool HaveAnArmy()
    {
        if (ArmyStore.ReadAll().Count > 0)
        {
            return true;
        }

        ToastRail.Show(ToastKind.Bad, Play.NoArmyToStart);
        return false;
    }

    private static readonly (string Title, int Width)[] ArmyColumns =
    [
        ("Machine", 0), ("Cost", 36), ("HP", 36), ("Move", 40), ("Range", 44), ("Power", 44),
        ("Attack", 52), ("Ability", 82),
    ];

    private const double ArmyTableMinWidth = 440;

    private static readonly double ArmyStatsWidth = ArmyColumns.Where(c => c.Width > 0).Sum(c => c.Width);
    private const double ArmyPickerFloor = 120;

    private static Grid ArmyRowGrid()
    {
        var grid = new Grid { MinWidth = ArmyTableMinWidth };
        foreach (var (_, width) in ArmyColumns)
        {
            grid.ColumnDefinitions.Add(width == 0
                ? new ColumnDefinition(1, GridUnitType.Star)
                : new ColumnDefinition(width, GridUnitType.Pixel));
        }

        return grid;
    }

    private void FitArmyTable()
    {
        var scroll = Find<ScrollViewer>("ArmyScroll");
        var rows = Find<StackPanel>("ArmyRows");
        var want = Math.Max(ArmyTableMinWidth, scroll.Bounds.Width);
        if (scroll.Bounds.Width < 1)
        {
            return;
        }

        if (double.IsNaN(rows.MinWidth) || Math.Abs(rows.MinWidth - want) > 0.5)
        {
            rows.MinWidth = want;
        }

        FitArmyPicker();
    }

    private void FitArmyPicker()
    {
        var scroll = Find<ScrollViewer>("ArmyScroll");
        var room = scroll.Bounds.Width;
        if (room < 1)
        {
            return;
        }

        var line = Find<TextBlock>("ArmyCostLine");
        var taken = 0.0;
        if (line.IsVisible)
        {
            line.Measure(Size.Infinity);
            taken = line.DesiredSize.Width;
        }

        var want = Math.Max(ArmyPickerFloor, Math.Min(ArmyStatsWidth, room - taken));
        var pick = Find<ComboBox>("ArmyBox");
        if (double.IsNaN(pick.Width) || Math.Abs(pick.Width - want) > 0.5)
        {
            pick.Width = want;
        }
    }

    private int armySortColumn = -1;
    private bool armySortAscending = true;
    private readonly List<TextBlock> armyHeaderCells = [];

    private void ArmySortBy(int column)
    {
        if (armySortColumn == column && armySortAscending)
        {
            armySortAscending = false;
        }
        else if (armySortColumn == column)
        {
            armySortColumn = -1;
            armySortAscending = true;
        }
        else
        {
            armySortColumn = column;
            armySortAscending = true;
        }

        BuildArmyRows();
    }

    private List<MachineBridge.Machine?> SortedArmyMachines(IReadOnlyList<string> shown)
    {
        var rows = shown.Select(uuid => machines.FirstOrDefault(r => r.Uuid == uuid)).ToList();
        if (armySortColumn < 0)
        {
            return rows;
        }

        IOrderedEnumerable<MachineBridge.Machine?> ordered = armySortColumn switch
        {
            1 => rows.OrderBy(m => m is null ? int.MaxValue : m.Cost),
            2 => rows.OrderBy(m => m is null ? int.MaxValue : m.Health),
            3 => rows.OrderBy(m => m is null ? int.MaxValue : m.Move),
            4 => rows.OrderBy(m => m is null ? int.MaxValue : m.Range),
            5 => rows.OrderBy(m => m is null ? int.MaxValue : m.Power),
            6 => rows.OrderBy(m => m is null ? "~" : Glossary.Attack(m.Pattern).Display,
                              StringComparer.OrdinalIgnoreCase),
            7 => rows.OrderBy(m => m is null ? 2 : Glossary.Ability(m.Ability).Display is "" or "-" ? 1 : 0)
                     .ThenBy(m => m is null ? "~" : Glossary.Ability(m.Ability).Display,
                             StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderBy(m => m is null ? "~" : m.Name, StringComparer.OrdinalIgnoreCase),
        };

        var sorted = ordered.ToList();
        if (!armySortAscending)
        {
            sorted.Reverse();
        }

        return sorted;
    }

    private static TextBlock ArmyCell(string text, int column, IBrush ink, bool bold = false)
    {
        var cell = new TextBlock
        {
            Text = text,
            FontSize = bold || column != 0 ? 12 : 13,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            FontFamily = bold || column is 0 or >= 6 ? FontFamily.Default : new FontFamily("Consolas"),
            Foreground = ink,
            HorizontalAlignment = column is 0 or >= 6 ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(cell, column);
        return cell;
    }

    private void BuildArmyRows()
    {
        BuildArmyRows(army);
    }

    private void BuildArmyRows(IReadOnlyList<string> shown)
    {
        var rows = Find<StackPanel>("ArmyRows");
        rows.Children.Clear();
        Find<TextBlock>("ArmyCostLine").IsVisible = false;

        var text = Palette.SlateDark.Text.ToBrush();
        var muted = Palette.SlateDark.Muted.ToBrush();

        var header = ArmyRowGrid();
        armyHeaderCells.Clear();
        for (var i = 0; i < ArmyColumns.Length; i++)
        {
            var arrow = i == armySortColumn ? (armySortAscending ? " \u25B2" : " \u25BC") : "";
            var cell = ArmyCell(ArmyColumns[i].Title + arrow, i, muted, bold: true);
            armyHeaderCells.Add(cell);
            var wrap = new Border
            {
                Child = cell,
                Cursor = new Cursor(StandardCursorType.Hand),
                Height = 22,
            };
            wrap.Classes.Add("headerCell");
            var column = i;
            wrap.PointerPressed += (_, _) => ArmySortBy(column);
            Grid.SetColumn(wrap, i);
            header.Children.Add(wrap);
        }

        var headerRule = new Border { Child = header };
        headerRule.Classes.Add("shelfHeader");
        rows.Children.Add(headerRule);

        var cost = 0;
        foreach (var m in SortedArmyMachines(shown))
        {
            var grid = ArmyRowGrid();
            if (m is null)
            {
                grid.Children.Add(ArmyCell("Not a machine this save has", 0,
                                           Palette.SlateDark.Bad.ToBrush()));
            }
            else
            {
                cost += m.Cost;
                var ability = Glossary.Ability(m.Ability).Display;
                var cells = new[]
                {
                    m.Name, m.Cost.ToString(), m.Health.ToString(), m.Move.ToString(),
                    m.Range.ToString(), m.Power.ToString(), Glossary.Attack(m.Pattern).Display,
                    ability.Length > 0 ? ability : "-",
                };
                for (var i = 0; i < cells.Length; i++)
                {
                    grid.Children.Add(ArmyCell(cells[i], i, text));
                }
            }

            var row = new Border { Child = grid };
            row.Classes.Add("row");
            if (m is not null)
            {
                ToolTip.SetTip(row, ArmiesPanel.MachineTip(m));
            }

            rows.Children.Add(row);
        }

        for (var i = shown.Count; i < Play.MaxArmy; i++)
        {
            rows.Children.Add(ArmyGhostRow());
        }

        Reflow();

        if (shown.Count == 0)
        {
            return;
        }

        var budget = Budget();
        var over = cost > budget;
        var line = Find<TextBlock>("ArmyCostLine");
        line.Inlines!.Clear();
        line.IsVisible = true;
        var ink = over ? Palette.SlateDark.Bad.ToBrush() : muted;
        line.Inlines.Add(new Run("Army cost ") { Foreground = ink });
        line.Inlines.Add(new Run(cost.ToString())
        {
            Foreground = over ? Palette.SlateDark.Bad.ToBrush() : Palette.SlateDark.Good.ToBrush(),
        });
        line.Inlines.Add(new Run(over ? $", over the {budget} allowed" : $" of {budget}") { Foreground = ink });
        FitArmyPicker();
    }

    private static Control ArmyGhostRow()
    {
        var grid = ArmyRowGrid();
        grid.Children.Add(ArmyCell("Ag", 0, Brushes.Transparent));
        var ghost = new Border { Child = grid, IsHitTestVisible = false };
        ghost.Classes.Add("row");
        return ghost;
    }

    private void ShowSafetyCode(string code, bool warning, bool hosting)
    {
        var block = Find<TextBlock>("SafetyCode");
        block.Inlines!.Clear();
        Find<TextBox>("TypedCode").PlaceholderText = Play.SafetyPlaceholder(hosting);

        if (warning)
        {
            block.Inlines.Add(new Run(code) { Foreground = Palette.SlateDark.Bad.ToBrush() });
            return;
        }

        var groups = Play.SafetyGroups(code);
        var typed = Play.SafetyTypedGroup(hosting, groups.Count);
        var readOut = Play.SafetyReadOutGroup(hosting, groups.Count);
        for (var i = 0; i < groups.Count; i++)
        {
            if (i == typed)
            {
                block.Inlines.Add(new Run(new string('*', groups[i].Length))
                {
                    Foreground = Palette.SlateDark.Muted.ToBrush(),
                });
            }
            else
            {
                block.Inlines.Add(new Run(groups[i])
                {
                    Foreground = i == readOut ? Palette.SlateDark.Accent.ToBrush() : Palette.SlateDark.Text.ToBrush(),
                });
            }

            if (i < groups.Count - 1)
            {
                block.Inlines.Add(new Run(" "));
            }
        }
    }

    private void ShowSetUpSteps()
    {
        var entry = chosen?.Name
                    ?? challenges.FirstOrDefault(c => c.Slots == 0)?.Name
                    ?? "Regular Challenge";

        var steps = new[]
        {
            "In game, go to Salma's table, Machine Strike, then Challenges.",
            $"Find {entry}. Do not open it yet.",
            "Press Set up the match.",
            "Wait until this screen says you are both ready.",
            "Open the challenge and place your machines.",
        };

        var list = Find<StackPanel>("SetUpList");
        list.Children.Clear();
        for (var i = 0; i < steps.Length; i++)
        {
            var dot = new Border
            {
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Palette.SlateDark.Text.ToBrush(),
                },
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 8, 0),
            };
            dot.Classes.Add("hintDot");

            var words = new TextBlock
            {
                Text = steps[i],
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Palette.SlateDark.Text.ToBrush(),
            };
            Grid.SetColumn(words, 1);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(dot);
            row.Children.Add(words);

            var ruled = new Border { Child = row, Padding = new Thickness(0, 5) };
            ruled.Classes.Add("formRule");
            list.Children.Add(ruled);
        }
    }

    private const double SectionGap = 45;

    private const double HeadingLead = 6;

    private void FitCreateLobby()
    {
        var holder = Find<Border>("BoardThumbHolder");
        var preview = holder.IsVisible && !double.IsNaN(holder.Width) ? holder.Width : 0;
        var want = Math.Max(preview, CreateLobbyNeed);

        var row = Find<Grid>("HostGoRow");
        var firstBox = Find<ComboBox>("FirstBox");
        if (row.Bounds.Width >= 1 && firstBox.Bounds.Width >= 1)
        {
            var oneLine = row.Bounds.Width >= firstBox.Bounds.Left + FirstBoxNeed + PreviewGap + want;
            var rule = Find<Border>("FirstRule");
            var placed = Find<Button>("CreateLobbyButton");
            if (oneLine != (Grid.GetRow(placed) == 0))
            {
                Grid.SetRow(placed, oneLine ? 0 : 1);
                Grid.SetColumn(placed, oneLine ? 2 : 0);
                Grid.SetColumnSpan(placed, oneLine ? 1 : 3);
                placed.Margin = oneLine ? new Thickness(0) : new Thickness(0, 10, 0, 0);
                Grid.SetColumnSpan(rule, oneLine ? 2 : 3);
                rule.Margin = oneLine ? new Thickness(0, 0, PreviewGap, 0) : new Thickness(0);
            }

            var right = oneLine ? PreviewGap : preview > 0 ? preview + PreviewGap : 0;
            if (Math.Abs(firstBox.Margin.Right - right) > 0.5)
            {
                firstBox.Margin = new Thickness(0, 6, right, 6);
            }
        }

        var button = Find<Button>("CreateLobbyButton");
        if (double.IsNaN(button.Width) || Math.Abs(button.Width - want) > 0.5)
        {
            button.Width = want;
        }
    }

    private void WireFit()
    {
        Find<Grid>("Spread").PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                FitStart();
            }
        };
    }

    private double lastHeadCost = 40;

    private void FitStart()
    {
        var page = Find<StackPanel>("StartPage");
        var viewport = Find<ScrollViewer>("StageScroll").Bounds.Height;
        if (viewport < 1)
        {
            return;
        }

        var head = Find<Border>("HostHead");
        if (head.IsVisible && head.Bounds.Height > 1)
        {
            lastHeadCost = head.Bounds.Height + head.Margin.Top + head.Margin.Bottom;
        }

        var need = page.DesiredSize.Height
                   + (page.Classes.Contains("compact") ? lastHeadCost : 0);
        var wantCompact = viewport + 0.5 < need;

        var isCompact = page.Classes.Contains("compact");
        if (wantCompact == isCompact)
        {
            return;
        }

        if (wantCompact)
        {
            page.Classes.Add("compact");
        }
        else
        {
            page.Classes.Remove("compact");
        }
    }

    private async Task Copy(string text)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is { } clipboard)
        {
            await clipboard.SetValueAsync(DataFormat.Text, text);
        }
    }

    private const string NotAnInvite = "That is not an invite. Paste the whole line your opponent sent.";

    private async Task PasteInvite()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is not { } clipboard)
        {
            return;
        }

        var text = "";
        var failed = await Play.Caught(async () =>
        {
            text = await clipboard.TryGetValueAsync(DataFormat.Text) ?? "";
        });

        if (failed is not null)
        {
            ToastRail.Show(ToastKind.Bad, "Could not read the clipboard. Press Paste again.");
            return;
        }

        if (text.Length == 0)
        {
            return;
        }

        MaskPaste();

        if (text.Length > Play.MaxInviteLength)
        {
            text = text[..Play.MaxInviteLength];
        }

        Find<TextBox>("PasteBox").Text = text;
        var (address, room, code) = Play.ParseInvite(text);
        if (address is null || room is null || code is null)
        {
            ToastRail.Show(ToastKind.Bad, NotAnInvite);
        }
    }
}
