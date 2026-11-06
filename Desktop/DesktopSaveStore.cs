using System;
using System.IO;
using TheTimelineIs.Core.Platform;

namespace TheTimelineIs.Desktop;

/// <summary>Saves to save.json in the player's own folder — see UserData.</summary>
public class DesktopSaveStore : ISaveStore
{
    private readonly string _path;

    public DesktopSaveStore() => _path = UserData.Path("save.json");

    /// <summary>A desktop build has a folder of its own to write to.</summary>
    public bool CanSave => true;

    public bool Exists => File.Exists(_path);

    public string? Load()
    {
        try { return File.ReadAllText(_path); }
        catch (Exception ex)
        {
            Console.WriteLine($"[save] failed to read {_path}: {ex.Message}");
            return null;
        }
    }

    public void Save(string payload) => File.WriteAllText(_path, payload);
}
