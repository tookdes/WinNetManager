using System.Windows;
using WinNetManager.Services;

namespace WinNetManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Initialize();
        EventManager.RegisterClassHandler(typeof(Window), Window.LoadedEvent,
            new RoutedEventHandler((_, args) =>
            {
                if (args.Source is Window w)
                    ThemeManager.ApplyTitleBar(w);
            }));
    }
}
