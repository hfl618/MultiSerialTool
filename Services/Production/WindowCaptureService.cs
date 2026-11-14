using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;

namespace MultiSerialTool.Services.Production;

/// <summary>
/// 主窗口截图服务：按窗口在屏幕上的实际矩形抓取（含 DPI 缩放），
/// 保存为 PNG 到产线 images 目录，返回相对数据根目录的路径。
/// 仅用于 Windows，System.Drawing 在 Windows 桌面运行时可用。
/// </summary>
public static class WindowCaptureService
{
    /// <summary>
    /// 截取当前主窗口并保存。
    /// </summary>
    /// <param name="fileBaseName">文件名（不含扩展名）</param>
    /// <returns>相对产线根目录的图片路径（如 images/xxx.png）；失败返回 null</returns>
    public static string? CaptureMainWindow(string fileBaseName)
    {
        var window = System.Windows.Application.Current?.MainWindow;
        if (window is null || window.WindowState == WindowState.Minimized)
            return null;

        try
        {
            var source = PresentationSource.FromVisual(window);
            double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            // 窗口客户区左上角的屏幕物理像素坐标
            System.Windows.Point topLeft =
                window.PointToScreen(new System.Windows.Point(0, 0));
            int width = (int)(window.ActualWidth * scaleX);
            int height = (int)(window.ActualHeight * scaleY);

            Directory.CreateDirectory(ProductionRecordService.ImageDirectory);
            string fileName = Sanitize(fileBaseName) + ".png";
            string fullPath = Path.Combine(ProductionRecordService.ImageDirectory, fileName);

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    (int)topLeft.X, (int)topLeft.Y,
                    0, 0, new System.Drawing.Size(width, height),
                    CopyPixelOperation.SourceCopy);
            }
            bitmap.Save(fullPath, ImageFormat.Png);

            return Path.Combine("images", fileName);
        }
        catch (Exception ex)
        {
            LogService.Current.Error("Production", $"主窗口截图失败：{ex.Message}");
            return null;
        }
    }

    private static string Sanitize(string key)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            key = key.Replace(c, '_');
        return key;
    }
}
