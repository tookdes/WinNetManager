using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinNetManager.Services;
using WinNetManager.Views;

namespace WinNetManager;

public partial class App : Application
{
    private const string MutexName = @"Global\WinNetManager_SingleInstance";
    private System.Threading.Mutex? _instanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例互斥：防止两个 WinNetManager 同时运行导致重复重启网卡/改配置文件。
        // 用 initiallyOwned:false + WaitOne(0) 并捕获 AbandonedMutexException，
        // 避免"前一个实例崩溃留下被遗弃的互斥体"导致 ApplicationException 闪退。
        _instanceMutex = new System.Threading.Mutex(initiallyOwned: false, MutexName, out bool createdNew);
        if (createdNew)
        {
            try { _instanceMutex.WaitOne(0); _ownsMutex = true; }
            catch (System.Threading.AbandonedMutexException) { _ownsMutex = true; }
            catch { _ownsMutex = false; }
        }
        if (!_ownsMutex)
        {
            MessageBox.Show("WinNetManager 已在运行。\n\n为防止自动化规则冲突（例如两实例同时重启同一网卡），本程序只允许单实例。",
                "已在运行", MessageBoxButton.OK, MessageBoxImage.Information);
            try { _instanceMutex?.Dispose(); } catch { }
            _instanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 自动化引擎：与 UI 解耦，启动即初始化；全局开关已开启时直接开始调度。
        // 必须先赋 GlobalEnabled 再 Start()：TickAsync 第一句就是 if (!GlobalEnabled) return;，
        // 否则即使启动也会空转（之前只 Start() 不设开关，且「自动化监控」标签页是最后一页、
        // WPF TabControl 默认不加载未选中页，导致引擎永不休眠但永不评估）。
        WinNetManager.Services.Automation.AutomationHost.Initialize();
        var settings = AppSettings.Load();
        WinNetManager.Services.Automation.AutomationHost.Engine.GlobalEnabled = settings.AutomationGlobalEnabled;
        if (settings.AutomationGlobalEnabled)
            WinNetManager.Services.Automation.AutomationHost.Engine.Start();

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

    protected override void OnExit(ExitEventArgs e)
    {
        // 停止引擎（保存运行时状态），再释放互斥体
        try { if (WinNetManager.Services.Automation.AutomationHost.Engine != null) WinNetManager.Services.Automation.AutomationHost.Engine.Stop(); } catch { }
        if (_ownsMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { }
        }
        try { _instanceMutex?.Dispose(); } catch { }
        _instanceMutex = null;
        base.OnExit(e);
    }
}
