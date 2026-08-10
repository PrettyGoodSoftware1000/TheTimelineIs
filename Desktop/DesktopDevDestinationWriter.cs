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
    public string Append(string name, int x, int y, string mission)
    {
        string path = FindSourceFile();
        bool endsWithNewline = true;
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            endsWithNewline = existing.Length == 0 || existing.EndsWith('\n');
        }
        string row = $"{name,-24}{x,-8}{y,-8}{mission}";
        File.AppendAllText(path, (endsWithNewline ? "" : Environment.NewLine) + row + Environment.NewLine);
        Console.WriteLine($"[devmap] appended to {path}: {row}");
        return path;
    }

    private static string FindSourceFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheTimelineIs.sln")))
                return Path.Combine(dir.FullName, "Content", "Levels", "Destinations.txt");
            dir = dir.Parent;
        }
        Console.WriteLine("[devmap] WARNING: repo root not found; writing next to the executable. " +
            "This copy is overwritten on the next build.");
        return Path.Combine(AppContext.BaseDirectory, "Content", "Levels", "Destinations.txt");
    }
}
