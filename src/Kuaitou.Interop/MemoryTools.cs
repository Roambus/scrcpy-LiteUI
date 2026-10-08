namespace Kuaitou.Interop;

/// <summary>
/// 进程内存相关的小工具。托管堆那一侧不归这里管（那是 GC 的事），这里只负责把已经用不到的
/// 物理页还给系统。
/// </summary>
public static class MemoryTools
{
    /// <summary>
    /// 把当前进程的工作集交还给系统（psapi!EmptyWorkingSet）。逻辑内存与 GC 堆一点没动，
    /// 只是把暂时不碰的页换出去，下次访问时按需读回来（代价是重新访问的第一下略慢）。
    /// 适合「缩小到托盘 / 最小化」这种真正进入后台的时刻调用。
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            NativeMethods.EmptyWorkingSet(NativeMethods.GetCurrentProcess());
        }
        catch (Exception)
        {
            // 瘦身失败没有任何后果，忽略即可
        }
    }

    /// <summary>
    /// 「进后台」时用：先把托管堆里已经没人引用的东西真正收掉（图标位图与卡片视觉树这类
    /// 大头都靠它，光把引用置空、不 GC 是不会还给系统的），再把工作集交还给系统。
    ///
    /// 平时绝不该调这个——GC 有自己的节奏，强行收反而伤性能；这里只在「收进托盘」这种
    /// 明确的低峰时刻收一次。
    /// </summary>
    public static void ReclaimBackgroundMemory()
    {
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        catch (Exception)
        {
            // 收不掉只是内存晚点还回去，不影响功能
        }
        TrimWorkingSet();
    }
}