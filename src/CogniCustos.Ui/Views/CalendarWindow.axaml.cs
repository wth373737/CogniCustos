using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using CogniCustos.Ui.ViewModels;

namespace CogniCustos.Ui.Views;

/// <summary>Title-bar dragging for the calendar window (an in-app window).</summary>
public sealed partial class CalendarWindow : UserControl
{
    private Point _dragStart;
    private double _originX;
    private double _originY;
    private bool _dragging;

    public CalendarWindow() => AvaloniaXamlLoader.Load(this);

    private CalendarViewModel? Calendar => (DataContext as ShellViewModel)?.Calendar;

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (Calendar is not { } calendar) return;

        _dragging = true;
        _dragStart = e.GetPosition(TopLevel.GetTopLevel(this));
        _originX = calendar.X;
        _originY = calendar.Y;
        e.Pointer.Capture(sender as IInputElement);
        e.Handled = true;
    }

    private void OnTitleBarMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || Calendar is not { } calendar) return;

        var now = e.GetPosition(TopLevel.GetTopLevel(this));
        calendar.MoveTo(_originX + (now.X - _dragStart.X), _originY + (now.Y - _dragStart.Y));
        e.Handled = true;
    }

    private void OnTitleBarReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
