using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Launchpad.App.Native;

/// <summary>
/// Builds the frosted backdrop: grab the monitor, shrink it (which already blurs), blur it some more with cheap box
/// passes, and grade it darker and a touch more saturated. Works regardless of the Windows "transparency effects" setting.
/// </summary>
internal static class BackdropCapture
{
    /// <param name="monitor">Monitor rectangle in physical pixels.</param>
    /// <param name="targetWidth">Width of the working image; smaller means a stronger blur for the same radius.</param>
    public static unsafe BitmapSource? Capture(NativeMethods.Rect monitor, int targetWidth = 480, int blurRadius = 9, double brightness = 0.58, double saturation = 1.18)
    {
        int w = monitor.Right - monitor.Left, h = monitor.Bottom - monitor.Top;
        if (w <= 0 || h <= 0) return null;

        IntPtr screen = GdiCapture.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr mem = GdiCapture.CreateCompatibleDC(screen);
        IntPtr dib = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            var bmi = new GdiCapture.BitmapInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<GdiCapture.BitmapInfo>(), Width = w, Height = -h, Planes = 1, BitCount = 32 };
            dib = GdiCapture.CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            old = GdiCapture.SelectObject(mem, dib);
            if (!GdiCapture.BitBlt(mem, 0, 0, w, h, screen, monitor.Left, monitor.Top, GdiCapture.SRCCOPY | GdiCapture.CAPTUREBLT)) return null;

            int f = Math.Max(1, w / targetWidth);
            int sw = w / f, sh = h / f;
            var small = new byte[sw * sh * 4];
            uint* src = (uint*)bits;
            int stride = w;
            int block = f * f;

            fixed (byte* dstPtr = small)
            {
                byte* dst = dstPtr;
                Parallel.For(0, sh, y =>
                {
                    byte* row = dst + y * sw * 4;
                    for (int x = 0; x < sw; x++)
                    {
                        int r = 0, g = 0, b = 0;
                        for (int yy = 0; yy < f; yy++)
                        {
                            uint* p = src + (long)(y * f + yy) * stride + x * f;
                            for (int xx = 0; xx < f; xx++)
                            {
                                uint px = p[xx];
                                b += (int)(px & 0xFF); g += (int)((px >> 8) & 0xFF); r += (int)((px >> 16) & 0xFF);
                            }
                        }
                        row[x * 4] = (byte)(b / block); row[x * 4 + 1] = (byte)(g / block); row[x * 4 + 2] = (byte)(r / block); row[x * 4 + 3] = 255;
                    }
                });
            }

            var tmp = new byte[small.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(small, tmp, sw, sh, blurRadius, horizontal: true);
                BoxBlur(tmp, small, sw, sh, blurRadius, horizontal: false);
            }
            Grade(small, brightness, saturation);

            var bmp = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgra32, null, small, sw * 4);
            bmp.Freeze();
            return bmp;
        }
        catch (Exception) { return null; }
        finally
        {
            if (old != IntPtr.Zero) GdiCapture.SelectObject(mem, old);
            if (dib != IntPtr.Zero) GdiCapture.DeleteObject(dib);
            GdiCapture.DeleteDC(mem);
            GdiCapture.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static void BoxBlur(byte[] src, byte[] dst, int w, int h, int r, bool horizontal)
    {
        int len = horizontal ? w : h, lines = horizontal ? h : w;
        int window = 2 * r + 1;
        for (int line = 0; line < lines; line++)
        {
            int Idx(int k) => (horizontal ? line * w + k : k * w + line) * 4;
            int sb = 0, sg = 0, sr = 0;
            for (int k = -r; k <= r; k++)
            {
                int i = Idx(Math.Clamp(k, 0, len - 1));
                sb += src[i]; sg += src[i + 1]; sr += src[i + 2];
            }
            for (int k = 0; k < len; k++)
            {
                int o = Idx(k);
                dst[o] = (byte)(sb / window); dst[o + 1] = (byte)(sg / window); dst[o + 2] = (byte)(sr / window); dst[o + 3] = 255;
                int add = Idx(Math.Min(len - 1, k + r + 1)), sub = Idx(Math.Max(0, k - r));
                sb += src[add] - src[sub]; sg += src[add + 1] - src[sub + 1]; sr += src[add + 2] - src[sub + 2];
            }
        }
    }

    private static void Grade(byte[] px, double brightness, double saturation)
    {
        for (int i = 0; i < px.Length; i += 4)
        {
            double b = px[i], g = px[i + 1], r = px[i + 2];
            double gray = 0.114 * b + 0.587 * g + 0.299 * r;
            b = (gray + (b - gray) * saturation) * brightness;
            g = (gray + (g - gray) * saturation) * brightness;
            r = (gray + (r - gray) * saturation) * brightness;
            px[i] = (byte)Math.Clamp(b, 0, 255); px[i + 1] = (byte)Math.Clamp(g, 0, 255); px[i + 2] = (byte)Math.Clamp(r, 0, 255);
        }
    }
}
