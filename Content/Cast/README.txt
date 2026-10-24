# Character art

- One folder per character. Inside it, a folder per FORM for a shapeshifter,
  then a folder per POSE.
- A POSE is the picture an animation starts from. Its directions sit right in
  the pose folder; each animation is a folder beside them.

    {Character}/{Form}/IdlePose/south-east.png
    {Character}/{Form}/IdlePose/SpellCast/south-east/frame_000.png

- A character with one shape skips the form level: Gun-O-Mancer/IdlePose/.
- Classes.txt names the form folder. Poses are found by looking: any folder
  with a direction picture in it.
- A character stands in IdlePose. Animations from every pose play, so a walk
  drawn from Mid-walkPose/ still walks. Same name in two poses: IdlePose wins.
- (Brackets) in an animation folder's name are notes and are ignored:
  "Running (Fix Edge)" plays as Running.
- POSE pictures only need the four diagonals: south-east, south-west,
  north-east, north-west. A character standing still is always drawn from one
  of those.
- ANIMATIONS may use all eight. A ranged attack faces its target, and a target
  can be due north, south, east or west of the caster — so GunShot/north/ is a
  real direction to draw, and it plays when it exists. Without it the nearest
  drawn direction is used, so one direction is enough to start with.
- Art is drawn at its own size. Nothing is scaled. A character stands with
  the lowest solid pixel of its picture on the middle of its square.
- No pose yet = a cube with the character's initial, in the Colour from
  Classes.txt or Enemies.txt.

## Animation folders

    Walk/        moving out of a fight, in the direction of the step.
    Run/         moving in a fight. "Running" works too. Missing one? The
                 other plays. Both loop at the frame rate below.
    SpellCast/   the default when a card is played.
    anything/    played when a card, class or enemy names it.

Which animation a card plays, first one with frames wins:

    1. the card's own "Animation:" line in Cards.txt
    2. the class or form's "Cast Animation:" line in Classes.txt or Enemies.txt
    3. SpellCast

Frame rate is one number for everything — 14 a second, changed live from the
~ menu.
