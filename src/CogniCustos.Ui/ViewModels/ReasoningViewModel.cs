using System.Collections.ObjectModel;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// Two independent panels with two independent entries:
/// the "Reasoning Record" row in the sidebar opens the day-grouped record list,
/// the chevron at the bottom of the sidebar panel opens the current thought trace.
/// </summary>
public sealed class ReasoningViewModel : ObservableObject
{
    private bool _listOpen;
    private bool _trajectoryOpen;
    private string _trajectoryRecordTitle = string.Empty;
    private string _trajectoryText = string.Empty;

    public ReasoningViewModel()
    {
        OpenListCommand = new RelayCommand(ToggleList);
        ToggleTrajectoryCommand = new RelayCommand(ToggleTrajectory);
        CloseCommand = new RelayCommand(Close);
        BuildDemo();
    }

    public ObservableCollection<ReasoningGroup> Groups { get; } = [];

    public RelayCommand OpenListCommand { get; }
    public RelayCommand ToggleTrajectoryCommand { get; }
    public RelayCommand CloseCommand { get; }

    public string ListTitle => Localizer.Get("Reasoning.Title");

    public string TrajectoryTitle => Localizer.Get("Reasoning.Trajectory");

    public string TrajectoryRecordTitle
    {
        get => _trajectoryRecordTitle;
        private set => Set(ref _trajectoryRecordTitle, value);
    }

    public string TrajectoryText
    {
        get => _trajectoryText;
        private set => Set(ref _trajectoryText, value);
    }

    public bool IsListOpen
    {
        get => _listOpen;
        private set
        {
            if (!Set(ref _listOpen, value)) return;
            OnPropertyChanged(nameof(IsAnyOpen));
        }
    }

    public bool IsTrajectoryOpen
    {
        get => _trajectoryOpen;
        private set
        {
            if (!Set(ref _trajectoryOpen, value)) return;
            OnPropertyChanged(nameof(IsAnyOpen));
            OnPropertyChanged(nameof(TrajectoryChevron));
        }
    }

    public bool IsAnyOpen => _listOpen || _trajectoryOpen;

    /// <summary>The sidebar chevron points right when closed and left when the trace is open.</summary>
    public string TrajectoryChevron => _trajectoryOpen ? "\uE76B" : "\uE76C";

    // ---------- open / close ----------

    public void OpenList()
    {
        IsTrajectoryOpen = false;
        IsListOpen = true;
    }

    public void ToggleList()
    {
        if (IsListOpen)
        {
            IsListOpen = false;
            return;
        }

        OpenList();
    }

    public void ToggleTrajectory()
    {
        if (IsTrajectoryOpen)
        {
            IsTrajectoryOpen = false;
            return;
        }

        ShowTrajectory(LatestRecord());
    }

    public void Close()
    {
        IsListOpen = false;
        IsTrajectoryOpen = false;
    }

    private void ShowTrajectory(ReasoningRecord? record)
    {
        record ??= LatestRecord();

        IsListOpen = false;
        IsTrajectoryOpen = record is not null;
        TrajectoryRecordTitle = record?.Title ?? string.Empty;
        TrajectoryText = record?.Trajectory ?? string.Empty;
    }

    private ReasoningRecord? LatestRecord()
    {
        for (var i = Groups.Count - 1; i >= 0; i--)
        {
            if (Groups[i].Records.Count > 0) return Groups[i].Records[^1];
        }

        return null;
    }

    /// <summary>Group headers and record titles are content; keep the fixed ones in the catalogs.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(ListTitle));
        OnPropertyChanged(nameof(TrajectoryTitle));
        BuildDemo();
    }

    // ---------- demo content (the template to replace with real records) ----------

    private void BuildDemo()
    {
        var today = DateTime.Today;
        Groups.Clear();

        // Oldest first, TODAY last, exactly as the sketch reads top to bottom.
        Groups.Add(new ReasoningGroup(today.AddDays(-5).ToString("yy/MM/dd"),
            [Record(Localizer.Get("Demo.Record3"), 3)]));
        Groups.Add(new ReasoningGroup(today.AddDays(-4).ToString("yy/MM/dd"),
            [Record(Localizer.Get("Demo.Record3"), 4)]));
        Groups.Add(new ReasoningGroup(today.AddDays(-3).ToString("yy/MM/dd"),
            [Record(Localizer.Get("Demo.Record1"), 1)]));
        Groups.Add(new ReasoningGroup(today.AddDays(-2).ToString("yy/MM/dd"),
            [Record(Localizer.Get("Demo.Record1"), 1)]));
        Groups.Add(new ReasoningGroup(Localizer.Get("Reasoning.Yesterday"),
            [Record(Localizer.Get("Demo.Record2"), 2)], expanded: true));
        Groups.Add(new ReasoningGroup(Localizer.Get("Reasoning.Today"),
            [
                Record(Localizer.Get("Demo.Record1"), 1),
                Record(Localizer.Get("Demo.Record2"), 2),
            ], expanded: true));

        if (!IsTrajectoryOpen)
        {
            TrajectoryRecordTitle = LatestRecord()?.Title ?? string.Empty;
            TrajectoryText = LatestRecord()?.Trajectory ?? string.Empty;
        }
    }

    private static ReasoningRecord Record(string title, int seed) =>
        new(DemoTime(seed), title, DemoTrajectory(seed));

    private static string DemoTime(int seed) => seed switch
    {
        1 => "08:01:29-08:05:17",
        2 => "08:07:35-08:10:46",
        3 => "08:12:52-08:14:03",
        _ => "08:15:44-08:16:21",
    };

    private static string DemoTrajectory(int seed)
    {
        var longLine = new string('x', 120);
        return string.Join('\n',
            "Run command xxxxxxx",
            "think:",
            longLine,
            "Change: xxxxxxx",
            "read xxxxxxx",
            "edit xxxxxxx",
            "think:",
            longLine,
            "Run the command xxxxxxx",
            $"Run the command xxxxxxx  ({seed:00})");
    }
}
