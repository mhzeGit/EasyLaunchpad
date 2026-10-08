using System.IO.Compression;
using Launchpad.Core.Imaging;

// Renders sample icons side by side on a blurred-looking backdrop so the tile styling can be judged by eye.
if (args.Length == 2 && args[0] == "ico") { MakeIco(args[1]); return; }
string outPath = args.Length > 0 ? args[0] : "iconlab.png";

byte[] Glyph(Func<float, float, (float r, float g, float b, float a)> f, int s = 256)
{
    var px = new byte[s * s * 4];
    for (int y = 0; y < s; y++) for (int x = 0; x < s; x++)
    {
        var (r, g, b, a) = f((x + .5f) / s, (y + .5f) / s);
        int i = (y * s + x) * 4;
        px[i] = (byte)(b * 255); px[i + 1] = (byte)(g * 255); px[i + 2] = (byte)(r * 255); px[i + 3] = (byte)(a * 255);
    }
    return px;
}

var samples = new List<byte[]>
{
    IconComposer.ComposeLogo(224, true),
    // white circle logo (light glyph -> dark tile)
    IconComposer.Compose(Glyph((u, v) => { float d = MathF.Sqrt((u - .5f) * (u - .5f) + (v - .5f) * (v - .5f)); return (1, 1, 1, d < .42f ? 1 : 0); }), 256, 256),
    // dark blue document-ish glyph (dark glyph -> light tile)
    IconComposer.Compose(Glyph((u, v) => (u > .2f && u < .8f && v > .1f && v < .9f) ? (.1f, .25f, .7f, 1) : (0, 0, 0, 0)), 256, 256),
    // orange glyph
    IconComposer.Compose(Glyph((u, v) => { float d = MathF.Abs(u - .5f) + MathF.Abs(v - .5f); return (1, .55f, .1f, d < .45f ? 1 : 0); }), 256, 256),
    // full-bleed coloured plate (UWP style)
    IconComposer.Compose(Glyph((u, v) => (.0f, .45f + .3f * v, .85f, 1)), 256, 256),
};

int S = 224, pad = 20, W = samples.Count * S + (samples.Count + 1) * pad, H = S + 2 * pad;
var img = new byte[W * H * 4];
for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) // a soft coloured backdrop, like a blurred wallpaper
{
    int i = (y * W + x) * 4;
    float u = (float)x / W, v = (float)y / H;
    img[i] = (byte)(120 + 80 * v); img[i + 1] = (byte)(70 + 50 * u); img[i + 2] = (byte)(90 + 60 * (1 - u)); img[i + 3] = 255;
}
for (int n = 0; n < samples.Count; n++)
{
    int ox = pad + n * (S + pad), oy = pad;
    for (int y = 0; y < S; y++) for (int x = 0; x < S; x++)
    {
        int si = (y * S + x) * 4, di = ((oy + y) * W + ox + x) * 4;
        int a = samples[n][si + 3];
        for (int c = 0; c < 3; c++) img[di + c] = (byte)(samples[n][si + c] + img[di + c] * (255 - a) / 255);
    }
}
WritePng(outPath, img, W, H);
Console.WriteLine($"wrote {outPath} ({W}x{H})");

static void WritePng(string path, byte[] bgra, int w, int h)
{
    using var fs = File.Create(path);
    fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    void Chunk(string type, byte[] data)
    {
        var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); fs.Write(len);
        var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray(); fs.Write(td);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in td) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1; }
        var c = BitConverter.GetBytes(~crc); Array.Reverse(c); fs.Write(c);
    }
    var ihdr = new byte[13];
    BitConverter.GetBytes(w).Reverse().ToArray().CopyTo(ihdr, 0); BitConverter.GetBytes(h).Reverse().ToArray().CopyTo(ihdr, 4);
    ihdr[8] = 8; ihdr[9] = 6;
    Chunk("IHDR", ihdr);
    var raw = new byte[(w * 4 + 1) * h];
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
    {
        int si = (y * w + x) * 4, di = y * (w * 4 + 1) + 1 + x * 4;
        raw[di] = bgra[si + 2]; raw[di + 1] = bgra[si + 1]; raw[di + 2] = bgra[si]; raw[di + 3] = bgra[si + 3];
    }
    using var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(raw);
    Chunk("IDAT", ms.ToArray());
    Chunk("IEND", Array.Empty<byte>());
}

static void MakeIco(string path)
{
    int[] sizes = { 256, 128, 64, 48, 32, 24, 16 };
    var pngs = new List<byte[]>();
    foreach (int s in sizes)
    {
        var px = IconComposer.ComposeLogo(s, shadow: false);
        // ICO wants straight alpha PNGs; the composer returns premultiplied.
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a > 0 && a < 255) for (int c = 0; c < 3; c++) px[i + c] = (byte)Math.Min(255, px[i + c] * 255 / a);
        }
        string tmp = Path.GetTempFileName();
        WritePng(tmp, px, s, s);
        pngs.Add(File.ReadAllBytes(tmp)); File.Delete(tmp);
    }
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        w.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
        w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
        w.Write(pngs[i].Length); w.Write(offset);
        offset += pngs[i].Length;
    }
    foreach (var p in pngs) w.Write(p);
    Console.WriteLine($"wrote {path}");
}
