using CogniCustos.Ui.Localization;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// One labelled value row of the model configuration: read-only until the user
/// double-clicks it (the pencil is the affordance, the double-click is the gesture).
/// Enter or losing focus commits, Escape cancels.
/// </summary>
public sealed class ModelField : ObservableObject
{
    private readonly string _labelKey;
    private string _value;
    private string _editText;
    private bool _editing;

    public ModelField(string labelKey, string value, bool secret = false)
    {
        _labelKey = labelKey;
        _value = value;
        _editText = value;
        _secret = secret;
        BeginEditCommand = new RelayCommand(BeginEdit);
    }

    public string Label => Localizer.Get(_labelKey);

    private readonly bool _secret;

    public string Value => _value;

    /// <summary>What the read-only row shows; a secret is always the same run of dots.</summary>
    public string DisplayValue => _secret ? new string('•', 22) : _value;

    public bool IsEditing
    {
        get => _editing;
        private set
        {
            if (!Set(ref _editing, value)) return;
            OnPropertyChanged(nameof(IsReadOnly));
        }
    }

    public bool IsReadOnly => !_editing;

    public string EditText
    {
        get => _editText;
        set => Set(ref _editText, value);
    }

    public RelayCommand BeginEditCommand { get; }

    public void BeginEdit()
    {
        if (IsEditing) return;
        EditText = _value;
        IsEditing = true;
    }

    /// <summary>Raised after a value was committed (Enter or losing focus).</summary>
    public event Action? Committed;

    public void Commit()
    {
        if (!IsEditing) return;

        var text = (EditText ?? string.Empty).Trim();
        if (text.Length > 0) _value = text;

        IsEditing = false;
        Committed?.Invoke();
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(DisplayValue));
    }

    /// <summary>Set the value without touching the editing state (used when seeding).</summary>
    public void SetValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        _value = value.Trim();
        _editText = _value;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(DisplayValue));
    }

    public void Cancel()
    {
        if (!IsEditing) return;
        EditText = _value;
        IsEditing = false;
    }

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}
