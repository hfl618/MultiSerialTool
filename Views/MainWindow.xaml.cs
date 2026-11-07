using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MultiSerialTool.Models;

namespace MultiSerialTool.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 状态栏时钟定时器：与日志/测试记录同源，均取 DateTime.Now。
    /// 每秒刷新一次，显示完整日期与时、分、秒，作为测试记录时间基准。
    /// </summary>
    private readonly DispatcherTimer _clockTimer;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            Title = AppInfo.FullName;
        }
        catch
        {
            // 标题设置失败时保留 XAML 中的默认标题
        }

        // 立即刷新一次，避免启动后 1 秒内仍显示占位文本
        UpdateClock();

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();

        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            // 释放常驻协议页的热插拔监听等资源
            if (DataContext is IDisposable disposable)
                disposable.Dispose();
        };
    }

    /// <summary>
    /// 更新状态栏时间。格式与日志时间戳保持同一时间源（本机系统时间 DateTime.Now），
    /// 状态栏显示值即可直接作为测试记录时间引用。
    /// </summary>
    private void UpdateClock()
    {
        ClockItem.Content = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }
}