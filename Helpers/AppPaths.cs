using System.IO;

namespace QRQuick.Helpers;

/// <summary>
/// 数据目录解析。默认写在 %AppData%\QRQuickLite；
/// 便携模式下（exe 同目录存在 portable.dat）写到 exe 所在目录，删掉文件夹即彻底清干净。
/// 便携目录不可写时（例如被放在只读位置）自动退回 %AppData%，避免启动就失败。
/// </summary>
internal static class AppPaths
{
    private const string PortableMarker = "portable.dat";

    private static readonly Lazy<string> RootPath = new(Resolve);

    /// <summary>配置、日志、自检报告的根目录。</summary>
    public static string DataRoot => RootPath.Value;

    /// <summary>当前是否在用便携模式（exe 目录可写且有标记文件）。</summary>
    public static bool IsPortable => RootPath.Value != RoamingRoot;

    private static string RoamingRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QRQuickLite");

    private static string Resolve()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;

            if (File.Exists(Path.Combine(baseDir, PortableMarker)) && IsWritable(baseDir))
            {
                return baseDir;
            }
        }
        catch (Exception)
        {
            // 探测失败按常规模式处理
        }

        return RoamingRoot;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, $".write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
