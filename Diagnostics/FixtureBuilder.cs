using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;

namespace QRQuick.Diagnostics;

/// <summary>
/// 用真实编码器生成测试图片（不做假数据）：二维码由 ZXing 编码，
/// 再合成为各种真实的拍摄/截图劣化情形。
/// </summary>
internal static class FixtureBuilder
{
    public sealed record Fixture(string Name, string Path, string ExpectedText, int ExpectedCount, bool ExpectEmpty);

    /// <summary>生成整套测试图片并写入目标目录。</summary>
    public static List<Fixture> BuildAll(string directory)
    {
        Directory.CreateDirectory(directory);

        var fixtures = new List<Fixture>();

        const string url = "https://www.example.com/qrquick?from=screenshot-runner";
        const string text = "QR Quick 本地识别测试 · 2026";
        const string second = "https://example.org/second-code";

        BitmapSource qrUrl = MakeQr(url, 420);
        BitmapSource qrText = MakeQr(text, 360);
        BitmapSource qrSecond = MakeQr(second, 360);

        // 1. 普通 PNG
        fixtures.Add(Save(directory, "01-normal.png", Compose(480, 480, qrUrl), url, 1, false));

        // 2. JPG
        fixtures.Add(Save(directory, "02-normal.jpg", Compose(480, 480, qrUrl), url, 1, false));

        // 3. BMP
        fixtures.Add(Save(directory, "03-normal.bmp", Compose(480, 480, qrUrl), url, 1, false));

        // 4. 中文文本二维码（验证 UTF-8 与原始内容保真）
        fixtures.Add(Save(directory, "04-utf8-text.png", Compose(420, 420, qrText), text, 1, false));

        // 5. 一张图两个二维码
        var multi = new DrawingVisual();
        using (DrawingContext dc = multi.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 800, 420));
            dc.DrawImage(qrUrl, new Rect(10, 10, 400, 400));
            dc.DrawImage(qrSecond, new Rect(420, 30, 360, 360));
        }

        fixtures.Add(Save(directory, "05-multi.png", Render(multi, 800, 420, PixelFormats.Pbgra32), url, 2, false, second));

        // 6. 反色（白码黑底）
        fixtures.Add(Save(directory, "06-inverted.png", Compose(480, 480, Invert(qrUrl)), url, 1, false));

        // 7. 旋转 90 度
        fixtures.Add(Save(directory, "07-rotated.png", Compose(480, 480, Rotate90(qrUrl)), url, 1, false));

        // 8. 小尺寸（120px 放大后仍可识别，模拟屏幕上的小码）
        fixtures.Add(Save(directory, "08-small.png", Compose(360, 360, MakeQr(url, 120), 120), url, 1, false));

        // 9. 低清晰度：先缩到 150 再双线性放大
        BitmapSource soft = MakeQr(url, 150);
        BitmapSource blurred = Upscale(soft, 450);
        fixtures.Add(Save(directory, "09-low-quality.png", Compose(480, 480, blurred), url, 1, false));

        // 10. 倾斜（旋转 13 度）
        fixtures.Add(Save(directory, "10-tilted.png", Compose(500, 500, Rotate(qrUrl, 13)), url, 1, false));

        // 11. 无二维码的图片（负数用例）
        var plain = new DrawingVisual();
        using (DrawingContext dc = plain.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 400, 200));
            var brush = new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x32));
            dc.DrawText(new FormattedText("QR Quick 测试图片：这里没有二维码",
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), 20, brush, 1.0), new Point(24, 90));
        }

        fixtures.Add(Save(directory, "11-no-qr.png", Render(plain, 400, 200, PixelFormats.Pbgra32), string.Empty, 0, true));

        return fixtures;
    }

    /// <summary>用 ZXing 生成二维码位图。</summary>
    public static BitmapSource MakeQr(string content, int size)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions { Width = size, Height = size, Margin = 2 }
        };

        // 关键：默认编码器按 ISO-8859-1 处理，中文会被替换成 '?'
        writer.Options.Hints[EncodeHintType.CHARACTER_SET] = "UTF-8";

        PixelData data = writer.Write(content);
        BitmapSource source = BitmapSource.Create(data.Width, data.Height, 96, 96,
            PixelFormats.Bgra32, null, data.Pixels, data.Width * 4);
        source.Freeze();
        return source;
    }

    private static Fixture Save(string directory, string name, BitmapSource image, string expected,
        int expectedCount, bool expectEmpty, string? second = null)
    {
        string path = Path.Combine(directory, name);
        BitmapEncoder encoder = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };

        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var fs = File.Create(path))
        {
            encoder.Save(fs);
        }

        string all = second is null ? expected : $"{expected}|{second}";
        return new Fixture(name, path, all, expectedCount, expectEmpty);
    }

    /// <summary>把二维码居中放到画布上。</summary>
    private static BitmapSource Compose(int width, int height, BitmapSource qr, int? drawSize = null)
    {
        int size = drawSize ?? (int)Math.Min(width - 40, height - 40);
        var visual = new DrawingVisual();

        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            double x = (width - size) / 2.0;
            double y = (height - size) / 2.0;
            dc.DrawImage(qr, new Rect(x, y, size, size));
        }

        return Render(visual, width, height, PixelFormats.Pbgra32);
    }

    private static BitmapSource Render(Visual visual, int width, int height, PixelFormat format)
    {
        var target = new RenderTargetBitmap(width, height, 96, 96, format);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static BitmapSource Invert(BitmapSource source)
    {
        int stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        bgra.CopyPixels(pixels, stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(255 - pixels[i]);
            pixels[i + 1] = (byte)(255 - pixels[i + 1]);
            pixels[i + 2] = (byte)(255 - pixels[i + 2]);
        }

        BitmapSource result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static BitmapSource Rotate90(BitmapSource source) => Rotate(source, 90);

    private static BitmapSource Rotate(BitmapSource source, double angle)
    {
        var visual = new DrawingVisual();

        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            dc.PushTransform(new RotateTransform(angle, source.PixelWidth / 2.0, source.PixelHeight / 2.0));
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            dc.Pop();
        }

        return Render(visual, source.PixelWidth, source.PixelHeight, PixelFormats.Pbgra32);
    }

    private static BitmapSource Upscale(BitmapSource source, int size)
    {
        var visual = new DrawingVisual();

        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, size, size));
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            dc.DrawImage(source, new Rect(0, 0, size, size));
        }

        return Render(visual, size, size, PixelFormats.Pbgra32);
    }
}
