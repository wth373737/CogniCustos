using System.Text;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace CogniCustos.Documents;

/// <summary>One rendered page: BGRA32 pixels, top row first.</summary>
public sealed record RenderedPage(int Width, int Height, byte[] Bgra)
{
    public byte[] ToPng() => PngEncoder.FromBgra(Width, Height, Bgra);

    /// <summary>Crop a rectangle given in normalised page coordinates (0..1, origin top-left).</summary>
    public RenderedPage Crop(double x0, double y0, double x1, double y1)
    {
        var left = (int)Math.Clamp(Math.Floor(Math.Min(x0, x1) * Width), 0, Width - 1);
        var right = (int)Math.Clamp(Math.Ceiling(Math.Max(x0, x1) * Width), left + 1, Width);
        var top = (int)Math.Clamp(Math.Floor(Math.Min(y0, y1) * Height), 0, Height - 1);
        var bottom = (int)Math.Clamp(Math.Ceiling(Math.Max(y0, y1) * Height), top + 1, Height);

        var w = right - left;
        var h = bottom - top;
        var pixels = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
        {
            var src = ((top + row) * Width + left) * 4;
            Buffer.BlockCopy(Bgra, src, pixels, row * w * 4, w * 4);
        }

        return new RenderedPage(w, h, pixels);
    }
}

/// <summary>One word with its box, normalised to 0..1 with the origin at the top-left.</summary>
public sealed record WordBox(string Text, double X0, double Y0, double X1, double Y1)
{
    public bool CentreInside(double x0, double y0, double x1, double y1)
    {
        var cx = (X0 + X1) / 2;
        var cy = (Y0 + Y1) / 2;
        return cx >= Math.Min(x0, x1) && cx <= Math.Max(x0, x1)
            && cy >= Math.Min(y0, y1) && cy <= Math.Max(y0, y1);
    }
}

/// <summary>
/// A document the reader can page through: rasterised pages, a text layer, and word boxes
/// for the selection rectangle.
///
/// Two engines, two jobs. Docnet (PDFium) draws the page to BGRA; PdfPig reads the text
/// layer *with coordinates*. Neither brings SkiaSharp, so the app keeps Avalonia's own one.
/// PDFium is not thread-safe, so every render goes through one lock.
/// </summary>
public sealed class DocumentBook : IDisposable
{
    private static readonly object RenderLock = new();

    private readonly PdfDocument? _pig;
    private readonly List<(double Width, double Height)> _sizes = [];

    /// <summary>PDFium bakes the pixel box into the reader, so it is re-created when the
    /// requested resolution changes (cheap: opening a document is a few milliseconds).</summary>
    private IDocReader? _doc;
    private (int Width, int Height) _docBox;

    public string Path { get; }
    public string Title { get; }
    public int PageCount { get; }
    public bool HasTextLayer => _pig is not null;

    private DocumentBook(string path, string title, PdfDocument? pig, int pageCount)
    {
        Path = path;
        Title = title;
        _pig = pig;
        PageCount = pageCount;
    }

    /// <summary>Open a PDF (already normalised by <see cref="DocumentOpener"/>).</summary>
    public static DocumentBook OpenPdf(string pdfPath)
    {
        var pig = TryOpenPig(pdfPath);
        var count = pig?.NumberOfPages ?? 0;

        if (count == 0)
        {
            // no text layer: ask PDFium for the page count, and keep that reader for rendering
            try
            {
                using var probe = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(600, 600));
                count = probe.GetPageCount();
            }
            catch (Exception ex)
            {
                pig?.Dispose();
                throw new InvalidOperationException("PDF 打不开：" + ex.Message, ex);
            }
        }

