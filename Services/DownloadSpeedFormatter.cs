using System.Globalization;

namespace DPL.Services;

/// <summary>
/// 把字节/秒格式化为界面显示的下载速度。
/// </summary>
public static class DownloadSpeedFormatter
{
    /// <summary>
    /// 格式化下载速度；小于 1 KB/s 或无效时返回空字符串。
    /// </summary>
    /// <param name="bytesPerSecond">每秒字节数。</param>
    public static string Format(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond <= 0)
            return string.Empty;

        var kb = bytesPerSecond / 1024d;
        if (kb < 1024d)
            return kb.ToString("0.0", CultureInfo.InvariantCulture) + " KB/s";

        return (kb / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
    }
}
