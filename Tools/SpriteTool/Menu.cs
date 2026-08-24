namespace TheTimelineIs.SpriteTool;

/// <summary>
/// The interactive front end — what you get by running spritetool with no
/// arguments. Pick a number, answer a few questions, done. Every question
/// offers a default in [brackets]; pressing Enter takes it.
///
/// Paths may be dragged onto the window from Explorer, which pastes them
/// wrapped in quotes, so quotes are stripped before use. The folder you last
/// worked in is remembered for the rest of the session, so a normal run is
/// "extract, Enter, Enter, name, Enter".
///
/// The command line still works exactly as before; this only adds a way in
/// that doesn't need one.
/// </summary>
public static class Menu
{
    private static string _lastFolder = Directory.GetCurrentDirectory();

    /// <summary>
    /// Set once stdin runs out — piped input, or a closed console. Every ask
    /// loop checks it, so the tool exits instead of spinning on a question
    /// nobody can answer.
    /// </summary>
    private static bool _eof;

    public static int Run()
    {
        Console.WriteLine();
        Console.WriteLine("  spritetool");
        Console.WriteLine("  video -> numbered pngs -> sprite sheet -> numbered pngs");
        Console.WriteLine();

        while (true)
        {
            Console.WriteLine("  ------------------------------------------------");
            Console.WriteLine("   1   Extract frames from a video");
            Console.WriteLine("   2   Build a sprite sheet from pngs");
            Console.WriteLine("   3   Slice a sprite sheet back into pngs");
            Console.WriteLine("   4   Measure a sheet made somewhere else");
            Console.WriteLine("   5   Remove a background from a folder of pngs");
            Console.WriteLine("   6   Check that ffmpeg is installed");
            Console.WriteLine("   7   Show the command-line options");
            Console.WriteLine("   0   Quit");
            Console.WriteLine("  ------------------------------------------------");
            string choice = Ask("choose", "1");
            if (_eof) return 0;

            Console.WriteLine();
            switch (choice)
            {
                case "1": DoExtract(); break;
                case "2": DoSheet(); break;
                case "3": DoSlice(); break;
                case "4": DoDetect(); break;
                case "5": DoCutout(); break;
                case "6": CheckFfmpeg(); break;
                case "7": Program.Usage(); break;
                case "0" or "q" or "quit" or "exit": return 0;
                default: Console.WriteLine($"  '{choice}' isn't on the menu."); break;
            }
            Console.WriteLine();
        }
    }

    // ---------------- the three jobs ----------------

    private static void DoExtract()
    {
        if (!Ffmpeg.Available())
        {
            Console.WriteLine(Ffmpeg.MissingMessage);
            Console.WriteLine();
            Console.WriteLine("  Install it, reopen this window, and try again.");
            return;
        }

        string video = AskExistingFile("video file (drag it onto this window)");
        if (video.Length == 0) return;
        _lastFolder = Path.GetDirectoryName(Path.GetFullPath(video)) ?? _lastFolder;

        int total = Ffmpeg.CountFrames(video);
        if (total > 0) Console.WriteLine($"  that clip holds {total} frames.");

        string suggested = Slug(Path.GetFileNameWithoutExtension(video));
        string name = Ask("name for the frames", suggested);
        if (name.Length == 0) name = suggested;

        Console.WriteLine();
        Console.WriteLine("   1  every frame");
        Console.WriteLine("   2  odd frames only  (1, 3, 5, ...)");
        Console.WriteLine("   3  even frames only (2, 4, 6, ...)");
        Console.WriteLine("   4  every Nth frame");
        string which = Ask("which frames", "1");

        var o = new Extract.Options { Input = video, Name = name };
        switch (which)
        {
            case "2": o.Every = 2; o.First = 1; break;
            case "3": o.Every = 2; o.First = 2; break;
            case "4":
                o.Every = AskInt("keep one frame in how many", 2, 1);
                o.First = AskInt("starting at which source frame", 1, 1);
                break;
        }

        o.OutDir = AskFolder("folder to write them into",
            Path.Combine(_lastFolder, name + "_frames"));
        _lastFolder = Path.GetFullPath(o.OutDir);

        int expect = total > 0 ? CountKept(total, o.First, o.Every) : 0;
        Console.WriteLine();
        Console.WriteLine($"  about to write {(expect > 0 ? expect + " " : "")}files like " +
                          $"{name}{1.ToString().PadLeft(PadFor(expect), '0')}.png");
        Console.WriteLine($"  into {Path.GetFullPath(o.OutDir)}");
        if (!AskYes("go ahead", true)) { Console.WriteLine("  cancelled."); return; }

        Console.WriteLine();
        Extract.Run(o);
    }

