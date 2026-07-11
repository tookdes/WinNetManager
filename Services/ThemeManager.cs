using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WinNetManager.Services;

public static class ThemeManager
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const string ThemeDictTag = "WinNetManager.Theme";

    public static bool IsDark { get; private set; }

    public static event Action? ThemeChanged;

    public static void Initialize()
    {
        var settings = AppSettings.Load();
        Apply(string.Equals(settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase), save: false);
    }

    public static void Toggle() => Apply(!IsDark, save: true);

    public static void Apply(bool dark, bool save = true)
    {
        IsDark = dark;
        var app = Application.Current;
        if (app == null) return;

        var existing = app.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Contains(ThemeDictTag) ||
                                 d.Source?.OriginalString.Contains("/Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) == true ||
                                 d.Source?.OriginalString.Contains("/Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) == true ||
                                 d.Source?.OriginalString.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) == true ||
                                 d.Source?.OriginalString.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) == true);

        // 按标记或路径移除旧主题字典
        for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var d = app.Resources.MergedDictionaries[i];
            string? src = d.Source?.OriginalString;
            if (src != null && (src.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                                src.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                app.Resources.MergedDictionaries.RemoveAt(i);
            }
        }

        var theme = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        };
        theme[ThemeDictTag] = true;
        app.Resources.MergedDictionaries.Insert(0, theme);

        foreach (Window window in app.Windows)
            ApplyTitleBar(window, dark);

        if (save)
        {
            var settings = AppSettings.Load();
            settings.Theme = dark ? "Dark" : "Light";
            settings.Save();
        }

        ThemeChanged?.Invoke();
    }

    public static void ApplyTitleBar(Window window, bool? dark = null)
    {
        bool useDark = dark ?? IsDark;
        window.Background = (Brush)Application.Current.Resources["WindowBgBrush"];
        window.Foreground = (Brush)Application.Current.Resources["PrimaryTextBrush"];

        void Set()
        {
            var helper = new WindowInteropHelper(window);
            if (helper.Handle == IntPtr.Zero) return;
            int value = useDark ? 1 : 0;
            DwmSetWindowAttribute(helper.Handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        }

        if (window.IsLoaded)
            Set();
        else
            window.SourceInitialized += (_, _) => Set();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
