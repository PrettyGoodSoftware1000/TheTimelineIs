using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Iso;
using TheTimelineIs.Core.Render;

namespace TheTimelineIs.Desktop;

/// <summary>
/// The Anchor tool: the half of the editor that decides where a piece of ground
/// art sits on its square, and how big it is drawn.
///
/// It shows the selected .png over a crosshair — one vertical and one
/// horizontal line, one pixel wide — with the real grid diamond drawn where the
/// two cross. Drag the art to move it under the crosshair; the pixel left under
/// the crossing point is the anchor. Size it against the diamond by scrolling,
/// or by typing a percentage.
///
/// Everything here is drawn at <see cref="PreviewZoom"/>x, art and diamond
/// alike, so a few pixels of misalignment are visible. Because both are
/// magnified by the same factor what is lined up here is still exactly what
/// appears in the level; the zoom never reaches the numbers that are saved.
/// </summary>
/// <summary>The editor's Reload button: pick up Blocks.txt again as it stands on disk.</summary>
public partial class IsoEditorScreen
{
    /// <summary>
    /// Re-reads Blocks.txt, so hand edits and anything the Anchor tool just
    /// wrote show up without restarting.
    ///
    /// The copy step is the awkward part. Content is read through
    /// TitleContainer, which resolves against the built output next to the
    /// executable, while the editor writes to the source tree — so a plain
    /// re-read would faithfully reload the stale copy that was there at build
    /// time. Copying source over output first is what makes the reload mean
    /// what it says.
    /// </summary>
    private void ReloadGroundArt()
    {
        if (MirrorContent("Content/Images/Blocks/Blocks.txt") is string why)
            Status($"could not refresh the built copy of Blocks.txt: {why}");
        BlockCatalog.Reset();
        RecountProblems();
    }

    /// <summary>
    /// Copies one content file from the source tree to the copy next to the
    /// executable, which is the one the game actually reads.
    ///
    /// Anything the editor writes — a saved level, a reloaded Blocks.txt, art
    /// that has just been imported — goes into the source tree, because that
    /// is what gets committed. Without this step it is written and invisible.
    ///
    /// The path is content-relative and starts with "Content/", the way every
    /// other path in the game is written. Returns null when it worked, and
    /// why when it did not.
    /// </summary>
    private static string? MirrorContent(string contentPath)
    {
        try
        {
            string tail = contentPath.StartsWith("Content/", StringComparison.OrdinalIgnoreCase)
                ? contentPath["Content/".Length..] : contentPath;
            tail = tail.Replace('/', Path.DirectorySeparatorChar);
            string source = Path.Combine(SourceContentDir, tail);
            string output = Path.Combine(AppContext.BaseDirectory, "Content", tail);
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(output),
                    StringComparison.OrdinalIgnoreCase))
                return null;                      // a shipped build: one copy only
            if (!File.Exists(source)) return $"{contentPath} is not there";
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(source, output, overwrite: true);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>The Reload button: pick up Blocks.txt as it stands on disk.</summary>
    private void ReloadFromDisk()
    {
        ReloadGroundArt();
        Status($"reloaded Blocks.txt — {BlockCatalog.Pieces.Count} piece(s) in " +
               $"{BlockCatalog.Families.Count} famil{(BlockCatalog.Families.Count == 1 ? "y" : "ies")}");
    }

    /// <summary>
    /// Keeps the built copy of a level file in step with the one the editor
    /// just wrote.
    ///
    /// The editor writes to the SOURCE tree, because that is what gets
    /// committed. Everything that READS a level — playtesting, opening
    /// another map from the menu — goes through TitleContainer, which resolves
    /// against the copy the build made next to the executable. Without this
    /// step a map just saved, renamed or made is only half there: listed in
    /// the folder the editor reads and missing from the folder the game does.
    ///
    /// Passing null for <paramref name="to"/> removes the built copy instead,
    /// which is what a rename needs for the name it left behind.
    /// </summary>
    private static void MirrorLevel(string from, string? to)
    {
        string source = Path.Combine(SourceContentDir, "Levels", from);
        string output = Path.Combine(AppContext.BaseDirectory, "Content", "Levels", to ?? from);
        try
        {
            if (string.Equals(Path.GetFullPath(Path.Combine(SourceContentDir, "Levels")),
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Content", "Levels")),
                    StringComparison.OrdinalIgnoreCase))
                return;                                   // a shipped build: one copy only
            if (to == null) { if (File.Exists(output)) File.Delete(output); return; }
            if (!File.Exists(source)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(source, output, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[editor] could not refresh the built copy of {from}: {ex.Message}");
        }
    }

    /// <summary>The repo's Content folder, so a save lands in the source tree.</summary>
    private static string SourceContentDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TheTimelineIs.sln")))
                    return Path.Combine(dir.FullName, "Content");
                dir = dir.Parent;
            }
            return Path.Combine(AppContext.BaseDirectory, "Content");
        }
    }

}
