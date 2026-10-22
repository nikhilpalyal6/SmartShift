using Microsoft.Win32;
using System.Windows;

namespace SmartShift.App.Ui;

internal static class ThemeLoader
{
    public static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int number)
            {
                return number != 0;
            }
        }
        catch
        {
            // Fall back to light.
        }

        return true;
    }

    public static void Apply(Application app)
    {
        if (!AppsUseLightTheme())
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("Themes/AppTheme.Dark.xaml", UriKind.Relative)
            });
        }
    }
}
