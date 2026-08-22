using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace TheTimelineIs.SpriteTool;

/// <summary>
/// Takes a flat background off artwork and gives it a proper alpha channel.
/// A whole folder at a time, because these arrive 60 to 100 frames at a go.
///
/// This is a job of its own. Nothing here touches video or sprite sheets, and
/// it is never run as part of extracting frames — art comes out of extract with
/// its background still on it, and gets keyed later, once, deliberately.
///
/// HOW IT WORKS, AND WHY NOT THE OBVIOUS WAY
///
/// The obvious way is "the whiter a pixel is, the more see-through it becomes".
/// That destroys the artwork: a pale highlight inside a character is exactly as
/// white as the background beside it, so it turns into a hole. Measured on real
/// art from this project, a global rule like that puts over a million pixels
/// wrong in a single frame.
///
/// So the background is found by CONNECTION instead of by colour. The fill
/// starts at the edges of the image and spreads inward through background-
/// coloured pixels; the character's black outline stops it dead. Whatever the
/// fill never reached is artwork, however pale it happens to be.
///
/// Then the soft edge. Where the fill stops, it stops on pixels that are part
/// outline and part background — the anti-aliasing the artist's brush left. The
/// outline is black and the background is one known colour, so such a pixel is
/// a mix of two knowns, and how much of each can be read straight back out of
/// it: a pixel a quarter of the way from background to black was a quarter
/// covered. Those become black at that coverage. That is what makes the result
/// exact rather than approximate, and it is why the edges have to be black.
/// </summary>
public static class Cutout
{
    public sealed class Options
    {
        /// <summary>Files, folders or globs to key. A folder takes every .png in it.</summary>
        public List<string> Inputs = new();

        /// <summary>Where the results go. Empty means beside the originals, with a suffix.</summary>
        public string OutDir = "";

        /// <summary>Added to the name when writing beside the original.</summary>
        public string Suffix = "_cut";

        /// <summary>The background colour to remove. White unless told otherwise.</summary>
        public Rgba32 Key = new(255, 255, 255);

        /// <summary>
        /// How far from the key colour still counts as background, 0-255 per
        /// channel. 0 demands an exact match; the default allows for the small
        /// wobble any lossy step leaves behind. Raise it for art that has been
        /// through video compression, lower it if pale artwork is being eaten.
        /// </summary>
        public int Tolerance = 12;

        /// <summary>
        /// The smallest sealed-off pocket of background colour, in pixels, that
        /// gets cleared. Anything smaller is kept.
        ///
        /// Art is full of small background-coloured details the outline seals
        /// in — the white of an eye, a tooth, a highlight on a boot — and those
        /// have to survive. The gaps that DO need clearing, between an arm and
        /// a body or between the legs, are far bigger. Size is what tells them
        /// apart, because colour cannot.
        ///
        /// 0 keeps every pocket whatever its size.
        /// </summary>
        public int MinHole = 500;

        /// <summary>
        /// Fade glows and gradients out into the background instead of cutting
        /// them off at a hard line.
        ///
        /// Off by default, because it only applies to art that has soft edges
        /// meeting the background with no outline between — a magic blast
        /// running from solid purple out through pale lavender to white. Without
        /// it that blast keeps a white square around it. With it, each of those
        /// pixels is read as its true colour at partial coverage.
        ///
        /// It cannot reach anything an outline encloses, so a character with a
        /// black line around her is untouched either way.
        /// </summary>
        public bool Glow;

        /// <summary>Overwrite the input files instead of writing new ones.</summary>
        public bool InPlace;

        /// <summary>Work out what would happen and report it, writing nothing.</summary>
        public bool DryRun;
    }

    public static int Run(Options o)
    {
        var files = Resolve(o.Inputs);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("no input .png files matched");
            return 2;
        }

        Console.WriteLine($"keying {files.Count} file(s) against " +
                          $"rgb({o.Key.R},{o.Key.G},{o.Key.B}), tolerance {o.Tolerance}, " +
                          (o.MinHole > 0 ? $"clearing sealed pockets from {o.MinHole:n0}px" : "keeping every sealed pocket") +
                          (o.Glow ? ", fading glows out" : ""));
        if (o.OutDir.Length > 0 && !o.DryRun) Directory.CreateDirectory(o.OutDir);

