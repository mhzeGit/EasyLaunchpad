namespace Launchpad.Core.Imaging;

/// <summary>Colours for the tile an arbitrary glyph is placed on (0..255 per channel, top to bottom).</summary>
public readonly record struct TileStyle(float TopR, float TopG, float TopB, float BotR, float BotG, float BotB)
{
    public static readonly TileStyle Light = new(252, 252, 255, 212, 218, 232);
    public static readonly TileStyle Dark = new(78, 88, 108, 30, 34, 46);
    public static readonly TileStyle Indigo = new(96, 112, 255, 38, 36, 142);
}

/// <summary>
/// Turns whatever icon an application ships into a consistent "launcher" icon: rounded tile, soft contact +
/// ambient shadow, a thin rim light, and a glossy sheen. Everything is plain pixel maths on arrays, so it is
/// fast, thread-safe and independent of WPF.
/// </summary>
public static class IconComposer
{
    public const int DefaultCanvas = 224;
    public const int DefaultTile = 176;

    /// <summary>Straight-alpha BGRA source in, premultiplied BGRA canvas out.</summary>
    /// <remarks>
    /// Opaque, full-bleed icons ("plates") fill a rounded glossy tile. Icons with transparent areas keep them: the artwork is
    /// scaled up and drawn on its own with a soft shadow, with no tile behind it. <paramref name="forcedStyle"/> (used for the
    /// product logo) always puts the artwork on a tile of that colour.
    /// </remarks>
    public static byte[] Compose(ReadOnlySpan<byte> srcBgra, int srcW, int srcH, int canvas = DefaultCanvas, int tile = DefaultTile,
        bool shadow = true, TileStyle? forcedStyle = null)
    {
        var src = ToPremultipliedFloat(srcBgra, srcW, srcH);
        Analyze(src, srcW, srcH, out int x0, out int y0, out int x1, out int y1, out float fill, out float lum);

        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        bool plate = forcedStyle == null && bw >= srcW * 0.92f && bh >= srcH * 0.92f && fill >= 0.88f;

        // A "plate" that is mostly one flat colour with a small logo in the middle (common for packaged apps) looks
        // lost on our tile: drop the flat colour and treat the logo as ordinary transparent artwork so it gets scaled up.
        if (plate && (TryKeyOutFlatBackground(src, srcW, srcH) || TryKeyOutLightBacking(src, srcW, srcH)))
        {
            Analyze(src, srcW, srcH, out x0, out y0, out x1, out y1, out fill, out lum);
            bw = x1 - x0 + 1; bh = y1 - y0 + 1;
            plate = false;
        }

        if (!plate && forcedStyle == null)
            return RenderTransparentArt(src, srcW, srcH, x0, y0, bw, bh, canvas, tile, shadow);

        float[] layer;     // premultiplied RGBA covering the whole tile (plate) or the glyph box
        int layerW, layerH, layerX, layerY;
        TileStyle style = forcedStyle ?? (plate ? PlateStyle(src, srcW, srcH) : TileStyle.Light);

        if (plate)
        {
            // Already a coloured plate: fill the tile with it (trimming a hair of its own border).
            int inset = Math.Min(Math.Max(1, (int)(Math.Min(srcW, srcH) * 0.015f)), (Math.Min(srcW, srcH) - 1) / 2);
            layer = Resample(src, srcW, srcH, inset, inset, srcW - 2 * inset, srcH - 2 * inset, tile, tile);
            layerW = layerH = tile; layerX = layerY = 0;
        }
        else
        {
            float maxBox = tile * 0.60f;
            float scale = Math.Min(maxBox / bw, maxBox / bh);
            layerW = Math.Max(1, (int)MathF.Round(bw * scale));
            layerH = Math.Max(1, (int)MathF.Round(bh * scale));
            layer = Resample(src, srcW, srcH, x0, y0, bw, bh, layerW, layerH);
            layerX = (tile - layerW) / 2;
            layerY = (tile - layerH) / 2 + (int)(tile * 0.005f);
        }

        float[]? glyphShadow = null;
        if (!plate) glyphShadow = BuildGlyphShadow(layer, layerW, layerH, tile, layerX, layerY);

        return Render(canvas, tile, shadow, style, plate, layer, layerW, layerH, layerX, layerY, glyphShadow, lum);
    }

