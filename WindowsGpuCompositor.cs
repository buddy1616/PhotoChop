using System.Runtime.InteropServices;
using System.Windows.Threading;
using Chop.Core;
using Chop.Rendering;
using SkiaSharp;

namespace PhotoChop;

/// <summary>Keeps the Win32 window, OpenGL context, and Skia GPU resources on one thread.</summary>
internal sealed class WindowsGpuCompositor : IDisposable
{
    private readonly TaskCompletionSource<Dispatcher> owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GlWindow? window;
    private SkiaRenderer.GpuCompositor? compositor;
    private string? failure;
    private int disposed;

    public WindowsGpuCompositor()
    {
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            owner.SetResult(dispatcher);
            try { Dispatcher.Run(); }
            finally
            {
                ReleaseGraphics();
            }
        }) { IsBackground = true, Name = "CHOP Skia GPU" };
        thread.Start();
    }

    public (Pixels? Pixels, string Backend) TryComposite(SkiaRenderer renderer, Document document)
    {
        if (!SkiaRenderer.CanCompositeOnGpu(document)) return (null, "CPU · document requires CPU");
        if (Volatile.Read(ref failure) is { } reason) return (null, reason);
        if (Volatile.Read(ref disposed) != 0) return (null, "CPU");
        try
        {
            return owner.Task.GetAwaiter().GetResult().Invoke(() =>
            {
                try
                {
                    window ??= new GlWindow();
                    compositor ??= renderer.CreateGpuCompositor(window.Context);
                    return compositor.Composite(document) is { } pixels
                        ? (pixels, "GPU · Skia OpenGL") : ((Pixels?)null, "CPU · GPU surface unavailable");
                }
                catch (Exception error) when (error is InvalidOperationException or ExternalException or
                    DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
                {
                    Volatile.Write(ref failure, "CPU · OpenGL unavailable");
                    System.Diagnostics.Debug.WriteLine($"CHOP GPU fallback: {error.Message}");
                    ReleaseGraphics();
                    return (null, failure!);
                }
            });
        }
        catch (OperationCanceledException) { return (null, "CPU"); }
    }

    private void ReleaseGraphics()
    {
        try { compositor?.Dispose(); }
        finally
        {
            compositor = null;
            window?.Dispose();
            window = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        owner.Task.GetAwaiter().GetResult().BeginInvokeShutdown(DispatcherPriority.Normal);
    }

    private sealed class GlWindow : IDisposable
    {
        private IntPtr window, dc, gl, library;
        private GRGlInterface? api;
        private GRContext? context;
        public GRContext Context => context!;

        public GlWindow()
        {
            try
            {
                library = NativeLibrary.Load("opengl32.dll", typeof(GlWindow).Assembly, DllImportSearchPath.System32);
                window = CreateWindowEx(0, "STATIC", "CHOP OpenGL", 0x80000000, 0, 0, 1, 1,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (window == IntPtr.Zero || (dc = GetDC(window)) == IntPtr.Zero) throw Unavailable();
                var format = new PixelFormat
                {
                    Size = (ushort)Marshal.SizeOf<PixelFormat>(), Version = 1,
                    Flags = 0x24, // PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL
                    ColorBits = 32, AlphaBits = 8
                };
                int choice = ChoosePixelFormat(dc, ref format);
                if (choice == 0 || DescribePixelFormat(dc, choice, format.Size, ref format) == 0 ||
                    (format.Flags & 0x40) != 0 || !SetPixelFormat(dc, choice, ref format)) // PFD_GENERIC_FORMAT
                    throw Unavailable();
                gl = wglCreateContext(dc);
                if (gl == IntPtr.Zero || !wglMakeCurrent(dc, gl)) throw Unavailable();
                System.Diagnostics.Debug.WriteLine($"CHOP OpenGL renderer: {Marshal.PtrToStringAnsi(glGetString(0x1F01))}");
                api = GRGlInterface.CreateOpenGl(Resolve) ?? throw Unavailable();
                context = GRContext.CreateGl(api) ?? throw Unavailable();
            }
            catch { Dispose(); throw; }
        }

        private IntPtr Resolve(string name)
        {
            IntPtr address = wglGetProcAddress(name);
            return address.ToInt64() is 0 or 1 or 2 or 3 or -1
                ? (NativeLibrary.TryGetExport(library, name, out var core) ? core : IntPtr.Zero)
                : address;
        }

        private static InvalidOperationException Unavailable() => new("No usable hardware OpenGL context.");

        public void Dispose()
        {
            context?.Dispose();
            api?.Dispose();
            if (gl != IntPtr.Zero)
            {
                wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                wglDeleteContext(gl);
            }
            if (dc != IntPtr.Zero) ReleaseDC(window, dc);
            if (window != IntPtr.Zero) DestroyWindow(window);
            if (library != IntPtr.Zero) NativeLibrary.Free(library);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PixelFormat
        {
            public ushort Size, Version;
            public uint Flags;
            public byte PixelType, ColorBits, RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift,
                AlphaBits, AlphaShift, AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits,
                DepthBits, StencilBits, AuxBuffers, LayerType, Reserved;
            public uint LayerMask, VisibleMask, DamageMask;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint extraStyle, string className, string title, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(IntPtr dc, ref PixelFormat format);
        [DllImport("gdi32.dll")] private static extern int DescribePixelFormat(IntPtr dc, int index, uint size, ref PixelFormat format);
        [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr dc, int index, ref PixelFormat format);
        [DllImport("opengl32.dll")] private static extern IntPtr glGetString(uint name);
        [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr dc);
        [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr dc, IntPtr context);
        [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr context);
        [DllImport("opengl32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr wglGetProcAddress(string name);
    }
}
