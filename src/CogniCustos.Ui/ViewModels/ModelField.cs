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
        IsSecret = secret;
        BeginEditCommand = new RelayCommand(BeginEdit);
    }

    public string Label => Localizer.Get(_labelKey);

    public bool IsSecret { get; }

    public string Value => _value;

    /// <summary>What the read-only row shows; a secret is always the same run of dots.</summary>
    public string DisplayValue => IsSecret ? new string('•', 22) : _value;

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

    public void Commit()
    {
        if (!IsEditing) return;

        var text = (EditText ?? string.Empty).Trim();
        if (text.Length > 0) _value = text;

        IsEditing = false;
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
