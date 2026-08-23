# spritetool

mp4 → numbered PNGs → sprite sheet → numbered PNGs again.

## Just run it

**Double-click `Tools/SpriteTool/spritetool.bat`.** That is the whole thing —
no arguments, no flags. From a terminal anywhere in the repo, this does the
same:

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
   5   Remove a background from a folder of pngs
   6   Check that ffmpeg is installed
   7   Show the command-line options
   0   Quit
```

**Everything the tool does is on that list.** The command line further down is
only for scripting and for repeating a run you already worked out — you never
have to touch it. Menu option 7 prints it if you want to look:

```
spritetool.bat --help
```

## Filenames

Frames are numbered with **at least three digits** — `werewolf_attack001.png`,
`werewolf_attack002.png`. Fixed width keeps them in order in Explorer and in
every tool that sorts filenames as text, which is most of them. Past 999 the
padding grows to fit.

## extract — every frame of a video as its own PNG

**Menu option 1.** The command-line form, for scripting:

```
spritetool.bat extract wolf.mp4 werewolf_attack -o out
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
an already-open one won't see the change. Menu option **6** checks whether it
worked.

If you would rather not touch PATH, put `ffmpeg.exe` anywhere you like and set
one environment variable to its full path:

```
setx SPRITETOOL_FFMPEG "C:\tools\ffmpeg\bin\ffmpeg.exe"
```

(also needs a fresh terminal). If ffmpeg is missing, the tool says so with
these instructions instead of failing with a stack trace.

## sheet — PNGs into one sprite sheet

**Menu option 2.** The command-line form:

```
spritetool.bat sheet out -o WerewolfAttack.png
spritetool.bat sheet out/a1.png out/a2.png -c 4 -o Sheet.png
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

**Menu option 3.** The command-line form:

```
spritetool.bat slice WerewolfAttack.png werewolf_attack -o frames
```

Reads the cell size and offset from the companion `.txt` when it's there. When
there isn't one it offers to measure the sheet for you (see `detect`), or takes
`--frame-width` and `--frame-height`.

## detect — measure a sheet somebody else made

**Menu option 4.** The command-line form:

```
spritetool.bat detect WerewitchSpell1.png
```

| option | what it does |
|---|---|
| `--columns`, `-c` | force the column count instead of counting the art |
| `--rows`, `-r` | force the row count |
| `--frames N` | force the frame count (default: however many cells aren't empty) |
| `--fps N` | playback rate to record (default 30) |
| `--scale N` | draw size percent to record (default 100) |
| `--dry-run` | print the numbers without writing the `.txt` |

A sheet built by `sheet` above needs none of this — it already has its `.txt`.
This is for the ones that arrive from an image generator or an artist's canvas.
Some of those tile their canvas neatly, like `WerewitchSpell1.png`, which is
3840x11520 holding a 3x16 grid of 1280x720 cells from the corner. Others sit in
the middle of a much larger image with wide transparent margins and a cell size
that divides nothing in particular — an earlier version of that same sheet was
3840x8000 with its cells 666x375 starting at (982, 1002), over 900px of dead
margin down each side. `detect` reads both without being told which it is
looking at; slicing the second as "width ÷ 3" would cut every frame in half.

### How it measures

The art is allowed to say where the frames are.

1. A row of pixels that is entirely transparent is a **gutter**; a run of rows
   that isn't is a **band**. Same across for columns.
2. Bands are **not** assumed to be one per frame. A frame's own art often has
   thin transparent slivers running clean across it — a trailing wisp of spell
   effect does exactly that — so any number of bands may share a cell. The only
   rule is that a band must not straddle a cell boundary.
3. A candidate grid is valid when no band straddles one of its boundaries **and
   every one of its cells holds something**. That second half is what stops the
   search padding the answer out with blank cells and calling four columns of
   art three.
4. The most cells that can be fitted that way wins, because a coarser grid
   always fits too: sixteen rows of art can always be read as eight rows of two.

At each count the tiling case is tried first, since it is both the common one
and exact — there is nothing to search when the cells are the image divided by
a whole number. Only sheets that aren't laid out that way pay for the general
search over cell size and offset, and there the grid whose boundaries sit
furthest from any art wins.

If nothing fits, it says so rather than guessing. Frames of different sizes, or
frames that run together with no transparent gap at all, cannot be measured this
way; `--columns` and `--rows` are the way through.

## Getting a transparent background

An mp4 essentially never carries alpha, so a sprite extracted from one arrives
with a solid background. `cutout` below takes it off.

Two things make that work well, and both are decisions made before the art is
recorded:

**Keep the anti-aliasing on.** Earlier advice here said to turn it off and use
magenta; that is no longer right. `cutout` reads a half-covered edge pixel and
works out how covered it was, so a soft edge becomes a soft alpha edge — which
is better than a hard one, because the game scales sprites up at draw time and
a hard edge stays hard when magnified.

**White or magenta, and white is usually the better bet.** The measured
difference on this project's art is small, and white wins on the thing that
actually goes wrong: ordinary H.264 stores colour at half resolution, which
smears a saturated magenta across edges far worse than it smears white. Magenta
is only clearly better when the art itself is white-heavy — which this project's
is not.



## cutout — take a flat background off artwork

**Menu option 5.** It asks five things and gets on with it. Results go into a
folder beside the one you pointed at, with `_cutout` on the name — point it at
`witchcast` and you get `witchcast_cutout`.

**Nothing is ever overwritten.** Run it again and you get `witchcast_cutout_2`,
then `_3`. Going again at a different hole size is the normal way to work, and
the previous attempt is the thing you are comparing against.

This is its own step. Extracting frames never does it for you.

### What it asks

**Background colour** — white, magenta, green, or anything you type
(`255,0,255`, `#ff00ff`).

