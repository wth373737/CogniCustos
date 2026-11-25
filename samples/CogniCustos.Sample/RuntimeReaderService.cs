using CogniCustos.Core;
using CogniCustos.Documents;
using CogniCustos.Ui.ViewModels;

namespace CogniCustos.Sample;

/// <summary>
/// The reader, backed by the real thing: PDFium for the pages, PdfPig for the words, and the
/// runtime's models for the commentary.
///
/// This is the only place where "a file on disk" meets "a page on screen". It lives in the
/// sample (not in the UI) for the same reason the shell's data source does: the sandbox must
/// stay runnable without a PDF engine or a model.
/// </summary>
public sealed class RuntimeReaderService : IReaderService
{
    private readonly CogniCustosRuntime _runtime;
    private readonly DocLecture _lecture;
    private readonly CogniCustos.Contracts.IVisionModel? _vision;
    private readonly Action<string>? _log;

    private DocumentBook? _book;
    private string _title = string.Empty;

    public RuntimeReaderService(CogniCustosRuntime runtime, CogniCustos.Contracts.IVisionModel? vision = null,
        Action<string>? log = null)
    {
        _runtime = runtime;
        _vision = vision;
        _log = log;
        _lecture = new DocLecture(runtime.Models, vision);
    }

    public string Title => _title;

    public int PageCount => _book?.PageCount ?? 0;

    public string? Open(string path)
    {
        Close();

        var opened = DocumentOpener.TryOpen(path, _runtime.Root, out var message, _log);
        if (opened is null) return message;

        _book = opened.Book;
        // show the name the user picked, not the converted PDF's name
        _title = Path.GetFileName(opened.OriginalPath);
        return null;
    }

    public ReaderPageImage? Render(int page, int targetWidth)
    {
        if (_book is null) return null;
        var rendered = _book.Render(page, targetWidth);
        return new ReaderPageImage(rendered.Bgra, rendered.Width, rendered.Height);
    }

    public string PageText(int page) => _book?.Text(page) ?? string.Empty;

    public string TextIn(int page, double x0, double y0, double x1, double y1) =>
        _book?.TextIn(page, x0, y0, x1, y1) ?? string.Empty;

    public async Task<ReaderOutline> OutlineAsync(CancellationToken ct = default)
    {
        if (_book is null) return new ReaderOutline(string.Empty, []);

        // one pass over the text layer; no model sees more than a few hundred chars per page
        var texts = new List<string>(_book.PageCount);
        for (var page = 1; page <= _book.PageCount; page++)
        {
            ct.ThrowIfCancellationRequested();
            texts.Add(_book.Text(page));
        }

        var outline = await _lecture.OutlineAsync(_title, texts, ct).ConfigureAwait(false);
        return new ReaderOutline(outline.Summary,
            outline.Sections.Select(s => new ReaderSection(s.Page, s.Title)).ToList());
    }

    public async Task<string> AskAsync(int page, double x0, double y0, double x1, double y1,
        string question, CancellationToken ct = default)
    {
        if (_book is null) return string.Empty;

        var regionText = _book.TextIn(page, x0, y0, x1, y1);

        // A box smaller than this is a stray click, not a question about a picture. Sending a
        // one-pixel crop to the vision model used to produce a confident answer about nothing.
        var area = Math.Abs(x1 - x0) * Math.Abs(y1 - y0);
        var hasBox = area >= 0.0025;   // a quarter of a percent of the page

        // No words under a real box means it is a picture (a chart, a scanned slide). Render the
        // page once at reading resolution and hand the cropped pixels to the vision model.
        byte[]? regionPng = null;
        if (hasBox && string.IsNullOrWhiteSpace(regionText) && _vision is { IsAvailable: true })
        {
            var full = _book.Render(page, 1600);
            regionPng = full.Crop(x0, y0, x1, y1).ToPng();
        }

        LastAskTrace = new AskTrace(page, regionText.Length, hasBox, regionPng?.Length ?? 0);

        var ask = new DocQuestion(_title, page, _book.Text(page), regionText, question, hasBox);
        return await _lecture.AskAsync(ask, regionPng, ct).ConfigureAwait(false);
    }

    /// <summary>What the last question looked like, for the diagnostics and the test-run sheet.</summary>
    public AskTrace LastAskTrace { get; private set; } = new(0, 0, false, 0);

    public sealed record AskTrace(int Page, int RegionChars, bool HadBox, int RegionPngBytes);

    /// <summary>See the exact prompt a question sends (system, user).</summary>
    public void TracePrompts(Action<string, string> trace) => _lecture.PromptTrace = trace;

    public void Close()
    {
        _book?.Dispose();
        _book = null;
        _title = string.Empty;
    }
}
