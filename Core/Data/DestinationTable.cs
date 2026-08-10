using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Data;

public record Destination(string Name, int X, int Y, string Level) { public int Line; }

/// <summary>
/// Parses Content/Levels/Destinations.txt. Columns are whitespace-separated;
/// the LAST three tokens are x, y and the level to load, and everything before
/// them is the destination name — so names may contain spaces. '#' lines are
/// comments.
/// Coordinates are in map-image pixel space, not screen space.
/// </summary>
public class DestinationTable
{
    public List<Destination> All { get; } = new();

    public const string Path = "Content/Levels/Destinations.txt";

    public static DestinationTable Load()
    {
        var table = new DestinationTable();
        try
        {
            foreach (var (lineNo, line) in AssetLoader.ReadNumbered(Path, Path))
            {
                var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 4 ||
                    !int.TryParse(tokens[^3], out int x) ||
                    !int.TryParse(tokens[^2], out int y))
                {
                    Diagnostics.Current.Error(Path, lineNo,
                        $"malformed destination '{line}' — expected 'Name  x  y  Level'");
                    continue;
                }
                string name = string.Join(' ', tokens[..^3]);
                table.All.Add(new Destination(name, x, y, tokens[^1]) { Line = lineNo });
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Current.Error(Path, 0, $"could not be read: {ex.Message}");
        }
        return table;
    }
}
