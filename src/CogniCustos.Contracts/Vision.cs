namespace CogniCustos.Contracts;

/// <summary>
/// A model that can look at a picture. Kept apart from <see cref="IModelProvider"/> because
/// the payload is different (bytes in, text out) and because most providers cannot do it:
/// the text-only path must stay usable when no vision model is installed.
/// </summary>
public interface IVisionModel
{
    string Id { get; }

    bool IsAvailable { get; }

    /// <summary>Describe / answer about one PNG. Returns the model's text.</summary>
    Task<string> DescribeAsync(string prompt, byte[] png, CancellationToken ct = default);
}
