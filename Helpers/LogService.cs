using System.IO;
using System.Text;

namespace QRQuick.Helpers;

/// <summary>
/// 极简错误日志。只记录异常与状态，**不记录二维码内容或图片数据**。
/// 单个文件上限 512 KB，超出后滚动为 .1 备份。
/// </summary>
internal static class LogService
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(AppPaths.DataRoot, "logs");

    private static readonly string LogPath = Path.Combine(LogDirectory, "app.log");
    private const long MaxBytes = 512 * 1024;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
    {
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = new FileInfo(LogPath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    string backup = LogPath + ".1";
                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }

                    File.Move(LogPath, backup);
                }

                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败绝不影响主流程
        }
    }
}