        // A hundred frames is a minute and a half one at a time, and every file
        // is independent of every other, so they go across the cores. The lines
        // are collected and printed in order afterwards rather than as they
        // finish, or the report would come out shuffled.
        var lines = new string[files.Count];
        int done = 0, untouched = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Parallel.For(0, files.Count, i =>
        {
            string file = files[i];
            try
            {
                var report = One(file, o);
                if (report.Reached == 0)
                {
                    Interlocked.Increment(ref untouched);
                    lines[i] = $"  {Path.GetFileName(file),-34} " +
                               "SKIPPED — no background of that colour reaches the edge of the image";
                    return;
                }
                Interlocked.Increment(ref done);
                lines[i] = $"  {Path.GetFileName(file),-30} " +
                           $"{Percent(report.Cleared, report.Total)} cleared, " +
                           $"{report.Softened:n0} edges" +
                           (report.Faded > 0 ? $", {report.Faded:n0} faded" : "") +
                           report.Holes;
            }
            catch (Exception ex)
            {
                lines[i] = $"  {Path.GetFileName(file)}: FAILED — {ex.Message}";
            }
        });
        foreach (var line in lines) Console.WriteLine(line);
        Console.WriteLine($"  ({watch.Elapsed.TotalSeconds:0.0}s)");

