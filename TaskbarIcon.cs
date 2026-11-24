using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PhotoChop;

internal static class TaskbarIcon
{
    private static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint RelaunchCommand = 2;
    private const uint RelaunchIconResource = 3;
    private const uint RelaunchDisplayName = 4;
    private const uint AppId = 5;

    public static void Apply(Window window, string iconPath)
    {
        if (Environment.ProcessPath is not { } executable) return;
        WithStore(window, store =>
        {
            Set(store, RelaunchCommand, $"\"{executable}\"");
            Set(store, RelaunchIconResource, $"{iconPath},0");
            Set(store, RelaunchDisplayName, "PhotoChop");
            // Assigning the identity asks the shell to read the relaunch properties.
            Set(store, AppId, "buddy1616.PhotoChop");
        });
    }

    public static void Clear(Window window) => WithStore(window, store =>
    {
        Set(store, AppId, null);
        Set(store, RelaunchCommand, null);
        Set(store, RelaunchIconResource, null);
        Set(store, RelaunchDisplayName, null);
    });

    private static void WithStore(Window window, Action<IPropertyStore> update)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var iid = typeof(IPropertyStore).GUID;
        try
        {
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(handle, in iid, out var store));
            try { update(store); }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch (COMException error)
        {
            System.Diagnostics.Debug.WriteLine($"Could not update the taskbar icon: {error.Message}");
        }
    }

    private static void Set(IPropertyStore store, uint property, string? text)
    {
        var key = new PropertyKey { Format = AppUserModel, Id = property };
        var value = text is null ? default :
            new PropVariant { Type = (ushort)VarEnum.VT_LPWSTR, Pointer = Marshal.StringToCoTaskMemUni(text) };
        try { store.SetValue(in key, in value); }
        finally { Marshal.FreeCoTaskMem(value.Pointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        private ushort reserved1, reserved2, reserved3;
        public IntPtr Pointer;
        private IntPtr unionPadding;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(in PropertyKey key, out PropVariant value);
        void SetValue(in PropertyKey key, in PropVariant value);
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetPropertyStoreForWindow(IntPtr handle, in Guid iid, out IPropertyStore store);
}
