using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Strikers.Core;

namespace Strikers.App;

public sealed class ToastRail : StackPanel
{
    private static ToastRail? live;

    private static readonly TimeSpan Slide = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(200);

    public ToastRail()
    {
        live = this;
        Spacing = 8;
        Width = 320;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
    }

    public static void Show(ToastKind kind, string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            live?.Add(kind, text);
        });
    }

    private sealed class Card : Border
    {
        public DispatcherTimer? Hold;
        public bool Leaving;
    }

    private void Add(ToastKind kind, string text)
    {
        while (Children.Count >= Toasts.MaxShown)
        {
            var oldest = Children[Children.Count - 1];
            (oldest as Card)?.Hold?.Stop();
            Children.Remove(oldest);
        }

        var kindInk = kind switch
        {
            ToastKind.Good => Palette.SlateDark.Good,
            ToastKind.Bad => Palette.SlateDark.Bad,
            _ => Palette.SlateDark.Muted,
        };

        var bar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = kindInk.ToBrush(),
            Margin = new Thickness(0, 1, 9, 1),
        };

        var words = new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.SlateDark.Text.ToBrush(),
        };
        Grid.SetColumn(words, 1);

        var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        inner.Children.Add(bar);
        inner.Children.Add(words);

        var panel = Palette.SlateDark.Panel;
        var card = new Card
        {
            Child = inner,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0xFA, panel.R, panel.G, panel.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 6 20 0 #59000000"),
            Padding = new Thickness(12, 10),

            Opacity = 0,
            Margin = new Thickness(16, 0, -16, 0),
        };
        card.Transitions =
        [
            new DoubleTransition { Property = OpacityProperty, Duration = Slide },
            new ThicknessTransition { Property = MarginProperty, Duration = Slide },
        ];

        card.PointerPressed += (_, _) =>
        {
            Dismiss(card);
        };

        Children.Insert(0, card);

        Dispatcher.UIThread.Post(() =>
        {
            card.Opacity = 1;
            card.Margin = new Thickness(0);
        });

        var hold = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Toasts.HoldMs(kind, text.Length)),
        };
        hold.Tick += (_, _) =>
        {
            Dismiss(card);
        };
        card.Hold = hold;
        hold.Start();
    }

    private void Dismiss(Card card)
    {
        if (card.Leaving)
        {
            return;
        }

        card.Leaving = true;
        card.Hold?.Stop();
        card.Opacity = 0;

        var gone = new DispatcherTimer { Interval = Fade };
        gone.Tick += (_, _) =>
        {
            gone.Stop();
            Children.Remove(card);
        };
        gone.Start();
    }
}
