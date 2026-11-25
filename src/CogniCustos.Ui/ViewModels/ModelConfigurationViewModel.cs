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
        OpenOllamaCommand = new RelayCommand(() =>
        {
            OllamaModelText = Localizer.Get("Settings.Model.LocalModelName");
            IsOllamaWindowOpen = true;
        });
        CloseOllamaCommand = new RelayCommand(() => IsOllamaWindowOpen = false);
        EnableOllamaCommand = new RelayCommand(() => OllamaEnableRequested?.Invoke(OllamaModelText.Trim()));
        ToggleGpuCommand = new RelayCommand(() => GpuToggleRequested?.Invoke(!GpuOn));

        // Editing a field only changes the value on screen. Saving is the explicit button below
        // the three fields: committing one field used to fire a save with the other two still
        // empty, so the row showed the new value while nothing had been written.
        foreach (var field in new[] { ModelName, Url, ApiKey })
            field.Committed += MarkDirty;

        ToggleAdvancedCommand = new RelayCommand(() =>
        {
            AdvancedOpen = !AdvancedOpen;
            AdvancedRequested?.Invoke();
        });
        CheckCommand = new RelayCommand(() => CheckRequested?.Invoke());
        DeleteCommand = new RelayCommand(() => DeleteRequested?.Invoke());
        AddCommand = new RelayCommand(() => AddRequested?.Invoke());

        // connection test: what is in the fields right now, not what was last saved
        TestCommand = new RelayCommand(
            () => CloudTestRequested?.Invoke(ModelName.Value, Url.Value, ApiKey.Value),
            () => !IsTesting);

        // explicit save: the three fields are written together or not at all
        SaveCommand = new RelayCommand(
            () => CloudSaveRequested?.Invoke(ModelName.Value, Url.Value, ApiKey.Value),
            () => HasUnsavedChanges);
    }

    // ---------- explicit save ----------

    private bool _dirty;
    private string _saveStatus = string.Empty;
    private bool _saveFailed;

    public RelayCommand SaveCommand { get; }

    public string SaveLabel => Localizer.Get("Settings.Model.Save");

    public string SaveHint => Localizer.Get("Settings.Model.SaveHint");

    /// <summary>True when the form holds something that has not been written to settings yet.</summary>
    public bool HasUnsavedChanges => _dirty;

    public string UnsavedLabel => Localizer.Get("Settings.Model.Unsaved");

    /// <summary>Inline result of the last save attempt (never only a toast).</summary>
    public string SaveStatus => _saveStatus;

    public bool HasSaveStatus => _saveStatus.Length > 0;

    public string SaveStatusBrush => _saveFailed ? "#FF8E1B1B" : "#FF2E6B5A";

    private void MarkDirty()
    {
        _dirty = true;
        _saveStatus = string.Empty;
        NotifySave();
    }

    public void MarkSaved(string model)
    {
        _dirty = false;
        _saveFailed = false;
        _saveStatus = Localizer.Get("Settings.Model.Saved") + " " + model;
        NotifySave();
    }

    public void ShowSaveFailure(string reason)
    {
        _saveFailed = true;
        _saveStatus = reason;
        NotifySave();
    }

    private void NotifySave()
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(SaveStatus));
        OnPropertyChanged(nameof(HasSaveStatus));
        OnPropertyChanged(nameof(SaveStatusBrush));
        SaveCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Seed the form from settings without marking it dirty.</summary>
    public void ClearDirty()
    {
        _dirty = false;
        _saveFailed = false;
        NotifySave();
    }

    // ---------- connection test ----------

    private bool _isTesting;
    private CloudTestResult? _testResult;

    /// <summary>Raised with the three field values; the shell answers with a real round trip.</summary>
    public event Action<string, string, string>? CloudTestRequested;

    public RelayCommand TestCommand { get; }

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (!Set(ref _isTesting, value)) return;
            TestCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(TestLabel));
        }
    }

    public string TestLabel => Localizer.Get(IsTesting ? "Settings.Model.Testing" : "Settings.Model.Test");

    public string TestHint => Localizer.Get("Settings.Model.TestHint");

    public void BeginTest()
    {
        IsTesting = true;
        _testResult = null;
        OnPropertyChanged(nameof(TestStatus));
        OnPropertyChanged(nameof(HasTestStatus));
    }

    public void ShowTestResult(CloudTestResult result)
    {
        _testResult = result;
        IsTesting = false;
        OnPropertyChanged(nameof(TestStatus));
        OnPropertyChanged(nameof(HasTestStatus));
        OnPropertyChanged(nameof(TestStatusBrush));
    }

    /// <summary>One line: what happened, how long it took, and the endpoint's own words on failure.</summary>
    public string TestStatus => _testResult is null
        ? string.Empty
        : (_testResult.Ok ? Localizer.Get("Settings.Model.TestOk") + " · " : Localizer.Get("Settings.Model.TestFail") + " · ")
          + _testResult.ElapsedMs + " ms · " + _testResult.Message;

    public bool HasTestStatus => _testResult is not null || IsTesting;

    /// <summary>Green when the round trip worked, red when it did not.</summary>
    public string TestStatusBrush => _testResult is { Ok: true } ? "#FF2E6B5A" : "#FF8E1B1B";

    public ModelField ModelName { get; }
    public ModelField Url { get; }
    public ModelField ApiKey { get; }

    public ObservableCollection<ModelField> Fields { get; }

    /// <summary>The "using Ollama" row opens a small window with a fixed address.</summary>
    public RelayCommand OpenOllamaCommand { get; }
    public RelayCommand CloseOllamaCommand { get; }
    public RelayCommand EnableOllamaCommand { get; }

    /// <summary>Raised when the user asks to switch; the shell answers via the data source.</summary>
    public event Action<string>? OllamaEnableRequested;

    /// <summary>Raised when the hardware-acceleration switch is flipped.</summary>
    public event Action<bool>? GpuToggleRequested;

    /// <summary>Raised when one of the primary-model fields is committed.</summary>
    public event Action<string, string, string>? CloudSaveRequested;

    private string _cloudStatus = string.Empty;

    /// <summary>Under the three fields: what is saved and in effect.</summary>
    public string CloudStatus
    {
        get => _cloudStatus;
        private set => Set(ref _cloudStatus, value);
    }

    public bool HasCloudStatus => _cloudStatus.Length > 0;

    public void SetCloudStatus(string status) => CloudStatus = status;

    /// <summary>Seed the fields from what is already saved.</summary>
    public void SetCloudValues(string model, string url, bool configured)
    {
        if (!string.IsNullOrWhiteSpace(model)) ModelName.SetValue(model);
        if (!string.IsNullOrWhiteSpace(url)) Url.SetValue(url);
        if (configured) ApiKey.SetValue("••••••");
    }

    public RelayCommand ToggleGpuCommand { get; }

    private bool _gpuOn;
    private string _gpuNote = string.Empty;

    public bool GpuOn
    {
        get => _gpuOn;
        private set
        {
            if (!Set(ref _gpuOn, value)) return;
            OnPropertyChanged(nameof(GpuStateLabel));
        OnPropertyChanged(nameof(GpuNoteText));
            OnPropertyChanged(nameof(GpuNoteText));
        }
    }

    public string GpuLabel => Localizer.Get("Settings.Model.GpuLabel");

    public string GpuStateLabel => GpuOn
        ? Localizer.Get("Settings.Model.GpuOn")
        : Localizer.Get("Settings.Model.GpuOff");

    public string GpuNoteText => Localizer.Get(GpuOn
        ? "Settings.Model.GpuNoteOn"
        : "Settings.Model.GpuNoteOff");

    /// <summary>Shown under the row: why it may be off, or what it costs.</summary>
    public string GpuNote
    {
        get => _gpuNote;
        private set => Set(ref _gpuNote, value);
    }

    public void SetGpuState(bool enabled, string note)
    {
        GpuOn = enabled;
        GpuNote = note;
    }

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

    /// <summary>
    /// The chevron tracks "the advanced panel was opened from here". Closing the panel puts it
    /// back, so the arrow does not keep pointing at a window that is gone.
    /// </summary>
    public void ResetAdvancedChevron() => AdvancedOpen = false;

    public string LocalModelName
    {
        get => _localModelName;
        private set => Set(ref _localModelName, value);
    }

    private bool _ollamaOpen;
    private string _ollamaModelText = string.Empty;
    private string _localStatus = string.Empty;

    /// <summary>The configuration window for the Ollama path.</summary>
    public bool IsOllamaWindowOpen
    {
        get => _ollamaOpen;
        private set => Set(ref _ollamaOpen, value);
    }

    /// <summary>Only the model name is typed; the address is fixed below it.</summary>
    public string OllamaModelText
    {
        get => _ollamaModelText;
        set => Set(ref _ollamaModelText, value);
    }

    public string OllamaAddress => "http://127.0.0.1:11434";

    public string OllamaTitleLabel => Localizer.Get("Settings.Model.OllamaTitle");
    public string OllamaAddressLabel => Localizer.Get("Settings.Model.OllamaAddress");
    public string OllamaModelLabel => Localizer.Get("Settings.Model.OllamaModelLabel");
    public string OllamaWindowNote => Localizer.Get("Settings.Model.OllamaWindowNote");
    public string OllamaEnableLabel => Localizer.Get("Settings.Model.OllamaEnable");
    public string CancelLabel => Localizer.Get("Dialog.Cancel");

    /// <summary>What the "Small local model" row shows (bundled name, Ollama model, or none).</summary>
    public string LocalStatus
    {
        get => _localStatus;
        private set => Set(ref _localStatus, value);
    }

    private string _installNote = string.Empty;
    private bool _busy;

    /// <summary>"下载中 42%" while the bundled model installs; empty otherwise.</summary>
    public string InstallNote
    {
        get => _installNote;
        private set
        {
            if (!Set(ref _installNote, value)) return;
            OnPropertyChanged(nameof(HasInstallNote));
        }
    }

    public bool HasInstallNote => _installNote.Length > 0;

    /// <summary>Disables Check/Delete while a download is running.</summary>
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !_busy;

    public void BeginInstall()
    {
        IsBusy = true;
        InstallNote = Localizer.Get("Settings.Model.Installing");
    }

    public void ReportInstallProgress(double fraction)
    {
        // progress callbacks can arrive after the install finished (they are posted);
        // do not resurrect the note once the row is idle again
        if (!IsBusy) return;
        InstallNote = $"{Localizer.Get("Settings.Model.Downloading")} {fraction * 100:0}%";
    }

    public void EndInstall() => InstallNote = string.Empty;

    public void EndInstallBusy() => IsBusy = false;

    public void SetLocalStatus(string status) => LocalStatus = status;

    /// <summary>The shell reports back after trying to switch.</summary>
    public void CompleteOllamaSwitch(bool ok, string status)
    {
        if (ok)
        {
            IsOllamaWindowOpen = false;
            LocalStatus = status;
        }
    }

    public void RemoveLocalModel() => LocalModelName = Localizer.Get("Settings.Model.LocalModelNone");

    public void RefreshText()
    {
        foreach (var field in Fields) field.RefreshLabel();

        OnPropertyChanged(nameof(PrimaryHeader));
        OnPropertyChanged(nameof(DocsNote));
        OnPropertyChanged(nameof(AdvancedLabel));
        OnPropertyChanged(nameof(AdvancedNote));
        OnPropertyChanged(nameof(TestLabel));
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(SaveHint));
        OnPropertyChanged(nameof(UnsavedLabel));
        OnPropertyChanged(nameof(SaveStatus));
        OnPropertyChanged(nameof(TestHint));
        OnPropertyChanged(nameof(TestStatus));
        OnPropertyChanged(nameof(LocalModelHeader));
        OnPropertyChanged(nameof(LocalModelNote));
        OnPropertyChanged(nameof(GpuLabel));
        OnPropertyChanged(nameof(GpuStateLabel));
        OnPropertyChanged(nameof(GpuNoteText));
        OnPropertyChanged(nameof(OllamaLabel));
        OnPropertyChanged(nameof(OllamaNote));
        OnPropertyChanged(nameof(CheckLabel));
        OnPropertyChanged(nameof(DeleteLabel));
        OnPropertyChanged(nameof(AddLabel));
        OnPropertyChanged(nameof(OllamaTitleLabel));
        OnPropertyChanged(nameof(OllamaAddressLabel));
        OnPropertyChanged(nameof(OllamaModelLabel));
        OnPropertyChanged(nameof(OllamaWindowNote));
        OnPropertyChanged(nameof(OllamaEnableLabel));
        OnPropertyChanged(nameof(CancelLabel));
        OnPropertyChanged(nameof(InstallNote));
        OnPropertyChanged(nameof(GpuLabel));
        OnPropertyChanged(nameof(GpuStateLabel));
        if (IsBusy) InstallNote = Localizer.Get("Settings.Model.Installing");
    }
}
