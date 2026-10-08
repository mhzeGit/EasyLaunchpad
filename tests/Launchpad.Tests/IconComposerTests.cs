using Launchpad.Core.Imaging;

namespace Launchpad.Tests;

public class IconComposerTests
{
    private const int S = 256, C = IconComposer.DefaultCanvas, T = IconComposer.DefaultTile;

    private static byte[] Source(Func<float, float, (byte r, byte g, byte b, byte a)> f)
    {
        var px = new byte[S * S * 4];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                var (r, g, b, a) = f((x + .5f) / S, (y + .5f) / S);
                int i = (y * S + x) * 4;
                px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a;
            }
        return px;
    }

    private static (int r, int g, int b, int a) Px(byte[] canvas, int x, int y)
    {
        int i = (y * C + x) * 4;
        return (canvas[i + 2], canvas[i + 1], canvas[i], canvas[i + 3]);
    }

    private static int Luma((int r, int g, int b, int a) p) => (p.r * 299 + p.g * 587 + p.b * 114) / 1000;

    // a point just inside the tile's top edge, away from the glyph
    private static (int, int, int, int) TileSample(byte[] c) => Px(c, C / 2, (C - T) / 2 + 16);

    private static bool InCircle(float u, float v, float r) => (u - .5f) * (u - .5f) + (v - .5f) * (v - .5f) < r * r;

    [Fact]
    public void OutputHasTheCanvasSizeAndAShapedAlpha()
    {
        var src = Source((u, v) => InCircle(u, v, .4f) ? ((byte)200, (byte)30, (byte)30, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));
        var o = IconComposer.Compose(src, S, S);
        Assert.Equal(C * C * 4, o.Length);
        Assert.Equal(255, Px(o, C / 2, C / 2).a);          // the art itself is opaque
        Assert.True(Px(o, 0, 0).a < 8);                       // far corner: nothing, not even shadow
    }

    private static byte[] RedCircle() =>
        Source((u, v) => InCircle(u, v, .4f) ? ((byte)200, (byte)30, (byte)30, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0));

    [Fact]
    public void TransparentAreasStayTransparent_NoTileBehindTheArt()
    {
        var o = IconComposer.Compose(RedCircle(), S, S);
        // well outside the circle but inside where a tile would have been: nothing there (a faint shadow at most)
        Assert.True(Px(o, C / 2 - 78, C / 2 - 78).a < 8, "top-left of the old tile area must be transparent");
        Assert.True(Px(o, C / 2 + 78, C / 2 - 78).a < 8);
        Assert.True(Px(o, C / 2 - 78, C / 2 + 78).a < 40);
    }

    [Fact]
    public void ArtKeepsItsOwnColoursAndIsScaledUp()
    {
        var o = IconComposer.Compose(RedCircle(), S, S);
        var (r, g, b, a) = Px(o, C / 2, C / 2);
        Assert.Equal(255, a);
        Assert.True(r > 180 && g < 60 && b < 60, $"expected the circle's red, got {r},{g},{b}");

        int spans = 0;   // the circle's diameter (0.8 of the source) is scaled to ~94% of the tile, not left at 80% of a smaller box
        for (int x = 0; x < C; x++) if (Px(o, x, C / 2).a == 255) spans++;
        Assert.True(spans > T * 0.88, $"art should fill most of the tile width, spans {spans}px");
    }

    [Fact]
    public void WhiteArtStaysWhiteOnTransparent()
    {
        var o = IconComposer.Compose(Source((u, v) => InCircle(u, v, .4f) ? ((byte)255, (byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0)), S, S);
        Assert.True(Luma(Px(o, C / 2, C / 2)) > 240);
        Assert.True(Px(o, 4, 4).a < 8);   // no dark plate invented behind white art
    }

    [Fact]
    public void ShadowFallsBelowTheArtOnly()
    {
        var o = IconComposer.Compose(RedCircle(), S, S);
        int below = Px(o, C / 2, (C + (int)(T * 0.94)) / 2 + 6).a;
        int above = Px(o, C / 2, (C - (int)(T * 0.94)) / 2 - 6).a;
        Assert.True(below > above, $"shadow should be heavier below ({below}) than above ({above})");
    }

    [Fact]
    public void FullBleedPlateFillsTheTile()
    {
        var o = IconComposer.Compose(Source((u, v) => ((byte)0, (byte)110, (byte)220, (byte)255)), S, S);
        var (r, g, b, a) = Px(o, C / 2, (C - T) / 2 + 30);
        Assert.Equal(255, a);
        Assert.True(b > 150 && r < 90, $"expected the plate's blue, got {r},{g},{b}");
    }

    [Fact]
    public void SmallLogoOnAFlatPlateIsScaledUpLikeAnyGlyph()
    {
        // a red plate with a small white logo: after composing, the tile must not be the flat red any more
        // (the plate is keyed out) and the logo must be much bigger than its 8% share of the source
        var src = Source((u, v) => Math.Abs(u - .5f) < .06f && Math.Abs(v - .5f) < .06f ? ((byte)255, (byte)255, (byte)255, (byte)255) : ((byte)220, (byte)30, (byte)30, (byte)255));
        var o = IconComposer.Compose(src, S, S);

        var edge = TileSample(o);
        Assert.False(edge.Item1 > 180 && edge.Item2 < 90, "the flat plate colour should have been dropped");

        int whiteWidth = 0;
        for (int x = 0; x < C; x++) if (Luma(Px(o, x, C / 2)) > 235 && Px(o, x, C / 2).a == 255) whiteWidth++;
        Assert.True(whiteWidth > T * 0.5, $"logo should span over half the tile, spans {whiteWidth}px");
    }

    [Fact]
    public void LightGradientBackingBehindADrawingIsRemoved()
    {
        // pale grey gradient square (like the Clock app) with a dark round drawing in the middle
        var src = Source((u, v) => InCircle(u, v, .3f)
            ? ((byte)60, (byte)70, (byte)80, (byte)255)
            : ((byte)(235 - 25 * v), (byte)(235 - 25 * v), (byte)(238 - 25 * v), (byte)255));
        var o = IconComposer.Compose(src, S, S);
        Assert.True(Px(o, C / 2 - 70, C / 2 - 70).a < 8, "the pale backing should be gone");
        Assert.True(Px(o, C / 2, C / 2).a == 255 && Luma(Px(o, C / 2, C / 2)) < 120, "the drawing stays");
    }

    [Fact]
    public void ColouredPlateWithDrawingIsKept()
    {
        var src = Source((u, v) => InCircle(u, v, .3f) ? ((byte)255, (byte)255, (byte)255, (byte)255) : ((byte)0, (byte)100, (byte)220, (byte)255));
        var o = IconComposer.Compose(src, S, S);
        Assert.Equal(255, Px(o, C / 2 - 70, (C - T) / 2 + 30).a);   // still a filled tile
    }

    [Fact]
    public void GreyBodyWithTransparentCornersBecomesAGreyPlate_NotAWhiteBox()
    {
        // like the Clock app: a grey rounded body that fills the frame, white face, transparent top corners
        var src = Source((u, v) =>
        {
            bool corner = (u < .2f && v < .2f && (.2f - u) * (.2f - u) + (.2f - v) * (.2f - v) > .04f * .6f)
                       || (u > .8f && v < .2f && (u - .8f) * (u - .8f) + (.2f - v) * (.2f - v) > .04f * .6f);
            if (corner) return ((byte)0, (byte)0, (byte)0, (byte)0);
            return InCircle(u, v, .32f) ? ((byte)240, (byte)240, (byte)240, (byte)255) : ((byte)110, (byte)115, (byte)120, (byte)255);
        });
        var o = IconComposer.Compose(src, S, S);
        var (r, g, b, a) = Px(o, C / 2 - 70, (C - T) / 2 + 14);   // inside the tile near its top-left, over the transparent corner
        Assert.Equal(255, a);
        Assert.True(Luma((r, g, b, a)) < 170, $"corner should be grey like the body, not white (luma {Luma((r, g, b, a))})");
    }

    [Fact]
    public void DegenerateInputsDontThrow()
    {
        Assert.Equal(C * C * 4, IconComposer.Compose(new byte[S * S * 4], S, S).Length);                 // fully transparent
        Assert.Equal(C * C * 4, IconComposer.Compose(new byte[4], 1, 1).Length);                          // 1x1
        var opaqueNoAlpha = new byte[16 * 16 * 4];                                                        // alpha all zero but colour present
        for (int i = 0; i < opaqueNoAlpha.Length; i += 4) opaqueNoAlpha[i] = 200;
        Assert.Equal(C * C * 4, IconComposer.Compose(opaqueNoAlpha, 16, 16).Length);
    }

    [Fact]
    public void LogoRendersAtEveryIconSize()
    {
        foreach (int size in new[] { 16, 32, 48, 256 })
        {
            var o = IconComposer.ComposeLogo(size, shadow: false);
            Assert.Equal(size * size * 4, o.Length);
            Assert.Equal(255, o[(size / 2 * size + size / 2) * 4 + 3]);
        }
    }
}
