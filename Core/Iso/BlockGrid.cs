using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// The ground of a level: what is on each square, bottom to top.
///
/// A square used to hold one block. It holds a COLUMN now, because a cube's
/// side wall is exactly one block tall — so a block sitting a whole block
/// above another one is a second cube on top of it, not the same cube moved.
///
/// THE RULE, and it is the only one worth remembering:
///
///   a new block lands in the same BLOCK LEVEL as one already there
///       -> it replaces it, which reads as moving that block up or down
///   a new block lands in a block level of its own
///       -> it stacks
///
/// A block level is a height divided by <see cref="IsoMath.StepsPerBlock"/>:
/// the quarter-steps inside one cube are the same level, and the next cube up
/// is the next level. So painting at 0 and then at 0.25 raises what is there,
/// and painting at 0 and then at 1 leaves two cubes standing on each other.
///
/// WHAT THE REST OF THE GAME SEES. Everything outside the editor cares about
/// one thing: the surface you walk on, which is the top of the column. That is
/// what <c>this[p]</c> and TryGetValue hand back, so walking, pathing, picking
/// and placing all carry on reading a square as one block. Values runs over
/// every block there is, lowest first within a square, which is the order the
/// painter's algorithm wants.
/// </summary>
public class BlockGrid : IEnumerable<KeyValuePair<Point, LevelBlock>>
{
    private readonly Dictionary<Point, List<LevelBlock>> _columns = new();

    /// <summary>Which cube a height belongs to. Four quarter-steps to one.</summary>
    public static int LevelOf(int height) => height / IsoMath.StepsPerBlock;

    /// <summary>Every block on the map, lowest first inside each square.</summary>
    public IEnumerable<LevelBlock> Values => _columns.Values.SelectMany(c => c);

    /// <summary>Every square that has anything standing on it.</summary>
    public IEnumerable<Point> Keys => _columns.Keys;

    /// <summary>How many blocks there are — stacked ones counted one each.</summary>
    public int Count => _columns.Values.Sum(c => c.Count);

    /// <summary>How many squares have ground on them.</summary>
    public int SquareCount => _columns.Count;

    public bool ContainsKey(Point p) => _columns.ContainsKey(p);

    /// <summary>The column on a square, bottom to top. Empty when there is none.</summary>
    public IReadOnlyList<LevelBlock> Column(Point p) =>
        _columns.TryGetValue(p, out var c) ? c : Array.Empty<LevelBlock>();

    /// <summary>Whether a square has a block standing at exactly this height.</summary>
    public bool HasHeight(Point p, int height) =>
        _columns.TryGetValue(p, out var c) && c.Any(b => b.Height == height);

    /// <summary>The block in a square's column that a new one at this height would replace.</summary>
    public LevelBlock? AtLevel(Point p, int height) =>
        _columns.TryGetValue(p, out var c)
            ? c.FirstOrDefault(b => LevelOf(b.Height) == LevelOf(height))
            : null;

    /// <summary>The TOP of the column — the surface anything standing here is standing on.</summary>
    public bool TryGetValue(Point p, out LevelBlock top)
    {
        if (_columns.TryGetValue(p, out var c) && c.Count > 0) { top = c[^1]; return true; }
        top = null!;
        return false;
    }

    public LevelBlock this[Point p]
    {
        get => _columns[p][^1];
        set => Place(p, value);
    }

    /// <summary>
    /// Puts a block on a square by the rule at the top of this file, and says
    /// which block it pushed out — null when it stacked on top of everything
    /// instead of replacing anything.
    /// </summary>
    public LevelBlock? Place(Point p, LevelBlock block)
    {
        block.X = p.X;
        block.Y = p.Y;
        if (!_columns.TryGetValue(p, out var column))
            _columns[p] = column = new List<LevelBlock>();

        var replaced = column.FirstOrDefault(b => LevelOf(b.Height) == LevelOf(block.Height));
        if (replaced != null) column.Remove(replaced);
        column.Add(block);
        column.Sort((a, b) => a.Height.CompareTo(b.Height));
        return replaced;
    }

    /// <summary>
    /// Takes the top block off a square, leaving anything under it standing.
    /// Rubbing out a stack is one cube per stroke, which is the only way to
    /// get back to a cube you buried.
    /// </summary>
    public bool Remove(Point p)
    {
        if (!_columns.TryGetValue(p, out var c) || c.Count == 0) return false;
        c.RemoveAt(c.Count - 1);
        if (c.Count == 0) _columns.Remove(p);
        return true;
    }

    /// <summary>Clears a whole square, however deep it is stacked.</summary>
    public List<LevelBlock> RemoveColumn(Point p)
    {
        if (!_columns.TryGetValue(p, out var c)) return new List<LevelBlock>();
        _columns.Remove(p);
        return c;
    }

    public void Clear() => _columns.Clear();

    public IEnumerator<KeyValuePair<Point, LevelBlock>> GetEnumerator() =>
        _columns.Select(kv => new KeyValuePair<Point, LevelBlock>(kv.Key, kv.Value[^1]))
                .GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
