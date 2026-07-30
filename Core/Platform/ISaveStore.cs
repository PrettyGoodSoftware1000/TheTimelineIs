namespace TheTimelineIs.Core.Platform;

/// <summary>
/// Read/write access to the one place the platform allows saving.
/// TitleContainer is read-only on every platform, so saves need their own door:
/// %AppData% on Windows, the app sandbox on iOS/Android.
/// </summary>
public interface ISaveStore
{
    bool Exists { get; }
    string? Load();
    void Save(string payload);
}
