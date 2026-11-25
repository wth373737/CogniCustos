namespace CogniCustos.Contracts;

/// <summary>
/// The outcome of one real round trip to a model endpoint. Used by the settings page's
/// connection test: it reports what actually happened (latency, or the endpoint's own error)
/// instead of a cached guess about availability.
/// </summary>
public sealed record CloudTestResult(bool Ok, string Message, long ElapsedMs);
