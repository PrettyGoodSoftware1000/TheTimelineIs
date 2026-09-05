# Character art

- One folder per character. Inside it, one folder per STATE (a form, or just
  the character's name when it has one look).
- A state folder holds:
    rotations/south-east.png      the eight compass rotations
    animations/GunShot/east/      an animation: one folder per direction,
                                  frames in name order (frame_000.png ...)
- ROTATIONS only need the four diagonals: south-east, south-west, north-east,
  north-west. A character standing still is always drawn from one of those.
- ANIMATIONS may use all eight. A ranged attack faces its target, and a target
  can be due north, south, east or west of the caster — so `GunShot/north/` is
  a real direction to draw, and it plays when it exists. Without it the nearest
  drawn direction is used, so one direction is enough to start with.
- Art is drawn at its own size. Nothing is scaled. A character stands with
  the lowest solid pixel of its picture on the middle of its square.
- No rotations yet = a cube with the character's initial, in the Colour from
  Classes.txt or Enemies.txt.

## Animation folders

Every state has these ready. Drop frames into a direction folder to use one.

    animations/Idle/    standing still
    animations/Walk/    one step
    animations/Melee/   a swing
    animations/Cast/    a spell

A class names the one it casts with in Classes.txt: `Cast Animation: GunShot`.
Frame rate is one number for everything, changed live from the ~ menu.
