using CogniCustos.Ui.Localization;

namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// The demo content the sandbox has always shown. Behaviour is identical to the
/// hard-coded seeds it replaced, so the tests and the screenshot states do not change.
/// </summary>
public sealed class DemoShellDataSource : IShellDataSource
{
    public IReadOnlyList<ReminderItem> Today() =>
    [
        new(Localizer.Get("Demo.TodayUrgent1"), true),
        new(Localizer.Get("Demo.TodayNormal"), false),
        new(Localizer.Get("Demo.TodayUrgent2"), true),
        new(Localizer.Get("Demo.TodayUrgent3"), true),
    ];

    public (string Title, string Empty, IReadOnlyList<CardItem> Items) CalendarCard() => (
        DateTime.Today.ToString("yy/MM/dd"),
        string.Empty,
        [
            new CardItem(Localizer.Get("Demo.Plan1.Title"), Localizer.Get("Demo.Plan1.When")),
        ]);

    public (string Title, string Empty, IReadOnlyList<CardItem> Items) PendingCard() => (
        Localizer.Get("Card.Pending"),
        Localizer.Get("Card.Empty.Pending"),
        [
            new CardItem(Localizer.Get("Demo.Plan1.Title"), Localizer.Get("Demo.Plan1.When"),
                Localizer.Get("Demo.Incoming.Source"), Localizer.Get("Demo.Incoming.File")),
            new CardItem(Localizer.Get("Demo.Plan2.Title"), Localizer.Get("Demo.Plan2.When")),
        ]);

    public (string Title, string Empty, IReadOnlyList<CardItem> Items) NoteCard() => (
        Localizer.Get("Card.Note"),
        Localizer.Get("Card.Empty.Note"),
        [
            new CardItem(Localizer.Get("Demo.Note1")),
        ]);

    public string? IncomingConfirmed(CardItem item) => null;

    public string? IncomingIgnored(CardItem item) => null;

    public void NoteEdited(CardItem item, string text)
    {
    }

    public Task<string?> AskAsync(string instruction) => Task.FromResult<string?>(null);
}
