using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace TheTimelineIs.SpriteTool;

/// <summary>
/// Works out how a sheet somebody else made is cut into frames, and writes the
/// companion .txt for it.
///
/// A sheet this tool built needs none of this: its cells tile the whole PNG
/// from (0,0) and the .txt was written alongside it. Sheets from anywhere else
/// — an image generator, an artist's canvas, a screenshot — may or may not do
/// the same. Some tile the canvas neatly; others sit in the middle of a much
/// larger image with wide transparent margins and a cell size that divides
/// nothing in particular, and guessing "width divided by columns" on one of
/// those slices every frame through the middle.
///
/// So the art is allowed to say where the frames are:
///
///   1. A row of pixels that is entirely transparent is a GUTTER; a run of rows
///      that isn't is a BAND. Bands are NOT assumed to be one per frame — a
///      frame's own art often has thin transparent slivers running clean across
///      it, and a trailing wisp of spell effect does exactly that. The rule is
///      only that a band must not straddle a cell boundary, so any number of
///      bands may share a cell.
///   2. A candidate grid is valid when no band straddles one of its boundaries
///      AND every one of its cells holds something. That second half is what
///      stops the search from padding the answer with blank cells and calling
///      four columns of art three.
///   3. The most cells that can be fitted that way wins, because a coarser grid
///      always also fits — sixteen rows of art can always be read as eight rows
///      of two.
///
/// The tiling case is tried first and separately, both because it is the common
/// one and because it is exact: there is nothing to search when the cells are
/// the image divided by a whole number. Only sheets that aren't laid out that
/// way pay for the general search.
/// </summary>
public static class Detect
{
    /// <summary>Alpha at or below this counts as transparent — PNG edges are rarely a clean 0.</summary>
    public const int AlphaThreshold = 8;

    public static int Run(Options o)
    {
        if (!File.Exists(o.SheetPath))
        {
            Console.Error.WriteLine($"no such file: {o.SheetPath}");
            return 2;
        }

        using var sheet = Image.Load<Rgba32>(o.SheetPath);
        var (rowHas, colHas) = Occupancy(sheet);
        var rowBands = Bands(rowHas);
        var colBands = Bands(colHas);
        string name = Path.GetFileName(o.SheetPath);

        if (rowBands.Count == 0 || colBands.Count == 0)
        {
            Console.Error.WriteLine($"{name} is entirely transparent — there is nothing to measure");
            return 2;
        }

        var across = FitAxis(colBands, sheet.Width, o.Columns);
        var down = FitAxis(rowBands, sheet.Height, o.Rows);
        if (across == null || down == null)
        {
            Console.Error.WriteLine(
                $"could not fit an even grid over the art in {name}. Frames of different sizes, " +
                "or frames that run together with no transparent gap at all, cannot be measured " +
                "this way — pass --columns and --rows to say what the grid is, or " +
                "--frame-width and --frame-height to slice it by hand.");
            return 2;
        }

        var (cw, ox, columns) = across.Value;
        var (ch, oy, rows) = down.Value;
        int frames = o.Frames > 0 ? o.Frames : CountFrames(sheet, columns, rows, cw, ch, ox, oy);

        Console.WriteLine($"{name} is {sheet.Width}x{sheet.Height}");
        Console.WriteLine($"cells are {cw}x{ch} — {columns} x {rows} = {columns * rows} cells, " +
                          $"{frames} of them used");
        if (ox > 0 || oy > 0)
            Console.WriteLine($"the grid is inset, starting at ({ox},{oy}): {ox}px of dead margin " +
                              $"on the left, {oy}px on top");
        else if (columns * cw == sheet.Width && rows * ch == sheet.Height)
            Console.WriteLine("the cells tile the whole image, the same as a sheet built here");

        string meta = Path.ChangeExtension(o.SheetPath, ".txt");
        if (o.DryRun)
        {
            Console.WriteLine($"--dry-run, so {Path.GetFileName(meta)} was not written");
            return 0;
        }
        File.WriteAllText(meta, Sheet.Metadata(name, cw, ch, columns, rows, frames, ox, oy,
            o.Fps, o.ScalePercent, sourceFrames: null,
            note: "measured from the art by 'spritetool detect'"));
        Console.WriteLine($"wrote {Path.GetFullPath(meta)}");
        return 0;
    }

