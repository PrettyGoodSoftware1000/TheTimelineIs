# Character art

- One folder per character. Inside it, a folder per FORM for a shapeshifter,
  then a folder per STATE.
- A STATE is the pose an animation starts from. Everything drawn so far starts
  from Idle.

    {Character}/{Form}/Idle/rotations/south-east.png
    {Character}/{Form}/Idle/animations/SpellCast/south-east/frame_000.png

- A character with one shape skips the form level: Gun-O-Mancer/Idle/.
- Classes.txt names the form folder. The state folder is found by looking, so
  nothing has to name it.
- ROTATIONS only need the four diagonals: south-east, south-west, north-east,
  north-west. A character standing still is always drawn from one of those.
- ANIMATIONS may use all eight. A ranged attack faces its target, and a target
  can be due north, south, east or west of the caster — so GunShot/north/ is a
  real direction to draw, and it plays when it exists. Without it the nearest
  drawn direction is used, so one direction is enough to start with.
- Art is drawn at its own size. Nothing is scaled. A character stands with
  the lowest solid pixel of its picture on the middle of its square.
- No rotations yet = a cube with the character's initial, in the Colour from
  Classes.txt or Enemies.txt.

## Animation folders

    animations/Walk/        one step. Plays while the character crosses a
                            square, in the direction it is stepping — one
                            cycle per square, whatever the walking speed.
    animations/SpellCast/   the default when a card is played.
    animations/Melee/       a swing.
    animations/Idle/        standing still.

Which animation a card plays, first one with frames wins:

    1. the card's own "Animation:" line in Cards.txt
    2. the class or form's "Cast Animation:" line in Classes.txt
    3. SpellCast

Frame rate is one number for everything, changed live from the ~ menu.
