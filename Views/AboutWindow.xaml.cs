using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace MultiSerialTool.Views;

/// <summary>
/// 版本（关于）窗口交互逻辑。
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DataContext = new AboutViewModel();
    }

    /// <summary>点击 GitHub 链接：用系统默认浏览器打开，异常保护</summary>
    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // 浏览器打开失败不影响版本页使用
        }

        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
