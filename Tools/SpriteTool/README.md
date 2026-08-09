# spritetool

mp4 → numbered PNGs → sprite sheet → numbered PNGs again.

## Just run it

**Double-click `Tools/SpriteTool/spritetool.bat`**, or from anywhere in the
repo:

```
dotnet run --project Tools/SpriteTool
```

With no arguments it opens a menu. Pick a number, answer a few questions,
done — every question offers a default in `[brackets]` that Enter accepts, and
you can **drag a file from Explorer onto the window** instead of typing its
path. The folder you last worked in is remembered, so the second job in a
session is mostly Enter.

```
   1   Extract frames from a video
   2   Build a sprite sheet from pngs
   3   Slice a sprite sheet back into pngs
   4   Measure a sheet made somewhere else
   5   Check that ffmpeg is installed
   6   Show the command-line options
   0   Quit
```

The command line below still works, for scripting or for repeating a run you
have already worked out.

```
dotnet run --project Tools/SpriteTool -- --help
```

## Filenames

Frames are numbered with **at least three digits** — `werewolf_attack001.png`,
`werewolf_attack002.png`. Fixed width keeps them in order in Explorer and in
every tool that sorts filenames as text, which is most of them. Past 999 the
padding grows to fit.

## extract — every frame of a video as its own PNG

```
dotnet run --project Tools/SpriteTool -- extract wolf.mp4 werewolf_attack -o out
```

writes `out/werewolf_attack001.png` … `werewolf_attack037.png`.

