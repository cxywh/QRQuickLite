using QRQuick.Helpers;
using QRQuick.Models;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace QRQuick.Services;

/// <summary>
/// 条码/二维码识别服务。全部在后台线程完成，UI 线程只做展示。
/// 两段式：先只认二维码（快），没命中再用全格式兜底（慢但覆盖面广）。
/// 每段内部按增强阶梯「原图 → 反色 → 对比度 → 2 倍放大 → 放大+对比度」逐级尝试，命中即停。
/// </summary>
public sealed class QRCodeService
{
    /// <summary>仅当像素数小于该值时做 2 倍放大，避免大图放大后内存暴涨。</summary>
    private const long UpScalePixelLimit = 4_000_000L;

    /// <summary>一维码结果的最小长度，低于此值按误报丢弃。</summary>
    private const int MinLinearLength = 5;

    /// <summary>快速通道：只认二维码。</summary>
    private static readonly BarcodeFormat[] FastFormats = { BarcodeFormat.QR_CODE };

    /// <summary>
    /// 兜底通道：只放实测能编码+解码闭环的格式。
    /// 不含 DataBar / DataBarExpanded / MaxiCode —— ZXing.Net 只有它们的 reader，
    /// 没有编码器可造样本验证，而且 RSS 系列 reader 在无码图上很慢、误报也高。
    /// </summary>
    private static readonly BarcodeFormat[] FallbackFormats =
    {
        BarcodeFormat.QR_CODE,
        BarcodeFormat.AZTEC,
        BarcodeFormat.DATA_MATRIX,
        BarcodeFormat.PDF_417,
        BarcodeFormat.EAN_13,
        BarcodeFormat.EAN_8,
        BarcodeFormat.UPC_A,
        BarcodeFormat.UPC_E,
        BarcodeFormat.ITF,
        BarcodeFormat.CODE_128,
        BarcodeFormat.CODE_93,
        BarcodeFormat.CODE_39,
        BarcodeFormat.CODABAR
    };

    /// <summary>异步识别，返回去重后的结果（可能为空）。</summary>
    public Task<IReadOnlyList<QRResult>> DecodeAsync(BitmapSource image, QRSource source, CancellationToken token = default)
    {
        if (image is null)
        {
            return Task.FromResult<IReadOnlyList<QRResult>>(Array.Empty<QRResult>());
        }

        return Task.Run(() => Decode(image, source, token), token);
    }

    private IReadOnlyList<QRResult> Decode(BitmapSource image, QRSource source, CancellationToken token)
    {
        var results = new List<QRResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        PixelBuffer original;

        try
        {
            original = PixelBuffer.FromBitmap(image);
        }
        catch (Exception ex)
        {
            LogService.Error("提取图像像素失败", ex);
            return results;
        }

        // 第一段：只认二维码。截图工具的绝大多数场景到这里就结束，延迟保持个位数到几十毫秒。
        if (RunChain(original, FastFormats, "快速", source, seen, results, token))
        {
            return results;
        }

        // 第二段：兜底识别其它码制。只在第一段完全没命中时才跑。
        // 全格式扫描在无码图上本就要一两百毫秒（实测 1600×1000 约 160ms、2560×1600 约 234ms），
        // 因此默认只跑原图一档；只有画面整体偏暗（可能是白码黑底）时才补跑反色。
        int attempts = original.SampleAverageLuminance() < 110 ? 2 : 1;
        RunChain(original, FallbackFormats, "兜底", source, seen, results, token, attemptLimit: attempts);

        return results;
    }

