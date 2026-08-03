namespace TheTimelineIs.Core.Platform;

/// <summary>
/// Writes the content-check log somewhere the player (or the author) can find
/// it. Like saves, this needs a writable location, which TitleContainer isn't.
/// </summary>
public interface ILogStore
{
    /// <summary>Human-readable location, shown in the error popup.</summary>
    string DisplayPath { get; }

    void Write(string contents);
}
