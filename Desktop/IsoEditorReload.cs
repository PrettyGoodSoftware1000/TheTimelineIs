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
        const string relative = "Images/Blocks/Blocks.txt";
        string source = Path.Combine(SourceContentDir, relative.Replace('/', Path.DirectorySeparatorChar));
        string output = Path.Combine(AppContext.BaseDirectory, "Content",
            relative.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (File.Exists(source) && !string.Equals(
                    Path.GetFullPath(source), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(source, output, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Status($"could not refresh the built copy of Blocks.txt: {ex.Message}");
        }
        BlockCatalog.Reset();
        RecountProblems();
    }

    /// <summary>The Reload button: pick up Blocks.txt as it stands on disk.</summary>
    private void ReloadFromDisk()
    {
        ReloadGroundArt();
        Status($"reloaded Blocks.txt — {BlockCatalog.Pieces.Count} piece(s) in " +
               $"{BlockCatalog.Families.Count} famil{(BlockCatalog.Families.Count == 1 ? "y" : "ies")}");
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
