namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// One entry in the scrolling reminder line. <see cref="Urgent"/> entries are things
/// that need attention today (an exam in six hours, a cancelled class) and are drawn
/// in dark red; normal ones keep the body colour.
/// </summary>
public sealed record ReminderItem(string Text, bool Urgent = false);
