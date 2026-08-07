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

## THIS BRANCH: isometric test mode

This branch replaces missions and side-view rooms with an **isometric
tactics test**: Title -> party select -> world map -> the destination opens
`Content/Levels/TestLevel.txt` over a black void.

- **Decorations** (`Content/Images/Decorations/`, listed in `Decorations.txt`)
  sit on a square and block it: trees, rocks, and a treasure chest. The chest
  is scenery for now; it will hand out items when a character steps beside it.
- **Blocks**: square-top tiles with textured sides and adjustable height in
  feet. Palette in `Content/Images/Blocks/` — `{Type}Top.png` is a 360x180
  diamond, `{Type}Side.png` a 360x90 strip stacked once per foot; `Blocks.txt`
  lists the types.
- **Movement**: orthogonal 1, diagonal 2, +1 per foot climbed, 4 ft max step
  up, drops free. The reachable region is washed blue with a border around its
  outer edge, and only shows while a character is selected. Every overlay's
  fill strength comes from `Config.txt` (`Movement opacity: 20%` and friends);
  0% there means outline only.
  Exploration is free-roam and per-character; a combat turn is movement
  **then** a card, and playing a card spends the rest of that turn's movement
  unless `Nimble` hands some back.
- **Party members do not block each other.** A character walks straight
  through its allies but can never stop on an occupied square. Enemies still
  wall a path off.
- **Sight**: walking within 15 tiles of an enemy in a revealed room springs
  combat — the rest of the party gets a free positioning move first (Done
  starts the fight). Doors open when clicked from beside them, reveal the room
  behind, and enemies see through them.
- **Cards**: melee cards reach 1 tile; ranged cards default to `Range: 5`
  (override per card). Hovering or selecting a card outlines its reach in red,
  measured **from where the caster is standing right now** rather than from
  everywhere it could walk to first. Red replaces the blue region while it
  shows.
- **Targeting is one click.** Clicking an enemy fires the card at it; if the
  caster has to close the distance first it walks there by the shortest route
  and strikes — the player never picks the angle of approach. A card wanting
  several targets (`Two targets, 1 hit.`) collects one per click and fires on
  the last. Right-click cancels the armed card.
- **Selection** is marked by a small gold arrow pointing down at the selected
  character's health bar.
- **Turn order** is a row of face thumbnails across the top. Whoever is acting
  sits at the far left at double size with a gold frame, so the strip shuffles
  along by one each turn and "next up" is always the face beside it. A green or
  red bar under each face says which side it is on.
- **The combat log** lives behind the small `+` button at the top left. Nothing
  about damage is printed over the level any more: every blow, burn, theft,
  shapeshift and turn event goes in there, newest at the bottom, and the mouse
  wheel scrolls back through the history. Only immediate "you can't do that"
  feedback still flashes on screen — and it is logged too.
- **Effects** (`Effects: Burning 1, Armor 5`) are shared behaviour any card
  can carry:
  - `Burning N` — N stacks; each stack burns the victim for 5 at the **start
    of their turn**, for 2 of their turns. **Stacks are independent**: one
    applied later expires later, and adding a stack never extends the ones
    already alight. Lighting a second stack a turn after the first gives
    5, 10, 5, then nothing. Burning shows as flames on the health bar.
  - `Armor N` — soaks damage before health does, shown as a metallic grey
    extension of the bar (10 health + 5 armour makes the grey a third of it).
    6 damage against 5 armour strips the armour and takes 1 off health.
  - `Nimble N` — the **caster** may move N more spaces after playing the card,
    instead of the turn ending. Movement always hits 0 when a card is played;
    Nimble hands some back afterwards, so it's a retreat, not a longer reach.
  - `Leap N` — the approach move for *that card* reaches N further and ignores
    height entirely, climbs and drops alike. A Leap card's red outline is its
    range measured from **everywhere the leap can put the caster**, not from the
    tile underfoot, since the jump is part of the attack.
  - `Curse N` — the victim takes N extra damage from **melee** cards for 10 of
    their turns. Curses stack and each keeps its own clock.
  - `Steal N` — takes a card off whoever it hits, **friend or foe**, and hands
    it to the caster for N of the caster's own turns counting the one it was
    stolen on. `Steal 3` is "play it now, or on either of your next two". The
    thief is shown the victim's hand and **picks one card**; right-click or
    Escape takes nothing. The owner cannot play it while it is gone, and it
    goes straight back the moment the thief plays it or the clock runs out. An
    enemy robbed of its only card has nothing to attack with.

    **One exception to one-card-per-steal**: if the card taken is a shapeshift
    card, the thief immediately gets a second pick from the hand of the shape it
    would have turned them into. Steal `Witch Form` off a Werewitch in wolf
    form and you may then take `Curse`, which the wolf's hand never offers.
  - `Form X` — the caster changes into their form X, swapping art and hand.
    **Changing shape is free**: it spends neither the turn's card nor its
    movement, so a shapeshifter can shift and then actually do something. The
    shape persists across turns and through a save.