    /// <summary>Draws icon artwork that has transparent areas on its own: scaled up, centred, with a soft two-part shadow.</summary>
    private static byte[] RenderTransparentArt(float[] src, int sw, int sh, int x0, int y0, int bw, int bh, int canvas, int tile, bool shadow)
    {
        float k = tile / 176f;
        float maxBox = tile * 0.94f;
        float scale = Math.Min(maxBox / bw, maxBox / bh);
        int lw = Math.Max(1, (int)MathF.Round(bw * scale)), lh = Math.Max(1, (int)MathF.Round(bh * scale));
        var art = Resample(src, sw, sh, x0, y0, bw, bh, lw, lh);
        int ox = (canvas - lw) / 2, oy = (canvas - lh) / 2 - (int)(2 * k);   // a touch above centre, the shadow falls below

        // alpha of the artwork on the full canvas, then two blurred, offset copies for the shadow
        float[] ambient = new float[canvas * canvas], contact = new float[canvas * canvas];
        if (shadow)
        {
            int ambOff = (int)MathF.Round(8 * k), conOff = Math.Max(1, (int)MathF.Round(2 * k));
            for (int y = 0; y < lh; y++)
                for (int x = 0; x < lw; x++)
                {
                    float a = art[(y * lw + x) * 4 + 3];
                    if (a <= 0) continue;
                    int cx = ox + x, cy = oy + y;
                    if (cx < 0 || cx >= canvas) continue;
                    if (cy + ambOff >= 0 && cy + ambOff < canvas) ambient[(cy + ambOff) * canvas + cx] = a;
                    if (cy + conOff >= 0 && cy + conOff < canvas) contact[(cy + conOff) * canvas + cx] = a;
                }
            var tmp = new float[canvas * canvas];
            int ra = Math.Max(2, (int)MathF.Round(5 * k)), rc = Math.Max(1, (int)MathF.Round(1.5f * k));
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(ambient, tmp, canvas, canvas, ra, horizontal: true); BoxBlur(tmp, ambient, canvas, canvas, ra, horizontal: false);
            }
            for (int pass = 0; pass < 2; pass++)
            {
                BoxBlur(contact, tmp, canvas, canvas, rc, horizontal: true); BoxBlur(tmp, contact, canvas, canvas, rc, horizontal: false);
            }
        }

