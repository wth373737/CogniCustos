namespace CogniCustos.Ui.ViewModels;

/// <summary>
/// The outcome of one real round trip to a model endpoint, as the settings page shows it.
///
/// It is declared here rather than reused from Contracts on purpose: the UI project deliberately
/// references nothing, and this is the shape the panel needs (ok, elapsed, one line of words).
/// The host maps its own result into this at the seam.
/// </summary>
public sealed record CloudTestResult(bool Ok, string Message, long ElapsedMs);
