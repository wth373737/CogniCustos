using System.Collections.ObjectModel;
using System.Globalization;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>One day cell in the calendar grid.</summary>
public sealed class CalendarDay : ObservableObject
{
    private string _number = string.Empty;
    private bool _inMonth;
    private bool _isToday;

    public string Number
    {
        get => _number;
        set => Set(ref _number, value);
    }

    public bool InMonth
    {
        get => _inMonth;
        set
        {
            if (!Set(ref _inMonth, value)) return;
            OnPropertyChanged(nameof(TextOpacity));
        }
    }

    /// <summary>Days outside the shown month are dimmed, like the mock.</summary>
    public double TextOpacity => _inMonth ? 1 : 0.45;

    public bool IsToday
    {
        get => _isToday;
        set => Set(ref _isToday, value);
    }

    public DateTime Date { get; set; }
}

/// <summary>One week row (always seven cells, so the grid never reflows).</summary>
public sealed class CalendarWeek
{
    public CalendarWeek(IReadOnlyList<CalendarDay> days) => Days = days;

    public IReadOnlyList<CalendarDay> Days { get; }
}

/// <summary>
/// The calendar window: a floating, draggable, minimisable child window of the shell.
/// It is an in-app window (not an OS window) so it scales with the 1280x720 canvas
/// and can be dragged anywhere inside it.
/// </summary>
public sealed class CalendarViewModel : ObservableObject
{
    private const double WidthValue = 1040;
    public const double ExpandedHeight = 620;
    public const double MinimizedHeight = 46;

    private const int WeeksShown = 6;

    private bool _open;
    private bool _minimized;
    private int _year;
    private int _month;
    private double _x = 120;
    private double _y = 52;

    public CalendarViewModel()
    {
        var today = DateTime.Today;
        _year = today.Year;
        _month = today.Month;

        CloseCommand = new RelayCommand(Close);
        ToggleMinimizeCommand = new RelayCommand(() => IsMinimized = !_minimized);
        PreviousMonthCommand = new RelayCommand(() => ShiftMonth(-1));
        NextMonthCommand = new RelayCommand(() => ShiftMonth(1));
        PreviousYearCommand = new RelayCommand(() => ShiftYear(-1));
        NextYearCommand = new RelayCommand(() => ShiftYear(1));

        BuildGrid();
    }

    public RelayCommand CloseCommand { get; }
    public RelayCommand ToggleMinimizeCommand { get; }
    public RelayCommand PreviousMonthCommand { get; }
    public RelayCommand NextMonthCommand { get; }
    public RelayCommand PreviousYearCommand { get; }
    public RelayCommand NextYearCommand { get; }

    public ObservableCollection<CalendarWeek> Weeks { get; } = [];

    public IReadOnlyList<string> WeekdayHeaders =>
    [
        Localizer.Get("Weekday.Mon"),
        Localizer.Get("Weekday.Tue"),
        Localizer.Get("Weekday.Wed"),
        Localizer.Get("Weekday.Thu"),
        Localizer.Get("Weekday.Fri"),
        Localizer.Get("Weekday.Sat"),
        Localizer.Get("Weekday.Sun"),
    ];

    public bool IsOpen
    {
        get => _open;
        private set
        {
            if (!Set(ref _open, value)) return;
            OnPropertyChanged(nameof(IsVisible));
        }
    }

    public bool IsVisible => _open;

    public bool IsMinimized
    {
        get => _minimized;
        private set
        {
            if (!Set(ref _minimized, value)) return;
            OnPropertyChanged(nameof(WindowHeight));
            OnPropertyChanged(nameof(BodyVisible));
            OnPropertyChanged(nameof(MinimizeGlyph));
        }
    }

    public bool BodyVisible => !_minimized;

    /// <summary>MDL2: E921 minimise, E923 restore (the same button toggles).</summary>
    public string MinimizeGlyph => _minimized ? "\uE923" : "\uE921";

    public double WindowWidth => WidthValue;

    public double WindowHeight => _minimized ? MinimizedHeight : ExpandedHeight;

    public double X
    {
        get => _x;
        private set => Set(ref _x, value);
    }

    public double Y
    {
        get => _y;
        private set => Set(ref _y, value);
    }

    public int Year => _year;

    public int Month => _month;

    public string YearTitle => $"{Localizer.Get("Calendar.Year")} {_year}";

    public string MonthTitle => $"{Localizer.Get("Calendar.Month")} {_month}";

    public string Title => Localizer.Get("Calendar.Title");

    /// <summary>Opens the window (or brings it forward) and un-minimises it.</summary>
    public void Open()
    {
        IsMinimized = false;
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    /// <summary>Called by the drag handler in the view; keeps the window inside the canvas.</summary>
    public void MoveTo(double x, double y)
    {
        X = Math.Clamp(x, 0, 1280 - WidthValue);
        Y = Math.Clamp(y, 0, 720 - WindowHeight);
    }

    /// <summary>Language switch: the grid headers and titles are localised.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(YearTitle));
        OnPropertyChanged(nameof(MonthTitle));
        OnPropertyChanged(nameof(WeekdayHeaders));
        BuildGrid();
    }

    private void ShiftMonth(int delta)
    {
        var date = new DateTime(_year, _month, 1).AddMonths(delta);
        _year = date.Year;
        _month = date.Month;
        OnPropertyChanged(nameof(Year));
        OnPropertyChanged(nameof(Month));
        OnPropertyChanged(nameof(YearTitle));
        OnPropertyChanged(nameof(MonthTitle));
        BuildGrid();
    }

    private void ShiftYear(int delta)
    {
        _year += delta;
        OnPropertyChanged(nameof(Year));
        OnPropertyChanged(nameof(YearTitle));
        BuildGrid();
    }

    private void BuildGrid()
    {
        var first = new DateTime(_year, _month, 1);
        // Monday-first grid, like the mock.
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var start = first.AddDays(-offset);
        var today = DateTime.Today;

        Weeks.Clear();
        for (var week = 0; week < WeeksShown; week++)
        {
            var days = new List<CalendarDay>(7);
            for (var day = 0; day < 7; day++)
            {
                var date = start.AddDays(week * 7 + day);
                days.Add(new CalendarDay
                {
                    Date = date,
                    Number = date.Day.ToString(CultureInfo.InvariantCulture),
                    InMonth = date.Month == _month,
                    IsToday = date.Date == today,
                });
            }

            Weeks.Add(new CalendarWeek(days));
        }

        OnPropertyChanged(nameof(Weeks));
    }
}