    /// <summary>按增强阶梯跑一轮识别，命中即停。返回是否命中。</summary>
    private bool RunChain(PixelBuffer original, BarcodeFormat[] formats, string stage,
        QRSource source, HashSet<string> seen, List<QRResult> results,
        CancellationToken token, int attemptLimit = int.MaxValue)
    {
        int attempt = 0;

        foreach (var (step, buffer) in BuildAttempts(original))
        {
            if (attempt++ >= attemptLimit)
            {
                break;
            }

            token.ThrowIfCancellationRequested();

            try
            {
                List<Result>? raw = TryRead(buffer, formats);
                if (raw is null || raw.Count == 0)
                {
                    continue;
                }

                int added = 0;

                foreach (Result r in raw)
                {
                    if (!IsPlausible(r.BarcodeFormat, r.Text) || !seen.Add(r.Text))
                    {
                        continue;
                    }

                    results.Add(new QRResult
                    {
                        Text = r.Text,
                        Format = FormatName(r.BarcodeFormat),
                        CapturedAt = DateTime.Now,
                        Source = source,
                        ValidUrl = UrlValidator.TryNormalize(r.Text, out string? url) ? url : null
                    });

                    added++;
                }

                if (added > 0)
                {
                    // 只记录码制与档位，不记录内容
                    LogService.Info($"识别成功（{stage}/{step}/{FormatName(raw[0].BarcodeFormat)}），命中 {added} 个");
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Error($"识别步骤 {stage}/{step} 异常", ex);
            }
        }

        return false;
    }

    /// <summary>
    /// 一维码的结构校验普遍很弱（Code 39 / Codabar 连校验位都没有），
    /// 界面上的杂纹理容易被认成条码。这里加最小长度与字符集约束，降低误报。
    /// 矩阵码自带强校验与纠错，不需要额外过滤。
    /// </summary>
    private static bool IsPlausible(BarcodeFormat format, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (format is BarcodeFormat.QR_CODE or BarcodeFormat.AZTEC
            or BarcodeFormat.DATA_MATRIX or BarcodeFormat.PDF_417)
        {
            return true;
        }

        if (text.Length < MinLinearLength)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c < 0x20 || c > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>码制的可读名称，用于界面展示与日志。</summary>
    private static string FormatName(BarcodeFormat format) => format switch
    {
        BarcodeFormat.QR_CODE => "QR Code",
        BarcodeFormat.AZTEC => "Aztec",
        BarcodeFormat.DATA_MATRIX => "Data Matrix",
        BarcodeFormat.PDF_417 => "PDF417",
        BarcodeFormat.EAN_13 => "EAN-13",
        BarcodeFormat.EAN_8 => "EAN-8",
        BarcodeFormat.UPC_A => "UPC-A",
        BarcodeFormat.UPC_E => "UPC-E",
        BarcodeFormat.ITF => "ITF",
        BarcodeFormat.CODE_128 => "Code 128",
        BarcodeFormat.CODE_93 => "Code 93",
        BarcodeFormat.CODE_39 => "Code 39",
        BarcodeFormat.CODABAR => "Codabar",
        _ => format.ToString()
    };

    /// <summary>按需生成识别尝试序列（惰性构造，避免不必要的内存分配）。</summary>
    private IEnumerable<(string Step, PixelBuffer Buffer)> BuildAttempts(PixelBuffer original)
    {
        yield return ("原图", original);

        // 反色（白码黑底）：显式生成反色缓冲，不依赖 ZXing 的 TryInverted 内部行为
        var inverted = original.Inverted();
        yield return ("反色", inverted);

        var contrast = original.WithContrastStretch(2, 98);
        yield return ("对比度增强", contrast);

        if (original.PixelCount <= UpScalePixelLimit)
        {
            var up = original.Scaled(2);
            yield return ("2 倍放大", up);

            var upContrast = up.WithContrastStretch(2, 98);
            yield return ("2 倍放大+对比度", upContrast);
        }
        else
        {
            // 大图不放大，改为缩小一半以降低噪点影响（等价于降采样去噪）
            var half = original.Scaled(0.5);
            yield return ("降采样", half);
        }
    }

    /// <summary>对单个缓冲区执行一次 ZXing 识别，返回带码制信息的结果。</summary>
    private static List<Result> TryRead(PixelBuffer buffer, BarcodeFormat[] formats)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = formats,
                CharacterSet = "UTF-8"
            }
        };

        var output = new List<Result>();