    private static void DoSheet()
    {
        Console.WriteLine("  Point this at the folder holding the frames you want on the sheet.");
        Console.WriteLine("  They are taken in natural order: frame2 before frame10.");
        Console.WriteLine();

        string folder = AskExistingFolder("folder of pngs", _lastFolder);
        if (folder.Length == 0) return;
        _lastFolder = Path.GetFullPath(folder);

        var pngs = Directory.GetFiles(folder, "*.png");
        if (pngs.Length == 0)
        {
            Console.WriteLine($"  no .png files in {Path.GetFullPath(folder)}");
            return;
        }
        Console.WriteLine($"  found {pngs.Length} png(s).");

        var o = new Sheet.Options { Inputs = { folder } };

        // an optional subset, for when a folder holds more than one animation
        if (AskYes("use only some of them", false))
        {
            string pattern = Ask("name pattern, * for anything (e.g. werewolf_attack*)", "*");
            o.Inputs.Clear();
            o.Inputs.Add(Path.Combine(folder, pattern.EndsWith(".png") ? pattern : pattern + ".png"));
        }

        string suggested = Path.Combine(_lastFolder,
            Pascal(new DirectoryInfo(Path.GetFullPath(folder)).Name) + ".png");
        o.Output = AskPath("sheet file to write", suggested);
        if (!o.Output.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) o.Output += ".png";

        if (AskYes("choose the column count yourself", false))
            o.Columns = AskInt("columns", 4, 1);

        Console.WriteLine();
        Sheet.Build(o);
    }

    private static void DoSlice()
    {
        string sheet = AskExistingFile("sheet .png");
        if (sheet.Length == 0) return;
        _lastFolder = Path.GetDirectoryName(Path.GetFullPath(sheet)) ?? _lastFolder;

        var o = new Sheet.SliceOptions { Sheet = sheet };
        if (!File.Exists(Path.ChangeExtension(sheet, ".txt")))
        {
            Console.WriteLine("  no .txt beside that sheet, so the cell size has to come from");
            Console.WriteLine("  somewhere. Measuring the art works whenever the frames have a");
            Console.WriteLine("  transparent gap between them.");
            if (AskYes("measure it now", true))
            {
                Console.WriteLine();
                if (Detect.Run(new Detect.Options { SheetPath = sheet }) != 0) return;
                Console.WriteLine();
            }
            else
            {
                o.FrameWidth = AskInt("frame width in px", 0, 1);
                o.FrameHeight = AskInt("frame height in px", 0, 1);
            }
        }

        string suggested = Slug(Path.GetFileNameWithoutExtension(sheet));
        o.Name = Ask("name for the frames", suggested);
        if (o.Name.Length == 0) o.Name = suggested;
        o.OutDir = AskFolder("folder to write them into",
            Path.Combine(_lastFolder, o.Name + "_frames"));

        Console.WriteLine();
        Sheet.Slice(o);
    }

    /// <summary>
    /// Measures a sheet that came from somewhere else and writes its .txt. The
    /// counts are offered rather than demanded: the art usually says how many
    /// rows and columns it has, and answering "no" to both is the normal case.
    /// </summary>
    private static void DoDetect()
    {
        string sheet = AskExistingFile("sheet .png");
        if (sheet.Length == 0) return;
        _lastFolder = Path.GetDirectoryName(Path.GetFullPath(sheet)) ?? _lastFolder;

        var o = new Detect.Options { SheetPath = sheet };
        if (AskYes("say how many columns and rows it has yourself", false))
        {
            o.Columns = AskInt("columns", 1, 1);
            o.Rows = AskInt("rows", 1, 1);
        }
        o.Fps = AskInt("frames per second", (int)Sheet.DefaultFps, 1);
        o.ScalePercent = AskInt("draw size, as a percent of the character's height",
            (int)Sheet.DefaultScalePercent, 1);

        string meta = Path.ChangeExtension(sheet, ".txt");
        if (File.Exists(meta) && !AskYes($"{Path.GetFileName(meta)} already exists — overwrite it", false))
        {
            o.DryRun = true;
            Console.WriteLine("  measuring only, nothing will be written.");
        }

        Console.WriteLine();
        Detect.Run(o);
    }

