# Terrain sets

A set is sixteen tiles that blend two terrains into each other. They come from
PixelLab; the map editor imports them.

## The sixteen

Which picture a square gets depends on its four CORNERS, not the square. Four
corners, two terrains, sixteen combinations — and the tile's number IS the
combination, read as four bits:

    bit 0 (1)  south corner, at (x+1, y+1)
    bit 1 (2)  west  corner, at (x,   y+1)
    bit 2 (4)  east  corner, at (x+1, y)
    bit 3 (8)  north corner, at (x,   y)

A bit that is 0 is terrain A, 1 is terrain B. So:

    mask 0   all A        mask 15  all B

Corners are shared: the corner at (x, y) belongs to the four squares around it.
That is why painting one square changes the eight beside it.

## Declaring one

`Terrain.txt`:

    Set: StoneToGrass
    A: stone
    B: grass
    Tile: 96 x 48
    Wall: 48

- `Tile` is the TOP FACE, not the whole picture.
- `Wall` is how far the sides hang below it. One step of height.
- Named A-to-B, so the name agrees with the masks.
- Pictures are `StoneToGrass/mask-00.png` to `mask-15.png`. The mask is the
  file name, so a missing one is a missing number.
- Anchoring is automatic: the centre of the top face, every time.

## In a level

    Block: 4, 7, 0, StoneToGrass:6, Main

The square stores the finished picture, not the terrain. Same reason the editor
writes down which piece a Random brush chose: what you painted is what loads.

## Importing

1. Drop a PixelLab .zip in `Content/Imports/`.
2. Editor, Import button, pick it.

You get the set to paint with, and the map's ground laid into the level open at
the time. Rooms, doors, triggers, starts and enemies are yours to add —
PixelLab has no idea they exist.

A set must match the grid: 96 x 48. Anything else is refused, with both sizes
named.

**The export's `mask` field is not trusted.** One of the two example exports had
six of its sixteen mislabelled and four masks missing, while the map itself used
all sixteen. `tile_6.png` is mask 6. Disagreements are reported, not obeyed.

**PixelLab lists its two terrains the other way round.** A set bit means
`terrains[0]`, so the importer makes A the second name and B the first. Measured
from both examples, not assumed.

## Where two sets meet

A set joins exactly two terrains. Stone-to-grass has nothing to say about wood,
so a square whose corners hold both draws solid, in whichever terrain holds most
of its corners — a hard edge.

Draw a wood-to-stone set later and drop it in. Those same squares start blending
with no other change.
