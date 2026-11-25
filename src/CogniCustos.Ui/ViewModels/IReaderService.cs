namespace CogniCustos.Ui.ViewModels;

/// <summary>One rasterised page, as the UI receives it: BGRA32, top row first.</summary>
public sealed record ReaderPageImage(byte[] Bgra, int Width, int Height);

/// <summary>One row of the page index.</summary>
public sealed record ReaderSection(int Page, string Title);

/// <summary>Summary + page index, produced by reading the whole file once.</summary>
public sealed record ReaderOutline(string Summary, IReadOnlyList<ReaderSection> Sections);

/// <summary>
/// Everything the reader window needs from the outside world, behind one seam.
///
/// The UI never touches a PDF library: it asks for a page as pixels, for the text of a
/// rectangle, and for answers. The demo fills this with a drawn dummy document; the runnable
/// sample fills it with PDFium + the models. That is what keeps the UI project free of
/// Core, of Docnet and of every model call.
/// </summary>
public interface IReaderService
{
    /// <summary>Open a file. Null on success, else the reason it could not be opened.</summary>
    string? Open(string path);

    string Title { get; }

    int PageCount { get; }

    /// <summary>Rasterise a page about <paramref name="targetWidth"/> pixels wide.</summary>
    ReaderPageImage? Render(int page, int targetWidth);

    string PageText(int page);

    /// <summary>Text of the selection (normalised 0..1, origin top-left). Empty when the
    /// region is a picture: the service decides whether to look at it with a vision model.</summary>
    string TextIn(int page, double x0, double y0, double x1, double y1);

    /// <summary>Read the whole file once → summary + page index. May take seconds.</summary>
    Task<ReaderOutline> OutlineAsync(CancellationToken ct = default);

    /// <summary>Answer a question about one selection on one page.</summary>
    Task<string> AskAsync(int page, double x0, double y0, double x1, double y1, string question,
        CancellationToken ct = default);

    void Close();
}
