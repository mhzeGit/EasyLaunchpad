using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Launchpad.App.Native;

/// <summary>Pulls icons out of the shell: high-resolution bitmaps for apps, small icons for file results.</summary>
internal static class ShellIcons
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size { public int Cx, Cy; public Size(int w, int h) { Cx = w; Cy = h; } }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IShellItemImageFactory factory);

    private static Guid IID_Factory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    /// <summary>Straight-alpha BGRA pixels of the shell icon for a parsing name such as <c>shell:AppsFolder\Foo</c>.</summary>
    public static bool TryGetBitmap(string parsingName, int size, out byte[] bgra, out int w, out int h)
    {
        bgra = Array.Empty<byte>(); w = h = 0;
        IShellItemImageFactory? factory = null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref IID_Factory, out factory);
            if (factory.GetImage(new Size(size, size), SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbm) != 0 || hbm == IntPtr.Zero)
                return false;

            if (NativeMethods.GetObject(hbm, Marshal.SizeOf<NativeMethods.BitmapObj>(), out var bmp) == 0) return false;
            w = bmp.Width; h = Math.Abs(bmp.Height);
            if (w <= 0 || h <= 0) return false;

            var bmi = new NativeMethods.BitmapInfoHeader
            {
                Size = Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(), Width = w, Height = -h, Planes = 1, BitCount = 32,
            };
            bgra = new byte[w * h * 4];
            IntPtr dc = NativeMethods.GetDC(IntPtr.Zero);
            try { if (NativeMethods.GetDIBits(dc, hbm, 0, (uint)h, bgra, ref bmi, 0) == 0) return false; }
            finally { NativeMethods.ReleaseDC(IntPtr.Zero, dc); }

            // The factory hands back premultiplied alpha; the composer wants straight alpha.
            for (int i = 0; i < bgra.Length; i += 4)
            {
                int a = bgra[i + 3];
                if (a is > 0 and < 255)
                    for (int c = 0; c < 3; c++) bgra[i + c] = (byte)Math.Min(255, bgra[i + c] * 255 / a);
            }
            return true;
        }
        catch (Exception) { return false; }
        finally
        {
            if (hbm != IntPtr.Zero) NativeMethods.DeleteObject(hbm);
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }

    // ------------------------------------------------------------------ small icons for file results

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref ShFileInfo info, uint size, uint flags);

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Icon for a file result; per-extension (cheap) except for types whose icon lives in the file itself.</summary>
    public static ImageSource? ForFile(string path, string ext, bool isDirectory)
    {
        bool perFile = !isDirectory && ext is "exe" or "lnk" or "ico" or "url" or "msi" or "scr";
        string key = isDirectory ? "<dir>" : perFile ? path : "." + ext;
        if (Cache.TryGetValue(key, out var hit)) return hit;

        ImageSource? img = Load(perFile ? path : isDirectory ? "folder" : "file." + ext,
            isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, useAttributesOnly: !perFile);
        if (Cache.Count > 4000) Cache.Clear();
        Cache[key] = img;
        return img;
    }

    private static ImageSource? Load(string name, uint attrs, bool useAttributesOnly)
    {
        var info = new ShFileInfo();
        uint flags = SHGFI_ICON | SHGFI_LARGEICON | (useAttributesOnly ? SHGFI_USEFILEATTRIBUTES : 0);
        IntPtr ok = SHGetFileInfo(name, attrs, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
        if (ok == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { NativeMethods.DestroyIcon(info.Icon); }
    }
}
