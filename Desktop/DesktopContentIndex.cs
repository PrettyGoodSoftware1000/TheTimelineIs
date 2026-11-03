using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Platform;

namespace TheTimelineIs.Desktop;

/// <summary>
/// Lists a content folder by looking at the folder, which is the whole point:
/// art you can add by dropping files in, with no manifest to keep in step.
///
/// Desktop only. System.IO lives on this side of the line so Core never has to
/// know a file system exists.
/// </summary>
public class DesktopContentIndex : IContentIndex
{
    private readonly Dictionary<string, IReadOnlyList<string>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A content path from where the GAME is, not from where it was started.
    ///
    /// TitleContainer already resolves against the executable's folder, and
    /// these listings have to agree with it. Run from a terminal sitting in
    /// the game's own folder the two are the same thing and the difference
    /// never showed. Double-clicked from the Finder the working directory is
    /// "/", and every rotations/ and animations/ folder came back empty while
    /// the text files still loaded — a whole cast of cubes, and nothing
    /// saying why.
    /// </summary>
    private static string Resolve(string folder) =>
        Path.IsPathRooted(folder) ? folder : Path.Combine(AppContext.BaseDirectory, folder);

    public IReadOnlyList<string> Folders(string folder)
    {
        string key = folder + "|<dirs>";
        if (_cache.TryGetValue(key, out var known)) return known;

        var names = new List<string>();
        try
        {
            folder = Resolve(folder);
            if (Directory.Exists(folder))
                names = Directory.GetDirectories(folder)
                    .Select(Path.GetFileName)
                    .Where(n => n != null)
                    .Select(n => n!)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
        catch (Exception ex)
        {
            Diagnostics.Current.Warn(folder, 0, $"could not be listed ({ex.Message})");
        }
        _cache[key] = names;
        return names;
    }

    public IReadOnlyList<string> Files(string folder, params string[] extensions)
    {
        string key = folder + "|" + string.Join(",", extensions);
        if (_cache.TryGetValue(key, out var known)) return known;

        var names = new List<string>();
        try
        {
            folder = Resolve(folder);
            if (Directory.Exists(folder))
                names = Directory.GetFiles(folder)
                    .Select(Path.GetFileName)
                    .Where(n => n != null && extensions.Any(e =>
                        n.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    .Select(n => n!)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
        catch (Exception ex)
        {
            // an unreadable folder is a content problem, not a crash: say so
            // and carry on with nothing in it
            Diagnostics.Current.Warn(folder, 0, $"could not be listed ({ex.Message})");
        }

        _cache[key] = names;
        return names;
    }
}
