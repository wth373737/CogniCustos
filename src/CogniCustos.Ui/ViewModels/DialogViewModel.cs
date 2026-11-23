using System.Collections.ObjectModel;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.Mvvm;

namespace CogniCustos.Ui.ViewModels;

/// <summary>What the dialog is currently asking.</summary>
public enum DialogKind
{
    None,
    IncomingDecision,
    Info,
    LocalModelDelete,
    IgnoreConfirm,
    Composer,
    NoteChoice,
    NoteEdit,
}

/// <summary>One button in the dialog footer.</summary>
public sealed record DialogButton(string Text, RelayCommand Command, bool Primary = false);

/// <summary>
/// The single dialog surface used by all four flows (incoming decision, ignore
/// confirmation, the AI composer and the note choice/editor). Keeping one surface
/// means one layout, one backdrop and one Esc rule.
/// </summary>
public sealed class DialogViewModel : ObservableObject
{
    private DialogKind _kind = DialogKind.None;
    private string _title = string.Empty;
    private string _body = string.Empty;
    private bool _showComposer;
    private bool _showEditor;
    private string _editorText = string.Empty;
    private string _composerText = string.Empty;

    public DialogViewModel()
    {
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke());

        Buttons = [];
    }

    /// <summary>Raised when the user wants the dialog gone (cancel / Esc / backdrop).</summary>
    public event Action? RequestClose;

    public ObservableCollection<DialogButton> Buttons { get; }

    public RelayCommand CancelCommand { get; }

    public DialogKind Kind => _kind;

    public bool IsOpen => _kind != DialogKind.None;

    public string Title => _title;

    public string Body => _body;

    public bool ShowComposer
    {
        get => _showComposer;
        private set => Set(ref _showComposer, value);
    }

    public bool ShowEditor
    {
        get => _showEditor;
        private set => Set(ref _showEditor, value);
    }

    public string EditorText
    {
        get => _editorText;
        set => Set(ref _editorText, value);
    }

    public string ComposerText
    {
        get => _composerText;
        set => Set(ref _composerText, value);
    }

    public void Open(DialogKind kind, string title, string body,
        IEnumerable<DialogButton> buttons, bool composer = false, bool editor = false,
        string editorText = "", string composerText = "")
    {
        _kind = kind;
        _title = title;
        _body = body;
        ShowComposer = composer;
        ShowEditor = editor;
        EditorText = editorText;
        ComposerText = composerText;

        Buttons.Clear();
        foreach (var button in buttons) Buttons.Add(button);

        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Body));
    }

    public void Close()
    {
        _kind = DialogKind.None;
        Buttons.Clear();
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(Kind));
    }

    /// <summary>Language switch: re-read the button labels the dialog was opened with.</summary>
    public void RefreshText(Func<DialogKind, IEnumerable<DialogButton>> rebuild)
    {
        if (!IsOpen) return;
        var buttons = rebuild(_kind).ToList();
        if (buttons.Count == 0) return;

        Buttons.Clear();
        foreach (var button in buttons) Buttons.Add(button);
    }

    public string ComposerPrompt => Localizer.Get("Composer.Prompt");
}
