# Explanations

Short, plain answers to things that come up. No jargon unless it's explained.
Add to it whenever something needs saying twice.

---

## Git

**`origin`** — a nickname for a URL. Here it means
`https://github.com/PrettyGoodSoftware1000/TheTimelineIs`. Nothing magic about
the word; it's just the usual name for the place you cloned from.

**`origin/Ground_Edits`** — your saved snapshot of what that branch looked like
on GitHub *last time you talked to GitHub*. It sits on your disk. Three
different things share the name:

| Name | What it is |
|---|---|
| `Ground_Edits` | Your branch, on your disk. Moves when you commit. |
| `origin/Ground_Edits` | A snapshot of GitHub's copy, on your disk. Moves only on fetch/pull/push. |
| the branch on GitHub | The real one, on their servers. |

That middle one is why git can say "behind by 6 commits" with the wifi off —
it's comparing two things already on your machine. It's also why the number can
be wrong until you `git fetch`.

**"behind by 6 commits, can be fast-forwarded"** — GitHub has 6 commits you
don't. You have none it doesn't. So git can just slide your branch forward; no
merge, no conflicts. `git pull` does it.

**"fetch" vs "pull"** — fetch downloads and updates the snapshot, changing
nothing you're working on. Pull is fetch *plus* moving your branch to match.
Fetch is always safe.

**`-u` in `git push -u origin Ground_Edits`** — links your branch to that
remote one so plain `git push` knows where to go. Only needed the first time.

---

## Running things

From the repo root:

| Command | What it does |
|---|---|
| `dotnet run --project Desktop` | The game |
| `dotnet run --project Desktop -- --editor` | The level editor |
| `dotnet run --project Tools/SpriteTool` | The sprite tool (a menu appears) |
| `dotnet build` | Compile everything, run nothing |

Or double-click `Tools/SpriteTool/spritetool.bat` for the sprite tool. Its menu
covers everything it does — extracting frames, sheets, slicing, measuring, and
removing backgrounds. You never need to type its command-line form.

`--devmap` still works but is no longer needed — **Ctrl+D** turns map placement
on and off inside the running game.

The `--` matters: everything before it is for `dotnet`, everything after is for
the game.

---

## Where things live

| Folder | What's in it |
|---|---|
| `Core/` | All the game logic. Knows nothing about Windows. |
| `Desktop/` | The bit that talks to the keyboard, screen and disk. The editor lives here. |
| `Content/` | Every `.txt` and every image. Edit these without rebuilding. |
| `Tools/SpriteTool/` | Video → numbered PNGs → sprite sheet. |

**Why the split:** anything in `Core` can run on a tablet later. Anything that
touches files or hardware directly has to live in `Desktop`.

---

## Content files

All of them: `Key: value`, `#` starts a comment, capitalisation doesn't matter,
blank lines are ignored.

**If you typo something**, the game says so on startup with the file and line
number, and writes `ContentErrors.log`. It never fails silently — that's the
one rule the whole content system is built around.

---

## Numbers you might want to change

| Thing | Where |
|---|---|
| Action points per turn (10) | `Core/Data/CharacterInstance.cs` |
| What a card costs | `Content/Cards/PlayerCards.txt`, `Action Points:` |
| Burning damage per stack (5) | `Core/Data/CardEffects.cs` |
| How long ground burns (2 turns) | `Core/Data/CardEffects.cs` |
| Character art size | `Content/Config.txt` |
| Overlay see-through-ness | `Content/Config.txt` |

---

## Config.txt scale rules

Most specific line wins **outright** — it replaces, it doesn't multiply.

    Dirtbag scale   beats   Cast scale   beats   Global scale

`0` means **"pretend this line isn't here"**, so `Dirtbag scale: 0` falls back
to Cast scale. Handy for switching a line off without deleting it.

Two catches:

- `Global scale: 0` has nothing above it to fall back to, so it lands on 100%.
- The same key twice means the **last one wins**. Two `Cast scale` lines is a
  common way to confuse yourself.

Opacity lines are different: `0` there means a real zero — outline, no fill.

---

## The yellow square

Always on, marking the square under your cursor — it is how you tell where a
click will land. It disappears only when the cursor is off the level entirely,
or over a room you have not opened yet.

Holding **Ctrl** is a separate thing: it fades characters and decorations down
so you can see the grid under them, and switches targeting to squares rather
than to whatever sprite the cursor is over.

Its strength is `Hover opacity` in `Config.txt`.

---

## Tap / hold

One key, two behaviours. Used by **Alt** (block heights) and **Space** (room
colours) in the editor.

- **Tap it** → stays on until you tap again. For reading the whole level.
- **Hold it past a second** → gone the moment you let go. For a quick peek.

It comes up the instant you press either way. The *release* is what decides
which one you meant.

---

## Sizes and footprints

An enemy's `Size:` in `Enemies.txt` is **squares per side**, not total squares.
`Size: 2` is a 2×2 body — four tiles. It's drawn twice as tall to match.

A big body needs its whole footprint flat, empty and in a revealed room before
it can stand there. It's in reach of anything touching *any* of its sides.

---

## Art placement

