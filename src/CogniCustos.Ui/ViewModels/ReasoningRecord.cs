using System.Collections.ObjectModel;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>One entry of the reasoning record: a time range, what was done, and its raw trace.</summary>
public sealed class ReasoningRecord
{
    public ReasoningRecord(string time, string title, string trajectory)
    {
        Time = time;
        Title = title;
        Trajectory = trajectory;
    }

    /// <summary>"08:01:29-08:05:17".</summary>
    public string Time { get; }

    public string Title { get; }

    /// <summary>The raw thought/tool stream shown by the trajectory panel.</summary>
    public string Trajectory { get; }
}

/// <summary>A day in the record list: a collapsible header plus its entries.</summary>
public sealed class ReasoningGroup : ObservableObject
{
    private bool _expanded;

    public ReasoningGroup(string header, IEnumerable<ReasoningRecord> records, bool expanded = false)
    {
        Header = header;
        Records = new ObservableCollection<ReasoningRecord>(records);
        _expanded = expanded;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public string Header { get; }

    public ObservableCollection<ReasoningRecord> Records { get; }

    public RelayCommand ToggleCommand { get; }

    public bool IsExpanded
    {
        get => _expanded;
        private set
        {
            if (!Set(ref _expanded, value)) return;
            OnPropertyChanged(nameof(Chevron));
        }
    }

    /// <summary>Down when collapsed, up when expanded, as in the sketch.</summary>
    public string Chevron => _expanded ? "\uE70E" : "\uE70D";
}
