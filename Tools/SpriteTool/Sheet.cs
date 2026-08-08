using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace TheTimelineIs.SpriteTool;

/// <summary>
/// PNGs in, one sprite sheet out — and back again. Frames are assumed to share
/// their dimensions, which the caller guarantees; the first frame sets the cell
/// size and anything that disagrees is reported rather than quietly stretched.
///
/// Every sheet is written with a plain-text .txt beside it holding the cell
/// size, the grid and the frame count. That is what lets the game (or the
/// slice command) cut it up again without anybody having to remember the
/// numbers, and it matches how the rest of this project stores its data.
/// </summary>
public static class Sheet
{
    public static int Build(Options o)
    {
        var files = ResolveInputs(o.Inputs);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("no input .png files matched");
            return 2;
        }

        using var first = Image.Load<Rgba32>(files[0]);
        int cw = first.Width, ch = first.Height;

        // a mismatch means the animation would tear; say which file and stop
        var odd = new List<string>();
        foreach (var f in files.Skip(1))
        {
            var info = Image.Identify(f);
            if (info.Width != cw || info.Height != ch)
                odd.Add($"    {Path.GetFileName(f)} is {info.Width}x{info.Height}");
        }
        if (odd.Count > 0)
        {
            Console.Error.WriteLine(
                $"frames must all be the same size. {Path.GetFileName(files[0])} is {cw}x{ch}, but:");
            foreach (var line in odd.Take(10)) Console.Error.WriteLine(line);
            if (odd.Count > 10) Console.Error.WriteLine($"    ...and {odd.Count - 10} more");
            return 2;
        }

        int columns = o.Columns > 0 ? o.Columns : ChooseColumns(files.Count, cw, ch, o.MaxWidth);
        int rows = (files.Count + columns - 1) / columns;

        Console.WriteLine($"{files.Count} frames of {cw}x{ch} -> {columns} x {rows} " +
                          $"({columns * cw} x {rows * ch} px)");

        using var sheet = new Image<Rgba32>(columns * cw, rows * ch);
        for (int i = 0; i < files.Count; i++)
        {
            using var frame = Image.Load<Rgba32>(files[i]);
            int x = i % columns * cw, y = i / columns * ch;
            sheet.Mutate(ctx => ctx.DrawImage(frame, new Point(x, y), 1f));
        }

