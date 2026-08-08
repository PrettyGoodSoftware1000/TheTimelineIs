# spritetool

mp4 → numbered PNGs → sprite sheet → numbered PNGs again.

```
dotnet run --project Tools/SpriteTool -- --help
```

## extract — every frame of a video as its own PNG

```
dotnet run --project Tools/SpriteTool -- extract wolf.mp4 werewolf_attack -o out
```

writes `out/werewolf_attack01.png` … `werewolf_attack37.png`.

| option | what it does |
|---|---|
| `--name`, `-n` | base name (or give it as the second loose argument) |
| `--out`, `-o` | folder to write into |
| `--odd` | keep source frames 1, 3, 5, … |
| `--even` | keep source frames 2, 4, 6, … |
| `--every N` | keep one frame in N |
| `--first N` | start counting from source frame N |
| `--pad N` | pad the numbers to at least N digits |
| `--rgba` | keep an alpha channel (only if the source has one — mp4 usually doesn't) |

**Output is always renumbered 1..N with no gaps**, whatever was skipped. `--odd`
on a 12-frame clip gives you six files numbered 1–6, holding source frames 1,
3, 5, 7, 9, 11.

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

**Needs ffmpeg on the PATH.** It is the only way to decode H.264; nothing else
in this tool touches it.

```
winget install Gyan.FFmpeg      # then reopen the terminal
```

or set `SPRITETOOL_FFMPEG` to the full path of `ffmpeg.exe`.

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
```

Frame *N* (counting from 0) sits at
`x = (N % Columns) * FrameWidth`, `y = (N / Columns) * FrameHeight`.

## slice — a sheet back into numbered PNGs

```
dotnet run --project Tools/SpriteTool -- slice WerewolfAttack.png werewolf_attack -o frames
```

Reads the cell size from the companion `.txt` when it's there. For a sheet from
somewhere else, pass `--frame-width` and `--frame-height`.

## Getting a transparent background

An mp4 essentially never carries alpha, so a sprite extracted from one has a
solid background. Shoot or render the animation against flat **magenta
`FF00FF`** and key it out — that colour sits furthest from black (your
outlines) and from every colour in the existing art. Turn anti-aliasing off on
the outer edge so pixels go straight from black to magenta with nothing in
between; the game scales sprites up at draw time, so the GPU softens the edge
for you and you never bake a purple fringe into the file.
