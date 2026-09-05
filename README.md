# The Timeline Is

Story-driven tactics: pick a destination on a world map, fight the isometric
level it opens, return to the map.

MonoGame (DesktopGL) on .NET 10. Desktop now, structured so tablet heads can be
added without rewriting game logic. **Pixel art**: every art pixel is drawn as
a whole number of screen pixels, always. Nothing scales.

## Build and run

```
dotnet tool restore                             # once, after cloning
dotnet run --project Desktop                    # the game
dotnet run --project Desktop -- --level PixelRooms   # straight into a level
dotnet run --project Desktop -- --editor        # the level editor
```

## Controls

| Action | Input |
|---|---|
| Pan | Arrows / WASD, or right-drag |
| Zoom | Mouse wheel (whole steps, 1x-8x) |
| Select, advance dialogue | Left click, Enter, Space |
| End turn | Space or End |
| Play card 1-10 | `1`-`9`, `0` |
| Add to selection | Shift+click |
| Toggle one in selection | Ctrl+click |
| Select whole party | Tab or middle mouse |
| Fade the board to read the grid | Hold Ctrl |
| Dev menu | `~` |
| Ruler | F12 |
| Back / quit | Escape |

## Layout

- `Core/` — all game logic. No `Keyboard`, `Mouse`, or `System.IO` for assets;
  everything goes through `IInputSource`, `ISaveStore`, `IContentIndex`,
  `TitleContainer`. This is what makes a tablet port a new folder, not a rewrite.
- `Core/Pixel/` — the pixel rules: the camera, facings, rotations, cubes.
- `Desktop/` — the DesktopGL head: input, save location, dev writers, editor.
- `Content/` — every asset and script, loaded raw. Only the font is baked at build time.

---

## The pixel grid

- A square is a 64x32 diamond. One foot of height lifts it 8 pixels.
- The board draws through `PixelCamera`: a whole-number zoom and a
  whole-number scroll, PointClamp. One art pixel is exactly `Zoom` screen
  pixels, everywhere, at every zoom.
- The HUD — cards, text, buttons — is laid out at 3840x2160 and letterboxed,
  as before. That art is not pixel art and may scale.
- **Facing.** A character is drawn from four rotations: south-east,
  south-west, north-east, north-west. A grid axis is a screen diagonal, so
  those are the only poses a walk can end in. Aiming can point north, south,
  east or west; it is rounded to the nearest pose.
- **Walking never goes straight across the screen.** Three squares to the
  right is down-right then up-right. Cones snap the same way.
- A character stands with the **lowest solid pixel** of its picture on the
  middle of its square. Transparent padding does not count.
- **Raised ground hides what is behind it.** The cast is drawn one depth band
  at a time, in among the ground, so a block one step nearer covers anyone
  standing behind it. Flat ground never does: its diamond's top corner lands
  exactly on the feet of the square behind.

## Character art

See `Content/Cast/README.txt`. In short:

- A folder per character, a folder per state inside it, `rotations/` and
  `animations/` inside that. `Classes.txt` names the folder; a form names its own.
- **No art yet = a cube** with the character's initial, in that character's
  `Colour:`, and a yellow triangle on the ground for its facing.
- An animation is a folder per direction of numbered frames. A class casts
  with the one its `Cast Animation:` line names (`GunShot`).
- Every state has `Idle/`, `Walk/`, `Melee/`, `Cast/` waiting for frames.

---

## Combat

- **Turn**: movement, then a card. Playing a card spends the rest of that
  turn's movement unless `Nimble` gives some back.
- **Action points**: 2 a turn, and **one** unspent point may carry, so 3 is the
  most anyone ever holds. Walking is free — separate budget.
- **Movement**: one square per step along a grid axis, +1 per foot climbed,
  4 ft max step up, drops free. Blue wash shows the budget **in combat only**.
- **Party members do not block each other**, but cannot stop on an occupied
  square. Enemies wall a path off.
- **Sight**: coming within 15 tiles of an enemy in a revealed room starts the
  fight. Everyone fights from where they were caught.
- **Targeting is one click**, resolved by **square**, never by whichever sprite
  is under the cursor. If the caster must close first it walks the shortest
  route and strikes. Right-click cancels.
