using System.Windows;

namespace Obsidian.Services;

/// <summary>Vertical rhythm of the UI: comfortable (default) or compact.</summary>
public enum UiDensity
{
    Comfortable = 0,
    Compact = 1,
}

/// <summary>
/// Applies the <see cref="UiDensity"/> setting by overriding the layout tokens
/// that <c>Themes/Theme.xaml</c> consumes through <c>{DynamicResource}</c>.
/// The defaults live in the style sheet (DESIGN_SYSTEM.md §3) so a page also
/// renders correctly when this service has not run yet.
/// </summary>
public static class DensityService
{
    public static void Apply(UiDensity density)
    {
        Application? application = Application.Current;
        if (application is null)
            return;

        ResourceDictionary resources = application.Resources;
        bool compact = density == UiDensity.Compact;

        resources["CardPadding"] = compact ? new Thickness(12) : new Thickness(16);
        resources["CardCornerRadius"] = new CornerRadius(4);
        resources["CardTightPadding"] = compact ? new Thickness(10) : new Thickness(12);
        resources["CardTightCornerRadius"] = new CornerRadius(3);
        resources["TableRowContentMargin"] = compact ? new Thickness(6, 0, 8, 0) : new Thickness(8, 0, 10, 0);
        resources["TableRowMargin"] = compact ? new Thickness(0) : new Thickness(0, 0, 0, 1);
        resources["PageMargin"] = compact ? new Thickness(12, 8, 12, 8) : new Thickness(16, 12, 16, 12);
        resources["NavItemPadding"] = compact ? new Thickness(8, 5, 8, 5) : new Thickness(8, 6, 8, 6);
        resources["CommandBarPadding"] = compact ? new Thickness(14, 8, 14, 8) : new Thickness(16, 10, 16, 10);
        resources["SettingsSectionSpacing"] = compact ? new Thickness(0, 8, 0, 0) : new Thickness(0, 12, 0, 0);
    }
}
