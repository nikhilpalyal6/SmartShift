namespace SmartShift.Processing.PresentationStudio.Models;

public enum SlideTextAlignment
{
    Left,
    Center
}

public sealed record PresentationTheme
{
    public string Name { get; init; } = "Dark Slate";
    public string BackgroundHex { get; init; } = "#080C14";
    public string SurfaceHex { get; init; } = "#111827";
    public string PrimaryTextHex { get; init; } = "#F9FAFB";
    public string AccentHex { get; init; } = "#EA580C";
    public string MutedTextHex { get; init; } = "#9CA3AF";
    public string FontFamily { get; init; } = "Segoe UI";
    public double TitleFontSize { get; init; } = 36.0;
    public double BodyFontSize { get; init; } = 18.0;
    public SlideTextAlignment Alignment { get; init; } = SlideTextAlignment.Left;

    public static PresentationTheme DarkSlate => new()
    {
        Name = "Dark Slate",
        BackgroundHex = "#080C14",
        SurfaceHex = "#111827",
        PrimaryTextHex = "#F9FAFB",
        AccentHex = "#EA580C",
        MutedTextHex = "#9CA3AF",
        FontFamily = "Segoe UI",
        TitleFontSize = 36.0,
        BodyFontSize = 18.0
    };

    public static PresentationTheme ModernMinimal => new()
    {
        Name = "Modern Minimal",
        BackgroundHex = "#F8FAFC",
        SurfaceHex = "#FFFFFF",
        PrimaryTextHex = "#0F172A",
        AccentHex = "#2563EB",
        MutedTextHex = "#64748B",
        FontFamily = "Arial",
        TitleFontSize = 36.0,
        BodyFontSize = 18.0
    };

    public static PresentationTheme ClassicNavy => new()
    {
        Name = "Classic Navy",
        BackgroundHex = "#0F172A",
        SurfaceHex = "#1E293B",
        PrimaryTextHex = "#F1F5F9",
        AccentHex = "#38BDF8",
        MutedTextHex = "#94A3B8",
        FontFamily = "Calibri",
        TitleFontSize = 36.0,
        BodyFontSize = 18.0
    };

    public static PresentationTheme VibrantSunset => new()
    {
        Name = "Vibrant Sunset",
        BackgroundHex = "#180D1C",
        SurfaceHex = "#281432",
        PrimaryTextHex = "#FDF4FF",
        AccentHex = "#F43F5E",
        MutedTextHex = "#D8B4FE",
        FontFamily = "Segoe UI",
        TitleFontSize = 36.0,
        BodyFontSize = 18.0
    };

    public static PresentationTheme ForestGreen => new()
    {
        Name = "Forest Green",
        BackgroundHex = "#061A14",
        SurfaceHex = "#0C2E24",
        PrimaryTextHex = "#ECFDF5",
        AccentHex = "#10B981",
        MutedTextHex = "#6EE7B7",
        FontFamily = "Georgia",
        TitleFontSize = 36.0,
        BodyFontSize = 18.0
    };

    public static IReadOnlyList<PresentationTheme> BuiltInThemes { get; } =
    [
        DarkSlate,
        ModernMinimal,
        ClassicNavy,
        VibrantSunset,
        ForestGreen
    ];
}

