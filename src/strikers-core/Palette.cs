namespace Strikers.Core;

public sealed record Palette(
    Rgb Window,
    Rgb Panel,
    Rgb Text,
    Rgb Muted,
    Rgb Accent,
    Rgb Good,
    Rgb Bad)
{
    public static readonly Palette SlateDark = new(
        Window: new Rgb(0x23, 0x26, 0x2B),
        Panel: new Rgb(0x2C, 0x30, 0x37),
        Text: new Rgb(0xE6, 0xE8, 0xEA),
        Muted: new Rgb(0xAE, 0xB4, 0xBB),
        Accent: new Rgb(0xE8, 0xB3, 0x6A),
        Good: new Rgb(0x8F, 0xC4, 0x6A),
        Bad: new Rgb(0xE4, 0x77, 0x6B));

    public static readonly Palette HighContrastDark = new(
        Window: new Rgb(0x10, 0x12, 0x16),
        Panel: new Rgb(0x19, 0x1C, 0x22),
        Text: new Rgb(0xFF, 0xFF, 0xFF),
        Muted: new Rgb(0xA8, 0xB0, 0xBA),
        Accent: new Rgb(0x46, 0xC8, 0xE0),
        Good: new Rgb(0x5B, 0xD9, 0x7E),
        Bad: new Rgb(0xFF, 0x5C, 0x5C));
}
