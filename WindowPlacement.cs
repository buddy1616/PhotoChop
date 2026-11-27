using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PhotoChop;

internal sealed record WindowPosition(int Left, int Top, int Right, int Bottom, bool Maximized);

internal static class WindowPlacement
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public int Size, Flags, Show;
        public Point Min, Max;
        public Rect Normal;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr handle, ref Placement placement);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rect rectangle, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    public static WindowPosition? Capture(Window window)
    {
        var position = new Placement { Size = Marshal.SizeOf<Placement>() };
        var handle = new WindowInteropHelper(window).Handle;
        if (!GetWindowPlacement(handle, ref position)) return null;
        var rect = position.Normal;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (position.Show == 1 && GetWindowRect(handle, out var current)) rect = current;
        else if (GetMonitorInfo(MonitorFromWindow(handle, 2), ref info))
            Offset(ref rect, info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
        return new WindowPosition(rect.Left, rect.Top, rect.Right, rect.Bottom,
            position.Show == 3 || (position.Flags & 2) != 0);
    }

    public static bool TryConstrain(WindowPosition? saved, out WindowPosition position)
    {
        position = null!;
        if (saved is null) return false;
        long width = (long)saved.Right - saved.Left, height = (long)saved.Bottom - saved.Top;
        if (width < 200 || height < 150 || width > 30000 || height > 30000) return false;
        var rect = new Rect { Left = saved.Left, Top = saved.Top, Right = saved.Right, Bottom = saved.Bottom };
        IntPtr monitor = MonitorFromRect(ref rect, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;
        var work = info.Work;
        int w = (int)Math.Min(width, work.Right - work.Left), h = (int)Math.Min(height, work.Bottom - work.Top);
        int x = Math.Clamp(rect.Left, work.Left, work.Right - w);
        int y = Math.Clamp(rect.Top, work.Top, work.Bottom - h);
        position = new WindowPosition(x, y, x + w, y + h, saved.Maximized);
        return true;
    }

    private static void Offset(ref Rect rect, int x, int y)
    {
        rect.Left += x;
        rect.Right += x;
        rect.Top += y;
        rect.Bottom += y;
    }

    public static void EnsureVisible(Window window)
    {
        if (window.WindowState == WindowState.Normal && Capture(window) is { } current &&
            TryConstrain(current, out var constrained) && constrained != current)
            Restore(window, constrained);
    }

    public static void Restore(Window window, WindowPosition position)
    {
        // Saved bounds are physical screen coordinates, matching SetWindowPos.
        const uint noZOrderOrActivation = 0x0004 | 0x0010;
        if (SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, position.Left, position.Top,
            position.Right - position.Left, position.Bottom - position.Top, noZOrderOrActivation) && position.Maximized)
            window.WindowState = WindowState.Maximized;
    }
}