    /// <summary>
    /// Background removal, a folder at a time. Its own step, never folded into
    /// extracting frames: art comes off a video with its background still on,
    /// and is keyed later, once, on purpose.
    /// </summary>
    private static void DoCutout()
    {
        Console.WriteLine("  Takes a flat background off artwork and gives it real transparency.");
        Console.WriteLine("  The art needs black edges — that is what makes the soft edge exact.");
        Console.WriteLine();

        string folder = AskExistingFolder("folder of pngs", _lastFolder);
        if (folder.Length == 0) return;
        _lastFolder = Path.GetFullPath(folder);

        var pngs = Directory.GetFiles(folder, "*.png");
        if (pngs.Length == 0)
        {
            Console.WriteLine($"  no .png files in {Path.GetFullPath(folder)}");
            return;
        }
        Console.WriteLine($"  found {pngs.Length} png(s).");

        var o = new Cutout.Options { Inputs = { folder } };

        Console.WriteLine();
        Console.WriteLine("   1  white background");
        Console.WriteLine("   2  magenta background");
        Console.WriteLine("   3  green background");
        Console.WriteLine("   4  something else");
        string which = Ask("background colour", "1");
        o.Key = which switch
        {
            "2" => Cutout.ParseColor("magenta"),
            "3" => Cutout.ParseColor("green"),
            "4" => AskColor(),
            _ => Cutout.ParseColor("white"),
        };

        Console.WriteLine();
        Console.WriteLine("  Tolerance is a COLOUR distance, not a size: how far each of red,");
        Console.WriteLine("  green and blue may be off that colour and still count as background,");
        Console.WriteLine("  on the usual 0 to 255 scale. 0 demands an exact match. Higher for art");
        Console.WriteLine("  that has been through video compression; lower if pale parts of the");
        Console.WriteLine("  artwork get eaten.");
        o.Tolerance = AskInt("tolerance (0-255 per colour channel)", o.Tolerance, 0);

        Console.WriteLine();
        Console.WriteLine("  Areas of background sealed inside the art are judged by AREA — how");
        Console.WriteLine("  many pixels the patch covers in total, not how wide it is. 500 is");
        Console.WriteLine("  roughly a 22x22 patch.");
        Console.WriteLine("  Small ones are detail worth keeping — the white of an eye, a tooth,");
        Console.WriteLine("  a highlight. Big ones are gaps that have to go, like the space");
        Console.WriteLine("  between an arm and a body. Below is the line between them, in pixels.");
        Console.WriteLine("  The report says how big the biggest kept and smallest cleared were,");
        Console.WriteLine("  so a second run can be aimed better. 0 keeps every one of them.");
        o.MinHole = AskInt("clear sealed areas covering at least (total px)", o.MinHole, 0);

        Console.WriteLine();
        Console.WriteLine("  Some frames have a glow or blast whose edge fades into the");
        Console.WriteLine("  background instead of stopping at a black line. Fading those out");
        Console.WriteLine("  reads each pixel as its true colour at partial cover, rather than");
        Console.WriteLine("  leaving a pale square around it. An outline stops it, so the");
        Console.WriteLine("  character herself is untouched either way.");
        o.Glow = AskYes("fade glows and gradients out", false);

        // The destination is not worth asking about: the same folder name with
        // _cutout on the end, beside the one the frames came from.
        var source = new DirectoryInfo(Path.GetFullPath(folder).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        o.OutDir = FreeFolder(source.Parent?.FullName ?? source.FullName, source.Name + "_cutout");

        Console.WriteLine();
        Console.WriteLine($"  writing to {o.OutDir}");
        Console.WriteLine();
        Cutout.Run(o);
    }

    /// <summary>
    /// A folder that does not exist yet: "frames_cutout", then
    /// "frames_cutout_2", "frames_cutout_3" and so on.
    ///
    /// A second run at different settings is the normal way to work — the first
    /// one tells you what the hole sizes actually were, and you go again with a
    /// better number. Writing over the previous attempt would throw away the
    /// thing you are comparing against, and worse, a run that produced FEWER
    /// files would leave stragglers from the last one mixed in with the new.
    /// </summary>
    private static string FreeFolder(string parent, string name)
    {
        string path = Path.Combine(parent, name);
        for (int n = 2; Directory.Exists(path) && n < 1000; n++)
            path = Path.Combine(parent, $"{name}_{n}");
        return path;
    }

    private static SixLabors.ImageSharp.PixelFormats.Rgba32 AskColor()
    {
        while (true)
        {
            string text = Ask("colour (name, 255,0,255 or #ff00ff)", "white");
            try { return Cutout.ParseColor(text); }
            catch (ArgumentException ex)
            {
                if (_eof) return Cutout.ParseColor("white");
                Console.WriteLine("  " + ex.Message);
            }
        }
    }

    private static void CheckFfmpeg()
    {
        Console.WriteLine($"  looking for: {Ffmpeg.Exe}");
        if (Ffmpeg.Available())
        {
            Ffmpeg.Run(Ffmpeg.Exe, "-version", out string v, out _);
            Console.WriteLine("  found it: " + v.Split('\n')[0].Trim());
            Console.WriteLine("  extract will work.");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine(Ffmpeg.MissingMessage);
        }
    }

    // ---------------- asking ----------------

    private static string Ask(string question, string fallback)
    {
        Console.Write($"  {question} [{fallback}]: ");
        string? line = Console.ReadLine();
        if (line == null) { _eof = true; Console.WriteLine(); return fallback; }
        line = Clean(line);
        return line.Length == 0 ? fallback : line;
    }

    private static bool AskYes(string question, bool fallback)
    {
        string answer = Ask(question + "? (y/n)", fallback ? "y" : "n");
        return answer.StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    private static int AskInt(string question, int fallback, int min)
    {
        while (true)
        {
            string answer = Ask(question, fallback > 0 ? fallback.ToString() : "");
            if (int.TryParse(answer, out int v) && v >= min) return v;
            if (_eof) return Math.Max(fallback, min);
            Console.WriteLine($"  needs a whole number of at least {min}.");
        }
    }

    private static string AskPath(string question, string fallback) => Ask(question, fallback);

    private static string AskExistingFile(string question)
    {
        while (true)
        {
            string path = Ask(question + " (blank to go back)", "");
            if (path.Length == 0 || _eof) return "";
            if (File.Exists(path)) return path;
            Console.WriteLine($"  can't find '{path}'.");
        }
    }

    private static string AskExistingFolder(string question, string fallback)
    {
        while (true)
        {
            string path = Ask(question + " (blank to go back)", fallback);
            if (path.Length == 0 || _eof) return "";
            if (Directory.Exists(path)) return path;
            Console.WriteLine($"  can't find the folder '{path}'.");
        }
    }

    /// <summary>Like AskExistingFolder, but for a folder that will be created.</summary>
    private static string AskFolder(string question, string fallback) => Ask(question, fallback);

    /// <summary>
    /// Explorer's drag-and-drop pastes a quoted path, and a trailing space is
    /// easy to leave behind. Both would otherwise become part of the filename.
    /// </summary>
    private static string Clean(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return s.Trim();
    }

    /// <summary>A filename-safe base name: lowercase, underscores, nothing exotic.</summary>
    private static string Slug(string s)
    {
        var chars = s.Trim().Select(c =>
            char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) :
            c is '-' or '_' ? c : '_').ToArray();
        string slug = new string(chars).Trim('_');
        return slug.Length == 0 ? "frame" : slug;
    }

    private static string Pascal(string s)
    {
        var parts = s.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "Sheet"
            : string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static int CountKept(int total, int first, int every)
    {
        int n = 0;
        for (int f = first; f <= total; f += every) n++;
        return n;
    }

    private static int PadFor(int count) =>
        Math.Max(Extract.MinPad, Math.Max(1, count).ToString().Length);
}
