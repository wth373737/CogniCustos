using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>One question and the answer the lecture gave, kept in the reader's log.</summary>
public sealed class ReaderExchange
{
    public ReaderExchange(int page, string question, string answer)
    {
        Page = page;
        Question = string.IsNullOrWhiteSpace(question) ? Localizer.Get("Reader.DefaultQuestion") : question;
        Answer = answer;
    }

    public int Page { get; }
    public string Question { get; }
    public string Answer { get; }
    public string PageTag => "#" + Page;
}

/// <summary>A page-index row: the model's title for a page, plus the jump it performs.</summary>
public sealed class ReaderSectionRow
{
    public ReaderSectionRow(int page, string title, RelayCommand open)
    {
        Page = page;
        Title = title;
        Open = open;
    }

    public int Page { get; }
    public string Title { get; }
    public RelayCommand Open { get; }
}

/// <summary>
/// The point-read window: a page on screen, a box the user drags over a region, and an
/// answer about exactly that region.
///
/// The whole view-model talks to <see cref="IReaderService"/> only, so the sandbox runs it
/// against a drawn demo document and the sample runs it against PDFium plus the models.
/// Rendering and the outline call are slow, so they never block the UI thread.
/// </summary>
public sealed class ReaderViewModel : ObservableObject, ILocalizedViewModel
{
    private readonly Func<IReaderService> _factory;

    private IReaderService? _service;
    private bool _isOpen;
    private bool _isReading;
    private bool _isAsking;
    private int _currentPage = 1;
    private int _pageCount;
    private Bitmap? _pageBitmap;
    private string _title = string.Empty;
    private string _summary = string.Empty;
    private string _status = string.Empty;
    private string _question = string.Empty;
    private string _selectionText = string.Empty;
    private bool _hasSelection;
    private (double X0, double Y0, double X1, double Y1) _selection;
    private CancellationTokenSource? _outlineCts;

