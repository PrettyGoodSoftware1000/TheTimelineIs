# Explanations

Short answers to things that come up twice. No jargon unless defined.

---

## Git

| Name | What it is |
|---|---|
| `Ground_Edits` | Your branch, on your disk. Moves when you commit. |
| `origin/Ground_Edits` | Your snapshot of GitHub's copy. Moves only on fetch/pull/push. |
| the branch on GitHub | The real one, on their servers. |

- `origin` is a nickname for the repo URL.
- The middle row is why git can talk about being "behind" with the wifi off.
- **fetch** updates the snapshot. **pull** = fetch + move your branch. Fetch is always safe.
- "fast-forward" = they have commits you don't, you have none they don't. No merge.
- `-u` links your branch to the remote one. First push only.

---

## Running things

| Command | What |
|---|---|
| `dotnet run --project Desktop` | The game |
| `dotnet run --project Desktop -- --editor` | The editor |
| `dotnet run --project Tools/SpriteTool` | The sprite tool |

`--` splits `dotnet`'s arguments from the game's.

---

## Where things live

| Folder | What |
|---|---|
| `Core/` | Game logic. Knows nothing about Windows. |
| `Desktop/` | Keyboard, screen, disk. The editor. |
| `Content/` | Every `.txt` and image. Edit without rebuilding. |
| `Tools/SpriteTool/` | Video -> PNGs -> sprite sheet. |

`Core` must be able to run on a tablet, so anything touching files or hardware
goes in `Desktop`.

---

## Content files

- `Key: value`. `#` comments. Case and blank lines don't matter.
- A typo names the file and line at startup and writes `ContentErrors.log`.
- Nothing ever fails silently. That is the rule the whole system is built on.

---

## Numbers you might want to change

| Thing | Where |
|---|---|
| Points a turn (2), how many carry (1) | `Core/Data/CharacterInstance.cs` |
| What a card costs | `PlayerCards.txt`, `Action Points:` |
| Burning damage and length | `Core/Data/CardEffects.cs` |
| Art size, overlay opacity | `Content/Config.txt` |

---

## Config.txt scale rules

- Most specific wins **outright** — replaces, never multiplies:
  `Dirtbag scale` > `Cast scale` > `Global scale`.
- `0` means "ignore this line", falling through to the next one up.
  `Global scale: 0` has nothing above it, so it lands on 100%.
- Same key twice: last wins.
- Opacity lines differ: `0` is a real zero — outline, no fill.
- The `~` menu's **Scale Stuff** edits all this live and writes the file.

---

## The yellow square

- Marks the square under the cursor. That is where a click lands — always by
  **square**, never by whichever sprite happens to be there.
- Gone only off the level, or over an unopened room.
- **Ctrl** is separate: it fades characters so you can read the grid.

---

## Picking characters (out of combat)

Same as picking files in Windows.

- Click — just that one.
- Shift+click — add to the selection.
- Ctrl+click — add, or drop if already picked.
- Tab or middle mouse — everybody.
- Click the ground with several picked and they all go, each to the nearest free square.
- No blue movement wash out of combat: movement is not rationed there.

---

## Tap / hold

**Alt** (block heights) and **Space** (room colours) in the editor.

- Tap -> stays on until you tap again.
- Hold past a second -> gone when you let go.
- The *release* decides which you meant.

---

## Sizes and footprints

- `Size: 2` is squares **per side** — a 2x2 body, four tiles. `Size: 2 x 1` is two side by side.
- A big body needs its whole footprint flat, empty and revealed.
- It is in reach of anything touching any of its sides.

---

## Art placement

- Cast art hangs by its **feet** — the lowest row with anything drawn on it, so
  exact cropping is not needed.
- The **shape** matters, the resolution does not.

---

## Doors

- A door is one square of ground **belonging to no room**, with rooms either side.
- In the editor: pick Door, click the square. That is all.
- Which rooms it joins is read off the neighbours. Nothing to name, no width, no axis.
- Walk anybody next to it and it opens, revealing both sides.
- Doorway squares touching each other are one wide door.
- In the file it is `Door: x, y`, and its block's room reads `-`.

---

## Area transitions

Not a door: an orange patch that moves the whole party elsewhere and lets the
old room go dark. Does nothing in combat.

- Paint a patch, paint another elsewhere, right-click one then the other.
  A teal line joins them (editor only).
- Grey = unlinked. Orange = goes somewhere.
- Squares touching side-on are one pad; corner-to-corner are two.
- Disarmed until *everyone* is off it.

---

## The ~ menu

`~` opens and closes it in a level.

- **Win Level** / **Die!** — end the mission either way.
- **Scale Stuff** — a percentage box per character, summon, enemy and
  decoration. Type, watch it change, Enter applies and saves. `0` = no line of
  its own. Tiles are never scaled.
- On the world map `~` arms destination placement: click, name it, Enter.

---

## Replays

- Off until asked. Button by End Turn: **Start Saving Replay** -> red -> press again to write.
- Records from the press, pinning where everyone stands, so mid-fight starts work.
- Written anyway if the mission ends mid-recording.
- Two files in `Replays/`: what happened, and a **copy** of the level as it was.
  Editing the level later cannot make the replay lie.
- Watch from the title screen. Next Turn or spacebar plays one whole turn;
  press again mid-animation to cut to the end.
- Nothing is re-simulated, so a replay cannot disagree with the mission.
- `Replays/` is gitignored. `git add -f` to keep one.

---

## The two numbers in cutout

One is a colour, one is an area.

**Tolerance** — colour distance, 0-255, not pixels. How far R, G and B may each
be off the background and still count as background.

- Raise if background survives (mp4 frames want 20-30). Lower if pale art gets eaten.

**Min-hole** — an area in pixels. 500 px is about 22x22, not 500 wide.

- Touching means edge to edge; diagonal-only patches are separate.
- Area grows with the square of resolution: halve the frame, divide by 4.

Pick it by running once and reading the report:

```
figure.png   holes 7 kept (to 736px) / 1 cleared (from 16,213px)
```

Anything between those two numbers behaves the same. Detail eaten? Raise it.
Gap survived? Lower it.

---

## ffmpeg

- Needed **only** for pulling frames out of a video.
- Install: `winget install Gyan.FFmpeg`, then open a **new** terminal.
- Check: option 6 in the sprite tool, or `ffmpeg -version`.
