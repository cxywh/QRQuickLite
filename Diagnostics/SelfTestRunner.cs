using QRQuick.Helpers;
using QRQuick.Models;
using QRQuick.Services;
using QRQuick.Views;
using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QRQuick.Diagnostics;

/// <summary>
/// 自检：在真实桌面上验证截图取色、坐标、二维码识别、URL 校验、配置读写、热键注册。
/// 结果同时写入文本报告（WinExe 没有控制台），退出码 0 表示全部通过。
/// </summary>
internal static class SelfTestRunner
{
    private static readonly StringBuilder Report = new();
    private static readonly StringBuilder MachineReadable = new();
    private static int _failed;
    private static int _passed;
    private static string _outputDirectory = string.Empty;

    public static async Task<int> RunAsync(string[] args)
    {
        _outputDirectory = Path.Combine(
            Helpers.AppPaths.DataRoot,
            "selftest", DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        Directory.CreateDirectory(_outputDirectory);

        Section("QR Quick Lite 自检");
        Line($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"系统：{Environment.OSVersion} / .NET {Environment.Version} / {(Environment.Is64BitProcess ? "x64" : "x86")}");
        Line($"输出目录：{_outputDirectory}");

        try
        {
            TestEnvironment();
            await TestCapturePipelineAsync();
            await TestDecodingAsync();
            TestUrlValidation();
            TestSettingsRoundTrip();
            TestHotkey();
            TestCoordinateMath();
            TestExplorerMenu();
            await TestOtherSymbologiesAsync();

            if (args.Any(a => a.Equals("--e2e", StringComparison.OrdinalIgnoreCase)))
            {
                await TestInteractionEndToEndAsync();
            }

            TestAutoOpenPolicy();
            TestOverlayCoverage();
        }
        catch (Exception ex)
        {
            Fail("自检执行异常", $"{ex.GetType().Name}: {ex.Message}");
            LogService.Error("自检异常", ex);
        }

        Section("汇总");
        Line($"通过 {_passed} 项，失败 {_failed} 项");

        string reportPath = Path.Combine(_outputDirectory, "report.txt");
        File.WriteAllText(reportPath, Report.ToString(), Encoding.UTF8);
        File.WriteAllText(Path.Combine(_outputDirectory, "result.tsv"), MachineReadable.ToString(), Encoding.UTF8);

        return _failed == 0 ? 0 : 1;
    }

    // ---------------- 环境 ----------------

    private static void TestEnvironment()
    {
        Section("一、环境与显示器");

        var monitors = ScreenCoordinateHelper.GetMonitors();
        Check("枚举到至少一个显示器", monitors.Count > 0, $"共 {monitors.Count} 个");

        foreach (MonitorInfo m in monitors)
        {
            Line($"  · {m}");
        }

        Int32Rect vs = ScreenCoordinateHelper.GetVirtualScreenRect();
        Line($"  · 虚拟桌面 [{vs.X},{vs.Y} {vs.Width}x{vs.Height}]");

        bool covers = monitors.All(m =>
            vs.X <= m.Left && vs.Y <= m.Top &&
            vs.X + vs.Width >= m.Right && vs.Y + vs.Height >= m.Bottom);

        Check("虚拟桌面覆盖全部显示器（含负坐标）", covers, covers ? "一致" : "存在未被覆盖的显示器");

        double scale = (monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0]).Scale;
        Check("主显示器 DPI 可用", scale > 0.5 && scale < 5, $"缩放 {scale * 100:0}%");

        if (monitors.Count > 1)
        {
            bool mixed = monitors.Select(m => m.Dpi).Distinct().Count() > 1;
            Line($"  · 多显示器 DPI 混合：{(mixed ? "是" : "否")}");
        }
    }

    // ---------------- 截图 ----------------