    public ReaderViewModel(Func<IReaderService> factory)
    {
        _factory = factory;

        CloseCommand = new RelayCommand(Close);
        NextPageCommand = new RelayCommand(() => GoToPage(_currentPage + 1), () => _currentPage < _pageCount);
        PreviousPageCommand = new RelayCommand(() => GoToPage(_currentPage - 1), () => _currentPage > 1);
        JumpCommand = new RelayCommand(parameter =>
        {
            if (parameter is ReaderSectionRow row) GoToPage(row.Page);
            else if (parameter is ReaderSection section) GoToPage(section.Page);
            else if (parameter is int page) GoToPage(page);
        });
        AskCommand = new RelayCommand(() => _ = AskAsync(), () => !_isAsking);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => _hasSelection);
    }

    // ---------- state ----------

    public bool IsOpen
    {
        get => _isOpen;
        private set => Set(ref _isOpen, value);
    }

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public int PageCount
    {
        get => _pageCount;
        private set
        {
            if (!Set(ref _pageCount, value)) return;
            OnPropertyChanged(nameof(PageLabel));
            NextPageCommand.RaiseCanExecuteChanged();
            PreviousPageCommand.RaiseCanExecuteChanged();
        }
    }

    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!Set(ref _currentPage, value)) return;
            OnPropertyChanged(nameof(PageLabel));
            NextPageCommand.RaiseCanExecuteChanged();
            PreviousPageCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Page counter, e.g. "3 / 8".</summary>
    public string PageLabel => _pageCount == 0 ? "–" : $"{_currentPage} / {_pageCount}";

    public Bitmap? PageBitmap
    {
        get => _pageBitmap;
        private set
        {
            var old = _pageBitmap;
            if (!Set(ref _pageBitmap, value)) return;
            old?.Dispose();
            OnPropertyChanged(nameof(HasPage));
        }
    }

    public bool HasPage => _pageBitmap is not null;

    /// <summary>True while the whole file is being read for the summary and page index.</summary>
    public bool IsReading
    {
        get => _isReading;
        private set
        {
            if (!Set(ref _isReading, value)) return;
            OnPropertyChanged(nameof(OutlineHint));
        }
    }

    public bool IsAsking
    {
        get => _isAsking;
        private set
        {
            if (!Set(ref _isAsking, value)) return;
            AskCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(AskLabel));
        }
    }

    public string AskLabel => Localizer.Get(_isAsking ? "Reader.Thinking" : "Reader.Ask");

    /// <summary>One line for errors and progress ("正在打开…", "转换失败：…").</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (!Set(ref _status, value)) return;
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(_status);

    public string Summary
    {
        get => _summary;
        private set
        {
            if (!Set(ref _summary, value)) return;
            OnPropertyChanged(nameof(HasSummary));
        }
    }

    public bool HasSummary => !string.IsNullOrWhiteSpace(_summary);

    public string OutlineHint => Localizer.Get(_isReading ? "Reader.Reading" : "Reader.Contents");

    public ObservableCollection<ReaderSectionRow> Contents { get; } = [];

    public ObservableCollection<ReaderExchange> Exchanges { get; } = [];

    public bool HasExchanges => Exchanges.Count > 0;

    public string Question
    {
        get => _question;
        set => Set(ref _question, value);
    }

    // ---------- selection ----------

    public bool HasSelection
    {
        get => _hasSelection;
        private set
        {
            if (!Set(ref _hasSelection, value)) return;
            ClearSelectionCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(SelectionLabel));
        }
    }

    public (double X0, double Y0, double X1, double Y1) Selection => _selection;

    public string SelectionText
    {
        get => _selectionText;
        private set => Set(ref _selectionText, value);
    }

    /// <summary>What the box currently covers, in words, for the row above the button.</summary>
    public string SelectionLabel
    {
        get
        {
            if (!_hasSelection) return Localizer.Get("Reader.NoSelection");
            return string.IsNullOrWhiteSpace(_selectionText)
                ? Localizer.Get("Reader.SelectionPicture")
                : Localizer.Get("Reader.Selection") + "：" + Clip(_selectionText, 90);
        }
    }

    // ---------- commands ----------

    public RelayCommand CloseCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand PreviousPageCommand { get; }
    public RelayCommand JumpCommand { get; }
    public RelayCommand AskCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }

    // ---------- opening ----------

    /// <summary>
    /// Open a file and show its first page. Returns null on success, or the reason the file
    /// could not be opened (the caller shows it; the window stays closed).
    /// </summary>
    public async Task<string?> OpenAsync(string path)
    {
        if (OpenCore(path) is { } failure) return failure;
        await ReadOutlineAsync().ConfigureAwait(true);
        return null;
    }

    /// <summary>
    /// Everything that must happen before the window appears: open, first page, clear the
    /// previous document's state. Shared by the async path and the fixture path.
    /// </summary>
    private string? OpenCore(string path)
    {
        _service ??= _factory();

        string? failure;
        try
        {
            failure = _service.Open(path);
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        if (failure is not null)
        {
            Status = Localizer.Get("Reader.OpenFailed") + " " + failure;
            return failure;
        }

        Title = _service.Title;
        PageCount = _service.PageCount;
        CurrentPage = 1;
        Summary = string.Empty;
        Status = string.Empty;
        Exchanges.Clear();
        OnPropertyChanged(nameof(HasExchanges));
        Contents.Clear();
        ClearSelection();
        IsOpen = true;

        ShowPage(1);
        return null;
    }

    /// <summary>
    /// Fixture path for the screenshot tool and the tests: opens and fills the outline
    /// synchronously. Only safe with a service whose outline completes without awaiting
    /// (the demo one does); the real service must go through <see cref="OpenAsync"/>.
    /// </summary>
    public void OpenPreview(string path)
    {
        if (OpenCore(path) is not null) return;
        try
        {
            ApplyOutline(_service!.OutlineAsync().GetAwaiter().GetResult());
        }
        catch
        {
            // a fixture that cannot read the outline still shows the page
        }
    }

    /// <summary>The whole-file pass: summary and page index, off the UI thread.</summary>
    public async Task ReadOutlineAsync()
    {
        if (_service is null || PageCount == 0) return;

        _outlineCts?.Cancel();
        _outlineCts = new CancellationTokenSource();
        var ct = _outlineCts.Token;

        IsReading = true;
        try
        {
            var outline = await Task.Run(() => _service.OutlineAsync(ct), ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            ApplyOutline(outline);
        }
        catch (OperationCanceledException)
        {
            // closing the reader cancels the pass
        }
        catch (Exception ex)
        {
            Status = Localizer.Get("Reader.OutlineFailed") + " " + ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsReading = false;
        }
    }

    private void ApplyOutline(ReaderOutline outline)
    {
        Summary = outline.Summary;
        Contents.Clear();
        foreach (var section in outline.Sections)
            Contents.Add(new ReaderSectionRow(section.Page, section.Title, JumpCommand));
        OnPropertyChanged(nameof(HasContents));
    }

    public bool HasContents => Contents.Count > 0;

    // ---------- paging ----------

    public void GoToPage(int page)
    {
        if (_service is null || _pageCount == 0) return;
        var target = Math.Clamp(page, 1, _pageCount);
        if (target == _currentPage && _pageBitmap is not null) return;

        CurrentPage = target;
        ClearSelection();
        ShowPage(target);
    }

    private void ShowPage(int page)
    {
        if (_service is null) return;
        try
        {
            var image = _service.Render(page, 1400);
            PageBitmap = image is null ? null : ToBitmap(image);
            Status = image is null ? Localizer.Get("Reader.PageFailed") : string.Empty;
        }
        catch (Exception ex)
        {
            PageBitmap = null;
            Status = Localizer.Get("Reader.PageFailed") + " " + ex.Message;
        }
    }

    // ---------- selection + asking ----------

    /// <summary>Called by the view when the rubber band is released (normalised coordinates).</summary>
    public void SelectRectangle(double x0, double y0, double x1, double y1)
    {
        var left = Math.Clamp(Math.Min(x0, x1), 0, 1);
        var right = Math.Clamp(Math.Max(x0, x1), 0, 1);
        var top = Math.Clamp(Math.Min(y0, y1), 0, 1);
        var bottom = Math.Clamp(Math.Max(y0, y1), 0, 1);

        // a stray click is not a selection
        if ((right - left) < 0.01 || (bottom - top) < 0.01)
        {
            ClearSelection();
            return;
        }

        _selection = (left, top, right, bottom);
        HasSelection = true;

        try
        {
            SelectionText = _service?.TextIn(_currentPage, left, top, right, bottom) ?? string.Empty;
        }
        catch
        {
            SelectionText = string.Empty;
        }

        OnPropertyChanged(nameof(SelectionLabel));
    }

    public void ClearSelection()
    {
        _selection = default;
        SelectionText = string.Empty;
        HasSelection = false;
    }

    public async Task AskAsync()
    {
        if (_service is null || !IsOpen || IsAsking) return;

        IsAsking = true;
        var page = _currentPage;
        var question = Question.Trim();
        var (x0, y0, x1, y1) = _selection;

        try
        {
            var answer = await _service.AskAsync(page, x0, y0, x1, y1, question).ConfigureAwait(true);
            Exchanges.Add(new ReaderExchange(page, question, answer));
            OnPropertyChanged(nameof(HasExchanges));
            Question = string.Empty;
        }
        catch (Exception ex)
        {
            Status = Localizer.Get("Reader.AskFailed") + " " + ex.Message;
        }
        finally
        {
            IsAsking = false;
        }
    }

    // ---------- lifecycle ----------

    public void Close()
    {
        _outlineCts?.Cancel();
        _outlineCts?.Dispose();
        _outlineCts = null;

        _service?.Close();
        _service = null;

        IsOpen = false;
        Title = string.Empty;
        Summary = string.Empty;
        Contents.Clear();
        Exchanges.Clear();
        OnPropertyChanged(nameof(HasExchanges));
        PageBitmap = null;
        PageCount = 0;
        CurrentPage = 1;
        Status = string.Empty;
        Question = string.Empty;
        IsReading = false;
        IsAsking = false;
        ClearSelection();
    }

    public void RefreshText()
    {
        OnPropertyChanged(nameof(AskLabel));
        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(OutlineHint));
        OnAllPropertiesChanged();
    }

    // ---------- pixels ----------

    /// <summary>BGRA32 from the service → an Avalonia bitmap (no encoder, no SkiaSharp of our own).</summary>
    private static Bitmap ToBitmap(ReaderPageImage image)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);

        using (var frame = bitmap.Lock())
        {
            var rowBytes = image.Width * 4;
            for (var y = 0; y < image.Height; y++)
            {
                Marshal.Copy(image.Bgra, y * rowBytes,
                    IntPtr.Add(frame.Address, y * frame.RowBytes), rowBytes);
            }
        }

        return bitmap;
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