        return new DocumentBook(pdfPath, System.IO.Path.GetFileName(pdfPath), pig, count);
    }

    private static PdfDocument? TryOpenPig(string path)
    {
        try
        {
            return PdfDocument.Open(path);
        }
        catch
        {
            // A broken or encrypted text layer must not stop the pages from being drawn.
            return null;
        }
    }

    /// <summary>Page size in PDF points (1/72 inch). Falls back to A4 when there is no text layer.</summary>
    public (double Width, double Height) PageSize(int page1)
    {
        lock (_sizes)
        {
            if (page1 >= 1 && page1 <= _sizes.Count) return _sizes[page1 - 1];
        }

        var size = (595d, 842d);
        if (_pig is not null && page1 >= 1 && page1 <= _pig.NumberOfPages)
        {
            var p = _pig.GetPage(page1);
            size = (p.Width, p.Height);
        }

        lock (_sizes)
        {
            while (_sizes.Count < page1) _sizes.Add(size);
            _sizes[page1 - 1] = size;
        }

        return size;
    }

    /// <summary>Draw one page so its long edge is about <paramref name="targetLongEdge"/> pixels.</summary>
    public RenderedPage Render(int page1, int targetLongEdge = 1600)
    {
        if (page1 < 1 || page1 > PageCount) throw new ArgumentOutOfRangeException(nameof(page1));

        var (pw, ph) = PageSize(page1);
        var scale = targetLongEdge / Math.Max(pw, ph);
        var boxW = Math.Max(64, (int)Math.Round(pw * scale));
        var boxH = Math.Max(64, (int)Math.Round(ph * scale));

        // Docnet wants the page box as (short side, long side) and rejects anything else.
        var shortSide = Math.Min(boxW, boxH);
        var longSide = Math.Max(boxW, boxH);

        lock (RenderLock)
        {
            if (_doc is null || _docBox != (shortSide, longSide))
            {
                _doc?.Dispose();
                _doc = DocLib.Instance.GetDocReader(Path, new PageDimensions(shortSide, longSide));
                _docBox = (shortSide, longSide);
            }

            using var page = _doc.GetPageReader(page1 - 1);
            var w = page.GetPageWidth();
            var h = page.GetPageHeight();
            var bytes = page.GetImage();
            return new RenderedPage(w, h, FlattenOnWhite(bytes));
        }
    }

    /// <summary>
    /// A PDF page is transparent where nothing was painted. Paper is not: flatten onto white
    /// so the reader (and the vision model) sees a page, not a hole in the window.
    /// </summary>
    private static byte[] FlattenOnWhite(byte[] bgra)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var alpha = bgra[i + 3];
            if (alpha == 255) continue;

            var uncovered = 255 - alpha;
            bgra[i] = (byte)(bgra[i] * alpha / 255 + uncovered);
            bgra[i + 1] = (byte)(bgra[i + 1] * alpha / 255 + uncovered);
            bgra[i + 2] = (byte)(bgra[i + 2] * alpha / 255 + uncovered);
            bgra[i + 3] = 255;
        }

        return bgra;
    }

    /// <summary>
    /// The text layer of one page, grouped into lines.
    ///
    /// PDFium/PdfPig hand back words; joining them with newlines (the first version did) turns a
    /// 20-word sentence into 20 "lines", which wastes prompt budget and reads like a list to the
    /// model. Words stay on a line while their baseline hardly moves, and a real line break is
    /// inserted when it does.
    /// </summary>
    public string Text(int page1)
    {
        if (_pig is null || page1 < 1 || page1 > _pig.NumberOfPages) return string.Empty;
        try
        {
            var page = _pig.GetPage(page1);
            if (page.Width <= 0) return string.Empty;

            var words = page.GetWords()
                .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .OrderByDescending(w => w.BoundingBox.Bottom)
                .ThenBy(w => w.BoundingBox.Left)
                .ToList();

            if (words.Count == 0) return string.Empty;

            var text = new StringBuilder();
            var baseline = words[0].BoundingBox.Bottom;
            var lineHeight = Math.Max(4, words[0].BoundingBox.Height);

            foreach (var word in words)
            {
                // a new visual line when the baseline jumps by more than ~60% of a line
                if (Math.Abs(word.BoundingBox.Bottom - baseline) > lineHeight * 0.6)
                {
                    text.Append('\n');
                    baseline = word.BoundingBox.Bottom;
                }
                else if (text.Length > 0 && text[^1] != '\n')
                {
                    text.Append(' ');
                }

                text.Append(word.Text);
            }

            return text.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Every word of a page, boxes normalised to 0..1 with the origin top-left.</summary>
    public IReadOnlyList<WordBox> Words(int page1)
    {
        if (_pig is null || page1 < 1 || page1 > _pig.NumberOfPages) return [];
        try
        {
            var page = _pig.GetPage(page1);
            var (w, h) = (page.Width, page.Height);
            if (w <= 0 || h <= 0) return [];

            return page.GetWords()
                .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                .Select(word =>
                {
                    var b = word.BoundingBox;
                    // PDF space is bottom-up; the UI's is top-down.
                    return new WordBox(
                        word.Text,
                        b.Left / w,
                        (h - b.Top) / h,
                        b.Right / w,
                        (h - b.Bottom) / h);
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Words whose centre falls inside the rectangle (normalised, origin top-left).</summary>
    public IReadOnlyList<WordBox> WordsIn(int page1, double x0, double y0, double x1, double y1)
        => Words(page1).Where(w => w.CentreInside(x0, y0, x1, y1)).ToList();

    public string TextIn(int page1, double x0, double y0, double x1, double y1)
        => string.Join(" ", WordsIn(page1, x0, y0, x1, y1).Select(w => w.Text));

    public void Dispose()
    {
        try { _doc?.Dispose(); } catch { /* pdfium teardown is best effort */ }
        try { _pig?.Dispose(); } catch { }
    }
}
