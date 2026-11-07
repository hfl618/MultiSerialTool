using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MultiSerialTool.Models.Localization;
using MultiSerialTool.Services;

namespace MultiSerialTool;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private const string Source = "App";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 加载上次语言偏好（嵌入静态资源，缺失时回退 zh-CN）
        LocalizationService.Instance.Initialize();

        // 全局异常挂钩：UI 线程、非 UI 线程、后台 Task 三处统一落日志
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        LogService.Current.Info(Source, "Startup",
            $"程序启动，版本 {GetType().Assembly.GetName().Version}，CLR {Environment.Version}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogService.Current.Info(Source, "Shutdown", $"程序退出，退出码 {e.ApplicationExitCode}");
        base.OnExit(e);
    }

    /// <summary>UI 线程未捕获异常：记录后吞掉，避免直接闪退</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Current.Error(Source, "UnhandledException", "UI 线程未处理异常", e.Exception);
        e.Handled = true;
    }

    /// <summary>非 UI 线程致命异常</summary>
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogService.Current.Error(Source, "UnhandledException",
                $"非 UI 线程未处理异常（终止进程：{e.IsTerminating}）", ex);
    }

    /// <summary>后台 Task 未观察的异常</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogService.Current.Error(Source, "UnhandledException", "后台 Task 未观察异常", e.Exception);
        e.SetObserved();
    }
}