**Tolerance** — how far off that colour still counts as background, 0–255.
Raise it for frames that came through video compression; lower it if pale parts
of the artwork are being eaten.

**Size of sealed areas to clear** — the interesting one. Art is full of
background-coloured details the outline seals in: the white of an eye, a tooth,
a highlight on a boot. Those must survive. The gaps that *do* need clearing —
between an arm and a body, between the legs — are the same colour, sealed by
the same outline, and equally unreachable from the edge of the image. **Only
size tells them apart.** Anything this big or bigger is cleared; anything
smaller is kept.

The report says where the line actually fell:

```
figure.png   77.9% cleared, holes 7 kept (to 736px) / 1 cleared (from 16,213px)
```

Everything worth keeping topped out at 736px and the gap was 16,213px, so
anywhere between those two works. If a detail got eaten, raise it; if a gap
survived, lower it. `0` keeps every sealed area whatever its size.

**Fade glows and gradients out** — off by default, because it only applies to
some frames. A magic blast running from solid purple, through paler and paler
lavender, into white has no outline where it meets the background. Without this
it keeps a pale disc around it. With it, each of those pixels is read as its
true colour at partial coverage: pale lavender becomes purple at 30% cover,
white becomes nothing, solid purple stays solid.

It spreads outward from cleared ground and stops the instant a pixel is fully
opaque — which a black outline always is. Measured on an outlined figure,
turning it on changed **0 of 640,000 pixels**. It cannot reach anything a line
encloses, so it is safe to leave on for a character.

### How the background is found

By connection, not by colour. The fill starts at the edges of the image and
spreads inward; the black outline stops it. Whatever it never reached is
artwork, however pale. The obvious approach — "the whiter it is, the more
see-through" — turns every white highlight into a hole; on this project's own
art it puts over a million pixels of one frame wrong.

Where the fill stops it stops on half-covered pixels, part outline and part
background. Both ends of that mix are known, so the coverage reads straight
back out and becomes the alpha. **That is why the art needs black edges.**

A file whose background does not reach the edge of the image is **skipped, not
mangled**, and the run says so. That is what a wrong colour looks like.

Files are keyed across every core: 80 frames of 1536x2752 take about 25 seconds.

### Command line

```
spritetool.bat cutout frames -o cut
spritetool.bat cutout blast --glow --min-hole 2000
```

| option | what it does |
|---|---|
| `--colour C` | `white` (default), `magenta`, `green`, `255,0,255`, `#ff00ff` |
| `--white` `--magenta` `--green` | shorthand for those three |
| `--tolerance N` | 0–255, default 12 |
| `--min-hole N` | smallest sealed area to clear, in px (default 500) |
| `--keep-enclosed` | same as `--min-hole 0` |
| `--glow` | fade gradients out into the background |
| `--out DIR` | where to write; otherwise `name_cut.png` beside the original |
| `--in-place` | overwrite the originals |
| `--dry-run` | report only |