        Console.WriteLine(o.DryRun
            ? $"dry run: {done} file(s) would be written"
            : $"wrote {done} file(s)");
        if (untouched > 0)
            Console.WriteLine(
                $"note: {untouched} file(s) were left alone — no background of that colour " +
                "touches the edge of the image. Check the colour, or raise --tolerance.");
        return 0;
    }

    private static string Percent(long part, long whole) =>
        whole == 0 ? "0%" : $"{100.0 * part / whole:0.#}%";

    /// <summary>
    /// Reached counts only background found from the BORDER inward. It is the
    /// honest test of whether the key colour was right: enclosed pockets can
    /// turn up by accident against any colour, so counting those too would hide
    /// a wrong answer behind a plausible-looking number.
    /// </summary>
    private readonly record struct Report(long Total, long Reached, long Softened, long Enclosed,
        int HolesCleared, int HolesKept, int BiggestKept, int SmallestCleared, long Faded)
    {
        public long Cleared => Reached + Enclosed;

        /// <summary>
        /// The two numbers worth seeing when the hole size needs tuning: how big
        /// the biggest kept pocket was, and how small the smallest cleared one
        /// was. The right threshold sits between them.
        /// </summary>
        public string Holes =>
            HolesCleared == 0 && HolesKept == 0 ? "" :
            $", holes {HolesKept} kept" + (BiggestKept > 0 ? $" (to {BiggestKept:n0}px)" : "") +
            $" / {HolesCleared} cleared" + (SmallestCleared < int.MaxValue ? $" (from {SmallestCleared:n0}px)" : "");
    }

    /// <summary>
    /// One image. Three passes: mark what looks like background, flood the
    /// reachable part of it from the border, then rebuild every pixel.
    /// </summary>
    private static Report One(string path, Options o)
    {
        using var image = Image.Load<Rgba32>(path);
        int w = image.Width, h = image.Height;

        // 0 = artwork, 1 = background-coloured, 2 = background and reached from
        // the border, 3 = background-coloured but walled in
        var mark = new byte[w * h];
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                    if (IsKey(row[x], o.Key, o.Tolerance)) mark[y * w + x] = 1;
            }
        });

        long reached = Flood(mark, w, h);
        // Nothing found from the border means the key colour is wrong for this
        // image. Carrying on would clear whatever pockets happen to match and
        // soften their edges — quietly damaging the art. Leave it alone and let
        // the caller say so.
        if (reached == 0)
            return new Report(w * (long)h, 0, 0, 0, 0, 0, 0, int.MaxValue, 0);

        var holes = Pockets(mark, w, h, o.MinHole);
        long enclosed = holes.Cleared;

        // the soft fade runs BEFORE the outline pass, so a pixel it has already
        // accounted for is not then read a second time as an outline blend
        long faded = o.Glow ? Fade(image, mark, w, h, o.Key) : 0;

        long softened = 0;
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    byte state = mark[y * w + x];
                    if (state is 2 or 3) { row[x] = new Rgba32(0, 0, 0, 0); continue; }
                    if (state is 1 or 6) continue;   // a pocket small enough to be detail
                    if (state == 4) continue;   // already faded out by the glow pass

                    // artwork touching cleared ground is the brush's soft edge:
                    // part outline, part background, and which part is readable
                    if (!Touches(mark, w, h, x, y)) continue;
                    if (Coverage(row[x], o.Key) is not float cover) continue;
                    if (cover >= 0.996f) continue;               // solid outline: leave it alone

                    row[x] = new Rgba32(0, 0, 0, (byte)Math.Clamp((int)MathF.Round(cover * 255f), 0, 255));
                    softened++;
                }
            }
        });

        if (!o.DryRun)
        {
            string outPath = o.InPlace ? path
                : o.OutDir.Length > 0
                    ? Path.Combine(o.OutDir, Path.GetFileName(path))
                    : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
                        Path.GetFileNameWithoutExtension(path) + o.Suffix + ".png");
            // The encoder copies the SOURCE's colour type by default, and the
            // source is a 24-bit PNG with no alpha channel — so every bit of
            // transparency worked out above would be thrown away on the way to
            // disk, silently, leaving a file that looks untouched. Say what is
            // wanted instead of letting it be inferred.
            image.SaveAsPng(outPath, new PngEncoder
            {
                ColorType = PngColorType.RgbWithAlpha,
                BitDepth = PngBitDepth.Bit8,
                TransparentColorMode = PngTransparentColorMode.Preserve,
            });
        }
        return new Report(w * (long)h, reached, softened, enclosed,
            holes.ClearedCount, holes.KeptCount, holes.BiggestKept, holes.SmallestCleared, faded);
    }

    private static bool IsKey(Rgba32 p, Rgba32 key, int tolerance) =>
        p.A > 0 &&
        Math.Abs(p.R - key.R) <= tolerance &&
        Math.Abs(p.G - key.G) <= tolerance &&
        Math.Abs(p.B - key.B) <= tolerance;

    /// <summary>
    /// Spreads inward from every edge pixel through background-coloured ones,
    /// promoting 1 to 2. Four-way, so a diagonal hairline in the outline still
    /// seals the background out. Returns how many squares it reached.
    /// </summary>
    private static long Flood(byte[] mark, int w, int h)
    {
        var queue = new Queue<int>();
        void Push(int i) { if (mark[i] == 1) { mark[i] = 2; queue.Enqueue(i); } }

        for (int x = 0; x < w; x++) { Push(x); Push((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Push(y * w); Push(y * w + w - 1); }

        long n = 0;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            n++;
            int x = i % w, y = i / w;
            if (x > 0) Push(i - 1);
            if (x < w - 1) Push(i + 1);
            if (y > 0) Push(i - w);
            if (y < h - 1) Push(i + w);
        }
        return n;
    }

    private readonly record struct HoleReport(long Cleared, int ClearedCount, int KeptCount,
        int BiggestKept, int SmallestCleared);

    /// <summary>
    /// Sorts the sealed-off pockets of background colour by size: anything of
    /// minSize pixels or more is cleared (state 3), anything smaller is left
    /// alone (state 1) because it is detail the artist drew.
    ///
    /// Nothing but size can tell them apart. The white of an eye and the gap
    /// between an arm and a body are the same colour, both sealed in by the
    /// same outline, and both unreachable from the edge of the image. One is
    /// twenty pixels and the other is twenty thousand.
    /// </summary>
    private static HoleReport Pockets(byte[] mark, int w, int h, int minSize)
    {
        if (minSize <= 0) return new HoleReport(0, 0, 0, 0, int.MaxValue);

        long cleared = 0;
        int clearedCount = 0, keptCount = 0, biggestKept = 0, smallestCleared = int.MaxValue;
        var pocket = new List<int>();
        var queue = new Queue<int>();

        for (int seed = 0; seed < mark.Length; seed++)
        {
            if (mark[seed] != 1) continue;

            // gather one pocket whole before deciding what to do with it
            pocket.Clear();
            queue.Enqueue(seed);
            mark[seed] = 5;                       // claimed, decision pending
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                pocket.Add(i);
                int x = i % w, y = i / w;
                void Step(int j) { if (mark[j] == 1) { mark[j] = 5; queue.Enqueue(j); } }
                if (x > 0) Step(i - 1);
                if (x < w - 1) Step(i + 1);
                if (y > 0) Step(i - w);
                if (y < h - 1) Step(i + w);
            }

            // A kept pocket gets its own state rather than going back to 1.
            // Restoring it to 1 made the loop rediscover the same pocket from
            // every one of its other pixels, counting one eye as hundreds.
            bool clear = pocket.Count >= minSize;
            foreach (int i in pocket) mark[i] = clear ? (byte)3 : (byte)6;
            if (clear)
            {
                cleared += pocket.Count;
                clearedCount++;
                smallestCleared = Math.Min(smallestCleared, pocket.Count);
            }
            else
            {
                keptCount++;
                biggestKept = Math.Max(biggestKept, pocket.Count);
            }
        }
        return new HoleReport(cleared, clearedCount, keptCount, biggestKept, smallestCleared);
    }

    /// <summary>
    /// Fades a glow out into the background, for art whose edge is a gradient
    /// rather than a line — a blast running from solid purple, through paler
    /// and paler lavender, into white.
    ///
    /// Such a pixel is the blast's real colour laid over the background at some
    /// coverage, and both the background and the direction the colour is
    /// travelling are known, so the coverage can be read back out: the further
    /// a pixel has moved from the background colour, the more of it there is.
    /// White goes to nothing, pale lavender becomes purple at low coverage,
    /// solid purple stays solid.
    ///
    /// It spreads outward from ground already cleared and stops on its own the
    /// moment a pixel is fully opaque — which a black outline always is. That
    /// is why this cannot eat a character: the line around her stops it, and
    /// nothing inside is ever reached.
    /// </summary>
    private static long Fade(Image<Rgba32> image, byte[] mark, int w, int h, Rgba32 key)
    {
        var queue = new Queue<int>();
        for (int i = 0; i < mark.Length; i++)
            if (mark[i] is 2 or 3) queue.Enqueue(i);

        long faded = 0;
        image.ProcessPixelRows(rows =>
        {
            Span<int> steps = stackalloc int[4];
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % w, y = i / w;

                int n = 0;
                if (x > 0) steps[n++] = i - 1;
                if (x < w - 1) steps[n++] = i + 1;
                if (y > 0) steps[n++] = i - w;
                if (y < h - 1) steps[n++] = i + w;

                for (int k = 0; k < n; k++)
                {
                    int j = steps[k];
                    if (mark[j] != 0) continue;               // background, a kept pocket, or done
                    var row = rows.GetRowSpan(j / w);
                    float alpha = Unmix(row[j % w], key, out var trueColor);
                    if (alpha >= 0.995f) continue;            // solid: the glow ends here

                    trueColor.A = (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
                    row[j % w] = trueColor;
                    mark[j] = 4;
                    faded++;
                    queue.Enqueue(j);
                }
            }
        });
        return faded;
    }

    /// <summary>
    /// Reads a pixel as "some colour laid over the background at some coverage"
    /// and hands back both halves.
    ///
    /// Coverage is set by whichever channel has travelled furthest from the
    /// background colour, since that channel is the one the colour underneath
    /// is pushing hardest. Over white that is the darkest channel, over black
    /// the brightest, over magenta whichever of the three has moved most.
    /// Knowing the coverage, the colour follows by taking the background's
    /// share back out again.
    /// </summary>
    private static float Unmix(Rgba32 p, Rgba32 key, out Rgba32 color)
    {
        float Travelled(byte value, byte k) => k >= 128
            ? (k - value) / (float)Math.Max(1, (int)k)        // away from a bright key means downward
            : (value - k) / (float)Math.Max(1, 255 - (int)k); // away from a dark key means upward

        float alpha = Math.Clamp(MathF.Max(
            Travelled(p.R, key.R), MathF.Max(Travelled(p.G, key.G), Travelled(p.B, key.B))), 0f, 1f);

        if (alpha <= 0.001f) { color = new Rgba32(0, 0, 0, 0); return 0f; }

        byte Recover(byte value, byte k) =>
            (byte)Math.Clamp((int)MathF.Round((value - (1f - alpha) * k) / alpha), 0, 255);

        color = new Rgba32(Recover(p.R, key.R), Recover(p.G, key.G), Recover(p.B, key.B), 255);
        return alpha;
    }

    /// <summary>Whether any of the eight neighbours has been cleared.</summary>
    private static bool Touches(byte[] mark, int w, int h, int x, int y)
    {
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                if (mark[ny * w + nx] is 2 or 3) return true;   // 4 faded, 6 kept: not ground
            }
        return false;
    }

    /// <summary>
    /// How covered by black outline a pixel on the boundary is, 0 to 1, or null
    /// when it is not a black-to-background mix at all.
    ///
    /// The mix runs along a straight line from the background colour to black,
    /// so the fraction is the same in every channel. Reading all three and
    /// checking they agree is what keeps coloured artwork out of it: a pixel
    /// that is not on that line is paint, not an edge, and is left alone.
    /// </summary>
    private static float? Coverage(Rgba32 p, Rgba32 key)
    {
        float cr = Fraction(p.R, key.R), cg = Fraction(p.G, key.G), cb = Fraction(p.B, key.B);

        // channels where the key is already 0 say nothing about the mix — with a
        // magenta key, green is 0 at both ends of the line
        Span<float> live = stackalloc float[3];
        int n = 0;
        if (key.R > 8) live[n++] = cr;
        if (key.G > 8) live[n++] = cg;
        if (key.B > 8) live[n++] = cb;
        if (n == 0) return null;

        float lo = 1f, hi = 0f;
        for (int i = 0; i < n; i++) { lo = MathF.Min(lo, live[i]); hi = MathF.Max(hi, live[i]); }
        if (hi - lo > 0.16f) return null;         // the channels disagree: this is paint

        float mean = 0f;
        for (int i = 0; i < n; i++) mean += live[i];
        return Math.Clamp(mean / n, 0f, 1f);
    }

    /// <summary>0 when the channel still reads as the background, 1 when fully black.</summary>
    private static float Fraction(byte value, byte keyValue) =>
        keyValue == 0 ? 0f : 1f - value / (float)keyValue;

    /// <summary>Files, folders and globs, sorted so frame2 lands before frame10.</summary>
    private static List<string> Resolve(List<string> inputs)
    {
        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input)) files.AddRange(Directory.GetFiles(input, "*.png"));
            else if (input.Contains('*') || input.Contains('?'))
            {
                string? parent = Path.GetDirectoryName(input);
                string dir = string.IsNullOrEmpty(parent) ? "." : parent;
                if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir, Path.GetFileName(input)));
            }
            else if (File.Exists(input)) files.Add(input);
            else Console.Error.WriteLine($"warning: skipping '{input}' — not a file, folder or glob");
        }
        return files.Distinct().OrderBy(f => f, new NaturalOrder()).ToList();
    }

    /// <summary>Same natural ordering the sheet builder uses.</summary>
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

    /// <summary>"white", "magenta", "255,0,255" or "#ff00ff".</summary>
    public static Rgba32 ParseColor(string text)
    {
        string s = text.Trim().ToLowerInvariant();
        switch (s)
        {
            case "white": return new Rgba32(255, 255, 255);
            case "magenta" or "pink": return new Rgba32(255, 0, 255);
            case "green": return new Rgba32(0, 255, 0);
            case "black": return new Rgba32(0, 0, 0);
        }
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length == 6 && int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int hex))
            return new Rgba32((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);

        var bits = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (bits.Length == 3 && bits.All(b => byte.TryParse(b, out _)))
            return new Rgba32(byte.Parse(bits[0]), byte.Parse(bits[1]), byte.Parse(bits[2]));

        throw new ArgumentException(
            $"'{text}' is not a colour. Use white, magenta, 255,0,255 or #ff00ff");
    }
}