Cast art hangs by its **feet** — the lowest row of the image with anything
drawn on it. Empty space below the feet is ignored, so you don't have to crop
exactly.

What *does* matter is the **shape** of the image. Goblin1 is 1536 × 2752, so it
draws about 1.8 times as tall as it is wide — and it would draw exactly the
same at half those numbers. Change the proportions and it changes on screen;
change the resolution alone and nothing moves.

---

## Doors

Widths are 1, 2 and 4 squares. A wide door is one door that happens to fill
several squares — one click opens the whole run.

Pick the size from the editor's **Door** button. "Along X" and "Along Y" are
which way the run points; if it comes out sideways, use the other one.

---

## Doors vs area transitions

Two ways to get between rooms, and they behave differently.

| | Door | Area transition |
|---|---|---|
| Looks like | A door on a square | An orange patch of squares |
| Blocks you | Yes, until opened | No, you walk onto it |
| What it does | Opens, revealing the room behind | Moves the whole party elsewhere |
| The old room | Stays visible | Goes dark again |
| In combat | Openable | Does nothing |

**Making a transition:** pick *Area transition* (last entry in the Door menu)
and paint a patch — usually 4 squares, one per party member. Paint another
patch somewhere else. Right-click the first, then the second: a teal line joins
them. That line is editor-only.

**Grey means unlinked.** Orange means it goes somewhere.

Squares that touch **side-on** are one pad. Corner-to-corner are two separate
pads — handy when you want two next to each other.

Landing on a pad doesn't bounce you back: it's disarmed until *everyone* is off
it. Once they are, one person stepping back on makes the return trip.

---

## Replays

A record of what happened in a mission: every turn, who moved where, what card
they played, what it hit and what it killed.

Recording is **off** until you ask for it. The button under End Turn says
**Start Saving Replay**; press it and it turns red, says **Stop Saving Replay**,
and a red dot sits beside it. Press again to write the file.

It records from the moment you press, and pins where everyone is standing at
that point — so starting mid-fight still plays back correctly. If the mission
ends while it is running, the file is written anyway.

They land in `Replays/` at the repo root, two files per mission:

    TestLevel_2026-08-22_1435.txt         what happened
    TestLevel_2026-08-22_1435.level.txt   the level, as it was that day

The level is **copied**, not pointed at. Edit a level afterwards and the replay
still shows what really happened, instead of people walking through new walls.

**To watch one:** Replays on the title screen. It only appears once you have
saved at least one. Next Turn — or spacebar — plays one whole turn. Press again
during an animation and it cuts to the end of it.

Nothing is re-simulated on playback. The file says what happened and the screen
shows that, so a replay can never disagree with the mission it came from.

The files are plain text on purpose: the point is to be able to read them, and
later to hand a pile of them over to work out what tactics a player favours.

`Replays/` is gitignored, so they stay on the machine that made them. To keep a
particular one, move it somewhere else or add it with `git add -f`.

---

## The two numbers in cutout

They sound alike and are not. One is a colour, one is a size.

**Tolerance — a colour distance, 0 to 255. Not pixels.**

How far each of red, green and blue may be off the background colour and still
count as background. 12 means "within 12 of 255 on each channel", so
rgb(247,251,255) is background and rgb(240,255,255) is not — one channel is 15
out. It is checked per channel, so it is a little box around the colour rather
than a ball.

- Raise it if background survives. Frames from a normal mp4 want 20–30.
- Lower it if pale artwork gets eaten.
- 0 demands the colour exactly.

**Min-hole — an AREA in pixels. How many, not how wide.**

A sealed patch of background is measured by counting the pixels in it. 500
means "500 pixels in total", which is about a 22x22 patch, not a 500-wide one.

| Area | About |
|---|---|
| 100 px | 10x10 |
| 500 px | 22x22 |
| 5,000 px | 70x70 |
| 20,000 px | 141x141 |

**Touching means edge to edge**, not corner to corner. Two patches that meet
only at a diagonal are two patches.

**Picking the number: run it once and read the report.**

```
figure.png   holes 7 kept (to 736px) / 1 cleared (from 16,213px)
```

The biggest thing kept was 736 and the smallest cleared was 16,213, so anything
between them behaves identically. Detail eaten? Raise it. Gap survived? Lower
it.

Real numbers from Goblin1 at 1536x2752: 477 sealed patches, median **2 px**,
biggest 1,126. The ten biggest were 61, 72, 152, 186, 219, 333, 362, 460, 826,
1126. Almost all real detail is tiny; the gaps are the outliers.

**Area grows with the square of the resolution.** Halve the frame size and a
patch covers a quarter as many pixels. A number tuned on 1920x1080 wants
dividing by about 4 for 960x540.

---

## ffmpeg

Only *extracting frames from a video* needs it. Sheets, slicing, measuring and
background removal are all pure code and need nothing installed.

The sprite tool shells out to `ffmpeg` to pull frames from a video. **"on the
PATH"** means Windows can find it by name from any folder, rather than you
having to type where it lives.

Install: `winget install Gyan.FFmpeg`, then **open a new terminal** — an
already-open one won't have noticed.

Check it worked: option 6 in the sprite tool menu, or type `ffmpeg -version`.
