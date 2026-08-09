using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace TheTimelineIs.SpriteTool;

/// <summary>
/// Works out how a sheet somebody else made is cut into frames, and writes the
/// companion .txt for it.
///
/// A sheet this tool built needs none of this: its cells tile the whole PNG
/// from (0,0), and the .txt was written alongside it. Sheets from anywhere else
/// — an AI image generator, an artist's canvas, a screenshot — routinely sit in
/// the middle of a much larger image with wide transparent margins all round,
/// and their cell size divides nothing in particular. Guessing "width divided
/// by columns" on one of those slices every frame through the middle.
///
/// The method is to let the art say where the frames are:
///
///   1. A row of pixels that is entirely transparent is a GUTTER; a run of rows
///      that isn't is a BAND. One band per row of frames, and the same down the
///      other axis for columns. That already gives the grid's shape.
///   2. The pitch and the starting offset then have to satisfy every band at
///      once: band i must fall inside cell i and touch neither edge. Written
///      out, that is
///          offset + i * pitch      &lt;= band[i].Start
///          offset + (i+1) * pitch  &gt;= band[i].End + 1
///      which for a fixed pitch is just a range of allowed offsets. Sweep pitch
///      across a few pixels either side of the spacing the bands imply, keep
///      whichever leaves the widest range, and take the middle of it — the
///      offset furthest from clipping anything.
///
/// If the bands don't agree on any pitch, that is said plainly rather than
/// guessed at, and --columns / --rows are there to force the issue.
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
        var rowBands = MergeSlivers(Bands(rowHas));
        var colBands = MergeSlivers(Bands(colHas));

        if (rowBands.Count == 0 || colBands.Count == 0)
        {
            Console.Error.WriteLine(
                $"{Path.GetFileName(o.SheetPath)} is entirely transparent — there is nothing to measure");
            return 2;
        }

        int columns = o.Columns > 0 ? o.Columns : colBands.Count;
        int rows = o.Rows > 0 ? o.Rows : rowBands.Count;
        Console.WriteLine($"{Path.GetFileName(o.SheetPath)} is {sheet.Width}x{sheet.Height}, " +
                          $"holding {colBands.Count} column(s) and {rowBands.Count} row(s) of art");

        var across = FitAxis(colBands, columns, sheet.Width);
        var down = FitAxis(rowBands, rows, sheet.Height);
        if (across == null || down == null)
        {
            Console.Error.WriteLine(
                $"could not fit {columns} x {rows} evenly spaced cells over the art in " +
                $"{Path.GetFileName(o.SheetPath)}. Frames of different sizes, or frames that touch, " +
                "cannot be measured this way — pass --columns and --rows if the count is wrong, " +
                "or --frame-width and --frame-height to slice it by hand.");
            return 2;
        }

        var (cw, ox) = across.Value;
        var (ch, oy) = down.Value;
        int frames = o.Frames > 0
            ? o.Frames
            : CountFrames(sheet, columns, rows, cw, ch, ox, oy);

        Console.WriteLine($"cells are {cw}x{ch} starting at ({ox},{oy}) — " +
                          $"{columns} x {rows} = {columns * rows} cells, {frames} of them used");
        if (ox > 0 || oy > 0)
            Console.WriteLine($"the grid is inset: {ox}px of margin on the left, {oy}px on top");

        string meta = Path.ChangeExtension(o.SheetPath, ".txt");
        if (o.DryRun)
        {
            Console.WriteLine($"--dry-run, so {Path.GetFileName(meta)} was not written");
            return 0;
        }
        File.WriteAllText(meta, Sheet.Metadata(Path.GetFileName(o.SheetPath), cw, ch, columns, rows,
            frames, ox, oy, o.Fps, o.ScalePercent, sourceFrames: null,
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
    /// A gap this many times wider than the one below it is taken to be where
    /// slivers stop and real gutters begin.
    /// </summary>
    private const double SliverStep = 3.0;

    /// <summary>
    /// Frames are separated by wide transparent gutters — but a frame's own art
    /// can have thin transparent slivers running clean across it too, and a
    /// trailing wisp of spell effect does exactly that. Left alone those read as
    /// extra frames.
    ///
    /// Both kinds of gap sit in the same list, so rather than guess a pixel
    /// threshold, the split between them is found: sort the gaps and look for
    /// the one place where the next is several times the last. On a sheet whose
    /// frames are evenly spaced there is no such step and nothing is merged.
    /// </summary>
    private static List<(int Start, int End)> MergeSlivers(List<(int Start, int End)> bands)
    {
        if (bands.Count < 3) return bands;

        var gaps = new List<int>();
        for (int i = 1; i < bands.Count; i++) gaps.Add(bands[i].Start - bands[i - 1].End - 1);

        var sorted = gaps.OrderBy(g => g).ToList();
        int sliverMax = 0;
        double biggestStep = SliverStep;
        for (int i = 0; i + 1 < sorted.Count; i++)
        {
            double step = sorted[i + 1] / (double)Math.Max(1, sorted[i]);
            if (step > biggestStep) { biggestStep = step; sliverMax = sorted[i]; }
        }
        if (sliverMax == 0) return bands;

        var merged = new List<(int Start, int End)> { bands[0] };
        for (int i = 1; i < bands.Count; i++)
        {
            if (gaps[i - 1] <= sliverMax) merged[^1] = (merged[^1].Start, bands[i].End);
            else merged.Add(bands[i]);
        }
        return merged;
    }

    /// <summary>
    /// The cell pitch and starting offset along one axis, or null when no
    /// uniform grid holds every band. See the class comment for the inequality
    /// being solved; the search runs over a handful of integer pitches because
    /// the true one is rarely a whole number and the art has margin to spare.
    /// </summary>
    private static (int Pitch, int Offset)? FitAxis(
        List<(int Start, int End)> bands, int count, int size)
    {
        if (count <= 0 || bands.Count != count) return null;
        // a single frame on this axis: the cell is simply the whole image
        if (count == 1) return (size, 0);

        int estimate = (int)Math.Round(Spacing(bands));
        (int Pitch, int Offset, int Slack)? best = null;

        for (int pitch = Math.Max(1, estimate - 6); pitch <= estimate + 6; pitch++)
        {
            if ((long)pitch * count > size) continue;
            int lo = 0, hi = size - pitch * count;
            for (int i = 0; i < count; i++)
            {
                // cell i has to start at or before its band and end after it
                lo = Math.Max(lo, bands[i].End + 1 - (i + 1) * pitch);
                hi = Math.Min(hi, bands[i].Start - i * pitch);
            }
            if (lo > hi) continue;
            int slack = hi - lo;
            if (best == null || slack > best.Value.Slack)
                best = (pitch, (lo + hi) / 2, slack);
        }
        return best is { } b ? (b.Pitch, b.Offset) : null;
    }

    /// <summary>
    /// Least-squares spacing of the band starts. Fitting the whole line beats
    /// measuring first to last, which lets one badly drawn end frame drag the
    /// estimate off.
    /// </summary>
    private static double Spacing(List<(int Start, int End)> bands)
    {
        double meanIndex = (bands.Count - 1) / 2.0;
        double meanStart = bands.Average(b => (double)b.Start);
        double top = 0, bottom = 0;
        for (int i = 0; i < bands.Count; i++)
        {
            double di = i - meanIndex;
            top += di * (bands[i].Start - meanStart);
            bottom += di * di;
        }
        return bottom == 0 ? 0 : top / bottom;
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
        /// <summary>0 = count the bands in the art.</summary>
        public int Columns, Rows;
        /// <summary>0 = count the cells that have something drawn in them.</summary>
        public int Frames;
        public float Fps = Sheet.DefaultFps;
        public float ScalePercent = Sheet.DefaultScalePercent;
        public bool DryRun;
    }
}