- **Forms**: a class may declare `Form: Name, Art.png` lines in `Classes.txt`
  (first one is where it starts). A card with a matching `Form:` line only
  appears while its owner wears that shape, so the Werewitch's claws and
  curses never share a hand. The validator warns about a form with no card
  that changes out of it.
- **Cone cards** (`Type: [cone] AoE damage`) spray a staircase wedge measured
  in whole tiles: **1 tile at depth 1, 3 at depth 2, 5 at depth 3** — the point
  sits on the square in front of the caster and the wide end faces away.
  `Range` caps the depth (range 3 = 9 tiles). The same shape rotates to all
  eight headings; a diagonal cone is measured in diagonal steps so it stays
  exactly congruent instead of covering twice the ground. Because a cone only
  takes a heading from the cursor, it can be aimed at any tile, and it shows as
  the purple wedge alone — no red range diamond, which would be a second,
  wrong-shaped answer.
- **Area cards can be aimed at bare ground**, not just at enemies — one click
  on a tile fires at it.
- **Blast cards** take `Explosion Range: N`, a radius in tiles around the
  impact point, kept separate from `Range` (how far it can be thrown). The
  blast is outlined in purple, following the cursor, and damages whatever that
  outline covers — not everything in throwing range.
- Health bars sit above each head with the current HP in the middle.
- **Dialogue**: trigger squares painted in the editor (G tool) play a named
  block from `Content/Levels/{Level}Dialogue.txt` the first time anyone steps
  on them. Same `Speaker: text` format as the old mission scripts.
- **Cards live in `Content/Cards/`**: `PlayerCards.txt` for the party,
  `EnemyCards.txt` for enemies. Identical format — the only difference is
  whether a card's `Tags:` match a class in `Classes.txt` or an enemy in
  `Enemies.txt`.
- **Action points.** Everyone gets **2 per turn** and may carry **at most 1**
  unspent point into the next, so a turn that spent nothing opens the next with
  three. **Walking costs nothing** — movement points and action points are
  separate budgets. Every card costs `Action Points: N` (default 1, `0` = free),
  set per card in `PlayerCards.txt` / `EnemyCards.txt`. At the default cost that
  is two cards a turn. Cards you cannot currently afford grey out individually,
  and the cost shows as orange pips on the card face.
- **Enemies act through cards**, exactly as the party does — and are bound by
  the same action points. Their turn:
  1. a **melee** card it can actually land this turn wins — it walks at the
     nearest player it can reach and swings;
  2. otherwise a **ranged** card — it closes only as far as it must to bring
     the nearest player inside that card's range, and no further;
  3. holding a weapon but out of reach of anyone, it advances and tries again
     next turn;
  4. holding **no usable attack card** — its last one has been stolen — it
     cannot attack at all, so it walks to a random square inside its movement
     range.

  An enemy in `Enemies.txt` with **no card tagged for it** is dealt the one
  tagged `Default` — `Smack Something`, a 5-damage melee swing — so a newly
  added enemy fights without you writing it a card first. The moment it has a
  card of its own the default drops away, which is why the Goblin never smacks
  anything. `Enemies.txt` no longer carries `Basic Attack Damage`, `Sounds` or
  `Range`: how hard an enemy hits, what it sounds like and how far it reaches
  all live on its cards. Those lines now warn and are ignored.
- **Classes.txt** now holds all class stats (`Class:`/`HP:`/`Movement:`,
  optional `Sprites:` and `Card Tags:`) — the per-character `{Name}.txt`
  manifests are gone. **Enemies.txt** does the same for enemies
  (`Enemy:`/`HP:`/`Movement:`/`Basic Attack Damage:`/`Sounds:`/`Range:`).
