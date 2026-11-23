using System.Collections.ObjectModel;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// The content of the settings page's Model Configuration section. UI state only for
/// now: the values live in memory, and the actions raise events the shell answers with
/// a dialog, until they are wired to the runtime's settings store.
/// </summary>
public sealed class ModelConfigurationViewModel : ObservableObject
{
    private bool _advancedOpen;
    private string _localModelName;

    public ModelConfigurationViewModel()
    {
        ModelName = new ModelField("Settings.Model.Name", "deepseek-flash");
        Url = new ModelField("Settings.Model.Url", "https://api.deepseek.com");
        ApiKey = new ModelField("Settings.Model.Api", "sk-placeholder-000000000000", secret: true);
        Fields = [ModelName, Url, ApiKey];

        _localModelName = Localizer.Get("Settings.Model.LocalModelName");

        ToggleAdvancedCommand = new RelayCommand(() =>
        {
            AdvancedOpen = !AdvancedOpen;
            AdvancedRequested?.Invoke();
        });
        CheckCommand = new RelayCommand(() => CheckRequested?.Invoke());
        DeleteCommand = new RelayCommand(() => DeleteRequested?.Invoke());
        AddCommand = new RelayCommand(() => AddRequested?.Invoke());
    }

    public ModelField ModelName { get; }
    public ModelField Url { get; }
    public ModelField ApiKey { get; }

    public ObservableCollection<ModelField> Fields { get; }

    public RelayCommand ToggleAdvancedCommand { get; }
    public RelayCommand CheckCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand AddCommand { get; }

    public event Action? AdvancedRequested;
    public event Action? CheckRequested;
    public event Action? DeleteRequested;
    public event Action? AddRequested;

    public string PrimaryHeader => Localizer.Get("Settings.Model.Primary");
    public string DocsNote => Localizer.Get("Settings.Model.DocsNote");
    public string AdvancedLabel => Localizer.Get("Settings.Model.Advanced");
    public string AdvancedNote => Localizer.Get("Settings.Model.AdvancedNote");
    public string LocalModelHeader => Localizer.Get("Settings.Model.LocalModel");
    public string LocalModelNote => Localizer.Get("Settings.Model.LocalModelNote");
    public string OllamaLabel => Localizer.Get("Settings.Model.Ollama");
    public string OllamaNote => Localizer.Get("Settings.Model.OllamaNote");
    public string CheckLabel => Localizer.Get("Settings.Model.Check");
    public string DeleteLabel => Localizer.Get("Settings.Model.Delete");
    public string AddLabel => Localizer.Get("Settings.Model.Add");

    private bool AdvancedOpen
    {
        get => _advancedOpen;
        set
        {
            if (!Set(ref _advancedOpen, value)) return;
            OnPropertyChanged(nameof(AdvancedChevron));
        }
    }

    public string AdvancedChevron => _advancedOpen ? "\uE70E" : "\uE70D";

    public string LocalModelName
    {
        get => _localModelName;
        private set => Set(ref _localModelName, value);
    }

    public void RemoveLocalModel() => LocalModelName = Localizer.Get("Settings.Model.LocalModelNone");

    public void RefreshText()
    {
        foreach (var field in Fields) field.RefreshLabel();

        OnPropertyChanged(nameof(PrimaryHeader));
        OnPropertyChanged(nameof(DocsNote));
        OnPropertyChanged(nameof(AdvancedLabel));
        OnPropertyChanged(nameof(AdvancedNote));
        OnPropertyChanged(nameof(LocalModelHeader));
        OnPropertyChanged(nameof(LocalModelNote));
        OnPropertyChanged(nameof(OllamaLabel));
        OnPropertyChanged(nameof(OllamaNote));
        OnPropertyChanged(nameof(CheckLabel));
        OnPropertyChanged(nameof(DeleteLabel));
        OnPropertyChanged(nameof(AddLabel));
    }
}
