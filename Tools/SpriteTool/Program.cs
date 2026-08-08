using TheTimelineIs.SpriteTool;

// spritetool — mp4 frames out, sprite sheets in and back out again.
// See Usage() for the whole surface.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Usage(); return 0; }

try
{
    return args[0].ToLowerInvariant() switch
    {
        "extract" => Extract.Run(ParseExtract(args)),
        "sheet" or "build" => Sheet.Build(ParseSheet(args)),
        "slice" => Sheet.Slice(ParseSlice(args)),
        _ => Unknown(args[0]),
    };
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("run 'spritetool --help' for the options");
    return 2;
}

int Unknown(string cmd)
{
    Console.Error.WriteLine($"unknown command '{cmd}'");
    Usage();
    return 2;
}

// ---------------- argument parsing ----------------

Extract.Options ParseExtract(string[] a)
{
    var o = new Extract.Options();
    var loose = new List<string>();
    for (int i = 1; i < a.Length; i++)
    {
        switch (a[i].ToLowerInvariant())
        {
            case "--name" or "-n": o.Name = Next(a, ref i, "--name"); break;
            case "--out" or "-o": o.OutDir = Next(a, ref i, "--out"); break;
            case "--odd": o.Every = 2; o.First = 1; break;
            case "--even": o.Every = 2; o.First = 2; break;
            case "--every": o.Every = Int(Next(a, ref i, "--every"), "--every", 1); break;
            case "--first": o.First = Int(Next(a, ref i, "--first"), "--first", 1); break;
            case "--pad": o.Pad = Int(Next(a, ref i, "--pad"), "--pad", 1); break;
            case "--rgba": o.Rgba = true; break;
            default:
                if (a[i].StartsWith('-')) throw new ArgumentException($"unknown option '{a[i]}'");
                loose.Add(a[i]);
                break;
        }
    }
    if (loose.Count == 0) throw new ArgumentException("extract needs a video file");
    o.Input = loose[0];
    // "extract clip.mp4 werewolf_attack" reads as naturally as the flag does
    if (loose.Count > 1 && o.Name == "frame") o.Name = loose[1];
    return o;
}

Sheet.Options ParseSheet(string[] a)
{
    var o = new Sheet.Options();
    for (int i = 1; i < a.Length; i++)
    {
        switch (a[i].ToLowerInvariant())
        {
            case "--out" or "-o": o.Output = Next(a, ref i, "--out"); break;
            case "--columns" or "-c": o.Columns = Int(Next(a, ref i, "--columns"), "--columns", 1); break;
            case "--max-width": o.MaxWidth = Int(Next(a, ref i, "--max-width"), "--max-width", 1); break;
            default:
                if (a[i].StartsWith('-')) throw new ArgumentException($"unknown option '{a[i]}'");
                o.Inputs.Add(a[i]);
                break;
        }
    }
    if (o.Inputs.Count == 0) throw new ArgumentException("sheet needs some .png files, a folder, or a glob");
    return o;
}

Sheet.SliceOptions ParseSlice(string[] a)
{
    var o = new Sheet.SliceOptions();
    var loose = new List<string>();
    for (int i = 1; i < a.Length; i++)
    {
        switch (a[i].ToLowerInvariant())
        {
            case "--out" or "-o": o.OutDir = Next(a, ref i, "--out"); break;
            case "--name" or "-n": o.Name = Next(a, ref i, "--name"); break;
            case "--frame-width": o.FrameWidth = Int(Next(a, ref i, "--frame-width"), "--frame-width", 1); break;
            case "--frame-height": o.FrameHeight = Int(Next(a, ref i, "--frame-height"), "--frame-height", 1); break;
            default:
                if (a[i].StartsWith('-')) throw new ArgumentException($"unknown option '{a[i]}'");
                loose.Add(a[i]);
                break;
        }
    }
    if (loose.Count == 0) throw new ArgumentException("slice needs a sheet .png");
    o.Sheet = loose[0];
    if (loose.Count > 1 && o.Name == "frame") o.Name = loose[1];
    return o;
}

static string Next(string[] a, ref int i, string flag)
{
    if (i + 1 >= a.Length) throw new ArgumentException($"{flag} needs a value after it");
    return a[++i];
}

static int Int(string s, string flag, int min)
{
    if (!int.TryParse(s, out int v) || v < min)
        throw new ArgumentException($"{flag} must be a whole number of at least {min}, got '{s}'");
    return v;
}

void Usage() => Console.WriteLine("""
spritetool — mp4 to numbered PNGs, PNGs to a sprite sheet, and back.

  dotnet run --project Tools/SpriteTool -- <command> [options]

EXTRACT   every frame of a video as its own PNG
  extract <video.mp4> [name] [options]
    --name, -n NAME    base name; files come out NAME1.png, NAME2.png, ...
    --out, -o DIR      where to write them (default: here)
    --odd              keep only frames 1, 3, 5, ... of the source
    --even             keep only frames 2, 4, 6, ...
    --every N          keep one frame in N
    --first N          start counting from source frame N (default 1)
    --pad N            pad numbers to at least N digits (default: as needed)
    --rgba             keep an alpha channel — only useful if the source has one

  Output is always renumbered 1..N with no gaps, whatever was skipped.
  Needs ffmpeg on the PATH; nothing else here does.

SHEET     PNGs into one sprite sheet, plus a .txt describing the grid
  sheet <files|folder|glob>... [options]
    --out, -o FILE     the sheet to write (default sheet.png)
    --columns, -c N    force a column count (default: roughly square)
    --max-width N      cap the sheet's width in px (default 4096)

  Frames must all be the same size. A folder or glob is sorted naturally, so
  frame2 comes before frame10; an explicit list of files keeps your order.

SLICE     a sheet back into numbered PNGs
  slice <sheet.png> [name] [options]
    --out, -o DIR      where to write them
    --name, -n NAME    base name (default: frame)
    --frame-width N    cell width, if there is no .txt beside the sheet
    --frame-height N   cell height, likewise

EXAMPLES
  dotnet run --project Tools/SpriteTool -- extract wolf.mp4 werewolf_attack -o out
  dotnet run --project Tools/SpriteTool -- extract wolf.mp4 -n werewolf_attack --odd -o out
  dotnet run --project Tools/SpriteTool -- sheet out -o WerewolfAttack.png
  dotnet run --project Tools/SpriteTool -- sheet out/a1.png out/a2.png -c 4 -o Sheet.png
  dotnet run --project Tools/SpriteTool -- slice WerewolfAttack.png werewolf_attack -o frames
""");
