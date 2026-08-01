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

Art scale tuning, e.g. `Global scale: 100%`. The most specific line wins
outright (override, not multiply): `Dirtbag scale` beats `Cast scale`, which
beats `Global scale`. Global covers the map and backgrounds too; UI and the
ruler never scale. Scaling happens at draw time, so future animation frames
scale with their character automatically — author all frames of an animation
on the same canvas size and they stay seamless.

## Debug ruler

Press **F12** to toggle a ruler along the left edge and across the top.
The screen is 12 units tall (1 unit = 180 virtual px = 1/12 of the screen
height at any window size); a 16:9 screen is 21.3 units wide. The ruler
ignores all Config scaling — it's a fixed yardstick.

## Party and formation

New Game leads to a party picker: choose 3 from the playable classes
(Dirtbag, Gun-O-Mancer, Cyborg — duplicates allowed). Each side of a room has
three rows: player Back/Mid/Front left-to-right, enemies mirrored. A row
holds up to 3 characters, stacked. Drag a player sprite between rows with
the mouse (or a finger, later). Rows are cosmetic for now; combat will use
them later.

## Battle

Turn order is rolled once per battle: each side shuffled, sides alternating,
random side first, leftovers appended. On a player character's turn their
class's cards appear (tag match against `Classes.txt` Card Tags); click a
card, pick targets if needed, and it resolves. Enemies hit a random player
character with their manifest `Attack`. All enemies dead = victory; all
player characters dead = death screen and reload. A character killed
mid-mission stays dead for later rooms of that mission.

## Cards — `Content/Cast/PlayerCharacters/Cards.txt`

Card definitions; the format legend is commented at the top of the file.
`Effect:` is the machine-readable line (keep the wording pattern per Type);
`Card Text:` is what the player reads; the bottom-right number is computed
live as total damage against the current room.

## Character stats

Character manifests (`Dirtbag.txt`, `Goblin.txt`, ...) now hold stats along
with sprite lists: `HP: 12`, and for enemies `Attack: 3 Smash` (damage and
damage type). Defaults if omitted: players 25 HP, enemies 12 HP / 3 attack.
