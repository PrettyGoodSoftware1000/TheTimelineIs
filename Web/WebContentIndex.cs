using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TheTimelineIs.Core.Platform;

namespace TheTimelineIs.Web;

/// <summary>
/// Lists a content folder from ContentIndex.txt, written at build time, because
/// a browser can fetch a file by name but cannot ask what is in a folder.
///
/// So art dropped into a folder still shows up on the web — after the next
/// build, rather than the next launch.
/// </summary>
public class WebContentIndex : IContentIndex
{
    private readonly Dictionary<string, List<string>> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SortedSet<string>> _folders = new(StringComparer.OrdinalIgnoreCase);

    public WebContentIndex()
    {
        using var stream = typeof(WebContentIndex).Assembly.GetManifestResourceStream("ContentIndex.txt");
        if (stream == null) return;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is string line)
        {
            // "wwwroot\Content\Cast\Gator\south-east.png" -> "Content/Cast/Gator/south-east.png"
            string path = line.Trim().Replace('\\', '/');
            if (path.StartsWith("wwwroot/", StringComparison.OrdinalIgnoreCase)) path = path["wwwroot/".Length..];
            int cut = path.LastIndexOf('/');
            if (cut < 0) continue;
            Add(_files, path[..cut], path[(cut + 1)..]);

            // every folder on the way down is a folder of its parent
            for (int at = path.IndexOf('/'); at >= 0 && at < cut; at = path.IndexOf('/', at + 1))
            {
                int next = path.IndexOf('/', at + 1);
                if (!_folders.TryGetValue(path[..at], out var set))
                    _folders[path[..at]] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(path[(at + 1)..next]);
            }
        }
    }

    private static void Add(Dictionary<string, List<string>> map, string folder, string name)
    {
        if (!map.TryGetValue(folder, out var list)) map[folder] = list = new List<string>();
        list.Add(name);
    }

    public IReadOnlyList<string> Files(string folder, params string[] extensions) =>
        _files.TryGetValue(folder.TrimEnd('/'), out var names)
            ? names.Where(n => extensions.Any(e => n.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();

    public IReadOnlyList<string> Folders(string folder) =>
        _folders.TryGetValue(folder.TrimEnd('/'), out var names) ? names.ToList() : Array.Empty<string>();
}
