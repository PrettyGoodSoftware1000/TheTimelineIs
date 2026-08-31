using System;
using System.IO;
using TheTimelineIs.Core.Platform;

namespace TheTimelineIs.Desktop;

/// <summary>
/// Appends rows to the SOURCE Destinations.txt, found by walking up from the
/// executable to the directory containing TheTimelineIs.sln — so a point
/// placed in dev mode survives the next build. Falls back to the copy next
/// to the executable if the repo isn't found (with a console warning).
/// </summary>
public class DesktopDevDestinationWriter : IDevDestinationWriter
{
    public string Append(string name, int x, int y)
    {
        string path = FindSourceFile();
        bool endsWithNewline = true;
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            endsWithNewline = existing.Length == 0 || existing.EndsWith('\n');
        }
        string row = $"{name,-24}{x,-8}{y}";
        File.AppendAllText(path, (endsWithNewline ? "" : Environment.NewLine) + row + Environment.NewLine);
        Console.WriteLine($"[devmap] appended to {path}: {row}");
        return path;
    }

    public string? Write(string contentPath, string text)
    {
        try
        {
            // into the repo when one is found, so an edit survives the next
            // build; otherwise next to the executable, which does not
            string path = Path.Combine(RepoRoot() ?? AppContext.BaseDirectory, contentPath);
            File.WriteAllText(path, text);
            Console.WriteLine($"[dev] wrote {path}");
            return path;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dev] could not write {contentPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The folder holding the .sln, or null when running outside the repo.</summary>
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheTimelineIs.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static string FindSourceFile()
    {
        if (RepoRoot() is string root)
            return Path.Combine(root, "Content", "Levels", "Destinations.txt");
        Console.WriteLine("[devmap] WARNING: repo root not found; writing next to the executable. " +
            "This copy is overwritten on the next build.");
        return Path.Combine(AppContext.BaseDirectory, "Content", "Levels", "Destinations.txt");
    }
}
