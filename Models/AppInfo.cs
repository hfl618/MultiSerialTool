using System.Reflection;

namespace MultiSerialTool.Models;

/// <summary>
/// 应用程序信息集中入口：统一维护版本号与命名格式 MultiSerialTool-版本号-hfl。
/// 版本号来自 csproj 的 Version（InformationalVersion），改 csproj 即可全局生效。
/// </summary>
public static class AppInfo
{
    /// <summary>应用基础名</summary>
    public const string BaseName = "MultiSerialTool";

    /// <summary>命名后缀</summary>
    public const string Suffix = "hfl";

    /// <summary>三段版本号，如 1.0.0</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>完整应用名：MultiSerialTool-版本号-hfl</summary>
    public static string FullName => $"{BaseName}-{Version}-{Suffix}";

    private static string ReadVersion()
    {
        try
        {
            var attribute = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>();

            if (!string.IsNullOrWhiteSpace(attribute?.InformationalVersion))
            {
                // InformationalVersion 可能带 "+commitsha" 后缀，只取前面的版本号
                var value = attribute!.InformationalVersion;
                var plusIndex = value.IndexOf('+');
                return plusIndex > 0 ? value[..plusIndex] : value;
            }
        }
        catch
        {
            // 读取失败回退默认版本号
        }

        return "0.0.0";
    }
}