- **Editor**: `dotnet run --project Desktop -- --editor`. Every tool has both a
  **button in the strip across the top** and a hotkey, and the two stay in step;
  palettes with more than one entry (blocks, decorations, enemies) hang a
  **dropdown** off their button rather than spending a button each.
  - `1-3`/`B` block types, `D` decorations, `O` doors, `E` enemies, `P` player
    starts, `G` dialogue triggers, `R` room label, `N` trigger's dialogue name,
    `V` save-as, `S` save, `T` play-test.
  - **Hold the left button to paint** — every square the cursor crosses is
    placed once, and the whole stroke is a single undo step. Blocks,
    decorations and triggers paint; doors, enemies and starts stay one click
    each, where a repeat would be meaningless.
  - **Hold `Delete` to rub out** whatever the cursor crosses, same stroke rule.
  - **`Ctrl`+`Delete`** arms a box: drag one out and everything inside goes at
    once. The cursor turns red while it is armed and returns to normal as soon
    as the box is drawn; `Esc` cancels.
  - **`Ctrl`+`Z`** undoes the last stroke, 40 deep.
  - **Right-click a trigger square** to open that level's
    `{Level}Dialogue.txt` in whatever the OS uses for `.txt`. If the file or
    the block the trigger names doesn't exist yet, it is stubbed in first — so
    right-clicking a fresh trigger lands you on the lines you need to write.
  - Scroll or `+`/`-` for placement height, `WASD`/arrows or right-drag to pan.
  - The yellow cursor square shows its height as a number in the middle, and
    for everything except the block tool it sits on top of the block under the
    pointer rather than on the ground plane beneath it.
  - `S` saves to `Content/Levels/{Level}.txt` in the repo; `V` saves-as under a
    typed name and keeps editing that file from then on.
- Levels complete when every enemy is dead; a wiped party reloads.

## Adding a level to the world map

A destination is one row in `Content/Missions/Destinations.txt`:

```
# name              x     y     level
Test Level          412   688   TestLevel
```

- **name** — what the player reads on the map. Spaces are fine.
- **x, y** — where the pin sits, in the map image's own pixels (`Map.png` is
  authored at 7680x4320). They scale with `Global scale` in `Config.txt`.
- **level** — the level file's base name: `TestLevel` loads
  `Content/Levels/TestLevel.txt`. No path, no `.txt`.

Two ways to add one:

1. **By hand** — make the level (`dotnet run --project Desktop -- --editor`,
   build it, `V` to save it under a new name), then add a row here pointing at
   that name.
2. **In game** — open the map and use dev placement: click where the pin
   should go, type the display name, Enter, type the level name, Enter. The row
   is appended to the real `Destinations.txt` in the repo, so you get the
   coordinates by clicking rather than guessing them.

The validator checks nothing about destinations yet, so a row naming a level
that doesn't exist fails when you click it, not at startup.

## Adding dialogue to an isometric level

Dialogue lives beside the level, in `Content/Levels/{Level}Dialogue.txt` — so
`TestLevel.txt` reads `TestLevelDialogue.txt`. The file is a list of named
blocks:

```
Dialogue: Intro
Dirtbag: Goddamn if my balls ain't itching.
Cyborg: SCANNING. THAT IS A PERSONAL PROBLEM.

Dialogue: DoorWarning
Gun-O-Mancer: Something's breathing on the other side of that door.
```

`Dialogue: Name` opens a block; every `Speaker: text` line after it belongs to
that block until the next `Dialogue:` line. The speaker's name is matched
against the characters on stage to pick the portrait, falling back to their
full sprite when there's no `{Name}Thumb.png`.

To fire one, paint a **trigger square** in the editor:

1. Press `N`, type the block's name (`Intro`), Enter — that sets which block
   new triggers will call.
2. Press `G` for the trigger tool.
3. Click the squares that should fire it. They show violet in the editor with
   the block name written on them, and violet in-game until they fire.
4. `S` to save.

A trigger fires **once**, the first time any character steps on it, and it
interrupts walking. Several squares can name the same block. The validator
errors if a trigger names a block that doesn't exist, or one with no lines, so
a typo is caught at startup rather than being silently skipped.

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
| Ground | `Content/Images/Grounds/Ground1.png` | 3840x720 |
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
  expands to the party chosen at New Game.
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

## Backdrop

A room's background is drawn **4 feet (720 px) higher than the screen**, and
`Content/Images/Grounds/Ground1.png` fills the 3840x720 strip that leaves bare
along the bottom — the band the characters stand on. Both the room and battle
screens go through `Backdrop.Draw`, so they can't drift apart, and the ground
stretches to close the gap even if the background is scaled or off-ratio.

The ground is currently the same for every room. Making it per-room is a
`Ground:` line in the mission file, mirroring `Background:`.

## Party and formation

New Game leads to a party picker: choose 4 from the playable classes
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
