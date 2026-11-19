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
    private static extern bool GetWindowPlacement(IntPtr handle, ref Placement placement);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr handle, ref Placement placement);
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
        if (GetMonitorInfo(MonitorFromWindow(handle, 2), ref info))
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
        IntPtr monitor = MonitorFromRect(ref rect, 0);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;
        if (InWorkArea(rect.Left, rect.Top) && InWorkArea(rect.Right - 1, rect.Bottom - 1))
        {
            position = saved;
            return true;
        }
        var work = info.Work;
        if (Math.Min(rect.Right, work.Right) - Math.Max(rect.Left, work.Left) < 80 ||
            Math.Min(rect.Bottom, work.Bottom) - Math.Max(rect.Top, work.Top) < 40) return false;
        int w = (int)Math.Min(width, work.Right - work.Left), h = (int)Math.Min(height, work.Bottom - work.Top);
        int x = Math.Clamp(rect.Left, work.Left, work.Right - w);
        int y = Math.Clamp(rect.Top, work.Top, work.Bottom - h);
        position = new WindowPosition(x, y, x + w, y + h, saved.Maximized);
        return true;
    }

    private static bool InWorkArea(int x, int y)
    {
        var pixel = new Rect { Left = x, Top = y, Right = x + 1, Bottom = y + 1 };
        var monitor = MonitorFromRect(ref pixel, 0);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) &&
            x >= info.Work.Left && x < info.Work.Right && y >= info.Work.Top && y < info.Work.Bottom;
    }

    private static void Offset(ref Rect rect, int x, int y)
    {
        rect.Left += x;
        rect.Right += x;
        rect.Top += y;
        rect.Bottom += y;
    }

    public static void Restore(Window window, WindowPosition position)
    {
        var rect = new Rect { Left = position.Left, Top = position.Top, Right = position.Right, Bottom = position.Bottom };
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(MonitorFromRect(ref rect, 2), ref info))
            Offset(ref rect, info.Monitor.Left - info.Work.Left, info.Monitor.Top - info.Work.Top);
        var placement = new Placement
        {
            Size = Marshal.SizeOf<Placement>(), Show = position.Maximized ? 3 : 1,
            Normal = rect
        };
        if (SetWindowPlacement(new WindowInteropHelper(window).Handle, ref placement) && position.Maximized)
            window.WindowState = WindowState.Maximized;
    }
}