- Everything an aimed card would hit is **outlined in red**, traced round the
  art itself. With `Friendly Fire: Yes` your own people light up too.
- **Turn order** is a strip of faces across the top; whoever acts sits far left
  at double size.
- **The combat log** is behind the `+` at the top left. Damage numbers rise
  off the health bar.

## Out of combat

- Free-roam, no movement rationing and no blue wash.
- Pick characters like files: click, Shift+click, Ctrl+click, Tab for all.
- With several picked, clicking the ground sends everyone to the nearest free square.

## Effects

Any card can carry these (`Effects: Burning 1, Armor 5`).

| Effect | What it does |
|---|---|
| `Burning N` | N stacks, 5 damage each at the victim's turn start, 2 turns. Stacks are independent — one added later expires later. |
| `Armor N` | Soaks damage before health. Grey extension of the bar. |
| `Nimble N` | The **caster** may move N more after playing. A retreat, not a longer reach. |
| `Leap N` | That card's approach reaches N further and ignores height. Its red outline is measured from everywhere the leap can reach. |
| `Curse N` | +N damage from **melee** for 10 of the victim's turns. Stacks, each with its own clock. |
| `Steal N` | Takes a card off anyone for N of the thief's turns. Stealing a shapeshift card gives a second pick from that shape's hand. |
| `Channel N` | Cast over two turns. The first roots the caster; the next aims and fires, paying again. |
| `FireTiles N` | Every covered square burns N turns. Fires age once per round. |
| `Form X` | Change shape. **Free** — costs neither the card nor the movement. |
| `Summon 1` | Puts a creature down on a square you pick. Needs `Summons:`. |
| `Guard N` | Plants the caster and marks the ground within N with skulls. Anyone stepping in stops, is shot, then walks on. |
| `Vulnerable N` | Bullseye under the bar. Next hit does +50%, and any rolled damage comes up at its maximum. One hit spends it. |
| `Stun N` | Loses N turns outright. Lightning bolt under the bar. |
| `Swap 1` | Exchanges one card in the caster's own hand. Needs `Replaces:` and `With:`. |
| `Mower N` | Sends a lawnmower N squares down a straight line. See `Core/Iso/MowerRun.cs`. |
| `BathSalts 1` | Blacks the screen out, plays the caster's picture folder, and hurts everybody. |

## Card shapes

- **Melee** reaches 1 tile; **ranged** defaults to `Range: 5`.
- **Cone** (`Type: [cone] AoE damage`) is a staircase wedge: 1 tile deep 1,
  3 at 2, 5 at 3. `Range` caps the depth. Fires along a grid axis only — a
  screen diagonal — never straight up, down, left or right.
- **Blast** takes `Explosion Range: N` around the impact point, kept separate
  from how far it can be thrown.
- **Area cards can be aimed at bare ground.**
- **`Sky Angle: N`** drops the shot out of the sky onto the aimed square.
- **`Friendly Fire: Yes/No`** decides whether a card touches its caster's own
  side. Read from the caster, so an enemy card with it hurts other enemies.
- **`Dealt: No`** keeps a card out of the opening hand until something loads it.
- **`Projectile Art:`** names a file in `Content/Images/Pixel/Effects/`.
  Missing or absent, the 16x16 ball is thrown.
- Damage may be a range: `1 to 20 damage` on the `Effect:` line.

## Rooms and doors

- Rooms are labels on blocks.
- **A door is one square belonging to no room (`-`), with rooms either side.**
  In the editor: pick Door, click the square. That is all.
- Which rooms it joins is read off its neighbours — nothing to name, no width,
  no axis. Walk anybody beside it and it opens, revealing both sides.
- Touching doorway squares are one wide door.
- **Area transitions** are the other way between rooms: an orange patch that
  moves the whole party and lets the old room go dark.

## Dialogue

- Lives in `Content/Levels/{Level}Dialogue.txt` as `Dialogue: Name` blocks of
  `Speaker: text` lines.
- Paint trigger squares with `G`; `N` sets which block they call.
- A dialogue fires **once per level**, however many squares name it.

## The ~ menu

