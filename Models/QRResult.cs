namespace QRQuick.Models;

/// <summary>识别来源。Lite 版只保留截图识别。</summary>
public enum QRSource
{
    ScreenCapture
}

/// <summary>单条二维码识别结果。</summary>
public sealed class QRResult
{
    /// <summary>二维码原始内容，完整保留，不做任何截断或改写。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>条码格式，当前仅产出 QR_CODE。</summary>
    public string Format { get; init; } = "QR_CODE";

    public DateTime CapturedAt { get; init; } = DateTime.Now;

    /// <summary>在结果列表中的序号（从 1 开始），由展示层赋值。</summary>
    public int Index { get; set; }

    public QRSource Source { get; init; }

    /// <summary>通过校验后的 HTTP/HTTPS 地址；不合法时为 null。</summary>
    public string? ValidUrl { get; init; }

    /// <summary>原始内容是否是可安全打开的网页链接。</summary>
    public bool IsValidUrl => ValidUrl is not null;

    public string SourceText => Source switch
    {
        QRSource.ScreenCapture => "屏幕截图",
        _ => "未知"
    };

    /// <summary>结果元信息（格式 · 来源 · 时间）。</summary>
    public string MetaText => $"{Format} · {SourceText} · {CapturedAt:HH:mm:ss}";
}
