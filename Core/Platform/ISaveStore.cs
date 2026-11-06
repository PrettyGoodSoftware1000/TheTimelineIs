namespace TheTimelineIs.Core.Platform;

/// <summary>
/// Read/write access to the one place the platform allows saving.
/// TitleContainer is read-only on every platform, so saves need their own door:
/// %AppData% on Windows, the app sandbox on iOS/Android.
/// </summary>
public interface ISaveStore
{
    /// <summary>
    /// Whether this build has anywhere to save at all.
    ///
    /// False in the browser. The Save button is not drawn where this is
    /// false: a button that looks like it keeps your game and does not is
    /// worse than no button.
    /// </summary>
    bool CanSave { get; }

    bool Exists { get; }
    string? Load();
    void Save(string payload);
}
