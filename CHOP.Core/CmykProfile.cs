using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Chop.Core;

/// <summary>Converts device CMYK samples from an embedded ICC profile to sRGB.</summary>
public sealed class CmykProfile : SafeHandleZeroOrMinusOneIsInvalid
{
    private const uint RelativeColorimetric = 1;
    // Little CMS packed 16-bit formats, four CMYK channels and three RGB channels.
    private const uint Cmyk16 = (6 << 16) | (4 << 3) | 2;
    private const uint Rgb16 = (4 << 16) | (3 << 3) | 2;

    private readonly bool fromSrgb;

    private CmykProfile(nint transform, bool fromSrgb) : base(true)
    {
        this.fromSrgb = fromSrgb;
        SetHandle(transform);
    }

    public static CmykProfile? Open(byte[] profile, bool fromSrgb = false)
    {
        if (profile.Length < 128 || !profile.AsSpan(16, 4).SequenceEqual("CMYK"u8)) return null;
        try
        {
            nint transform = OperatingSystem.IsWindows() ? OpenWindows(profile, fromSrgb) :
                OperatingSystem.IsLinux() ? OpenLinux(profile, fromSrgb) : 0;
            return transform == 0 ? null : new(transform, fromSrgb);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    public bool Convert(ushort[] input, ushort[] output, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        int inChannels = fromSrgb ? 3 : 4, outChannels = fromSrgb ? 4 : 3;
        if (count > input.Length / inChannels || count > output.Length / outChannels)
            throw new ArgumentException("Color buffers are too short.");
        if (count == 0) return true;
        if (OperatingSystem.IsWindows())
        {
            // COLOR is an eight-byte union on 32-bit Windows and a sixteen-byte union on 64-bit Windows.
            const uint ColorRgb = 2, ColorCmyk = 7;
            int stride = IntPtr.Size == 8 ? 8 : 4;
            var from = new ushort[count * stride];
            var to = new ushort[count * stride];
            for (int i = 0; i < count; i++) Array.Copy(input, i * inChannels, from, i * stride, inChannels);
            if (!TranslateColors(this, from, (uint)count, fromSrgb ? ColorRgb : ColorCmyk,
                to, fromSrgb ? ColorCmyk : ColorRgb)) return false;
            for (int i = 0; i < count; i++) Array.Copy(to, i * stride, output, i * outChannels, outChannels);
            return true;
        }
        cmsDoTransform(this, input, output, (uint)count);
        return true;
    }

    protected override bool ReleaseHandle()
    {
        if (OperatingSystem.IsWindows()) return DeleteColorTransform(handle);
        cmsDeleteTransform(handle);
        return true;
    }

    private static nint OpenLinux(byte[] profile, bool fromSrgb)
    {
        nint source = cmsOpenProfileFromMem(profile, (uint)profile.Length);
        if (source == 0) return 0;
        nint target = cmsCreate_sRGBProfile();
        try { return target == 0 ? 0 : fromSrgb
            ? cmsCreateTransform(target, Rgb16, source, Cmyk16, RelativeColorimetric, 0)
            : cmsCreateTransform(source, Cmyk16, target, Rgb16, RelativeColorimetric, 0); }
        finally
        {
            if (target != 0) cmsCloseProfile(target);
            cmsCloseProfile(source);
        }
    }

    private static nint OpenWindows(byte[] profile, bool fromSrgb)
    {
        const uint Srgb = 0x73524742;
        uint length = 0;
        GetStandardColorSpaceProfileW(0, Srgb, null, ref length);
        if (length is 0 or > 65536) return 0;
        var path = new System.Text.StringBuilder((int)length);
        if (!GetStandardColorSpaceProfileW(0, Srgb, path, ref length)) return 0;
        string filename = path.ToString();
        var memory = GCHandle.Alloc(profile, GCHandleType.Pinned);
        var name = Marshal.StringToHGlobalUni(filename);
        nint source = 0, target = 0;
        try
        {
            source = OpenColorProfileW(new(2, memory.AddrOfPinnedObject(), (uint)profile.Length), 1, 1, 3);
            target = OpenColorProfileW(new(1, name, (uint)((filename.Length + 1) * 2)), 1, 1, 3);
            return source == 0 || target == 0 ? 0 :
                CreateMultiProfileTransform(fromSrgb ? [target, source] : [source, target], 2, [RelativeColorimetric, RelativeColorimetric], 2, 3, 0);
        }
        finally
        {
            if (target != 0) CloseColorProfile(target);
            if (source != 0) CloseColorProfile(source);
            Marshal.FreeHGlobal(name);
            memory.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Profile(uint Type, nint Data, uint Size);

    private const string WindowsColor = "mscms.dll";
    [DllImport(WindowsColor, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool GetStandardColorSpaceProfileW(nint machine, uint space, System.Text.StringBuilder? path, ref uint size);
    [DllImport(WindowsColor, ExactSpelling = true)]
    private static extern nint OpenColorProfileW(in Profile profile, uint access, uint sharing, uint creation);
    [DllImport(WindowsColor)] private static extern bool CloseColorProfile(nint profile);
    [DllImport(WindowsColor)] private static extern nint CreateMultiProfileTransform(nint[] profiles, uint profileCount,
        uint[] intents, uint intentCount, uint flags, uint cmm);
    [DllImport(WindowsColor)] private static extern bool DeleteColorTransform(nint transform);
    [DllImport(WindowsColor)] private static extern bool TranslateColors(CmykProfile transform, ushort[] input,
        uint count, uint inputType, [Out] ushort[] output, uint outputType);

    private const string LittleCms = "liblcms2.so.2";
    [DllImport(LittleCms)] private static extern nint cmsOpenProfileFromMem(byte[] data, uint length);
    [DllImport(LittleCms)] private static extern nint cmsCreate_sRGBProfile();
    [DllImport(LittleCms)] private static extern int cmsCloseProfile(nint profile);
    [DllImport(LittleCms)] private static extern nint cmsCreateTransform(nint source, uint inputFormat, nint target, uint outputFormat, uint intent, uint flags);
    [DllImport(LittleCms)] private static extern void cmsDoTransform(CmykProfile transform, ushort[] input, [Out] ushort[] output, uint count);
    [DllImport(LittleCms)] private static extern void cmsDeleteTransform(nint transform);
}
