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
`Content/missions/destinations.txt` in the repo):

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

## How to refer to files

Always use repo-relative paths with forward slashes, e.g.
`Content/cast/enemy_characters/Goblin/Goblin2.png`. Never absolute paths
(`C:\...`) — the repo lives at different roots on different machines.

## Content authoring

The game is authored at **3840x2160** and letterboxes to any window or screen.

| Asset | Path | Size |
|---|---|---|
| World map | `Content/images/map/map.png` | 7680x4320 (bigger than the view so it scrolls) |
| Room backgrounds | `Content/images/backgrounds/*.png` | 3840x2160 |
| Character sprites | `Content/cast/.../{Name}/{Name}N.png` | 1200x1800, transparent background |
| Dialogue thumbnails | same folder, `{Name}N_thumb.png` | 512x512 |

### Destinations — `Content/missions/destinations.txt`

```
# name              x     y     mission
Forest Clearing     412   688   Forest_mission_1
```

The last three columns are x, y (in **map-image pixels**), and the mission
folder name; everything before them is the display name (spaces allowed).
Easiest way to add one: run with `--devmap` and click the map.

### Missions — `Content/missions/{Mission}/{Mission}.txt`

```
Room 1:
Background: forest_clearing.png
Cast: Joe_dirtbag, Goblin
Joe_dirtbag: Goddamn if my balls ain't itching.
[Battle!]
Room 2:
Cast: Joe_dirtbag, Goblin, Goblin
```

- `Background:` names a file in `Content/images/backgrounds/`. Omit it in a
  later room to keep the previous room's background.
- `Cast:` lists who's on stage. The Nth mention of a name is the same
  individual across rooms — it keeps its sprite. Anyone who died in an
  earlier room is silently omitted. New mentions spawn with an unused sprite
  variant when one is available.
- `Speaker: text` is dialogue; `[Battle!]` starts a battle (placeholder for
  now: win continues, lose reloads the last save).
- After the last room, the mission completes and the map returns.

### Characters — `Content/cast/{player_characters|enemy_characters}/{Name}/`

Each folder holds sprite variants (`Goblin1.png`, `Goblin2.png`, ...), matching
thumbnails (`Goblin1_thumb.png`, ...), and a manifest `{Name}.txt` listing the
variant file names one per line. The manifest exists because mobile app bundles
can't list directory contents — when you add `Goblin4.png`, add a line for it.

### Player-facing text — `Content/text/strings.txt`

Every string the game shows outside of room dialogue (menus, buttons, save
confirmations, the death screen) lives here as `key = text`. Edit the text
freely; keys must stay.

### Font

`Content/fonts/CourierPrime-Regular.ttf` (Courier Prime, SIL OFL — license in
the same folder) is baked at build time via `Content/fonts/courier.spritefont`.

## Saves

`%AppData%/TheTimelineIs/save.json` on Windows (platform equivalent elsewhere).
Saving on the map resumes on the map; saving in a room restarts that room's
dialogue from the top on reload. Dying reloads the last save.
