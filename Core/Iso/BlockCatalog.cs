using System;
using System.Collections.Generic;
using System.Linq;
using TheTimelineIs.Core.Data;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// The block palette (Content/Images/Blocks/Blocks.txt, one type name per
/// line: {Type}Top.png is the 360x180 diamond, {Type}Side.png the 360x90
/// strip stacked once per foot of height) and the decoration list
/// (Content/Images/Decorations/Decorations.txt, one file name per line).
/// Index files exist because app bundles can't enumerate directories.
/// </summary>
public static class BlockCatalog
{
    public const string BlocksIndex = "Content/Images/Blocks/Blocks.txt";
    public const string DecorationsIndex = "Content/Images/Decorations/Decorations.txt";

    private static List<string>? _blocks;
    private static List<string>? _decorations;

    public static IReadOnlyList<string> BlockTypes =>
        _blocks ??= AssetLoader.ReadNumbered(BlocksIndex, BlocksIndex).Select(l => l.Text).ToList();

    public static IReadOnlyList<string> Decorations =>
        _decorations ??= AssetLoader.ReadNumbered(DecorationsIndex, DecorationsIndex).Select(l => l.Text).ToList();

    public static bool IsBlockType(string type) =>
        BlockTypes.Contains(type, StringComparer.OrdinalIgnoreCase);

    public static string TopPath(string type) => $"Content/Images/Blocks/{type}Top.png";
    public static string SidePath(string type) => $"Content/Images/Blocks/{type}Side.png";
    public static string DecorationPath(string file) => $"Content/Images/Decorations/{file}";

    /// <summary>Tests re-load content between runs.</summary>
    public static void Reset() { _blocks = null; _decorations = null; }
}
