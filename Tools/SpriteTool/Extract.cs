using System.Text.RegularExpressions;

namespace TheTimelineIs.SpriteTool;

/// <summary>
/// mp4 -> one PNG per frame, named "{name}{n}.png" starting at 1.
///
/// On "as lossless as possible": PNG itself is lossless, so nothing is thrown
/// away on the way out. The two places loss could sneak in are both handled —
///   * frame timing: -fps_mode passthrough hands over exactly the frames the
///     file holds, with none duplicated to pad a constant rate and none
///     dropped. Without it ffmpeg silently resamples to the output rate.
///   * colour: H.264 is almost always 4:2:0 YUV with a limited (16-235) range,
///     so it has to be converted to RGB. Naming the full pipeline explicitly
///     and using the accurate scaler keeps that conversion honest instead of
///     letting ffmpeg guess and clip the darks and brights.
/// The mp4's own compression is lossy and already happened; this stage adds
/// nothing on top of it.
/// </summary>
public static class Extract
{
    /// <summary>
    /// Frame numbers are padded to at least three digits — name001.png. Fixed
    /// width keeps them in order in every file browser and every tool that
    /// sorts as text, which is most of them.
    /// </summary>
    public const int MinPad = 3;

    public static int Run(Options o)
    {
        if (!Ffmpeg.Available())
        {
            Console.Error.WriteLine(Ffmpeg.MissingMessage);
            return 2;
        }
        if (!File.Exists(o.Input))
        {
            Console.Error.WriteLine($"no such file: {o.Input}");
            return 2;
        }

        Directory.CreateDirectory(o.OutDir);

        int total = Ffmpeg.CountFrames(o.Input);
        Console.WriteLine($"{Path.GetFileName(o.Input)}: " +
            (total > 0 ? $"{total} frames" : "frame count unknown, extracting anyway"));

        // ffmpeg writes into a scratch folder with plain sequential numbers;
        // the rename pass afterwards applies the name and the odd/every filter.
        // Doing it in two passes keeps the numbering contiguous — an ffmpeg
        // select filter would leave gaps in the sequence.
        string scratch = Path.Combine(o.OutDir, ".spritetool-raw");
        if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        Directory.CreateDirectory(scratch);

        string pattern = Path.Combine(scratch, "f%08d.png");
        string args =
            $"-hide_banner -loglevel error -stats -i \"{o.Input}\" " +
            "-fps_mode passthrough " +            // every frame, no padding, no dropping
            "-sws_flags accurate_rnd+full_chroma_int " +
            $"-pix_fmt {(o.Rgba ? "rgba" : "rgb24")} " +
            "-compression_level 9 " +             // PNG deflate effort: smaller, still lossless
            $"-start_number 1 \"{pattern}\"";

        Console.WriteLine("decoding...");
        int code = Ffmpeg.RunLive(args);
        if (code != 0)
        {
            Console.Error.WriteLine($"ffmpeg exited with {code}");
            Directory.Delete(scratch, recursive: true);
            return code;
        }

        var frames = Directory.GetFiles(scratch, "f*.png")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (frames.Count == 0)
        {
            Console.Error.WriteLine("ffmpeg produced no frames — is that really a video file?");
            Directory.Delete(scratch, recursive: true);
            return 1;
        }

        // keep every Nth frame, counting the source frames from 1. --odd is
        // "--every 2" starting on frame 1, which is what "odd numbered" means.
        var kept = new List<string>();
        for (int i = 0; i < frames.Count; i++)
        {
            int frameNo = i + 1;                       // 1-based, as a person counts
            if ((frameNo - o.First) % o.Every != 0) continue;
            if (frameNo < o.First) continue;
            kept.Add(frames[i]);
        }

        int pad = Math.Max(Math.Max(o.Pad, MinPad), kept.Count.ToString().Length);
        int written = 0;
        foreach (var src in kept)
        {
            string dst = Path.Combine(o.OutDir,
                $"{o.Name}{(++written).ToString().PadLeft(pad, '0')}.png");
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(src, dst);
        }
        Directory.Delete(scratch, recursive: true);

        Console.WriteLine($"wrote {written} png(s) to {Path.GetFullPath(o.OutDir)}");
        Console.WriteLine($"  {o.Name}{1.ToString().PadLeft(pad, '0')}.png " +
                          $"... {o.Name}{written.ToString().PadLeft(pad, '0')}.png");
        if (o.Every > 1)
            Console.WriteLine($"  (kept every {o.Every}{Ordinal(o.Every)} source frame " +
                              $"from #{o.First}: {frames.Count} -> {written})");
        return 0;
    }

    private static string Ordinal(int n) => n switch
    {
        1 => "st", 2 => "nd", 3 => "rd", _ => "th",
    };

    public sealed class Options
    {
        public string Input = "";
        public string Name = "frame";
        public string OutDir = ".";
        /// <summary>Keep one frame in this many. 2 with First=1 is "odd frames only".</summary>
        public int Every = 1;
        /// <summary>The first source frame to keep, 1-based.</summary>
        public int First = 1;
        /// <summary>
        /// Minimum digits in the number, so frames come out as name001.png.
        /// Grows past this when there are more frames than it can hold.
        /// </summary>
        public int Pad = MinPad;
        /// <summary>Write RGBA rather than RGB. Only useful for sources that carry alpha.</summary>
        public bool Rgba;
    }
}
