using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinNetManager.Services;
using WinNetManager.Views;

namespace WinNetManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常兜底：避免未处理异常直接闪退
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                CopyableMessageBox.Show($"发生未处理的异常：\n{args.Exception}", "错误", MessageBoxImage.Error);
            }
            catch { }
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                MessageBox.Show($"发生未处理的异常：\n{args.ExceptionObject}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        // 右键点击表格时同步选中被点击的行/单元格，
        // 使各标签页右键菜单的「复制选中值」复制的是右键点击的那一格
        EventManager.RegisterClassHandler(typeof(DataGrid), UIElement.PreviewMouseRightButtonDownEvent,
            new MouseButtonEventHandler((_, args) =>
            {
                var src = args.OriginalSource as DependencyObject;
                while (src != null && src is not DataGrid)
                    src = VisualTreeHelper.GetParent(src);
                if (src is DataGrid grid)
                    NetworkProfileTab.HandleRightClick(grid, args);
            }),
            handledEventsToo: true);

        ThemeManager.Initialize();
        EventManager.RegisterClassHandler(typeof(Window), Window.LoadedEvent,
            new RoutedEventHandler((_, args) =>
            {
                if (args.Source is Window w)
                    ThemeManager.ApplyTitleBar(w);
            }));
    }
}