- **Win Level** / **Die!** — end the mission either way.
- **Frame rate** — one number for every animation. Press to step through
  4..30; the board changes as you press.
- On the world map, `~` arms destination placement: click, name it, Enter.

## Editor

`dotnet run --project Desktop -- --editor`. Every tool has a button and a hotkey.

- `1-3`/`B` blocks, `D` decorations, `O` doors, `E` enemies, `P` starts,
  `G` triggers, `R` room label, `N` trigger name, `S` save, `V` save-as, `T` play-test.
- **Hold left** to paint a stroke (one undo step). **Hold Delete** to rub out.
- **Ctrl+Delete** arms a box delete. **Shift+drag** fills a box.
- **Ctrl+drag** selects: `+`/`-` raise and lower, Ctrl+C/V copy and paste,
  Delete empties, Esc drops.
- **Middle click** eyedroppers type, height and room.
- **`Level: ▾`** opens any level without restarting.
- **`OK` / `! n`** counts what the startup validator would complain about.
- **Ctrl+Z** undoes, 40 deep.
- **Right-click a trigger** opens its dialogue file, stubbing it if needed.
- Wheel for placement height, **Ctrl+wheel to zoom**. The editor pans on
  arrows and right-drag only — WASD are tool keys there.
- Enemies are drawn as the game draws them: rotation or cube.

## Content files

All of them: `Key: value`, `#` comments, case-insensitive, blank lines ignored.

| File | Holds |
|---|---|
| `Content/Levels/Destinations.txt` | Map pins: `Name  x  y`. The name **is** the level file. |
| `Content/Levels/{Name}.txt` | One level. Written by the editor. |
| `Content/Cast/PlayerCharacters/Classes.txt` | Classes, and `Summon:` blocks for what they call up |
| `Content/Cast/EnemyCharacters/Enemies.txt` | Enemies |
| `Content/Cards/PlayerCards.txt`, `EnemyCards.txt` | Cards. Same format; the tag decides who holds one |
| `Content/Text/Strings.txt` | Every player-facing string, as `key = text` |
| `Content/Config.txt` | Overlay opacity |
| `Content/Images/Blocks/Blocks.txt` | Ground families: 64x32 pixel pieces, anchor `32, 16` |

- A card's `Tags:` are **labels, not class names**. A class holds its own name
  unless `Card Tags:` says otherwise, so several classes can share a pool.
- A summon lives in `Classes.txt` with `Summoned By:`, keeping it out of the
  party picker. Its art lives inside its summoner's folder.
- An enemy with no card of its own is dealt the one tagged `Default`.
- Old-style lines — `Sprites:`, a `.png` on a `Form:`, a `scale` line — are
  reported at startup with what to write instead.

### Art

| Asset | Path | Notes |
|---|---|---|
| World map | `Content/Images/Map/Map.png` | painted; the map is not pixel art |
| Ground | `Content/Images/Blocks/` | 64x32 surfaces, 64x56 blocks |
| Characters | `Content/Cast/.../{Name}/{State}/rotations/*.png` | any size, drawn 1:1 |
| Effects | `Content/Images/Pixel/Effects/` | 8x8 icons, 16x16 ball |
| Decorations | `Content/Images/Decorations/` | hung by the bottom on the square |

- Folders, files and declared names must match exactly: `Dirtbag`, not `Joe_dirtbag`.
- Always refer to files repo-relative with forward slashes.

## Saves

`%AppData%/TheTimelineIs/save.json`. Progress is recorded on the world map, so a
save resumes there and an unfinished level restarts.

## Replays

- Off until asked. Button beside End Turn.
- Two files in `Replays/`: what happened, and a **copy** of the level as it was.
- Nothing is re-simulated on playback, so a replay cannot disagree with the mission.
- `Replays/` is gitignored.

## Content checking

- Everything is parsed through one diagnostics channel and cross-checked at startup.
- Problems open a popup before the game — errors red, warnings amber, each with
  file and line. The full list goes to `ContentErrors.log`.
- **Errors** mean something is broken. **Warnings** mean it will run but
  probably isn't what you meant.
- Problems found mid-play raise the same popup through `GameContext.ReportProblem`.
- `TIMELINE_TRACE=1` prints every mode change to the console while a level runs.
