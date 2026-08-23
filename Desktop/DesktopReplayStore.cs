using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TheTimelineIs.Core.Platform;

namespace TheTimelineIs.Desktop;

/// <summary>
/// Replays kept in a Replays folder at the root of the repo, beside Content and
/// Core — found by walking up from the executable looking for the .sln, exactly
/// as the dev map writer does.
///
/// The repo rather than AppData, because these are meant to be looked at: read
/// by eye while working out why a fight went the way it did, and later handed
/// over in a batch to work out what tactics a player favours. A folder nobody
/// can find serves neither. Falls back to beside the executable if the repo
/// is not there, which is what a shipped build would do.
/// </summary>
public class DesktopReplayStore : IReplayStore
{
    private const string ReplaySuffix = ".txt";
    private const string LevelSuffix = ".level.txt";

    public string DisplayPath => Folder;

    private static string Folder
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TheTimelineIs.sln")))
                    return Path.Combine(dir.FullName, "Replays");
                dir = dir.Parent;
            }
            return Path.Combine(AppContext.BaseDirectory, "Replays");
        }
    }

    public IReadOnlyList<string> List()
    {
        try
        {
            if (!Directory.Exists(Folder)) return Array.Empty<string>();
            return new DirectoryInfo(Folder).GetFiles("*" + ReplaySuffix)
                .Where(f => !f.Name.EndsWith(LevelSuffix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => Path.GetFileNameWithoutExtension(f.Name))
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[replay] could not list {Folder}: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public string? Save(string name, string replayText, string levelText)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, name + ReplaySuffix);
            File.WriteAllText(path, replayText);
            File.WriteAllText(Path.Combine(Folder, name + LevelSuffix), levelText);
            Console.WriteLine($"[replay] wrote {path}");
            return path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[replay] could not write {name}: {ex.Message}");
            return null;
        }
    }

    public (string Replay, string Level)? Load(string name)
    {
        try
        {
            string replay = Path.Combine(Folder, name + ReplaySuffix);
            string level = Path.Combine(Folder, name + LevelSuffix);
            if (!File.Exists(replay) || !File.Exists(level)) return null;
            return (File.ReadAllText(replay), File.ReadAllText(level));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[replay] could not read {name}: {ex.Message}");
            return null;
        }
    }
}
