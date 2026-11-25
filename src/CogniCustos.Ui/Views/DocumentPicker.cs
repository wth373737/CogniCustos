using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CogniCustos.Ui.Localization;
using CogniCustos.Ui.ViewModels;

namespace CogniCustos.Ui.Views;

/// <summary>
/// The one file dialog the reader uses, shared by the drawer tile and the reader's own
/// "open another file" button. It lives in the view layer because picking a file needs a
/// TopLevel; the view-model only ever receives a path.
/// </summary>
public static class DocumentPicker
{
    /// <summary>Ask for a document, open it in the reader, and report failures on the line.</summary>
    public static async Task OpenIntoReaderAsync(ShellViewModel shell, TopLevel? top)
    {
        var path = await PickAsync(top);
        if (path is null) return;

        if (await shell.Reader.OpenAsync(path) is { } failure)
        {
            shell.Notify(failure);
            return;
        }

        // the reader is a full page: get the drawer and the settings page out of its way
        shell.ClosePanels();
    }

    public static async Task<string?> PickAsync(TopLevel? top)
    {
        if (top?.StorageProvider is not { CanOpen: true } storage) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("Reader.PickTitle"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Documents")
                {
                    Patterns = ["*.pdf", "*.doc", "*.docx", "*.ppt", "*.pptx"],
                    MimeTypes = ["application/pdf"],
                },
            ],
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
