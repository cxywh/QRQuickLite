namespace QRQuick.Models;

/// <summary>
/// 一个物理显示器的信息。所有坐标均为**物理像素**，与截图 API 一致。
/// </summary>
public sealed class MonitorInfo
{
    public required IntPtr Handle { get; init; }

    /// <summary>显示器在虚拟桌面中的物理矩形（左上角可能为负）。</summary>
    public required int Left { get; init; }

    public required int Top { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>有效 DPI（96 为 100%）。</summary>
    public required uint Dpi { get; init; }

    public required bool IsPrimary { get; init; }

    public required string DeviceName { get; init; }

    public double Scale => Dpi / 96.0;

    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public override string ToString() =>
        $"{DeviceName} [{Left},{Top} {Width}x{Height}] dpi={Dpi} ({Scale * 100:0}%){(IsPrimary ? " primary" : string.Empty)}";
}
