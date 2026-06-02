using System.Windows;
using System.Windows.Input;

namespace WinNetManager.Views;

public partial class CopyableMessageBox : Window
{
    public CopyableMessageBox(string message, string title, MessageBoxImage icon)
    {
        InitializeComponent();
        Title = title;
        TxtContent.Text = message;
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TxtContent.Text); } catch { }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    /// <summary>
    /// Shows a copyable message box. Text content can be selected and copied.
    /// </summary>
    public static void Show(string message, string title = "提示",
        MessageBoxImage icon = MessageBoxImage.Information, Window? owner = null)
    {
        var dlg = new CopyableMessageBox(message, title, icon) { Owner = owner };
        dlg.ShowDialog();
    }
}