| option | what it does |
|---|---|
| `--name`, `-n` | base name (or give it as the second loose argument) |
| `--out`, `-o` | folder to write into |
| `--odd` | keep source frames 1, 3, 5, … |
| `--even` | keep source frames 2, 4, 6, … |
| `--every N` | keep one frame in N |
| `--first N` | start counting from source frame N |
| `--pad N` | pad the numbers to at least N digits (default 3) |
| `--rgba` | keep an alpha channel (only if the source has one — mp4 usually doesn't) |

**Output is always renumbered 1..N with no gaps**, whatever was skipped. `--odd`
on a 12-frame clip gives you `name001.png` … `name006.png`, holding source
frames 1, 3, 5, 7, 9, 11.

### How lossless it is

PNG is lossless, so nothing is thrown away on the way out. The two places loss
could sneak in are both handled:

- **Frame timing** — `-fps_mode passthrough` hands over exactly the frames the
  file holds. Without it ffmpeg resamples to a constant output rate, silently
  duplicating or dropping frames. That is the single most common way frame
  extraction goes wrong.
- **Colour** — H.264 is nearly always 4:2:0 YUV with a limited (16–235) range,
  so it must be converted to RGB. `accurate_rnd+full_chroma_int` keeps that
  conversion honest instead of letting the scaler round sloppily and clip the
  darks and brights.

Verified: a clip encoded with `libx264rgb -crf 0` (no colour conversion
anywhere) extracts **bit-identical** to the PNGs it was built from. Against a
normal YUV mp4 the difference is ±1 per channel, and that comes from the
encoder's own RGB→YUV step, which happened before this tool ever saw the file.

### Why extract needs ffmpeg

An `.mp4` is not a folder of pictures. It is a **compressed video stream**,
almost always H.264: instead of storing each frame, it stores a few whole
frames and then, for everything in between, only the *differences* — "this
block moved four pixels left, that one got slightly darker". Turning that back
into pictures means running the H.264 decoder, which is a large, patent-encumbered
piece of engineering that nobody reimplements for fun. ffmpeg is the standard
one. Every video tool you have ever used is either ffmpeg or something like it
underneath.

There is no way around it: no .NET library, and no amount of code in this repo,
can decode an mp4 without it. Sheet building and slicing are pure managed code
and need nothing.

**"On the PATH"** means Windows can find `ffmpeg.exe` when a program just says
"run ffmpeg", without being told which folder it lives in. PATH is a list of
folders Windows searches for programs. Installing ffmpeg properly adds its
folder to that list; unzipping it to your Desktop does not.

```
winget install Gyan.FFmpeg
```

Then **close and reopen the terminal** — PATH is read when a window opens, so
an already-open one won't see the change. Menu option **4** checks whether it
worked.

If you would rather not touch PATH, put `ffmpeg.exe` anywhere you like and set
one environment variable to its full path:

```
setx SPRITETOOL_FFMPEG "C:\tools\ffmpeg\bin\ffmpeg.exe"
```

(also needs a fresh terminal). If ffmpeg is missing, the tool says so with
these instructions instead of failing with a stack trace.

## sheet — PNGs into one sprite sheet

```
dotnet run --project Tools/SpriteTool -- sheet out -o WerewolfAttack.png
dotnet run --project Tools/SpriteTool -- sheet out/a1.png out/a2.png -c 4 -o Sheet.png
```

| option | what it does |
|---|---|
| `--out`, `-o` | the sheet to write |
| `--columns`, `-c` | force a column count (default: roughly square) |
| `--max-width N` | cap the sheet width in px (default 4096) |

Inputs may be a folder, a glob, or a list of files. **A folder or glob is
sorted naturally**, so `frame2` comes before `frame10` however the padding
falls; an explicit list of files keeps the order you typed.

Frames must all be the same size. If one isn't, the tool names it and stops
rather than stretching it.

Every sheet gets a `.txt` beside it:

```
Sheet: WerewolfAttack.png
FrameWidth: 160
FrameHeight: 120
Columns: 4
Rows: 3
Frames: 12
OffsetX: 0
OffsetY: 0
FPS: 30
Scale: 100
```

Frame *N* (counting from 0) sits at
`x = OffsetX + (N % Columns) * FrameWidth`, `y = OffsetY + (N / Columns) * FrameHeight`.

`OffsetX`/`OffsetY` are 0 for anything built here — the cells fill the PNG from
its top-left corner. They exist for `detect` below. `FPS` and `Scale` are for
the game, which reads the same file: the playback rate, and how tall to draw a
frame as a percentage of the character it replaces.

## slice — a sheet back into numbered PNGs

```
dotnet run --project Tools/SpriteTool -- slice WerewolfAttack.png werewolf_attack -o frames
```

Reads the cell size and offset from the companion `.txt` when it's there. When
there isn't one it offers to measure the sheet for you (see `detect`), or takes
`--frame-width` and `--frame-height`.

## detect — measure a sheet somebody else made

```
dotnet run --project Tools/SpriteTool -- detect WerewitchSpell1.png
```

| option | what it does |
|---|---|
| `--columns`, `-c` | force the column count instead of counting the art |
| `--rows`, `-r` | force the row count |
| `--frames N` | force the frame count (default: however many cells aren't empty) |
| `--fps N` | playback rate to record (default 30) |
| `--scale N` | draw size percent to record (default 100) |
| `--dry-run` | print the numbers without writing the `.txt` |

A sheet built by `sheet` above needs none of this. This is for the ones that
arrive from an image generator or an artist's canvas, where the frames sit
somewhere in the middle of a much bigger transparent image and the cell size
divides nothing in particular. `WerewitchSpell1.png` is 3840x8000 with its 3x16
grid of 666x375 cells starting at (982, 1002) — over 900px of dead margin down
each side. Slicing that as "width ÷ 3" cuts every frame in half.

### How it measures

The art is allowed to say where the frames are.

1. A row of pixels that is entirely transparent is a **gutter**; a run of rows
   that isn't is a **band**. One band per row of frames, and the same across for
   columns. That gives the grid's shape without assuming anything.
2. A frame's own art can have thin transparent slivers running clean across it
   — a trailing wisp of spell effect does exactly that — and those would read as
   extra frames. So instead of picking a pixel threshold, the gaps are sorted
   and the one place where the next gap is several times the last is taken as
   the line between slivers and real gutters. An evenly spaced sheet has no such
   step and nothing is merged.
3. The pitch and starting offset then have to satisfy every band at once: band
   *i* falls inside cell *i* and touches neither edge. For a fixed pitch that is
   just a range of allowed offsets, so the pitch is swept across a few pixels
   either side of the spacing the bands imply, and whichever leaves the widest
   range wins. The offset taken is the middle of it — the one furthest from
   clipping anything.

If no pitch satisfies every band, it says so rather than guessing. Frames of
different sizes, or frames that touch with no gutter at all, cannot be measured
this way; `--columns` and `--rows` are the way through.

## Getting a transparent background

An mp4 essentially never carries alpha, so a sprite extracted from one has a
solid background. Shoot or render the animation against flat **magenta
`FF00FF`** and key it out — that colour sits furthest from black (your
outlines) and from every colour in the existing art. Turn anti-aliasing off on
the outer edge so pixels go straight from black to magenta with nothing in
between; the game scales sprites up at draw time, so the GPU softens the edge
for you and you never bake a purple fringe into the file.
