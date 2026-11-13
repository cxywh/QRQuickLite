using QRQuick.Helpers;
using QRQuick.Models;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QRQuick.Services;

/// <summary>
/// 配置持久化。文件位于 %AppData%\QRQuickLite\settings.json。
/// 读取失败、文件损坏、字段非法都不会导致程序无法启动：一律回落到默认值。
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();

    public SettingsService()
    {
        Directory = AppPaths.DataRoot;
        FilePath = Path.Combine(Directory, "settings.json");
        Current = new AppSettings();
    }

    public string Directory { get; }

    public string FilePath { get; }

    /// <summary>当前生效配置。修改后调用 <see cref="Save"/> 落盘。</summary>
    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    /// <summary>读取配置；任何异常都返回默认配置。</summary>
    public AppSettings Load()
    {
        AppSettings settings;

        try
        {
            if (!File.Exists(FilePath))
            {
                settings = new AppSettings();
                Save(settings);
                return Apply(settings);
            }

            string json = File.ReadAllText(FilePath);
            var parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            settings = Normalize(parsed ?? new AppSettings());
            LogService.Info("配置已加载");
        }
        catch (Exception ex)
        {
            LogService.Error("配置加载失败，使用默认值", ex);
            TryBackupBrokenFile();
            settings = new AppSettings();
            Save(settings);
        }

        return Apply(settings);
    }

    /// <summary>保存配置。失败时抛出 <see cref="IOException"/> 供界面提示。</summary>
    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, json);

                if (File.Exists(FilePath))
                {
                    File.Replace(temp, FilePath, null);
                }
                else
                {
                    File.Move(temp, FilePath);
                }

                Current = settings;
            }
            catch (Exception ex)
            {
                LogService.Error("配置保存失败", ex);
                throw new IOException($"无法写入配置文件（{FilePath}）：{ex.Message}", ex);
            }
        }
    }

    /// <summary>保存当前配置。</summary>
    public void SaveCurrent() => Save(Current);

    /// <summary>应用一次修改并立即保存（失败时抛出 IOException）。</summary>
    public void Update(Action<AppSettings> mutate)
    {
        var next = Current.Clone();
        mutate(next);
        Save(next);
        Changed?.Invoke(this, Current);
    }

    private AppSettings Apply(AppSettings settings)
    {
        Current = settings;
        return settings;
    }

    private static AppSettings Normalize(AppSettings s)
    {
        s.CaptureHotkey ??= HotkeyDefinition.Default;

        if (string.IsNullOrWhiteSpace(s.CaptureHotkey.Key))
        {
            s.CaptureHotkey = HotkeyDefinition.Default;
        }

        if (!Enum.IsDefined(typeof(CloseBehavior), s.CloseBehavior))
        {
            s.CloseBehavior = CloseBehavior.Exit;
        }

        return s;
    }

    private void TryBackupBrokenFile()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string backup = Path.Combine(Directory, $"settings.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Copy(FilePath, backup, true);
                LogService.Warn($"损坏的配置文件已备份到 {backup}");
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"备份损坏配置失败：{ex.Message}");
        }
    }
}
