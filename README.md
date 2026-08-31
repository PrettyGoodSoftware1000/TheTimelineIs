# The Timeline Is

Story-driven tactics: pick a destination on a world map, fight the isometric
level it opens, return to the map.

MonoGame (DesktopGL) on .NET 10. Desktop now, structured so tablet heads can be
added without rewriting game logic.

## Build and run

```
dotnet tool restore                          # once, after cloning
dotnet run --project Desktop                 # the game
dotnet run --project Desktop -- --editor     # the level editor
```

## Controls

| Action | Input |
|---|---|
| Pan | Arrows / WASD, or right-drag |
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
- `Desktop/` — the DesktopGL head: input, save location, dev writers, editor.
- `Content/` — every asset and script, loaded raw. Only the font is baked at build time.

---

## Combat

- **Turn**: movement, then a card. Playing a card spends the rest of that
  turn's movement unless `Nimble` gives some back.
- **Action points**: 2 a turn, and **one** unspent point may carry, so 3 is the
  most anyone ever holds. Walking is free — separate budget.
- **Movement**: orthogonal 1, diagonal 2, +1 per foot climbed, 4 ft max step
  up, drops free. Blue wash shows the budget **in combat only**.
- **Party members do not block each other**, but cannot stop on an occupied
  square. Enemies wall a path off.
- **Sight**: coming within 15 tiles of an enemy in a revealed room starts the
  fight. Only the Dirtbag gets a free positioning move — he cheats.
- **Targeting is one click**, resolved by **square**, never by whichever sprite
  is under the cursor. If the caster must close first it walks the shortest
  route and strikes. Right-click cancels.
- Everything an aimed card would hit is **outlined in red**, traced round the
  art itself. With `Friendly Fire: Yes` your own people light up too.
- **Turn order** is a strip of faces across the top; whoever acts sits far left
  at double size.
- **The combat log** is behind the `+` at the top left. Damage is never printed
  over the level.

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
  3 at 2, 5 at 3. `Range` caps the depth. Rotates to all eight headings.
- **Blast** takes `Explosion Range: N` around the impact point, kept separate
  from how far it can be thrown.
- **Area cards can be aimed at bare ground.**
- **`Sky Angle: N`** drops the shot out of the sky onto the aimed square.
- **`Friendly Fire: Yes/No`** decides whether a card touches its caster's own
  side. Read from the caster, so an enemy card with it hurts other enemies.
- **`Dealt: No`** keeps a card out of the opening hand until something loads it.
- Damage may be a range: `1 to 20 damage` on the `Effect:` line.

## Rooms and doors

- Rooms are labels on blocks.
- **A door is one square belonging to no room, with rooms either side.** In the
  editor: pick Door, click the square. That is all.
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
- **Scale Stuff** — a percentage box for every character, summon, enemy and
  decoration. Type, watch it change, Enter applies and writes `Config.txt`.
  `0` = no line of its own. Tiles are never scaled.
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
- Scroll or `+`/`-` for placement height. The editor pans on arrows and
  right-drag only — WASD are tool keys there.

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
| `Content/Config.txt` | Art scale and overlay opacity |

- A card's `Tags:` are **labels, not class names**. A class holds its own name
  unless `Card Tags:` says otherwise, so several classes can share a pool.
- A summon lives in `Classes.txt` with `Summoned By:`, keeping it out of the
  party picker. Its art lives in its summoner's folder.
- An enemy with no card of its own is dealt the one tagged `Default`.

### Config.txt

- Most specific wins **outright**: `Dirtbag scale` > `Cast scale` > `Global scale`.
- `0` means "ignore this line", falling through to the next up.
- Opacity lines differ: `0` there is a real zero — outline, no fill.

### Art

Authored at 3840x2160, letterboxed to any window.

| Asset | Path | Size |
|---|---|---|
| World map | `Content/Images/Map/Map.png` | 7680x4320 |
| Sprites | `Content/Cast/.../{Name}/{Name}N.png` | 1200x1800, transparent |
| Thumbnails | same folder, `{Name}NThumb.png` | 512x512, optional |

- Undersized art is scaled up, aspect preserved. Nothing is ever stretched.
- Cast art hangs by its **feet** — the lowest drawn row.
- Folders, files and declared names must match exactly: `Dirtbag`, not `Joe_dirtbag`.
- Always refer to files repo-relative with forward slashes.

## Casting animations

- Declared on a class in `Classes.txt`, as a path inside that character's folder.
  A third field on a `Form:` line gives that shape its own.
- Needs the `.txt` beside the sheet that spritetool writes.
- `FPS:` sets playback; the animation lasts as long as its frame count makes it.
- `Scale: 100` draws a frame as tall as the character it replaces.
- **Casting time and animation are independent clocks** — deliberately, so art
  is timed to art and combat to combat.
- A missing or broken sheet is reported at startup and falls back to the sprite.

## spritetool

`Tools/SpriteTool` turns an mp4 into numbered PNGs, builds sheets, and slices
them back apart. Run it with no arguments for a menu.

```
dotnet run --project Tools/SpriteTool -- extract wolf.mp4 werewolf_attack -o out
dotnet run --project Tools/SpriteTool -- sheet out -o WerewolfAttack.png
dotnet run --project Tools/SpriteTool -- slice WerewolfAttack.png frames -o frames
dotnet run --project Tools/SpriteTool -- detect WerewitchSpell1.png
```

- Extraction is lossless. `--odd` keeps frames 1, 3, 5 and renumbers 1..N.
- Every sheet gets a `.txt` giving cell size and grid.
- **`detect`** measures a sheet somebody else made, reading the grid off the art
  rather than guessing width ÷ columns.
- Only `extract` needs ffmpeg. Details in [`Tools/SpriteTool/README.md`](Tools/SpriteTool/README.md).

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
