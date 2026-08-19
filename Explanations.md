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
| `dotnet run --project Tools/SpriteTool` | The sprite tool (menu appears) |
| `dotnet build` | Compile everything, run nothing |

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

## ffmpeg

The sprite tool shells out to `ffmpeg` to pull frames from a video. **"on the
PATH"** means Windows can find it by name from any folder, rather than you
having to type where it lives.

Install: `winget install Gyan.FFmpeg`, then **open a new terminal** — an
already-open one won't have noticed.

Check it worked: option 5 in the sprite tool menu, or type `ffmpeg -version`.
