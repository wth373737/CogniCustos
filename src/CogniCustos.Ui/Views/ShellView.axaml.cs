using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CogniCustos.Ui.ViewModels;

namespace CogniCustos.Ui.Views;

public sealed partial class ShellView : UserControl
{
    private const double TickerGap = 48;
    private const double TickerSpeedPixelsPerSecond = 26;

    private DispatcherTimer? _ticker;
    private TranslateTransform? _tickerTransform;
    private Control? _tickerClip;
    private Control? _tickerRuns;
    private Control? _tickerCopy;
    private StackPanel? _tickerRow;
    private ShellViewModel? _vm;
    private double _offset;

    public ShellView()
    {
        AvaloniaXamlLoader.Load(this);

        // The VS Code XAML previewer instantiates the view without a host window,
        // so give the designer a view-model to bind against.
        if (Design.IsDesignMode) DataContext = new ShellViewModel();

        AttachedToVisualTree += (_, _) => StartTicker();
        DetachedFromVisualTree += (_, _) => StopTicker();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null) _vm.Today.PropertyChanged -= OnTodayChanged;
        _vm = DataContext as ShellViewModel;
        if (_vm is not null) _vm.Today.PropertyChanged += OnTodayChanged;
        ResetTicker();

        base.OnDataContextChanged(e);
    }

    /// <summary>The page behind the drawer stack closes one layer at a time.</summary>
    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ShellViewModel vm) vm.CloseTopDrawer();
        e.Handled = true;
    }

    /// <summary>Click outside the drawer, help panel or reminder list closes it.</summary>
    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ShellViewModel vm) vm.CloseOverlays();
        e.Handled = true;
    }

    // ---------- card paging ----------

    /// <summary>Both chevrons page the card body; they never fold the card away.</summary>
    private void OnCardScrollUp(object? sender, RoutedEventArgs e) => PageCard(sender, -1);

    private void OnCardScrollDown(object? sender, RoutedEventArgs e) => PageCard(sender, 1);

    private static void PageCard(object? sender, int direction)
    {
        var scroll = CardScrollOf(sender);
        if (scroll is null) return;

        var page = Math.Max(48, scroll.Viewport.Height * 0.85);
        var limit = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var next = Math.Clamp(scroll.Offset.Y + direction * page, 0, limit);
        scroll.Offset = new Vector(scroll.Offset.X, next);
    }

    private void OnCardScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer scroll) UpdateCardArrows(scroll);
    }

    private void OnCardScrollLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer scroll) Dispatcher.UIThread.Post(() => UpdateCardArrows(scroll));
    }

    /// <summary>Rows can be added long after load; the extent (not the offset) changes then.</summary>
    private void OnCardScrollLayout(object? sender, EventArgs e)
    {
        if (sender is ScrollViewer scroll) UpdateCardArrows(scroll);
    }

    private static ScrollViewer? CardScrollOf(object? sender)
    {
        var root = CardRootOf(sender);
        return root?.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(scroll => scroll.Name == "CardScrollViewer");
    }

    private static Control? CardRootOf(object? sender) =>
        sender is Control control
            ? control.GetVisualAncestors().OfType<Control>().FirstOrDefault(a => a.Name == "CardRoot")
            : null;

    /// <summary>An arrow is only live when its direction still has content left.</summary>
    private static void UpdateCardArrows(ScrollViewer scroll)
    {
        var root = CardRootOf(scroll);
        if (root is null) return;

        var limit = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var canGoUp = scroll.Offset.Y > 0.5;
        var canGoDown = scroll.Offset.Y < limit - 0.5;

        foreach (var button in root.GetVisualDescendants().OfType<Button>())
        {
            switch (button.Name)
            {
                case "CardScrollUp":
                    ShowArrow(button, canGoUp);
                    break;
                case "CardScrollDown":
                    ShowArrow(button, canGoDown);
                    break;
            }
        }
    }

    private static void ShowArrow(Button button, bool live)
    {
        // LayoutUpdated is frequent: only touch the two properties when they change.
        if (button.IsEnabled != live) button.IsEnabled = live;
        var opacity = live ? 1d : 0.25d;
        if (Math.Abs(button.Opacity - opacity) > 0.01) button.Opacity = opacity;
    }

    /// <summary>A click outside the record list or the trace closes them.</summary>
    private void OnReasoningScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ShellViewModel vm) vm.Reasoning.Close();
        e.Handled = true;
    }

    /// <summary>
    /// The drawer's bottom tile: pick a document and open it in the reader. Picking needs a
    /// TopLevel, which is why this one small step lives in the view and not in the view-model.
    /// </summary>
    private void OnReadFileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel vm)
            _ = DocumentPicker.OpenIntoReaderAsync(vm, TopLevel.GetTopLevel(this));
    }

    private void OnTodayChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TodayViewModel.Items))
        {
            Dispatcher.UIThread.Post(ResetTicker);
        }
    }

    // ---------- marquee ----------

    private void StartTicker()
    {
        _tickerClip = this.FindControl<Border>("TickerClip") ?? _tickerClip;
        _tickerRuns = this.FindControl<ItemsControl>("TickerRuns") ?? _tickerRuns;
        _tickerCopy = this.FindControl<ItemsControl>("TickerCopyRuns") ?? _tickerCopy;
        _tickerRow = this.FindControl<StackPanel>("TickerRow") ?? _tickerRow;
        if (_tickerRow is null) return;

        if (_tickerTransform is null)
        {
            _tickerTransform = new TranslateTransform();
            _tickerRow.RenderTransform = _tickerTransform;
        }

        _ticker ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _ticker.Tick -= OnTick;
        _ticker.Tick += OnTick;
        _ticker.Start();
    }

    private void StopTicker()
    {
        _ticker?.Stop();
    }

    private void ResetTicker()
    {
        _offset = 0;
        if (_tickerTransform is not null) _tickerTransform.X = 0;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_tickerTransform is null || _tickerRuns is null) return;

        var textWidth = _tickerRuns.Bounds.Width;
        var clipWidth = _tickerClip?.Bounds.Width ?? Bounds.Width;
        if (textWidth <= 0 || clipWidth <= 0) return;

        // Short content does not need to scroll at all — and the duplicated run
        // must stay hidden until it is actually needed, or the line reads twice.
        var needsScroll = textWidth > clipWidth;
        if (_tickerCopy is not null) _tickerCopy.IsVisible = needsScroll;
        if (!needsScroll)
        {
            ResetTicker();
            return;
        }

        _offset -= TickerSpeedPixelsPerSecond * _ticker!.Interval.TotalSeconds;
        if (_offset <= -(textWidth + TickerGap)) _offset = 0;
        _tickerTransform.X = _offset;
    }
}
