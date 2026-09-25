using System.Windows;

namespace LBAAssembler;

// The available themes, in the order they should be offered in the UI: default first, then the
// accessibility-motivated ones grouped by what each solves (low light / eye strain, then low vision /
// contrast sensitivity in both directions, then colour vision deficiency).
public enum AppTheme
{
    Light,
    Dark,
    HighContrastDark,
    HighContrastLight,
    ColorBlindSafe,
}

// Swaps the active theme's colour ResourceDictionary (Themes/*.xaml) into Application.Resources, live,
// with no restart needed -- every consumer (Theme.xaml's shared Styles/ControlTemplates, and every window
// converted to use them) references a theme colour with {DynamicResource ...}, not {StaticResource ...},
// specifically so this works: DynamicResource re-resolves whenever the dictionary backing it changes,
// StaticResource only resolves once at load time. A few things aren't reachable through WPF resources at
// all (ZoneStyle draws zone-type colours onto a canvas from a plain C# array, not a styled control) --
// those are updated here directly, and Changed lets interested code (MainWindow's own overlay redraw)
// repaint whatever it already drew in the old colours.
internal static class ThemeManager
{
    private static ResourceDictionary? active;

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static event Action? Changed;

    public static void Apply(AppTheme theme)
    {
        var dictionary = new ResourceDictionary { Source = new Uri($"Themes/{FileName(theme)}.xaml", UriKind.Relative) };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (active is not null) merged.Remove(active);
        merged.Add(dictionary);
        active = dictionary;
        Current = theme;

        ZoneStyle.ApplyTheme(theme);
        Changed?.Invoke();
    }

    private static string FileName(AppTheme theme) => theme switch
    {
        AppTheme.Light => "Light",
        AppTheme.Dark => "Dark",
        AppTheme.HighContrastDark => "HighContrastDark",
        AppTheme.HighContrastLight => "HighContrastLight",
        AppTheme.ColorBlindSafe => "ColorBlindSafe",
        _ => "Light",
    };

    // The label shown in Settings and the display name persisted to settings.json (EditorSettings.Theme) --
    // kept as names rather than the enum's own ToString() so a future rename of the enum member doesn't
    // silently change what's stored/shown.
    public static string DisplayName(AppTheme theme) => theme switch
    {
        AppTheme.Light => "Light (original)",
        AppTheme.Dark => "Dark",
        AppTheme.HighContrastDark => "High contrast (dark)",
        AppTheme.HighContrastLight => "High contrast (light)",
        AppTheme.ColorBlindSafe => "Colour-blind safe",
        _ => "Light",
    };

    public static AppTheme Parse(string? name) => name switch
    {
        "Dark" => AppTheme.Dark,
        "HighContrastDark" => AppTheme.HighContrastDark,
        "HighContrastLight" => AppTheme.HighContrastLight,
        "ColorBlindSafe" => AppTheme.ColorBlindSafe,
        _ => AppTheme.Light,
    };

    public static string Save(AppTheme theme) => theme.ToString();
}