        var output = new byte[canvas * canvas * 4];
        for (int y = 0; y < canvas; y++)
            for (int x = 0; x < canvas; x++)
            {
                float sa = shadow ? 1 - (1 - Math.Min(1, ambient[y * canvas + x] * 0.55f)) * (1 - Math.Min(1, contact[y * canvas + x] * 0.40f)) : 0;
                float r = 0, g = 0, b = 0, a = 0;
                int ax = x - ox, ay = y - oy;
                if (ax >= 0 && ax < lw && ay >= 0 && ay < lh)
                {
                    int i = (ay * lw + ax) * 4;
                    b = art[i]; g = art[i + 1]; r = art[i + 2]; a = art[i + 3];
                }
                // premultiplied "over": art on top of a black shadow
                float outA = a + sa * (1 - a);
                int o = (y * canvas + x) * 4;
                output[o] = (byte)Math.Clamp(b * 255f + 0.5f, 0, 255);
                output[o + 1] = (byte)Math.Clamp(g * 255f + 0.5f, 0, 255);
                output[o + 2] = (byte)Math.Clamp(r * 255f + 0.5f, 0, 255);
                output[o + 3] = (byte)Math.Clamp(outA * 255f + 0.5f, 0, 255);
            }
        return output;
    }

    // ------------------------------------------------------------------ analysis & resampling

    /// <summary>
    /// If the image is a flat-coloured plate whose real content (a logo) occupies well under half of it, makes the flat
    /// colour transparent in place and returns true. Soft edge so anti-aliased logo borders survive.
    /// </summary>
    private static bool TryKeyOutFlatBackground(float[] src, int w, int h)
    {
        // sample the plate colour well inside the corners (rounded plates have transparent corners)
        (float r, float g, float b) Sample(float fx, float fy)
        {
            int x = (int)(w * fx), y = (int)(h * fy), i = (y * w + x) * 4;
            float a = src[i + 3];
            return a < 0.9f ? (-1, -1, -1) : (src[i + 2] / a, src[i + 1] / a, src[i] / a);
        }
        var s = new[] { Sample(0.12f, 0.12f), Sample(0.88f, 0.12f), Sample(0.12f, 0.88f), Sample(0.88f, 0.88f), Sample(0.5f, 0.07f), Sample(0.5f, 0.93f) };
        if (s.Any(c => c.r < 0)) return false;
        float bgR = s.Average(c => c.r), bgG = s.Average(c => c.g), bgB = s.Average(c => c.b);
        if (s.Any(c => Math.Max(Math.Abs(c.r - bgR), Math.Max(Math.Abs(c.g - bgG), Math.Abs(c.b - bgB))) > 0.08f)) return false;   // not flat

        float Dist(int i)
        {
            float a = src[i + 3];
            if (a < 0.05f) return 0;
            return Math.Max(Math.Abs(src[i + 2] / a - bgR), Math.Max(Math.Abs(src[i + 1] / a - bgG), Math.Abs(src[i] / a - bgB)));
        }

        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (src[(y * w + x) * 4 + 3] > 0.5f && Dist((y * w + x) * 4) > 0.22f)
                {
                    if (x < x0) x0 = x; if (x > x1) x1 = x;
                    if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
        if (x1 < 0) return false;
        if ((x1 - x0 + 1) > w * 0.58f && (y1 - y0 + 1) > h * 0.58f) return false;   // content already fills the plate

        for (int i = 0; i < w * h * 4; i += 4)
        {
            float keep = SmoothStep(0.10f, 0.22f, Dist(i));
            src[i] *= keep; src[i + 1] *= keep; src[i + 2] *= keep; src[i + 3] *= keep;
        }
        return true;
    }

    /// <summary>
    /// Some icons ship a light, neutral backing (white or pale grey, often a soft gradient) behind a smaller drawing, e.g. the
    /// Clock app. Flood-fills that backing inward from the border and makes it transparent so the drawing is treated like any
    /// other transparent artwork. Coloured plates are never touched. Returns false (and changes nothing) if it isn't that case.
    /// </summary>
    /// <summary>
    /// Tile colours for a plate icon, taken from the plate's own edge. Where the plate has transparent corners (a rounded body,
    /// a dome...) the tile shows through, and it should look like more of the plate rather than a white box.
    /// </summary>
    private static TileStyle PlateStyle(float[] src, int w, int h)
    {
        int band = Math.Max(2, Math.Min(w, h) / 16);
        double r = 0, g = 0, b = 0, wsum = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (x >= band && x < w - band && y >= band && y < h - band) { x = w - band - 1; continue; }
                int i = (y * w + x) * 4; float a = src[i + 3];
                if (a < 0.9f) continue;
                b += src[i] / a; g += src[i + 1] / a; r += src[i + 2] / a; wsum++;
            }
        if (wsum < 8) return TileStyle.Light;
        float R = (float)(r / wsum) * 255f, G = (float)(g / wsum) * 255f, B = (float)(b / wsum) * 255f;
        static float Up(float v) => Math.Min(255f, v * 1.10f);
        static float Down(float v) => v * 0.88f;
        return new TileStyle(Up(R), Up(G), Up(B), Down(R), Down(G), Down(B));
    }

    private static readonly int[] Neighbours = { 1, 0, -1, 0, 0, 1, 0, -1 };

    private static bool TryKeyOutLightBacking(float[] src, int w, int h)
    {
        if (w < 8 || h < 8) return false;
        float Lum(int i, float a) => a <= 0 ? 0 : (0.114f * src[i] + 0.587f * src[i + 1] + 0.299f * src[i + 2]) / a;
        (float r, float g, float b) Rgb(int i) { float a = src[i + 3]; return a <= 0 ? (0, 0, 0) : (src[i + 2] / a, src[i + 1] / a, src[i] / a); }

        // the border band must be light and neutral, or this isn't a pale backing (a grey drawing is not a backing)
        int band = Math.Max(2, Math.Min(w, h) / 20);
        double lum = 0, sat = 0; int n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (x >= band && x < w - band && y >= band && y < h - band) { x = w - band - 1; continue; }
                int i = (y * w + x) * 4; float a = src[i + 3];
                if (a < 0.9f) continue;
                var (r, g, b) = Rgb(i);
                lum += Lum(i, a); sat += Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)); n++;
            }
        if (n < (w + h)) return false;
        if (lum / n < 0.72 || sat / n > 0.10) return false;

        var removed = new bool[w * h];
        var origin = new (float r, float g, float b)[w * h];   // colour of the border pixel each removed pixel was reached from
        var queue = new Queue<int>();
        void Seed(int x, int y) { int p = y * w + x; if (!removed[p]) { removed[p] = true; origin[p] = Rgb(p * 4); queue.Enqueue(p); } }
        for (int x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
        for (int y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }

        int count = 0;
        while (queue.Count > 0)
        {
            int p = queue.Dequeue(); count++;
            int px = p % w, py = p / w, pi = p * 4;
            var (pr, pg, pb) = Rgb(pi);
            float pa = src[pi + 3];
            for (int dir = 0; dir < 4; dir++)
            {
                int dx = Neighbours[dir * 2], dy = Neighbours[dir * 2 + 1];
                int nx = px + dx, ny = py + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int q = ny * w + nx;
                if (removed[q]) continue;
                int qi = q * 4; float qa = src[qi + 3];
                bool backing = qa < 0.05f;
                if (!backing && pa >= 0.05f)
                {
                    var (qr, qg, qb) = Rgb(qi);
                    // continues smoothly from its neighbour (a gradient), and is still light and neutral
                    float step = Math.Max(Math.Abs(qr - pr), Math.Max(Math.Abs(qg - pg), Math.Abs(qb - pb)));
                    // also close to the border colour it started from: a gradient fades slowly, the drawing's own rim differs at the same height
                    var o = origin[p];
                    float drift = Math.Max(Math.Abs(qr - o.r), Math.Max(Math.Abs(qg - o.g), Math.Abs(qb - o.b)));
                    backing = step < 0.045f && drift < 0.25f && Lum(qi, qa) > 0.62f && Math.Max(qr, Math.Max(qg, qb)) - Math.Min(qr, Math.Min(qg, qb)) < 0.14f;
                }
                if (backing) { removed[q] = true; origin[q] = origin[p]; queue.Enqueue(q); }
            }
        }

        double fraction = (double)count / (w * h);
        if (fraction < 0.06 || fraction > 0.85) return false;   // nothing to remove, or it would erase the icon itself

        for (int p = 0; p < removed.Length; p++)
            if (removed[p]) { int i = p * 4; src[i] = src[i + 1] = src[i + 2] = src[i + 3] = 0; }
        return true;
    }

    private static float[] ToPremultipliedFloat(ReadOnlySpan<byte> bgra, int w, int h)
    {
        var f = new float[w * h * 4];
        bool anyAlpha = false;
        for (int i = 3; i < w * h * 4; i += 4) if (bgra[i] != 0) { anyAlpha = true; break; }
        for (int i = 0; i < w * h; i++)
        {
            float a = anyAlpha ? bgra[i * 4 + 3] / 255f : 1f;
            f[i * 4 + 0] = bgra[i * 4 + 0] / 255f * a;
            f[i * 4 + 1] = bgra[i * 4 + 1] / 255f * a;
            f[i * 4 + 2] = bgra[i * 4 + 2] / 255f * a;
            f[i * 4 + 3] = a;
        }
        return f;
    }

    private static void Analyze(float[] src, int w, int h, out int x0, out int y0, out int x1, out int y1, out float fill, out float lum)
    {
        x0 = w; y0 = h; x1 = -1; y1 = -1;
        double lumSum = 0, aSum = 0;
        long opaque = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                float a = src[i + 3];
                if (a > 0.06f)
                {
                    if (x < x0) x0 = x; if (x > x1) x1 = x;
                    if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
                if (a > 0.78f) opaque++;
                // premultiplied: sum of colour already includes alpha weight
                lumSum += 0.0722 * src[i] + 0.7152 * src[i + 1] + 0.2126 * src[i + 2];
                aSum += a;
            }
        if (x1 < 0) { x0 = 0; y0 = 0; x1 = w - 1; y1 = h - 1; }
        long area = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
        fill = area == 0 ? 0 : (float)opaque / area;
        lum = aSum < 1e-3 ? 0.5f : (float)(lumSum / aSum);
    }

    /// <summary>Tent-filter resample of a sub-rectangle (premultiplied RGBA floats). Handles both up and down scaling.</summary>
    private static float[] Resample(float[] src, int sw, int sh, int rx, int ry, int rw, int rh, int dw, int dh)
    {
        var tmp = new float[dw * rh * 4];
        float sx = (float)rw / dw;
        for (int x = 0; x < dw; x++)
        {
            float center = (x + 0.5f) * sx - 0.5f + rx;
            float support = Math.Max(1f, sx);
            int lo = (int)MathF.Floor(center - support), hi = (int)MathF.Ceiling(center + support);
            for (int y = 0; y < rh; y++)
            {
                float r = 0, g = 0, b = 0, a = 0, wsum = 0;
                for (int k = lo; k <= hi; k++)
                {
                    int kk = Math.Clamp(k, rx, rx + rw - 1);
                    float wgt = Math.Max(0, 1 - MathF.Abs(k - center) / support);
                    if (wgt <= 0) continue;
                    int i = ((ry + y) * sw + kk) * 4;
                    r += src[i] * wgt; g += src[i + 1] * wgt; b += src[i + 2] * wgt; a += src[i + 3] * wgt; wsum += wgt;
                }
                int o = (y * dw + x) * 4;
                if (wsum > 0) { tmp[o] = r / wsum; tmp[o + 1] = g / wsum; tmp[o + 2] = b / wsum; tmp[o + 3] = a / wsum; }
            }
        }

        var dst = new float[dw * dh * 4];
        float sy = (float)rh / dh;
        for (int y = 0; y < dh; y++)
        {
            float center = (y + 0.5f) * sy - 0.5f;
            float support = Math.Max(1f, sy);
            int lo = (int)MathF.Floor(center - support), hi = (int)MathF.Ceiling(center + support);
            for (int x = 0; x < dw; x++)
            {
                float r = 0, g = 0, b = 0, a = 0, wsum = 0;
                for (int k = lo; k <= hi; k++)
                {
                    int kk = Math.Clamp(k, 0, rh - 1);
                    float wgt = Math.Max(0, 1 - MathF.Abs(k - center) / support);
                    if (wgt <= 0) continue;
                    int i = (kk * dw + x) * 4;
                    r += tmp[i] * wgt; g += tmp[i + 1] * wgt; b += tmp[i + 2] * wgt; a += tmp[i + 3] * wgt; wsum += wgt;
                }
                int o = (y * dw + x) * 4;
                if (wsum > 0) { dst[o] = r / wsum; dst[o + 1] = g / wsum; dst[o + 2] = b / wsum; dst[o + 3] = a / wsum; }
            }
        }
        return dst;
    }

    /// <summary>Soft shadow of the glyph's alpha, tile-sized, so the logo seems to float slightly above the plate.</summary>
    private static float[] BuildGlyphShadow(float[] layer, int lw, int lh, int tile, int lx, int ly)
    {
        var a = new float[tile * tile];
        int off = Math.Max(1, tile / 60);
        for (int y = 0; y < lh; y++)
            for (int x = 0; x < lw; x++)
            {
                int tx = lx + x, ty = ly + y + off;
                if (tx >= 0 && tx < tile && ty >= 0 && ty < tile) a[ty * tile + tx] = layer[(y * lw + x) * 4 + 3];
            }
        int radius = Math.Max(2, tile / 48);
        var tmp = new float[a.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            BoxBlur(a, tmp, tile, tile, radius, horizontal: true);
            BoxBlur(tmp, a, tile, tile, radius, horizontal: false);
        }
        return a;
    }

    private static void BoxBlur(float[] src, float[] dst, int w, int h, int r, bool horizontal)
    {
        int len = horizontal ? w : h, lines = horizontal ? h : w;
        float inv = 1f / (2 * r + 1);
        for (int line = 0; line < lines; line++)
        {
            float sum = 0;
            int Idx(int k) => horizontal ? line * w + k : k * w + line;
            for (int k = -r; k <= r; k++) sum += src[Idx(Math.Clamp(k, 0, len - 1))];
            for (int k = 0; k < len; k++)
            {
                dst[Idx(k)] = sum * inv;
                sum += src[Idx(Math.Min(len - 1, k + r + 1))] - src[Idx(Math.Max(0, k - r))];
            }
        }
    }

    // ------------------------------------------------------------------ rendering

    private static byte[] Render(int canvas, int tile, bool shadow, TileStyle style, bool plate,
        float[] layer, int lw, int lh, int lx, int ly, float[]? glyphShadow, float glyphLum)
    {
        var output = new byte[canvas * canvas * 4];
        float k = tile / 176f;                       // everything below is authored for a 176px tile
        float half = tile / 2f, radius = tile * 0.2237f;
        float cx = canvas / 2f, cy = canvas / 2f;
        float left = cx - half, top = cy - half;
        bool darkTile = style.TopR + style.TopG + style.TopB < 450;
        float glyphShadowStrength = darkTile ? 0.55f : 0.30f;

        Parallel.For(0, canvas, py =>
        {
            for (int px = 0; px < canvas; px++)
            {
                float fx = px + 0.5f, fy = py + 0.5f;
                float d = RoundRectSdf(fx - cx, fy - cy, half, radius);

                // --- shadow (premultiplied black)
                float sa = 0;
                if (shadow)
                {
                    float d1 = RoundRectSdf(fx - cx, fy - cy - 2.5f * k, half, radius);
                    float d2 = RoundRectSdf(fx - cx, fy - cy - 9f * k, half - 1f * k, radius);
                    float a1 = 0.34f * (1 - SmoothStep(-2.5f * k, 3.5f * k, d1));
                    float a2 = 0.40f * (1 - SmoothStep(-12f * k, 12f * k, d2));
                    sa = 1 - (1 - a1) * (1 - a2);
                }

                float cov = Math.Clamp(0.5f - d, 0f, 1f);
                float r = 0, g = 0, b = 0, a = 0;

                if (cov > 0)
                {
                    float t = Math.Clamp((fy - top) / tile, 0f, 1f);
                    int tx = Math.Clamp((int)(fx - left), 0, tile - 1), ty = Math.Clamp((int)(fy - top), 0, tile - 1);

                    // base: tile gradient, or the app's own plate
                    if (plate)
                    {
                        int i = (ty * lw + tx) * 4;
                        float la = layer[i + 3];
                        // composite the plate over the neutral tile to avoid see-through edges
                        float br = Lerp(style.TopR, style.BotR, t) / 255f, bg = Lerp(style.TopG, style.BotG, t) / 255f, bb = Lerp(style.TopB, style.BotB, t) / 255f;
                        b = layer[i] + bb * (1 - la); g = layer[i + 1] + bg * (1 - la); r = layer[i + 2] + br * (1 - la);
                    }
                    else
                    {
                        b = Lerp(style.TopB, style.BotB, t) / 255f;
                        g = Lerp(style.TopG, style.BotG, t) / 255f;
                        r = Lerp(style.TopR, style.BotR, t) / 255f;

                        if (glyphShadow != null)
                        {
                            float gs = glyphShadow[ty * tile + tx] * glyphShadowStrength;
                            r *= 1 - gs; g *= 1 - gs; b *= 1 - gs;
                        }
                        int gx = tx - lx, gy = ty - ly;
                        if (gx >= 0 && gx < lw && gy >= 0 && gy < lh)
                        {
                            int i = (gy * lw + gx) * 4;
                            float la = layer[i + 3];
                            b = layer[i] + b * (1 - la); g = layer[i + 1] + g * (1 - la); r = layer[i + 2] + r * (1 - la);
                        }
                    }

                    // --- gloss: a large circle centred above the tile paints a curved sheen on the top half
                    float gdx = fx - cx, gdy = fy - (top - 0.62f * tile);
                    float dd = 1.18f * tile - MathF.Sqrt(gdx * gdx + gdy * gdy);
                    float sheen = SmoothStep(0f, 16f * k, dd) * (1 - SmoothStep(0.05f, 0.58f, t));
                    float sheenA = 0.26f * sheen;
                    r += (1 - r) * sheenA; g += (1 - g) * sheenA; b += (1 - b) * sheenA;

                    // --- soft light from below
                    float glow = 0.10f * SmoothStep(0.72f, 1f, t) * (1 - SmoothStep(0, 14f * k, -d));
                    r += (1 - r) * glow; g += (1 - g) * glow; b += (1 - b) * glow;

                    // --- rim: bright hairline along the top edge, faint dark one along the bottom
                    float inner = -d;
                    float rim = 1 - SmoothStep(0f, 1.6f * k, inner);
                    float rimTop = rim * (1 - SmoothStep(0.15f, 0.85f, t)) * 0.60f;
                    float rimBot = rim * SmoothStep(0.45f, 1f, t) * 0.22f;
                    r += (1 - r) * rimTop; g += (1 - g) * rimTop; b += (1 - b) * rimTop;
                    r *= 1 - rimBot; g *= 1 - rimBot; b *= 1 - rimBot;

                    r = Math.Clamp(r, 0, 1); g = Math.Clamp(g, 0, 1); b = Math.Clamp(b, 0, 1);
                    a = 1;
                }

                // tile over shadow (premultiplied "over")
                float outA = cov + sa * (1 - cov);
                float outR = r * cov;
                float outG = g * cov;
                float outB = b * cov;
                int o = (py * canvas + px) * 4;
                output[o + 0] = (byte)Math.Clamp(outB * 255f + 0.5f, 0, 255);
                output[o + 1] = (byte)Math.Clamp(outG * 255f + 0.5f, 0, 255);
                output[o + 2] = (byte)Math.Clamp(outR * 255f + 0.5f, 0, 255);
                output[o + 3] = (byte)Math.Clamp(outA * 255f + 0.5f, 0, 255);
                _ = a;
            }
        });
        return output;
    }

    private static float RoundRectSdf(float px, float py, float half, float radius)
    {
        float qx = MathF.Abs(px) - (half - radius), qy = MathF.Abs(py) - (half - radius);
        float ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
        return MathF.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - radius;
    }

    private static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3 - 2 * t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // ------------------------------------------------------------------ the product logo

    /// <summary>The Launchpad logo: a 3x3 grid of colourful rounded squares on a deep indigo tile.</summary>
    public static byte[] ComposeLogo(int canvas, bool shadow)
    {
        const int s = 256;
        var glyph = new byte[s * s * 4];
        (byte r, byte g, byte b)[] colors =
        {
            (255, 99, 132), (255, 170, 64), (255, 214, 64),
            (64, 220, 140), (72, 190, 255), (120, 120, 255),
            (200, 110, 255), (255, 130, 200), (240, 244, 255),
        };
        float cell = s / 3.6f, gap = (s - 3 * cell) / 2f;
        float rr = cell * 0.30f;
        for (int gy = 0; gy < 3; gy++)
            for (int gx = 0; gx < 3; gx++)
            {
                float ox = gx * (cell + gap) + cell / 2f, oy = gy * (cell + gap) + cell / 2f;
                var (cr, cg, cb) = colors[gy * 3 + gx];
                for (int y = (int)(oy - cell / 2) - 1; y <= (int)(oy + cell / 2) + 1; y++)
                    for (int x = (int)(ox - cell / 2) - 1; x <= (int)(ox + cell / 2) + 1; x++)
                    {
                        if (x < 0 || y < 0 || x >= s || y >= s) continue;
                        float d = RoundRectSdf(x + 0.5f - ox, y + 0.5f - oy, cell / 2f, rr);
                        float cov = Math.Clamp(0.5f - d, 0f, 1f);
                        if (cov <= 0) continue;
                        int i = (y * s + x) * 4;
                        glyph[i] = cb; glyph[i + 1] = cg; glyph[i + 2] = cr; glyph[i + 3] = (byte)(cov * 255);
                    }
            }
        int tile = shadow ? (int)(canvas * 0.786f) : canvas;
        return Compose(glyph, s, s, canvas, tile, shadow, TileStyle.Indigo);
    }
}
