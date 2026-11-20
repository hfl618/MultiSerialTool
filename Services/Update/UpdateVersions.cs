namespace MultiSerialTool.Services.Update;

/// <summary>
/// 版本号比较：以数字段为主，带后缀的预发布版本（1.2.3-beta）低于同号正式版。
/// </summary>
public static class UpdateVersions
{
    /// <summary>解析版本号主段，允许前导 v 与 -/+ 后缀；失败返回 false。</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string core = text.Trim().TrimStart('v', 'V');
        int cut = core.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0)
            core = core[..cut];

        if (!Version.TryParse(core, out var parsed))
            return false;

        version = parsed;
        return true;
    }

    /// <summary>大于 0 表示 x 更新；任一侧无法解析时回退字符串比较，避免异常中断检查。</summary>
    public static int Compare(string? x, string? y)
    {
        if (!TryParse(x, out var vx) || !TryParse(y, out var vy))
            return string.Compare(x ?? string.Empty, y ?? string.Empty, StringComparison.Ordinal);

        int core = vx.CompareTo(vy);
        if (core != 0)
            return core;

        string px = Prerelease(x);
        string py = Prerelease(y);

        if (px.Length == 0 && py.Length == 0)
            return 0;
        if (px.Length == 0)
            return 1;
        if (py.Length == 0)
            return -1;

        return string.Compare(px, py, StringComparison.OrdinalIgnoreCase);
    }

    private static string Prerelease(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string value = text.Trim();
        int dash = value.IndexOf('-');
        return dash >= 0 && dash + 1 < value.Length ? value[(dash + 1)..] : string.Empty;
    }
}
