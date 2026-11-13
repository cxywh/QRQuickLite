using System.Runtime;

namespace QRQuick.Helpers;

/// <summary>
/// 后台内存回收，分两档：
/// ReleaseBuffers 只做托管回收，用于一次识别结束后释放解码位图，不影响界面响应；
/// TrimToBackground 回收后再把工作集还给系统，只在退到托盘或最小化时执行。
/// 两档都带最小间隔，避免快速操作时反复完整回收。
/// </summary>
internal static class MemoryTrimmer
{
    private static readonly TimeSpan ReleaseInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TrimInterval = TimeSpan.FromSeconds(5);

    private static DateTime _lastRelease = DateTime.MinValue;
    private static DateTime _lastTrim = DateTime.MinValue;

    /// <summary>只回收托管内存（含大对象堆紧凑），保留工作集。</summary>
    public static void ReleaseBuffers()
    {
        if (!TryClaim(ref _lastRelease, ReleaseInterval))
        {
            return;
        }

        Collect();
    }

    /// <summary>回收托管内存并裁剪工作集，用于程序退到后台。</summary>
    public static void TrimToBackground()
    {
        if (!TryClaim(ref _lastTrim, TrimInterval))
        {
            return;
        }

        Collect();

        try
        {
            IntPtr process = NativeMethods.GetCurrentProcess();
            if (!NativeMethods.EmptyWorkingSet(process))
            {
                // psapi 不可用时退回内核接口，效果相同
                NativeMethods.SetProcessWorkingSetSize(process, new IntPtr(-1), new IntPtr(-1));
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"裁剪工作集失败：{ex.Message}");
        }
    }

    private static void Collect()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            // Forced：Optimized 在空闲时可能整轮跳过，而这里正是要真正回收的时刻
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            // Forced：Optimized 在空闲时可能整轮跳过，而这里正是要真正回收的时刻
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch (Exception ex)
        {
            LogService.Warn($"内存回收失败：{ex.Message}");
        }
    }

    private static bool TryClaim(ref DateTime last, TimeSpan interval)
    {
        DateTime now = DateTime.UtcNow;
        if (now - last < interval)
        {
            return false;
        }

        last = now;
        return true;
    }
}
