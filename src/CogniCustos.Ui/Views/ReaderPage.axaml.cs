using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using CogniCustos.Ui.ViewModels;

namespace CogniCustos.Ui.Views;

/// <summary>
/// The page surface: rubber-band selection on top of the rendered page, and the one click
/// that opens another file.
///
/// The selection is kept in *normalised* page coordinates (0..1, origin top-left) because
/// that is the only form that survives a window resize, a different zoom or a different
/// file: the service turns it into PDF points when it needs to find words in the box.
/// </summary>
public partial class ReaderPage : UserControl
{
    private Point? _dragStart;
    private ReaderViewModel? _watched;

    public ReaderPage()
    {
        InitializeComponent();

        // The page can be laid out (or a fixture can set a box) after the selection was
        // announced, so keep the band pinned to the current page geometry as well.
        LayoutUpdated += (_, _) =>
        {
            if (_watched?.HasSelection == true) DrawCommitted();
        };
    }

    private ReaderViewModel? Reader => (DataContext as ShellViewModel)?.Reader;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        WatchReader();
    }

    /// <summary>Watch the view-model so a page change (which clears the box) also hides the band.</summary>
    private void WatchReader()
    {
        if (_watched is not null) _watched.PropertyChanged -= OnReaderPropertyChanged;

        _watched = Reader;
        if (_watched is not null) _watched.PropertyChanged += OnReaderPropertyChanged;
    }

    private void OnReaderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReaderViewModel.HasSelection))
        {
            // a selection can also be set without a drag (fixtures, tests): keep the band honest
            if (_watched?.HasSelection == true) DrawCommitted();
            else HideBand();
            return;
        }

        // a new page means new pixel geometry: put the box back where it now belongs
        if (e.PropertyName is nameof(ReaderViewModel.HasPage) or nameof(ReaderViewModel.CurrentPage))
        {
            if (_watched?.HasSelection == true) DrawCommitted();
            else HideBand();
        }
    }

    private void OnOpenFileClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
            _ = DocumentPicker.OpenIntoReaderAsync(shell, TopLevel.GetTopLevel(this));
    }

    // ---------- rubber band ----------

    private void OnPagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Reader?.HasPage != true) return;
        if (!e.GetCurrentPoint(PageHost).Properties.IsLeftButtonPressed) return;

        _dragStart = e.GetPosition(PageHost);
        e.Pointer.Capture(PageHost);
        HideBand();
        Reader.ClearSelection();
    }

    private void OnPagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start) return;
        DrawBand(start, e.GetPosition(PageHost));
    }

    private void OnPagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStart is not { } start) return;
        _dragStart = null;
        e.Pointer.Capture(null);

        var end = e.GetPosition(PageHost);

        // a click, not a box: drop the selection and the band
        if (Math.Abs(end.X - start.X) < 4 || Math.Abs(end.Y - start.Y) < 4)
        {
            HideBand();
            Reader?.ClearSelection();
            return;
        }

        DrawBand(start, end);

        var (ax, ay) = ToNormalised(start);
        var (bx, by) = ToNormalised(end);
        Reader?.SelectRectangle(ax, ay, bx, by);
    }

    // ---------- page geometry ----------
    //
    // Avalonia already letterboxes for us: with Stretch=Uniform the Image control's own Bounds
    // *is* the drawn rectangle, offset inside the host. So the page rect in host coordinates is
    // (image origin, image size) — no scaling maths, and no chance of mixing two coordinate
    // spaces (which is what made an earlier version shift every selection to the left).

    /// <summary>The drawn page, in the coordinates the pointer events use (the host).</summary>
    private bool TryPageRect(out Rect page)
    {
        page = default;
        if (PageImage.Source is not Bitmap) return false;

        var origin = PageImage.TranslatePoint(new Point(0, 0), PageHost);
        if (origin is null) return false;

        page = new Rect(origin.Value, PageImage.Bounds.Size);
        return page.Width > 0.5 && page.Height > 0.5;
    }

    /// <summary>Window point → fraction of the page.</summary>
    private (double X, double Y) ToNormalised(Point point)
    {
        if (!TryPageRect(out var page)) return (0, 0);

        return (Math.Clamp((point.X - page.X) / page.Width, 0, 1),
                Math.Clamp((point.Y - page.Y) / page.Height, 0, 1));
    }

    /// <summary>Fraction of the page → window point.</summary>
    private Point ToViewPoint(double x, double y)
    {
        if (!TryPageRect(out var page)) return default;
        return new Point(page.X + x * page.Width, page.Y + y * page.Height);
    }

    private void DrawCommitted()
    {
        if (_watched is null || PageImage.Source is not Bitmap) return;

        var (x0, y0, x1, y1) = _watched.Selection;
        DrawBand(ToViewPoint(x0, y0), ToViewPoint(x1, y1));
    }

    private void DrawBand(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var width = Math.Abs(b.X - a.X);
        var height = Math.Abs(b.Y - a.Y);

        // LayoutUpdated calls this on every pass; only touch the band when it really moved,
        // otherwise writing Width/Height would schedule another layout forever.
        if (SelectionBand.IsVisible
            && Math.Abs(Canvas.GetLeft(SelectionBand) - x) < 0.5
            && Math.Abs(Canvas.GetTop(SelectionBand) - y) < 0.5
            && Math.Abs(SelectionBand.Width - width) < 0.5
            && Math.Abs(SelectionBand.Height - height) < 0.5)
        {
            return;
        }

        Canvas.SetLeft(SelectionBand, x);
        Canvas.SetTop(SelectionBand, y);
        SelectionBand.Width = width;
        SelectionBand.Height = height;
        SelectionBand.IsVisible = true;
    }

    private void HideBand()
    {
        SelectionBand.IsVisible = false;
        SelectionBand.Width = 0;
        SelectionBand.Height = 0;
    }
}
