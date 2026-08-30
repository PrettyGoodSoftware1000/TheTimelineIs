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

    public IReadOnlyList<string> Files(string folder, string extension)
    {
        string key = folder + "|" + extension;
        if (_cache.TryGetValue(key, out var known)) return known;

        var names = new List<string>();
        try
        {
            // relative to the working directory, the same place TitleContainer
            // resolves content from
            if (Directory.Exists(folder))
                names = Directory.GetFiles(folder, "*" + extension)
                    .Select(Path.GetFileName)
                    .Where(n => n != null)
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