        string outPng = o.Output;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng))!);
        sheet.SaveAsPng(outPng);

        string meta = Path.ChangeExtension(outPng, ".txt");
        File.WriteAllText(meta, Metadata(Path.GetFileName(outPng), cw, ch, columns, rows, files));
        Console.WriteLine($"wrote {Path.GetFullPath(outPng)}");
        Console.WriteLine($"wrote {Path.GetFullPath(meta)}");
        return 0;
    }

    /// <summary>
    /// A sheet's companion file. Keys match the style of the game's other
    /// content files: "Key: value", '#' comments, case-insensitive in practice.
    /// </summary>
    private static string Metadata(string png, int cw, int ch, int cols, int rows,
        List<string> files) =>
        $"# Sprite sheet written by spritetool. One frame per cell, left to right,\n" +
        $"# top to bottom. Cells are uniform, so frame N sits at\n" +
        $"#   x = (N % Columns) * FrameWidth,  y = (N / Columns) * FrameHeight\n" +
        $"# counting N from 0.\n" +
        $"Sheet: {png}\n" +
        $"FrameWidth: {cw}\n" +
        $"FrameHeight: {ch}\n" +
        $"Columns: {cols}\n" +
        $"Rows: {rows}\n" +
        $"Frames: {files.Count}\n" +
        $"# source frames, in order:\n" +
        string.Join("\n", files.Select((f, i) => $"#   {i}: {Path.GetFileName(f)}")) + "\n";

    /// <summary>
    /// Cuts a sheet back into numbered PNGs. Reads the companion .txt when it
    /// is there; otherwise the caller has to say what the cells are.
    /// </summary>
    public static int Slice(SliceOptions o)
    {
        if (!File.Exists(o.Sheet))
        {
            Console.Error.WriteLine($"no such file: {o.Sheet}");
            return 2;
        }

        int cw = o.FrameWidth, ch = o.FrameHeight, count = 0, columns = 0;
        string meta = Path.ChangeExtension(o.Sheet, ".txt");
        if (File.Exists(meta))
        {
            foreach (var raw in File.ReadAllLines(meta))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var bits = line.Split(':', 2);
                if (bits.Length < 2 || !int.TryParse(bits[1].Trim(), out int v)) continue;
                switch (bits[0].Trim().ToLowerInvariant())
                {
                    case "framewidth": if (cw <= 0) cw = v; break;
                    case "frameheight": if (ch <= 0) ch = v; break;
                    case "columns": columns = v; break;
                    case "frames": count = v; break;
                }
            }
            Console.WriteLine($"read cell size from {Path.GetFileName(meta)}");
        }

        if (cw <= 0 || ch <= 0)
        {
            Console.Error.WriteLine(
                "cell size unknown: no companion .txt beside the sheet, so pass " +
                "--frame-width and --frame-height");
            return 2;
        }

        using var sheet = Image.Load<Rgba32>(o.Sheet);
        if (columns <= 0) columns = sheet.Width / cw;
        int rows = sheet.Height / ch;
        if (count <= 0) count = columns * rows;

        Directory.CreateDirectory(o.OutDir);
        int pad = count.ToString().Length;
        for (int i = 0; i < count; i++)
        {
            int x = i % columns * cw, y = i / columns * ch;
            if (x + cw > sheet.Width || y + ch > sheet.Height) break;
            using var frame = sheet.Clone(ctx => ctx.Crop(new Rectangle(x, y, cw, ch)));
            frame.SaveAsPng(Path.Combine(o.OutDir,
                $"{o.Name}{(i + 1).ToString().PadLeft(pad, '0')}.png"));
        }
        Console.WriteLine($"wrote {count} png(s) of {cw}x{ch} to {Path.GetFullPath(o.OutDir)}");
        return 0;
    }

    /// <summary>
    /// Inputs may be folders, globs, or a list of files. A folder or glob is
    /// sorted naturally, so frame2 lands before frame10 whatever the padding.
    /// An explicit list of files is left in the order it was given.
    /// </summary>
    private static List<string> ResolveInputs(List<string> inputs)
    {
        var files = new List<string>();
        bool explicitList = true;

        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                files.AddRange(Directory.GetFiles(input, "*.png"));
                explicitList = false;
            }
            else if (input.Contains('*') || input.Contains('?'))
            {
                string? parent = Path.GetDirectoryName(input);
                string dir = string.IsNullOrEmpty(parent) ? "." : parent;
                if (Directory.Exists(dir))
                    files.AddRange(Directory.GetFiles(dir, Path.GetFileName(input)));
                explicitList = false;
            }
            else if (File.Exists(input)) files.Add(input);
            else Console.Error.WriteLine($"warning: skipping '{input}' — not a file, folder or glob");
        }

        return explicitList ? files : files.OrderBy(f => f, new NaturalOrder()).ToList();
    }

    /// <summary>
    /// Roughly square, but never wider than MaxWidth pixels — GPUs are happier
    /// with sheets that don't run to one enormous strip.
    /// </summary>
    private static int ChooseColumns(int count, int cw, int ch, int maxWidth)
    {
        int byWidth = Math.Max(1, maxWidth / Math.Max(1, cw));
        int square = (int)Math.Ceiling(Math.Sqrt(count));
        return Math.Max(1, Math.Min(byWidth, Math.Max(square, 1)));
    }

    /// <summary>
    /// Sorts frame2 before frame10 by comparing runs of digits as numbers.
    /// Plain string order gets this wrong whenever the padding is inconsistent.
    /// </summary>
    private sealed class NaturalOrder : IComparer<string>
    {
        public int Compare(string? a, string? b)
        {
            a ??= ""; b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var na = a.AsSpan(si, i - si).TrimStart('0');
                    var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length - nb.Length;
                    int cmp = na.SequenceCompareTo(nb);
                    if (cmp != 0) return cmp;
                }
                else
                {
                    int cmp = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (cmp != 0) return cmp;
                    i++; j++;
                }
            }
            return (a.Length - i) - (b.Length - j);
        }
    }

    public sealed class Options
    {
        public List<string> Inputs = new();
        public string Output = "sheet.png";
        /// <summary>0 = pick a roughly square grid under MaxWidth.</summary>
        public int Columns;
        public int MaxWidth = 4096;
    }

    public sealed class SliceOptions
    {
        public string Sheet = "";
        public string OutDir = ".";
        public string Name = "frame";
        public int FrameWidth, FrameHeight;
    }
}
