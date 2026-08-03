# The Timeline Is

A story-driven game: pick a destination on a scrolling world map, play through
that mission's rooms of dialogue and battles, return to the map.

MonoGame (DesktopGL) on .NET 9. Desktop now; structured so tablet heads
(Android/iOS) can be added later without rewriting game logic.

## Building and running

```
dotnet tool restore        # once, after cloning
dotnet run --project Desktop
```

Dev map mode (click the map to place destinations, rows are appended to
`Content/Missions/Destinations.txt` in the repo):

```
dotnet run --project Desktop -- --devmap
```

## Controls (desktop)

| Action | Input |
|---|---|
| Pan the map | Arrow keys / WASD, or hold right mouse button and drag |
| Select / advance dialogue | Left click, or Enter/Space |
| Back / quit | Escape |

## Project layout

- `Core/` — all game logic. Never touches Keyboard, Mouse, or `System.IO.File`
  for assets; everything goes through `IInputSource`, `ISaveStore`, and
  `TitleContainer`. This is what makes a tablet port a new `Platform` folder
  instead of a rewrite.
- `Desktop/` — the DesktopGL head: input mapping, save location, dev-mode writer.
- `Content/` — every asset and script, loaded raw at runtime (no content
  pipeline rebuild needed when art or text changes). The one exception is the
  font, which MonoGame must bake at build time via `Content/Content.mgcb`.

## Naming convention

Folders and files use initial caps with no underscores; multi-word names run
together with each word capitalized — `EnemyCharacters`, `ForestMission1`,
`RuinedBridge.png`. Character names follow the same rule (`Dirtbag`, not
`Joe_dirtbag`), and a character's folder, sprite, manifest, and the speaker
name in mission scripts must all match exactly.

## How to refer to files

Always use repo-relative paths with forward slashes, e.g.
`Content/Cast/EnemyCharacters/Goblin/Goblin2.png`. Never absolute paths
(`C:\...`) — the repo lives at different roots on different machines.

## Content authoring

The game is authored at **3840x2160** and letterboxes to any window or screen.

| Asset | Path | Optimal size |
|---|---|---|
| World map | `Content/Images/Map/Map.png` | 7680x4320 (bigger than the view so it scrolls) |
| Room backgrounds | `Content/Images/Backgrounds/*.png` | 3840x2160 |
| Character sprites | `Content/Cast/.../{Name}/{Name}N.png` | 1200x1800, transparent background |
| Dialogue thumbnails | same folder, `{Name}NThumb.png` | 512x512 (optional) |

**Undersized art is scaled up automatically.** If an image is smaller than the
optimal size for its kind, it is enlarged until its longer side matches the
corresponding optimal dimension, with the aspect ratio preserved — so a
960x1440 sprite renders as 1200x1800, and a 1000x500 one renders as 1200x600.
Art already at or above the optimal size is left at its native resolution.
Nothing is ever stretched to fit; images are centered in their slot instead.

Thumbnails are optional: if `Goblin1Thumb.png` doesn't exist, the dialogue box
falls back to the full `Goblin1.png` sprite.

### Destinations — `Content/Missions/Destinations.txt`

```
# name              x     y     mission
Forest Clearing     412   688   ForestMission1
```

The last three columns are x, y (in **map-image pixels**), and the mission
folder name; everything before them is the display name (spaces allowed).
Easiest way to add one: run with `--devmap` and click the map.

### Missions — `Content/Missions/{Mission}/{Mission}.txt`

```
Room 1:
Background: ForestClearing.png
Cast: Dirtbag, Goblin
Dirtbag: Goddamn if my balls ain't itching.
[Battle!]
Room 2:
Cast: Dirtbag, Goblin, Goblin
```

- `Background:` names a file in `Content/Images/Backgrounds/`. Omit it in a
  later room to keep the previous room's background.
- `Cast:` lists who's on stage. The Nth mention of a name is the same
  individual across rooms — it keeps its sprite. Anyone who died in an
  earlier room is silently omitted. New mentions spawn with an unused sprite
  variant when one is available.
- `Speaker: text` is dialogue; `[Battle!]` starts a turn-based card battle
  (win continues the room; a wiped party reloads the last save).
- `Cast: Player characters, Goblin` — the literal token `Player characters`
  expands to the 3-member party chosen at New Game.
- After the last room, the mission completes and the map returns.

### Characters — `Content/Cast/{PlayerCharacters|EnemyCharacters}/{Name}/`

Each folder holds sprite variants (`Goblin1.png`, `Goblin2.png`, ...), optional
thumbnails (`Goblin1Thumb.png`, ...), and a manifest `{Name}.txt` listing the
variant file names one per line. The manifest exists because mobile app bundles
can't list directory contents — when you add `Goblin4.png`, add a line for it.

### Player-facing text — `Content/Text/Strings.txt`

Every string the game shows outside of room dialogue (menus, buttons, save
confirmations, the death screen) lives here as `key = text`. Edit the text
freely; keys must stay.

### Font

`Content/Fonts/CourierPrime-Regular.ttf` (Courier Prime, SIL OFL — license in
the same folder) is baked at build time via `Content/Fonts/Courier.spritefont`.

## Saves

`%AppData%/TheTimelineIs/save.json` on Windows (platform equivalent elsewhere).
Saving on the map resumes on the map; saving in a room restarts that room's
dialogue from the top on reload. Dying reloads the last save.

## Config — `Content/Config.txt`