    private static async Task TestCapturePipelineAsync()
    {
        Section("二、截图取色与【遮罩不入镜】");

        var monitors = ScreenCoordinateHelper.GetMonitors();
        if (monitors.Count == 0)
        {
            Fail("截图测试", "没有可用显示器");
            return;
        }

        MonitorInfo primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

        const int width = 400;
        const int height = 300;
        int x = primary.Left + primary.Width / 2 - width / 2;
        int y = primary.Top + primary.Height / 2 - height / 2;
        var panelRect = new Int32Rect(x, y, width, height);

        var color = Color.FromRgb(0xFF, 0x00, 0xFF);
        var panel = new SolidPanelWindow(color);
        var service = new ScreenCaptureService();
        CaptureOverlayWindow? overlay = null;

        try
        {
            panel.ShowAt(panelRect);
            await Task.Delay(220);

            // 1) 干净截图：颜色应与面板完全一致
            BitmapSource clean = service.CaptureRegion(panelRect);
            SavePng(clean, "capture-1-clean.png");

            var cleanPixel = PixelAt(clean, width / 2, height / 2);
            Check("截图尺寸与选区一致",
                clean.PixelWidth == width && clean.PixelHeight == height,
                $"{clean.PixelWidth}x{clean.PixelHeight}");

            Check("截图取色准确（无遮罩）",
                Near(cleanPixel, color, 2),
                $"中心像素 BGR=({cleanPixel.B},{cleanPixel.G},{cleanPixel.R}) 期望 (255,0,255)");

            // 2) 遮罩可见时截图会被染色：证明“不隐藏就截图”确实会污染结果
            overlay = new CaptureOverlayWindow(primary)
            {
                Left = primary.Left,
                Top = primary.Top,
                Width = primary.Width,
                Height = primary.Height,
                Opacity = 1
            };
            overlay.Show();
            overlay.ApplyGeometry();
            overlay.SetCrosshair(new Point(width / 2.0 + (x - primary.Left), height / 2.0 + (y - primary.Top)));
            await Task.Delay(260);

            BitmapSource withOverlay = service.CaptureRegion(panelRect);
            SavePng(withOverlay, "capture-2-overlay-visible.png");

            var tinted = PixelAt(withOverlay, width / 2, height / 2);
            Check("遮罩可见时截图确实被染色（说明隐藏步骤必要）",
                !Near(tinted, color, 3),
                $"遮罩下像素 BGR=({tinted.B},{tinted.G},{tinted.R})");

            // 3) 隐藏遮罩后按正式流程等待合成，再截图：必须回到原始颜色
            overlay.HideForCapture();
            await service.WaitForUiHiddenAsync();

            BitmapSource afterHide = service.CaptureRegion(panelRect);
            SavePng(afterHide, "capture-3-after-hide.png");

            var restored = PixelAt(afterHide, width / 2, height / 2);
            Check("隐藏遮罩后截图恢复原始像素（遮罩/十字线未入镜）",
                Near(restored, color, 2),
                $"像素 BGR=({restored.B},{restored.G},{restored.R})");
        }
        catch (Exception ex)
        {
            Fail("截图流程测试", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            overlay?.Close();
            panel.Close();
            await Task.Delay(120);
        }
    }

    // ---------------- 识别 ----------------

    private static async Task TestDecodingAsync()
    {
        Section("三、二维码识别（真实编码器生成的样本）");

        string fixtureDirectory = Path.Combine(_outputDirectory, "fixtures");
        List<FixtureBuilder.Fixture> fixtures;

        try
        {
            fixtures = FixtureBuilder.BuildAll(fixtureDirectory);
        }
        catch (Exception ex)
        {
            Fail("生成测试图片", $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        Line($"生成样本 {fixtures.Count} 个：{fixtureDirectory}");

        var qrService = new QRCodeService();

        foreach (FixtureBuilder.Fixture fixture in fixtures)
        {
            try
            {
                var start = DateTime.Now;
                BitmapSource image = LoadImageFile(fixture.Path);
                IReadOnlyList<QRResult> results = await qrService.DecodeAsync(image, QRSource.ScreenCapture);
                double ms = (DateTime.Now - start).TotalMilliseconds;

                bool countOk = results.Count == fixture.ExpectedCount;

                bool textOk;
                if (fixture.ExpectEmpty)
                {
                    textOk = results.Count == 0;
                }
                else
                {
                    string[] expected = fixture.ExpectedText.Split('|');
                    textOk = expected.All(e => results.Any(r => r.Text == e));
                }

                Check($"识别 {fixture.Name}",
                    countOk && textOk,
                    $"{results.Count} 个结果 / {ms:0} ms" +
                    (countOk && textOk
                        ? string.Empty
                        : $" / 实际：{(results.Count == 0 ? "无" : string.Join(" ¦ ", results.Select(r => r.Text)))}"));
            }
            catch (Exception ex)
            {
                Fail($"识别 {fixture.Name}", $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>自检内部读样本图：WIC 解码，读完全部进内存，随后文件可删。</summary>
    private static BitmapSource LoadImageFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];

        if (frame.CanFreeze)
        {
            frame.Freeze();
        }

        return frame;
    }

    // ---------------- 其它码制（两段式兜底） ----------------

    /// <summary>
    /// 兜底通道的实测：13 个格式逐个「编码 → 服务解码 → 校验内容与码制」，
    /// 再用一张无码的界面图确认不会误报。后者比前者更重要——误报会把用户坑得莫名其妙。
    /// </summary>
    private static async Task TestOtherSymbologiesAsync()
    {
        Section("十一、其它码制与误报控制");

        var service = new QRCodeService();

        var cases = new (string Name, ZXing.BarcodeFormat Format, string Content, string Expect)[]
        {
            ("QR Code",     ZXing.BarcodeFormat.QR_CODE,     "https://example.com/qr",  "https://example.com/qr"),
            ("Aztec",       ZXing.BarcodeFormat.AZTEC,       "AZTEC-2026",              "AZTEC-2026"),
            ("Data Matrix", ZXing.BarcodeFormat.DATA_MATRIX, "DM-2026",                 "DM-2026"),
            ("PDF417",      ZXing.BarcodeFormat.PDF_417,     "PDF417-2026",             "PDF417-2026"),
            ("EAN-13",      ZXing.BarcodeFormat.EAN_13,      "978712123456",            "9787121234569"),
            ("EAN-8",       ZXing.BarcodeFormat.EAN_8,       "1234567",                 "12345670"),
            ("UPC-A",       ZXing.BarcodeFormat.UPC_A,       "12345678901",             "123456789012"),
            ("ITF",         ZXing.BarcodeFormat.ITF,         "123456",                  "123456"),
            ("Code 128",    ZXing.BarcodeFormat.CODE_128,    "CODE128-2026",            "CODE128-2026"),
            ("Code 93",     ZXing.BarcodeFormat.CODE_93,     "CODE93-2026",             "CODE93-2026"),
            ("Code 39",     ZXing.BarcodeFormat.CODE_39,     "CODE39-2026",             "CODE39-2026"),
            ("Codabar",     ZXing.BarcodeFormat.CODABAR,     "123456789",               "123456789")
        };

        foreach ((string name, ZXing.BarcodeFormat format, string content, string expect) in cases)
        {
            BitmapSource image;

            try
            {
                int width = format == ZXing.BarcodeFormat.EAN_13 || format == ZXing.BarcodeFormat.CODE_128 ? 520 : 360;
                int height = format == ZXing.BarcodeFormat.EAN_13 || format == ZXing.BarcodeFormat.CODE_128 ? 200 : 360;

                ZXing.Common.BitMatrix matrix = new ZXing.MultiFormatWriter().encode(content, format, width, height);
                image = MatrixToBitmap(matrix, 24);
            }
            catch (Exception ex)
            {
                Fail($"生成 {name} 样本", ex.Message);
                continue;
            }

            IReadOnlyList<QRResult> results = await service.DecodeAsync(image, QRSource.ScreenCapture);

            if (results.Count == 0)
            {
                Fail($"识别 {name}", "没有解出任何结果");
                continue;
            }

            QRResult first = results[0];
            Check($"识别 {name}", first.Text == expect, $"期望“{expect}”，实际“{first.Text}”");
            Check($"{name} 码制标注", first.Format == name, $"实际“{first.Format}”");
        }

        // UPC-E 会被补校验位，单独按前缀断言
        try
        {
            ZXing.Common.BitMatrix matrix = new ZXing.MultiFormatWriter()
                .encode("1234567", ZXing.BarcodeFormat.UPC_E, 360, 200);
            IReadOnlyList<QRResult> upce = await service.DecodeAsync(MatrixToBitmap(matrix, 24), QRSource.ScreenCapture);

            Check("识别 UPC-E",
                upce.Count > 0 && upce[0].Text.StartsWith("1234567", StringComparison.Ordinal) && upce[0].Format == "UPC-E",
                upce.Count == 0 ? "没有解出任何结果" : $"实际“{upce[0].Text}”/“{upce[0].Format}”");
        }
        catch (Exception ex)
        {
            Fail("识别 UPC-E", ex.Message);
        }

        // 误报控制：一张只有文字状纹理的界面图，必须 0 结果
        var falsePositives = new List<string>();

        for (int i = 0; i < 3; i++)
        {
            BitmapSource busy = BuildBusyUiImage(480, 300, seed: 20261112 + i);
            IReadOnlyList<QRResult> none = await service.DecodeAsync(busy, QRSource.ScreenCapture);

            if (none.Count > 0)
            {
                falsePositives.Add($"{none[0].Format}:{none[0].Text}");
            }
        }

        Check("无码界面不误报", falsePositives.Count == 0,
            falsePositives.Count == 0 ? "连续 3 次均为 0 结果" : string.Join(" / ", falsePositives));
    }

    /// <summary>把 ZXing 位图转成 WPF 位图，四周补白边（条码解码需要静区）。</summary>
    private static BitmapSource MatrixToBitmap(ZXing.Common.BitMatrix matrix, int quiet)
    {
        int width = matrix.Width + quiet * 2;
        int height = matrix.Height + quiet * 2;
        byte[] pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);

        for (int y = 0; y < matrix.Height; y++)
        {
            for (int x = 0; x < matrix.Width; x++)
            {
                if (!matrix[x, y])
                {
                    continue;
                }

                int index = ((y + quiet) * width + x + quiet) * 4;
                pixels[index] = 0;
                pixels[index + 1] = 0;
                pixels[index + 2] = 0;
            }
        }

        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    /// <summary>合成"界面截图"：浅底 + 大量文字状色块，用于验证不会把杂纹理认成条码。</summary>
    private static BitmapSource BuildBusyUiImage(int width, int height, int seed)
    {
        byte[] pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)240);

        var random = new Random(seed);

        for (int line = 0; line * 14 + 12 < height; line++)
        {
            int y = 8 + line * 14;
            int x = 10;

            while (x < width - 20)
            {
                int runLength = random.Next(4, 26);
                bool dark = random.Next(0, 3) > 0;

                for (int dy = 0; dy < 8; dy++)
                {
                    for (int dx = 0; dx < runLength && x + dx < width - 6; dx++)
                    {
                        int index = ((y + dy) * width + x + dx) * 4;
                        byte value = dark ? (byte)55 : (byte)246;
                        pixels[index] = value;
                        pixels[index + 1] = value;
                        pixels[index + 2] = value;
                        pixels[index + 3] = 255;
                    }
                }

                x += runLength + random.Next(3, 14);
            }
        }

        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    // ---------------- 资源管理器右键菜单 ----------------

    private static void TestExplorerMenu()
    {
        Section("七、资源管理器右键菜单");

        var service = new ExplorerContextMenuService();
        bool wasInstalled = service.IsInstalled();
        string? previousCommand = service.GetInstalledCommand();
        string probeExe = Environment.ProcessPath ?? @"C:\Windows\notepad.exe";

        try
        {
            var (installed, installError) = service.Install(probeExe);
            Check("写入四处右键菜单", installed, installed ? "文件 / 文件夹 / 桌面空白处" : installError);
            Check("菜单项存在", service.IsInstalled(), $"IsInstalled={service.IsInstalled()}");

            string? command = service.GetInstalledCommand();
            Check("命令行带 --capture 参数",
                command is not null && command.Contains("--capture", StringComparison.Ordinal),
                command ?? "(null)");

            Check("命令行指向当前 exe",
                command is not null && command.Contains(probeExe, StringComparison.OrdinalIgnoreCase),
                command ?? "(null)");

            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\QRQuickLiteCapture"))
            {
                Check("菜单文字为“二维码识别”",
                    key?.GetValue("MUIVerb") as string == "二维码识别",
                    key?.GetValue("MUIVerb") as string ?? "(null)");

                Check("菜单图标指向 exe",
                    key?.GetValue("Icon") is string icon && icon.Length > 0,
                    key?.GetValue("Icon") as string ?? "(null)");
            }

            var (removed, removeError) = service.Uninstall();
            Check("移除右键菜单", removed, removed ? "已移除" : removeError);
            Check("菜单项已删除", !service.IsInstalled(), $"IsInstalled={service.IsInstalled()}");
        }
        finally
        {
            RestoreContextMenu(service, wasInstalled, previousCommand, probeExe);
        }
    }

    /// <summary>自检动过注册表，必须还原用户原来的状态。</summary>
    private static void RestoreContextMenu(ExplorerContextMenuService service, bool wasInstalled,
        string? previousCommand, string fallbackExe)
    {
        try
        {
            if (!wasInstalled)
            {
                service.Uninstall();
                return;
            }

            service.Install(ExtractExe(previousCommand) ?? fallbackExe);
        }
        catch (Exception ex)
        {
            LogService.Warn($"恢复右键菜单失败：{ex.Message}");
        }
    }

    private static string? ExtractExe(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        int first = command.IndexOf('"');
        int second = command.IndexOf('"', first + 1);
        if (first >= 0 && second > first)
        {
            return command.Substring(first + 1, second - first - 1);
        }

        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    // ---------------- URL ----------------
    private static void TestUrlValidation()
    {
        Section("四、URL 安全校验");

        (string Input, bool Expected)[] cases =
        {
            ("https://example.com", true),
            ("http://example.com/path?a=1&b=2", true),
            ("HTTPS://EXAMPLE.COM", true),
            ("  https://example.com/trim  ", true),
            ("example.com", false),
            ("www.example.com", false),
            ("file:///C:/Windows/System32", false),
            ("javascript:alert(1)", false),
            ("cmd://calc", false),
            ("powershell://-c+whoami", false),
            ("ftp://example.com", false),
            ("mailto:a@b.com", false),
            ("https://", false),
            ("", false),
            ("   ", false),
            ("https://exam ple.com", false),
            ("https://example.com/\r\nX: y", false),
            ("WIFI:S:HomeNet;T:WPA;P:12345678;;", false),
            ("普通文本内容", false),
            ("https://例子.测试/路径", true)
        };

        foreach ((string input, bool expected) in cases)
        {
            bool actual = UrlValidator.TryNormalize(input, out string? normalized);
            Check($"校验 {(input.Length == 0 ? "<空>" : input.Length > 34 ? input[..34] + "…" : input)}",
                actual == expected,
                actual ? $"允许 → {normalized}" : "已拒绝");
        }
    }

    // ---------------- 配置 ----------------

    private static void TestSettingsRoundTrip()
    {
        Section("五、配置读写与损坏恢复");

        var service = new SettingsService();
        string path = service.FilePath;
        string backup = path + ".selftest-backup";
        bool hadOriginal = File.Exists(path);

        // 记录测试前已存在的损坏备份，测试结束后只清理本次新增的
        HashSet<string> brokenBefore = Directory.Exists(service.Directory)
            ? Directory.GetFiles(service.Directory, "settings.broken-*.json").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (hadOriginal)
            {
                File.Copy(path, backup, true);
            }

            // 默认值
            AppSettings defaults = service.Load();
            Check("首次加载使用默认值（自动打开浏览器关闭）",
                !defaults.AutoOpenBrowser, $"AutoOpenBrowser={defaults.AutoOpenBrowser}");

            // 往返
            var custom = defaults.Clone();
            custom.AutoOpenBrowser = true;
            custom.CloseBehavior = CloseBehavior.MinimizeToTray;
            custom.CaptureHotkey = new HotkeyDefinition { Modifiers = "Ctrl,Alt", Key = "F9" };
            service.Save(custom);

            AppSettings reloaded = new SettingsService().Load();
            Check("保存后重新加载一致",
                reloaded.AutoOpenBrowser &&
                reloaded.CloseBehavior == CloseBehavior.MinimizeToTray &&
                reloaded.CaptureHotkey.Key == "F9",
                $"AutoOpen={reloaded.AutoOpenBrowser}, Close={reloaded.CloseBehavior}, Key={reloaded.CaptureHotkey.Key}");

            // 损坏文件
            File.WriteAllText(path, "{ this is not valid json ][");
            AppSettings recovered = new SettingsService().Load();
            bool backupCreated = Directory.GetFiles(service.Directory, "settings.broken-*.json").Length > 0;

            Check("配置损坏时回落默认值且不抛异常",
                !recovered.AutoOpenBrowser && recovered.CaptureHotkey.Key == "Q",
                $"AutoOpen={recovered.AutoOpenBrowser}, Key={recovered.CaptureHotkey.Key}");

            Check("损坏的配置已备份", backupCreated, backupCreated ? "已生成 settings.broken-*.json" : "未找到备份");

            // 默认快捷键解析
            bool parseOk = HotkeyConverter.TryParse(HotkeyDefinition.Default, out uint mods, out uint vk, out _);
            Check("默认快捷键 Alt+Q 可解析",
                parseOk && mods == 0x0001 && vk == 0x51,
                $"mods=0x{mods:X}, vk=0x{vk:X}");
        }
        catch (Exception ex)
        {
            Fail("配置测试", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (hadOriginal && File.Exists(backup))
                {
                    File.Copy(backup, path, true);
                    File.Delete(backup);
                }
                else if (!hadOriginal && File.Exists(path))
                {
                    // 原本没有配置文件：删除测试写入的损坏内容，让程序下次启动重新生成默认配置
                    File.Delete(path);
                }

                // 清理本次测试产生的损坏备份
                foreach (string file in Directory.GetFiles(service.Directory, "settings.broken-*.json"))
                {
                    if (!brokenBefore.Contains(file))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"自检恢复配置失败：{ex.Message}");
            }
        }
    }

    // ---------------- 热键 ----------------

    private static void TestHotkey()
    {
        Section("六、全局快捷键注册");

        AppMessageWindow? sink = null;
        HotkeyService? hotkey = null;

        try
        {
            sink = new AppMessageWindow();
            hotkey = new HotkeyService(sink);

            var testDefinition = new HotkeyDefinition { Modifiers = "Ctrl,Alt", Key = "F9" };
            var (ok, error) = hotkey.Register(testDefinition);
            Check("注册 Ctrl+Alt+F9", ok, ok ? "成功" : error);

            var (reuseOk, reuseError) = hotkey.Register(testDefinition);
            Check("重复注册同一组合不抛异常", reuseOk, reuseOk ? "成功（幂等）" : reuseError);

            hotkey.Unregister();
            Check("注销后状态为未注册", !hotkey.IsRegistered, $"IsRegistered={hotkey.IsRegistered}");

            var invalid = new HotkeyDefinition { Modifiers = "", Key = "Q" };
            var (invalidOk, invalidError) = hotkey.Register(invalid);
            Check("无修饰键的组合被拒绝", !invalidOk, invalidError);

            var reserved = new HotkeyDefinition { Modifiers = "Alt", Key = "F12" };
            var (f12Ok, f12Error) = hotkey.Register(reserved);
            Check("F12 被拒绝", !f12Ok, f12Error);
        }
        catch (Exception ex)
        {
            Fail("热键测试", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            hotkey?.Dispose();
            sink?.Dispose();
        }
    }

    // ---------------- 坐标换算逻辑（模拟多显示器布局） ----------------

    private static void TestCoordinateMath()
    {
        Section("七、坐标换算逻辑（模拟多显示器布局）");

        // 模拟布局：主屏 2560x1600@150%，左屏 1920x1080@100%（负坐标），上屏 1280x720@125%
        var primary = new MonitorInfo
        {
            Handle = new IntPtr(1), Left = 0, Top = 0, Width = 2560, Height = 1600,
            Dpi = 144, IsPrimary = true, DeviceName = "SIM-PRIMARY"
        };

        var left = new MonitorInfo
        {
            Handle = new IntPtr(2), Left = -1920, Top = 0, Width = 1920, Height = 1080,
            Dpi = 96, IsPrimary = false, DeviceName = "SIM-LEFT"
        };

        var above = new MonitorInfo
        {
            Handle = new IntPtr(3), Left = 0, Top = -720, Width = 1280, Height = 720,
            Dpi = 120, IsPrimary = false, DeviceName = "SIM-ABOVE"
        };

        // 1) 四个方向拖动应得到同一个规范化矩形
        Int32Rect a = ScreenCoordinateHelper.NormalizeRect(100, 200, 400, 500);
        Int32Rect b = ScreenCoordinateHelper.NormalizeRect(400, 500, 100, 200);
        Int32Rect c = ScreenCoordinateHelper.NormalizeRect(400, 200, 100, 500);
        Int32Rect d = ScreenCoordinateHelper.NormalizeRect(100, 500, 400, 200);

        Check("四个拖动方向得到相同选区",
            a == b && b == c && c == d && a.X == 100 && a.Y == 200 && a.Width == 300 && a.Height == 300,
            $"左上→右下={a.X},{a.Y} {a.Width}x{a.Height}；右下→左上={b.X},{b.Y} {b.Width}x{b.Height}");

        // 2) 跨屏选区在主屏上的裁切
        var spanning = new Int32Rect(-400, 100, 1200, 800);
        Int32Rect onPrimary = ScreenCoordinateHelper.Intersect(spanning, primary);
        Check("跨屏选区在主屏的裁切结果正确",
            onPrimary.X == 0 && onPrimary.Y == 100 && onPrimary.Width == 800 && onPrimary.Height == 800,
            $"{onPrimary.X},{onPrimary.Y} {onPrimary.Width}x{onPrimary.Height}（期望 0,100 800x800）");

        // 3) 同一个跨屏选区在负坐标显示器上的裁切结果
        Int32Rect onLeft = ScreenCoordinateHelper.Intersect(spanning, left);
        Check("跨屏选区在负坐标显示器上的裁切结果正确",
            onLeft.X == -400 && onLeft.Y == 100 && onLeft.Width == 400 && onLeft.Height == 800,
            $"{onLeft.X},{onLeft.Y} {onLeft.Width}x{onLeft.Height}（期望 -400,100 400x800）");

        // 4) 与显示器完全不重叠时返回空矩形（不会画出错误选区）
        Int32Rect none = ScreenCoordinateHelper.Intersect(spanning, above);
        Check("与显示器无交集时返回空矩形", none.Width == 0 && none.Height == 0,
            $"{none.Width}x{none.Height}");

        // 5) 遮罩窗口的本地坐标换算（虚拟物理坐标 - 显示器原点）
        var overlayLocal = new Int32Rect(onLeft.X - left.Left, onLeft.Y - left.Top, onLeft.Width, onLeft.Height);
        Check("负坐标显示器上的本地坐标换算正确",
            overlayLocal.X == 1520 && overlayLocal.Y == 100 && overlayLocal.Width == 400,
            $"{overlayLocal.X},{overlayLocal.Y} {overlayLocal.Width}x{overlayLocal.Height}（期望 1520,100）");

        // 6) 命中测试边界（左闭右开，避免两屏交界处重复命中）
        Check("显示器命中测试在右/下边界不重复命中",
            primary.Contains(0, 0) &&
            primary.Contains(2559, 1599) &&
            !primary.Contains(2560, 0) &&
            !left.Contains(0, 0),
            "边界判定符合左闭右开");

        // 7) DPI 缩放换算
        Check("DPI 缩放值正确",
            Math.Abs(primary.Scale - 1.5) < 0.001 &&
            Math.Abs(left.Scale - 1.0) < 0.001 &&
            Math.Abs(above.Scale - 1.25) < 0.001,
            $"150%={primary.Scale}, 100%={left.Scale}, 125%={above.Scale}");
    }

    // ---------------- 端到端交互 ----------------

    private static async Task TestInteractionEndToEndAsync()
    {
        Section("八、截图交互端到端（合成输入驱动真实窗口）");

        string text = "https://www.example.com/e2e-check";
        BitmapSource qr = FixtureBuilder.MakeQr(text, 300);

        var monitors = ScreenCoordinateHelper.GetMonitors();
        MonitorInfo primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];

        const int size = 360;
        int x = primary.Left + 120;
        int y = primary.Top + 120;
        var qrRect = new Int32Rect(x, y, size, size);

        var panel = new QrPanelWindow(qr);
        var controller = new CaptureController(new ScreenCaptureService(), new QRCodeService());
        CaptureCompletedEventArgs? completed = null;
        controller.Completed += (_, e) => completed = e;

        try
        {
            panel.ShowAt(qrRect);
            await Task.Delay(300);

            // --- 1. 启动截图模式（同时测量呼出耗时） ---
            var startupWatch = System.Diagnostics.Stopwatch.StartNew();
            bool started = controller.Start();

            while (controller.State != CaptureState.CaptureReady && startupWatch.ElapsedMilliseconds < 3000)
            {
                await Task.Delay(10);
            }

            startupWatch.Stop();

            Check("进入截图模式", started && controller.State == CaptureState.CaptureReady,
                $"started={started}, state={controller.State}");
            Line($"  · 呼出遮罩耗时 {startupWatch.ElapsedMilliseconds} ms（{monitors.Count} 个显示器）");

            // --- 2. 左键拖动建立选区（跨过二维码区域） ---
            SyntheticInput.MoveTo(x + 30, y + 30);
            await Task.Delay(80);
            SyntheticInput.LeftDown();
            await Task.Delay(60);

            for (int i = 1; i <= 8; i++)
            {
                SyntheticInput.MoveTo(x + 30 + (size - 60) * i / 8, y + 30 + (size - 60) * i / 8);
                await Task.Delay(35);
            }

            SyntheticInput.LeftUp();
            await Task.Delay(220);

            Check("松开左键后选区固定为 SelectionConfirmed（未立即识别）",
                controller.State == CaptureState.SelectionConfirmed,
                $"state={controller.State}");

            Int32Rect selection = controller.CurrentSelection;
            Check("选区尺寸合理",
                selection.Width is > 150 and < 500 && selection.Height is > 150 and < 500,
                $"{selection.X},{selection.Y} {selection.Width}x{selection.Height}");

            // --- 3. 右键菜单 ---
            SyntheticInput.RightClick();
            await Task.Delay(220);
            Check("右键打开操作菜单", controller.State == CaptureState.ContextMenu, $"state={controller.State}");

            // --- 4. Enter 执行识别 ---
            SyntheticInput.PressKey(0x0D);
            await Task.Delay(1200);

            Check("识别结果返回", completed is not null && completed.Error is null,
                completed is null ? "未收到完成事件" : completed.Error ?? "正常");

            if (completed is not null && completed.Results.Count > 0)
            {
                bool matched = completed.Results.Any(r => r.Text == text);
                Check("识别内容与二维码一致", matched, completed.Results[0].Text);
                Check("识别结果标记为合法 URL", completed.Results[0].IsValidUrl, completed.Results[0].ValidUrl ?? "无");
            }
            else
            {
                Fail("识别内容与二维码一致", "结果为空");
            }

            SavePngIfNotNull(completed?.Image, "e2e-captured-region.png");

            // --- 5. ESC 全流程取消 ---
            completed = null;
            controller.Start();
            await Task.Delay(300);
            SyntheticInput.PressKey(0x1B);
            await Task.Delay(400);

            Check("ESC 取消整个截图流程",
                completed is { Cancelled: true } && controller.State == CaptureState.Idle,
                $"cancelled={completed?.Cancelled}, state={controller.State}");
        }
        catch (Exception ex)
        {
            Fail("端到端交互测试", $"{ex.GetType().Name}: {ex.Message}");
            LogService.Error("端到端交互测试异常", ex);
        }
        finally
        {
            controller.Dispose();
            panel.Close();
            await Task.Delay(150);
        }
    }

    // ---------------- 遮罩覆盖不变量 ----------------

    private static void TestOverlayCoverage()
    {
        Section("十、遮罩覆盖不变量（防「遮罩未覆盖全屏」回归）");

        var monitor = ScreenCoordinateHelper.GetMonitors().First(m => m.IsPrimary);
        var overlay = new Views.CaptureOverlayWindow(monitor);
        try
        {
            overlay.Show();
            overlay.ApplyGeometry();
            overlay.UpdateLayout();

            double rootW = overlay.Root.ActualWidth;
            double rootH = overlay.Root.ActualHeight;
            double pxPerDip = overlay.PixelPerDip;
            double layerPxW = overlay.Layer.Width / pxPerDip;

            Check("Root 布局尺寸换算回物理像素后等于显示器宽度",
                Math.Abs(rootW * pxPerDip - monitor.Width) <= 1.5,
                $"rootW={rootW:0.#} × {pxPerDip:0.##} = {rootW * pxPerDip:0.#} px，显示器 {monitor.Width} px");

            Check("Root 布局尺寸换算回物理像素后等于显示器高度",
                Math.Abs(rootH * pxPerDip - monitor.Height) <= 1.5,
                $"rootH={rootH:0.#} × {pxPerDip:0.##} = {rootH * pxPerDip:0.#} px，显示器 {monitor.Height} px");

            Check("内容层缩放后覆盖整个 Root（不会只剩 1/scale）",
                layerPxW >= rootW - 1.5,
                $"Layer {overlay.Layer.Width} 单位 / {pxPerDip:0.##} = {layerPxW:0.#} DIP，Root {rootW:0.#} DIP");

            // 窗口裁剪发生在渲染变换之前：缩放挂在 Root 上会被先裁掉一块
            Check("渲染变换挂在内容层而不是 Root",
                overlay.Root.RenderTransform is MatrixTransform rootTransform && rootTransform.Matrix.IsIdentity
                && overlay.Layer.RenderTransform is ScaleTransform layerTransform && layerTransform.ScaleX < 0.99,
                $"root={overlay.Root.RenderTransform?.GetType().Name}, layer={overlay.Layer.RenderTransform?.GetType().Name}");
        }
        finally
        {
            overlay.HideForCapture();
            overlay.Close();
        }
    }

    // ---------------- 自动打开浏览器决策 ----------------

    private static void TestAutoOpenPolicy()
    {
        Section("九、自动打开浏览器决策（第九章规则）");

        QRResult Url(string text) => new()
        {
            Text = text,
            Source = QRSource.ScreenCapture,
            ValidUrl = UrlValidator.TryNormalize(text, out string? u) ? u : null
        };

        QRResult Plain(string text) => new() { Text = text, Source = QRSource.ScreenCapture };

        var oneUrl = new[] { Url("https://example.com/a") };
        var twoDifferent = new[] { Url("https://example.com/a"), Url("https://example.org/b") };
        var twoSame = new[] { Url("https://example.com/a"), Url("https://EXAMPLE.com/a") };
        var textOnly = new[] { Plain("普通文本内容") };
        var wifi = new[] { Plain("WIFI:S:HomeNet;T:WPA;P:12345678;;") };
        var mixed = new[] { Url("https://example.com/a"), Plain("普通文本") };

        Check("开关关闭时不自动打开", AutoOpenPolicy.Decide(false, oneUrl) is null, "返回 null");
        Check("单个合法 HTTP/HTTPS 链接自动打开",
            AutoOpenPolicy.Decide(true, oneUrl) == "https://example.com/a",
            AutoOpenPolicy.Decide(true, oneUrl) ?? "null");
        Check("多个不同链接不批量打开", AutoOpenPolicy.Decide(true, twoDifferent) is null, "返回 null");
        Check("多个相同链接只打开一次", AutoOpenPolicy.Decide(true, twoSame) is not null, "返回去重后的地址");
        Check("普通文本不打开", AutoOpenPolicy.Decide(true, textOnly) is null, "返回 null");
        Check("Wi-Fi 二维码不打开", AutoOpenPolicy.Decide(true, wifi) is null, "返回 null");
        Check("链接与文本混合时打开唯一链接", AutoOpenPolicy.Decide(true, mixed) is not null, "返回该链接");
        Check("空结果不打开", AutoOpenPolicy.Decide(true, Array.Empty<QRResult>()) is null, "返回 null");
    }

    // ---------------- 输出辅助 ----------------

    private static void Section(string title)
    {
        Report.AppendLine();
        Report.AppendLine("## " + title);
    }

    private static void Line(string text)
    {
        Report.AppendLine(text);
    }

    private static void Check(string name, bool passed, string detail)
    {
        if (passed)
        {
            _passed++;
        }
        else
        {
            _failed++;
        }

        Report.AppendLine($"  [{(passed ? "通过" : "失败")}] {name} — {detail}");
        MachineReadable.AppendLine($"{name}\t{(passed ? "PASS" : "FAIL")}\t{detail}");
        LogService.Info($"自检 [{(passed ? "通过" : "失败")}] {name} — {detail}");
    }

    private static void Fail(string name, string detail) => Check(name, false, detail);

    private static bool Near((byte B, byte G, byte R) pixel, Color expected, int tolerance) =>
        Math.Abs(pixel.B - expected.B) <= tolerance &&
        Math.Abs(pixel.G - expected.G) <= tolerance &&
        Math.Abs(pixel.R - expected.R) <= tolerance;

    private static (byte B, byte G, byte R) PixelAt(BitmapSource source, int x, int y)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var pixel = new byte[4];
        bgra.CopyPixels(new Int32Rect(Math.Clamp(x, 0, source.PixelWidth - 1), Math.Clamp(y, 0, source.PixelHeight - 1), 1, 1),
            pixel, 4, 0);

        return (pixel[0], pixel[1], pixel[2]);
    }

    private static void SavePng(BitmapSource image, string name) => SavePngIfNotNull(image, name);

    private static void SavePngIfNotNull(BitmapSource? image, string name)
    {
        if (image is null)
        {
            return;
        }

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var fs = File.Create(Path.Combine(_outputDirectory, name));
            encoder.Save(fs);
        }
        catch (Exception ex)
        {
            LogService.Warn($"保存自检截图失败：{ex.Message}");
        }
    }

    // ---------------- 测试用窗口 ----------------

    private sealed class SolidPanelWindow : Window
    {
        public SolidPanelWindow(Color color)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = new SolidColorBrush(color);
            Width = 1;
            Height = 1;
        }

        public void ShowAt(Int32Rect rect)
        {
            Show();
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }

    private sealed class QrPanelWindow : Window
    {
        public QrPanelWindow(BitmapSource image)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.White;

            Content = new System.Windows.Controls.Image
            {
                Source = image,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(20)
            };
        }

        public void ShowAt(Int32Rect rect)
        {
            Show();
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }
}
