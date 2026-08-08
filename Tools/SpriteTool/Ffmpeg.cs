using System.Diagnostics;

namespace TheTimelineIs.SpriteTool;

/// <summary>
/// The one place that shells out to ffmpeg. Decoding H.264 is the only part of
/// this tool that cannot be done in managed code, so ffmpeg is a hard
/// dependency — but only for <c>extract</c>. Sheet building and slicing never
/// touch it.
/// </summary>
public static class Ffmpeg
{
    /// <summary>Where to look, in order: an override, then whatever is on PATH.</summary>
    public static string Exe =>
        Environment.GetEnvironmentVariable("SPRITETOOL_FFMPEG") ?? "ffmpeg";

    private static string Probe =>
        Environment.GetEnvironmentVariable("SPRITETOOL_FFPROBE") ?? "ffprobe";

    public static bool Available()
    {
        try { return Run(Exe, "-version", out _, out _) == 0; }
        catch { return false; }
    }

    /// <summary>
    /// Every frame the file actually contains, counted exactly rather than
    /// guessed from duration x rate — a variable-rate recording makes that
    /// guess wrong, and the frame count decides the number padding.
    /// </summary>
    public static int CountFrames(string video)
    {
        try
        {
            int code = Run(Probe,
                $"-v error -select_streams v:0 -count_frames -show_entries stream=nb_read_frames " +
                $"-of default=nokey=1:noprint_wrappers=1 \"{video}\"", out string outp, out _);
            if (code == 0 && int.TryParse(outp.Trim(), out int n) && n > 0) return n;
        }
        catch { /* fall through to the estimate */ }
        return 0;
    }

    public static int Run(string exe, string args, out string stdout, out string stderr)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {exe}");
        stdout = p.StandardOutput.ReadToEnd();
        stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>Runs ffmpeg and passes its progress lines straight through.</summary>
    public static int RunLive(string args)
    {
        var psi = new ProcessStartInfo(Exe, args)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {Exe}");
        string? line;
        while ((line = p.StandardError.ReadLine()) != null)
            if (line.StartsWith("frame=") || line.Contains("Error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Invalid", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("  " + line.Trim());
        p.WaitForExit();
        return p.ExitCode;
    }

    public const string MissingMessage =
        "ffmpeg was not found.\n" +
        "  It is the only way to decode an mp4, so 'extract' needs it (build and slice do not).\n" +
        "  Windows:  winget install Gyan.FFmpeg      then reopen the terminal\n" +
        "  or download from https://www.gyan.dev/ffmpeg/builds/ and put ffmpeg.exe on your PATH.\n" +
        "  If it lives somewhere odd, set SPRITETOOL_FFMPEG to its full path.";
}
