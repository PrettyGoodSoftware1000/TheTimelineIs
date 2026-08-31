using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// A place on the map. The name IS the level: "Not Ohio" loads
/// Content/Levels/Not Ohio.txt. There is no second field to disagree with it.
/// </summary>
public record Destination(string Name, int X, int Y)
{
    public int Line;

    /// <summary>The level this pin opens. Always named after the pin.</summary>
    public string Level => Name;
}

/// <summary>
/// Parses Content/Levels/Destinations.txt.
///
/// - One pin per line: "Name  x  y".
/// - The last two tokens are x and y; everything before is the name, so names
///   may contain spaces.
/// - The name is also the level file, so the two can never drift apart. It used
///   to carry a fourth column naming the level, and a name with a space in it
///   made that unparseable anyway.
/// - Coordinates are map-image pixels, not screen pixels. '#' lines are comments.
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
                if (tokens.Length < 3 ||
                    !int.TryParse(tokens[^2], out int x) ||
                    !int.TryParse(tokens[^1], out int y))
                {
                    Diagnostics.Current.Error(Path, lineNo,
                        $"malformed destination '{line}' — expected 'Name  x  y'");
                    continue;
                }
                string name = string.Join(' ', tokens[..^2]);
                table.All.Add(new Destination(name, x, y) { Line = lineNo });
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Current.Error(Path, 0, $"could not be read: {ex.Message}");
        }
        return table;
    }
}
