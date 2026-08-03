using System;
using System.IO;
using TheTimelineIs.Core.Platform;

namespace TheTimelineIs.Desktop;

/// <summary>
/// Writes ContentErrors.log next to the save file, and — when running from the
/// source tree — a second copy at the repo root, where it's actually convenient
/// to open while editing Cards.txt.
/// </summary>
public class DesktopLogStore : ILogStore
{
    private readonly string _path;
    private readonly string? _repoPath;

    public DesktopLogStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TheTimelineIs");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "ContentErrors.log");
        _repoPath = FindRepoRoot() is string root
            ? Path.Combine(root, "ContentErrors.log") : null;
    }

    public string DisplayPath => _repoPath ?? _path;

    public void Write(string contents)
    {
        foreach (var target in new[] { _path, _repoPath })
        {
            if (target == null) continue;
            try
            {
                File.WriteAllText(target, contents);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[log] could not write {target}: {ex.Message}");
            }
        }
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheTimelineIs.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
