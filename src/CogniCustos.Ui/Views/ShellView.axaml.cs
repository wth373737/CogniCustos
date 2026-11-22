using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
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
