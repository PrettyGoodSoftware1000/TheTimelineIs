# Character art

- One folder per character. Inside it, one folder per STATE (a form, or just
  the character's name when it has one look).
- A state folder holds:
    rotations/south-east.png      the eight compass rotations
    animations/GunShot/east/      an animation: one folder per direction,
                                  frames in name order (frame_000.png ...)
- Only the four diagonals are drawn: south-east, south-west, north-east,
  north-west. A north/south/east/west rotation is rounded to the nearest one.
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
