using System.Threading.Tasks;

namespace WinNetManager.Views;

/// <summary>
/// 标签页实现此接口后，MainWindow 可直接调用 RefreshAsync 刷新数据，
/// 无需依赖反射按方法名调用。
/// </summary>
public interface IRefreshableTab
{
    Task RefreshAsync();
}