Art scale tuning, e.g. `Global scale: 100%` (the `%` is optional). The most
specific line wins outright (override, not multiply): `Dirtbag scale` beats
`Cast scale`, which beats `Global scale`. **A value of 0 means ignore that
line**, so `Dirtbag scale: 0` falls through to `Cast scale` exactly as if the
line weren't there — a way to switch a line off without deleting it. The same
rule applies to a card's `Speed: 0`, which falls back to the default. Global covers the map and backgrounds too; UI and the
ruler never scale. Scaling happens at draw time, so future animation frames
scale with their character automatically — author all frames of an animation
on the same canvas size and they stay seamless.

## Debug ruler

Press **F12** to toggle a ruler measured in **feet**, with **(0,0) at the
bottom-left** of the screen: feet run up the left edge and right along the
bottom. One foot = 180 virtual px = 1/12 of the screen height at any window
size, so the screen is 12 feet tall and (at 16:9) 21.3 feet wide. Whole-foot
ticks are yellow and labeled; half-foot ticks are teal. The ruler ignores all
Config scaling — it's a fixed yardstick.

## Party and formation

New Game leads to a party picker: choose 3 from the playable classes
(duplicates allowed). `Classes.txt` is the authoritative roster — one
`Class: Name` line each — and a class appears in the picker once it also has a
`Content/Cast/PlayerCharacters/{Name}/` folder.

A card's `Tags:` are **labels, not class names**. A class plays every card
carrying a tag it holds; with no `Card Tags:` line, a class holds one tag —
its own name. Adding `Card Tags: 'Mancer, Gun-O-Mancer` to a class lets it
play cards tagged either way, so several classes can share a card pool without
the tag having to be anybody's name. Each side of a room has
three rows: player Back/Mid/Front left-to-right, enemies mirrored. A row
holds up to 3 characters, stacked. Drag a player sprite between rows with
the mouse (or a finger, later). Rows are cosmetic for now; combat will use
them later.

## Battle

Turn order is rolled once per battle: each side shuffled, sides alternating,
random side first, leftovers appended. On a player character's turn their
cards appear — every card carrying a tag their class holds (see `Classes.txt`);
click a card, pick targets if needed, and it resolves. Enemies hit a random player
character with their manifest `Attack`. All enemies dead = victory; all
player characters dead = death screen and reload. A character killed
mid-mission stays dead for later rooms of that mission.

## Cards — `Content/Cast/PlayerCharacters/Cards.txt`

Card definitions; the format legend is commented at the top of the file.
`Effect:` is the machine-readable line (keep the wording pattern per Type);
`Card Text:` is what the player reads; the bottom-right number is computed
live as total damage against the current room.

Cards are separated by `[]` lines. **Keys are case-insensitive** and tolerate
loose punctuation (`Speed: 2`, `Speed 2`, `Speed 2:`); only Card Name, Card
Text, and Bottom Right keep their authored capitalization, since the player
reads those. The full legend is commented at the top of the file.

Presentation fields:

| Field | Meaning |
|---|---|
| `Type: [melee] …` / `[ranged] …` | Walk to the target and back, or throw a projectile |
| `… Single Projectile` | One shot, aimed at the enemy the player clicks (AoE still damages everyone — the click only aims) |
| `… Multiple Projectiles` | One shot per target (the default) |
| `Projectile Art: X.png` | File in `Content/Images/Effects/`. **Art must point right**; it is rotated onto the travel vector |
| `Casting Sound: [X.wav]` | Played when targeting completes. `[Blank]` = no sound, no delay |
| `Casting Time: Use Sound Time` or `Casting Time: 0.9` | Either wait exactly as long as the casting sound runs, or give a fixed number of seconds. Both work on any card; with no casting sound, "Use Sound Time" is 0 |
| `Speed: 2` | **Feet per second** for the projectile or the melee walk — distance now determines duration |
| `Melee Time: 0.5` | Pause on arrival before the first blow |
| `Hit Sound: [a.wav], Delay 0.2, [a.wav]` | A sequence of blows. Health drops once per blow, timed to its sound; the Effect line's damage is split across them |

WAVs live in `Content/Sounds/` (PCM `.wav` only — not MP3 or OGG). A missing
file is logged once and then ignored, so timing still works silently.

**Speed is in feet per second and the screen is 12 feet tall.** Front row to
front row is about 4.4 feet; back row to back row is about 17. At `Speed: 0.5`
that second case takes 35 seconds. Values around 6–12 feel like a game.

## Battle presentation

The hand rests half below the bottom edge; hovering a card lifts it into full
view at 130% size, and cards may overlap the characters. Characters stand in
their formation rows, staggered down and across so nobody is fully hidden,
with a compact HP bar under each one's feet. Anything struck recoils
side-to-side once over a quarter second.

## Character stats

Character manifests (`Dirtbag.txt`, `Goblin.txt`, ...) now hold stats along
with sprite lists: `HP: 12`, and for enemies `Attack: 3 Smash` (damage and
damage type). Defaults if omitted: players 25 HP, enemies 12 HP / 3 attack.

## Content checking

Every content file is parsed through one diagnostics channel, and a validator
cross-checks the results at startup: card tags against `Classes.txt`, mission
backgrounds and cast names against the folders on disk, every referenced sound
and projectile image against whether the file exists, plus the string keys the
code depends on.

If anything is wrong, **the game opens on a popup listing the problems and
waits for you to press Continue** — errors in red, warnings in amber, each with
the file and line number. The complete list is always written to
`ContentErrors.log` at the repo root (and beside the save file), so it can be
kept open while editing.

Errors mean something is broken (a card no class can play, a missing sound, a
mission pointing at a background that isn't there). Warnings mean it will run
but probably isn't what you meant (a card with no text, a speed so low a
fighter takes 35 seconds to cross the stage). Problems found mid-play raise the
same popup through `GameContext.ReportProblem`.