    /// <summary>
    /// Which rows and which columns hold any pixel at all. One pass over the
    /// image; everything after this works on the two boolean arrays.
    /// </summary>
    private static (bool[] Rows, bool[] Cols) Occupancy(Image<Rgba32> image)
    {
        var rows = new bool[image.Height];
        var cols = new bool[image.Width];
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var span = accessor.GetRowSpan(y);
                for (int x = 0; x < span.Length; x++)
                {
                    if (span[x].A <= AlphaThreshold) continue;
                    rows[y] = true;
                    cols[x] = true;
                }
            }
        });
        return (rows, cols);
    }

    /// <summary>Runs of true, as inclusive [start, end] pairs.</summary>
    private static List<(int Start, int End)> Bands(bool[] occupied)
    {
        var bands = new List<(int Start, int End)>();
        int start = -1;
        for (int i = 0; i < occupied.Length; i++)
        {
            if (occupied[i]) { if (start < 0) start = i; }
            else if (start >= 0) { bands.Add((start, i - 1)); start = -1; }
        }
        if (start >= 0) bands.Add((start, occupied.Length - 1));
        return bands;
    }

    /// <summary>
    /// The cell size, starting offset and count along one axis, or null when no
    /// even grid holds the art. <paramref name="forced"/> above zero pins the
    /// count and only the size and offset are looked for.
    /// </summary>
    private static (int Pitch, int Offset, int Count)? FitAxis(
        List<(int Start, int End)> bands, int size, int forced)
    {
        int first = bands[0].Start, last = bands[^1].End;
        // no cell can be shorter than the tallest band, or that band straddles
        int tallest = bands.Max(b => b.End - b.Start + 1);
        int highest = forced > 0 ? forced : bands.Count;   // a cell needs a band, so this is the ceiling
        int lowest = forced > 0 ? forced : 1;

        // Most cells first, so a coarse grid never wins over the real one: any
        // sheet of sixteen rows can also be read as eight rows of two.
        for (int count = highest; count >= lowest; count--)
        {
            // The common case, and exact: cells tiling the whole image from its
            // corner. There is nothing to search — the size is the image
            // divided by the count — so it is tried first at every count.
            if (size % count == 0 && size / count >= tallest &&
                Clearance(bands, count, size / count, 0) >= 0)
                return (size / count, 0, count);

            // Otherwise the grid may be inset somewhere in a bigger canvas.
            // Among the grids of this count, take the one whose boundaries sit
            // furthest from any art.
            int widest = size / count;
            int narrowest = Math.Max(tallest, (last + 1 - first + count - 1) / count);
            (int Pitch, int Offset, int Clear)? best = null;

            for (int pitch = narrowest; pitch <= widest; pitch++)
            {
                // the grid has to start at or before the art and end at or after it
                int lo = Math.Max(0, last + 1 - count * pitch);
                int hi = Math.Min(first, size - count * pitch);
                for (int offset = lo; offset <= hi; offset++)
                {
                    int clear = Clearance(bands, count, pitch, offset);
                    if (clear >= 0 && (best == null || clear > best.Value.Clear))
                        best = (pitch, offset, clear);
                }
            }
            if (best is { } b) return (b.Pitch, b.Offset, count);
        }
        return null;
    }

    /// <summary>
    /// How much room this grid leaves: the smallest distance from any band to
    /// the boundary of the cell holding it. Negative means the grid is invalid
    /// — a band straddles a boundary, or a cell has nothing in it at all.
    /// </summary>
    private static int Clearance(List<(int Start, int End)> bands, int count, int pitch, int offset)
    {
        var used = new bool[count];
        int clear = int.MaxValue;

        foreach (var (start, end) in bands)
        {
            int cell = (start - offset) / pitch;
            if (cell < 0 || cell >= count) return -1;
            // both ends in the same cell, or the band is cut in half
            if ((end - offset) / pitch != cell) return -1;
            used[cell] = true;

            int cellStart = offset + cell * pitch;
            clear = Math.Min(clear, Math.Min(start - cellStart, cellStart + pitch - 1 - end));
        }

        // a grid with an empty cell is a grid with one cell too many
        foreach (bool u in used) if (!u) return -1;
        return clear == int.MaxValue ? 0 : clear;
    }

    /// <summary>
    /// How many cells are actually drawn in. Frames run in reading order, so
    /// trailing empty cells are padding on the last row rather than blank
    /// frames in the middle of the animation.
    /// </summary>
    private static int CountFrames(Image<Rgba32> sheet, int columns, int rows,
        int cw, int ch, int ox, int oy)
    {
        for (int n = columns * rows - 1; n >= 0; n--)
        {
            var cell = new Rectangle(ox + n % columns * cw, oy + n / columns * ch, cw, ch);
            if (HasContent(sheet, cell)) return n + 1;
        }
        return 0;
    }

    private static bool HasContent(Image<Rgba32> sheet, Rectangle cell)
    {
        bool found = false;
        sheet.ProcessPixelRows(accessor =>
        {
            for (int y = cell.Top; y < cell.Bottom && !found; y++)
            {
                var span = accessor.GetRowSpan(y);
                for (int x = cell.Left; x < cell.Right; x++)
                    if (span[x].A > AlphaThreshold) { found = true; break; }
            }
        });
        return found;
    }

    public sealed class Options
    {
        public string SheetPath = "";
        /// <summary>0 = take the most cells the art will support.</summary>
        public int Columns, Rows;
        /// <summary>0 = count the cells that have something drawn in them.</summary>
        public int Frames;
        public float Fps = Sheet.DefaultFps;
        public float ScalePercent = Sheet.DefaultScalePercent;
        public bool DryRun;
    }
}
