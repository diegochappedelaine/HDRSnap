using Xunit;

namespace HDRSnap.Tests;

public class PixelMathTests
{
    // ----- helpers : image scRGB FP16, 4 canaux (RGBA), rowPitch = w * 8 octets -----

    static Half[] Solid(int w, int h, float r, float g, float b)
    {
        var px = new Half[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 4 + 0] = (Half)r;
            px[i * 4 + 1] = (Half)g;
            px[i * 4 + 2] = (Half)b;
            px[i * 4 + 3] = (Half)1f;
        }
        return px;
    }

    static void Set(Half[] px, int w, int x, int y, float r, float g, float b)
    {
        int i = (y * w + x) * 4;
        px[i] = (Half)r; px[i + 1] = (Half)g; px[i + 2] = (Half)b;
    }

    static unsafe T Pinned<T>(Half[] px, Func<IntPtr, T> body)
    {
        fixed (Half* p = px) return body((IntPtr)p);
    }

    // ----- SrgbByte -----

    [Theory]
    [InlineData(-1f, 0)]
    [InlineData(0f, 0)]
    [InlineData(1f, 255)]
    [InlineData(5f, 255)]
    [InlineData(0.5f, 188)]   // 1.055 * 0.5^(1/2.4) - 0.055 = 0.7354 -> 188
    public void SrgbByte_encodes_and_clips(float linear, int expected)
        => Assert.Equal((byte)expected, Capturer.SrgbByte(linear));

    // ----- EncodeRow -----

    [Fact]
    public void EncodeRow_without_tone_writes_bgra_and_clips()
    {
        // 3 pixels : rouge pur, blanc x2 (écrêté), noir
        float[] rgb = { 1f, 0f, 0f,  2f, 2f, 2f,  0f, 0f, 0f };
        var bgra = new byte[3 * 4];
        Capturer.EncodeRow(rgb, bgra, 0, 3, tone: false, invWhite2: 0f);
        Assert.Equal(new byte[] { 0, 0, 255, 255,  255, 255, 255, 255,  0, 0, 0, 255 }, bgra);
    }

    [Fact]
    public void EncodeRow_with_tone_dims_white()
    {
        // Reinhard étendu, blanc = 3.0 : un pixel à 1.0 devient ~0.556 linéaire (~197 sRGB)
        float[] rgb = { 1f, 1f, 1f };
        var bgra = new byte[4];
        Capturer.EncodeRow(rgb, bgra, 0, 1, tone: true, invWhite2: 1f / 9f);
        Assert.Equal(bgra[0], bgra[1]);
        Assert.Equal(bgra[1], bgra[2]);
        Assert.InRange(bgra[0], 190, 205);
        Assert.Equal(255, bgra[3]);
    }

    // ----- LuminanceStats -----

    [Fact]
    public void LuminanceStats_percentile_ignores_isolated_bright_pixel()
    {
        const int w = 100, h = 100;              // 10 000 pixels -> les 1 plus lumineux ignorés
        var px = Solid(w, h, 1f, 1f, 1f);
        Set(px, w, 50, 50, 10f, 10f, 10f);
        var (p9999, max) = Pinned(px, p => Capturer.LuminanceStats(p, w * 8, w, h));
        Assert.InRange(p9999, 1.0f, 1.02f);      // bord haut du bin contenant 1.0
        Assert.Equal(10f, max);
    }

    [Fact]
    public void LuminanceStats_ignores_nan_and_inf()
    {
        const int w = 100, h = 100;
        var px = Solid(w, h, 1f, 1f, 1f);
        Set(px, w, 1, 1, float.NaN, float.NaN, float.NaN);
        Set(px, w, 2, 2, float.PositiveInfinity, 0f, 0f);
        var (p9999, max) = Pinned(px, p => Capturer.LuminanceStats(p, w * 8, w, h));
        Assert.InRange(p9999, 1.0f, 1.02f);
        Assert.Equal(1f, max);
    }

    // ----- ToneMapScRgbToBgra (chemin CPU) -----

    [Fact]
    public void ToneMapScRgbToBgra_keeps_sdr_white_at_255()
    {
        const int w = 4, h = 2;
        var px = Solid(w, h, 1f, 1f, 1f);        // 1.0 scRGB = 80 nits = blanc SDR à 80 nits
        var bgra = Pinned(px, p => Capturer.ToneMapScRgbToBgra(p, w * 8, w, h, sdrNits: 80f));
        Assert.All(bgra, b => Assert.Equal(255, b));
    }

    // ----- CopyBgra -----

    [Fact]
    public unsafe void CopyBgra_forces_opaque_alpha()
    {
        byte[] src = { 1, 2, 3, 0,  4, 5, 6, 7 };
        byte[] outp;
        fixed (byte* p = src) outp = Capturer.CopyBgra((IntPtr)p, 8, 2, 1);
        Assert.Equal(new byte[] { 1, 2, 3, 255,  4, 5, 6, 255 }, outp);
    }
}
