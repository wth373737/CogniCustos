using CogniCustos.Ui.Localization;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// The demo document the sandbox opens. It draws its pages (no PDF library, no file on
/// disk) so the reader can be built, screenshotted and regression-tested on its own; the
/// runnable sample swaps in the real thing through the same <see cref="IReaderService"/>.
/// </summary>
public sealed class DemoReaderService : IReaderService
{
    private const int PageWidth = 900;
    private const int PageHeight = 1273;

    private static readonly uint[] PageAccents =
    [
        0xFF4B6B63, 0xFF5B4B6B, 0xFF6B5B4B, 0xFF4B5B6B, 0xFF695B5B, 0xFF4B6B5B, 0xFF5B6B4B, 0xFF5B4B5B,
    ];

    public string Title => Localizer.Get("Demo.Reader.Title");

    public int PageCount => 8;

    public string? Open(string path) => null;

    public ReaderPageImage Render(int page, int targetWidth)
    {
        var scale = Math.Max(0.25, (double)targetWidth / PageWidth);
        var w = (int)Math.Round(PageWidth * scale);
        var h = (int)Math.Round(PageHeight * scale);
        var pixels = new byte[w * h * 4];

        var accent = PageAccents[(Math.Max(1, page) - 1) % PageAccents.Length];
        Fill(pixels, w, h, 0xFFFFFFFF);

        // header band
        FillRect(pixels, w, h, Scaled(28, scale), Scaled(30, scale), Scaled(844, scale), Scaled(96, scale), accent);

        // a title bar and two subtitle bars
        FillRect(pixels, w, h, Scaled(60, scale), Scaled(170, scale), Scaled(620, scale), Scaled(26, scale), 0xFF3A3A44);
        FillRect(pixels, w, h, Scaled(60, scale), Scaled(212, scale), Scaled(430, scale), Scaled(16, scale), 0xFF8A8A96);
        FillRect(pixels, w, h, Scaled(60, scale), Scaled(240, scale), Scaled(360, scale), Scaled(16, scale), 0xFFB0B0BA);

        // body lines: deterministic widths so screenshots stay stable
        var seed = Math.Max(1, page) * 7919;
        var y = 320;
        for (var i = 0; i < 22 && y < 1180; i++)
        {
            seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF;
            var width = 240 + seed % 520;
            FillRect(pixels, w, h, Scaled(60, scale), Scaled(y, scale), Scaled(width, scale), Scaled(14, scale), 0xFFC9C9D2);
            y += i % 6 == 5 ? 46 : 30;
        }

        // page number block, bottom right
        FillRect(pixels, w, h, Scaled(760, scale), Scaled(1200, scale), Scaled(80, scale), Scaled(34, scale), accent);

        return new ReaderPageImage(pixels, w, h);
    }

    public string PageText(int page) => Localizer.Get("Demo.Reader.PageText");

    public string TextIn(int page, double x0, double y0, double x1, double y1) =>
        Localizer.Get("Demo.Reader.SelectionText");

    public Task<ReaderOutline> OutlineAsync(CancellationToken ct = default) =>
        Task.FromResult(new ReaderOutline(
            Localizer.Get("Demo.Reader.Summary"),
            [
                new ReaderSection(1, Localizer.Get("Demo.Reader.Section1")),
                new ReaderSection(2, Localizer.Get("Demo.Reader.Section2")),
                new ReaderSection(5, Localizer.Get("Demo.Reader.Section3")),
                new ReaderSection(7, Localizer.Get("Demo.Reader.Section4")),
            ]));

    public Task<string> AskAsync(int page, double x0, double y0, double x1, double y1,
        string question, CancellationToken ct = default) =>
        Task.FromResult(Localizer.Get("Demo.Reader.Answer"));

    public void Close()
    {
    }

    private static int Scaled(int value, double scale) => (int)Math.Round(value * scale);

    private static void Fill(byte[] pixels, int w, int h, uint argb)
    {
        var b = (byte)argb;
        var g = (byte)(argb >> 8);
        var r = (byte)(argb >> 16);
        for (var i = 0; i < w * h; i++)
        {
            var o = i * 4;
            pixels[o] = b;
            pixels[o + 1] = g;
            pixels[o + 2] = r;
            pixels[o + 3] = 0xFF;
        }
    }

    private static void FillRect(byte[] pixels, int w, int h, int x, int y, int rw, int rh, uint argb)
    {
        if (rw <= 0 || rh <= 0) return;
        var b = (byte)argb;
        var g = (byte)(argb >> 8);
        var r = (byte)(argb >> 16);

        for (var row = y; row < y + rh && row < h; row++)
        {
            if (row < 0) continue;
            for (var col = x; col < x + rw && col < w; col++)
            {
                if (col < 0) continue;
                var o = (row * w + col) * 4;
                pixels[o] = b;
                pixels[o + 1] = g;
                pixels[o + 2] = r;
                pixels[o + 3] = 0xFF;
            }
        }
    }
}