        // 1) 尝试多码识别
        var multiple = reader.DecodeMultiple(buffer.Pixels, buffer.Width, buffer.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
        if (multiple is not null)
        {
            foreach (var r in multiple)
            {
                if (!string.IsNullOrEmpty(r?.Text))
                {
                    output.Add(r!);
                }
            }
        }

        // 2) 多码识别没结果时再试单码（某些小图 / 特殊排版只对单码路径有效）
        if (output.Count == 0)
        {
            var single = reader.Decode(buffer.Pixels, buffer.Width, buffer.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
            if (single is not null && !string.IsNullOrEmpty(single.Text))
            {
                output.Add(single);
            }
        }

        return output;
    }

    /// <summary>BGRA32 像素缓冲区，附带常用的低成本图像处理。</summary>
    private sealed class PixelBuffer
    {
        private PixelBuffer(int width, int height, byte[] pixels)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public int Width { get; }

        public int Height { get; }

        public byte[] Pixels { get; }

        public long PixelCount => (long)Width * Height;

        public static PixelBuffer FromBitmap(BitmapSource source)
        {
            BitmapSource bgra = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            int width = bgra.PixelWidth;
            int height = bgra.PixelHeight;
            int stride = width * 4;
            var buffer = new byte[(long)stride * height];

            bgra.CopyPixels(buffer, stride, 0);
            return new PixelBuffer(width, height, buffer);
        }

        /// <summary>抽样估计平均亮度（0–255），用于判断要不要补跑反色识别。每 16 个像素取一个。</summary>
        public int SampleAverageLuminance()
        {
            long sum = 0;
            long count = 0;

            for (long i = 0; i + 3 < Pixels.Length; i += 16 * 4)
            {
                // BGRA → 近似亮度
                sum += (Pixels[i + 2] * 299 + Pixels[i + 1] * 587 + Pixels[i] * 114) / 1000;
                count++;
            }

            return count == 0 ? 255 : (int)(sum / count);
        }

        /// <summary>反色（用于白码黑底）。</summary>
        public PixelBuffer Inverted()
        {
            var copy = (byte[])Pixels.Clone();

            for (int i = 0; i < copy.Length; i += 4)
            {
                copy[i] = (byte)(255 - copy[i]);
                copy[i + 1] = (byte)(255 - copy[i + 1]);
                copy[i + 2] = (byte)(255 - copy[i + 2]);
            }

            return new PixelBuffer(Width, Height, copy);
        }

        /// <summary>对比度拉伸（按亮度百分位裁剪），三通道同步处理以保持色相。</summary>
        public PixelBuffer WithContrastStretch(double lowPercentile, double highPercentile)
        {
            var copy = (byte[])Pixels.Clone();

            int[] histogram = new int[256];
            for (int i = 0; i < copy.Length; i += 4)
            {
                int luma = (copy[i] * 29 + copy[i + 1] * 150 + copy[i + 2] * 77) >> 8;
                histogram[luma]++;
            }

            long total = (long)Width * Height;
            long lowCount = (long)(total * lowPercentile / 100.0);
            long highCount = (long)(total * highPercentile / 100.0);

            int low = 0, high = 255;
            long acc = 0;
            for (int i = 0; i < 256; i++)
            {
                acc += histogram[i];
                if (acc >= lowCount)
                {
                    low = i;
                    break;
                }
            }

            acc = 0;
            for (int i = 0; i < 256; i++)
            {
                acc += histogram[i];
                if (acc >= highCount)
                {
                    high = i;
                    break;
                }
            }

            if (high <= low + 8)
            {
                return new PixelBuffer(Width, Height, copy);
            }

            var lut = new byte[256];
            double scale = 255.0 / (high - low);
            for (int i = 0; i < 256; i++)
            {
                int v = (int)((i - low) * scale);
                lut[i] = (byte)Math.Clamp(v, 0, 255);
            }

            for (int i = 0; i < copy.Length; i += 4)
            {
                copy[i] = lut[copy[i]];
                copy[i + 1] = lut[copy[i + 1]];
                copy[i + 2] = lut[copy[i + 2]];
            }

            return new PixelBuffer(Width, Height, copy);
        }

        /// <summary>双线性缩放（放大用双线性，缩小用区域平均）。</summary>
        public PixelBuffer Scaled(double factor)
        {
            int newWidth = Math.Max(8, (int)Math.Round(Width * factor));
            int newHeight = Math.Max(8, (int)Math.Round(Height * factor));
            var dst = new byte[newWidth * newHeight * 4];

            double xRatio = (double)Width / newWidth;
            double yRatio = (double)Height / newHeight;
            bool upscale = factor > 1.0;

            for (int y = 0; y < newHeight; y++)
            {
                for (int x = 0; x < newWidth; x++)
                {
                    int di = (y * newWidth + x) * 4;

                    if (upscale)
                    {
                        double sx = Math.Min(Width - 1.001, x * xRatio);
                        double sy = Math.Min(Height - 1.001, y * yRatio);
                        int x0 = (int)sx, y0 = (int)sy;
                        int x1 = Math.Min(Width - 1, x0 + 1), y1 = Math.Min(Height - 1, y0 + 1);
                        double fx = sx - x0, fy = sy - y0;

                        int i00 = (y0 * Width + x0) * 4;
                        int i10 = (y0 * Width + x1) * 4;
                        int i01 = (y1 * Width + x0) * 4;
                        int i11 = (y1 * Width + x1) * 4;

                        for (int c = 0; c < 3; c++)
                        {
                            double top = Pixels[i00 + c] * (1 - fx) + Pixels[i10 + c] * fx;
                            double bottom = Pixels[i01 + c] * (1 - fx) + Pixels[i11 + c] * fx;
                            dst[di + c] = (byte)Math.Clamp((int)Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
                        }
                    }
                    else
                    {
                        // 区域平均，抑制缩小带来的摩尔纹
                        int xStart = (int)(x * xRatio), xEnd = Math.Min(Width, (int)((x + 1) * xRatio) + 1);
                        int yStart = (int)(y * yRatio), yEnd = Math.Min(Height, (int)((y + 1) * yRatio) + 1);
                        long b = 0, g = 0, r = 0;
                        int count = 0;

                        for (int sy = yStart; sy < yEnd; sy++)
                        {
                            for (int sx = xStart; sx < xEnd; sx++)
                            {
                                int si = (sy * Width + sx) * 4;
                                b += Pixels[si];
                                g += Pixels[si + 1];
                                r += Pixels[si + 2];
                                count++;
                            }
                        }

                        if (count == 0)
                        {
                            count = 1;
                        }

                        dst[di] = (byte)(b / count);
                        dst[di + 1] = (byte)(g / count);
                        dst[di + 2] = (byte)(r / count);
                    }

                    dst[di + 3] = 255;
                }
            }

            return new PixelBuffer(newWidth, newHeight, dst);
        }
    }
}
