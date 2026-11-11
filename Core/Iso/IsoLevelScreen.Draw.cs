using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Pixel;
using TheTimelineIs.Core.Render;
using TheTimelineIs.Core.Screens;

namespace TheTimelineIs.Core.Iso;

public partial class IsoLevelScreen
{
    // ---------------- drawing ----------------

    private Point? FindTileAt(Vector2 screen)
    {
        foreach (var b in _level.Blocks.Values
                     .Where(b => _level.Shown(new Point(b.X, b.Y), _revealed))
                     .OrderByDescending(b => b.X + b.Y).ThenByDescending(b => b.Height))
            if (IsoMath.HitsTop(screen, b.X, b.Y, b.Height, Origin))
                return new Point(b.X, b.Y);
        return null;
    }

    /// <summary>
    /// Where a character's picture goes, at its OWN size.
    ///
    /// Nothing is stretched to fit a square: pixel art drawn at anything but
    /// 1:1 stops being pixel art. A picture is placed instead — hung by the
    /// bottom-centre of its solid pixels, so a character exported onto a roomy
    /// canvas stands on the floor rather than floating above it, and a big
    /// enemy is big because its art is.
    /// </summary>
    private Rectangle SpriteRect(CharacterInstance c) => RectFor(c, ArtFor(c));

    /// <summary>
    /// Where a picture of this character goes. Every frame of every animation
    /// is placed by the same rule the standing pose is, so nothing jumps
    /// sideways when one starts or stops.
    /// </summary>
    private Rectangle RectFor(CharacterInstance c, Texture2D art)
    {
        var solid = ArtBounds.Solid(art);
        var foot = FootOf(c);
        // whatever the artist left round the figure, nudged by hand from the
        // ~ menu. Both numbers move it across the GROUND — east-west and
        // north-south — so a nudge of nothing moves nothing and nothing is
        // ever lifted off the floor. Height stays with the lowest solid pixel.
        var nudge = _ctx.Anchors.For(c.Name, c.Art);
        return new Rectangle(
            (int)foot.X - (solid.Left + solid.Right) / 2 + nudge.X,
            (int)foot.Y - solid.Bottom - nudge.Y,
            art.Width, art.Height);
    }

    /// <summary>
    /// How many depth bands a southward nudge moves something.
    ///
    /// One band is half a tile down the screen, and a band's ground starts
    /// exactly at the centre of the band behind it. So a picture pushed even a
    /// pixel south overlaps ground that is painted after it, and its feet end
    /// up underneath — which is what sinking into the floor looks like. Moving
    /// it down the bands with the nudge keeps it standing ON the ground it has
    /// moved onto. A northward nudge needs none of this: the ground it moves
    /// over was already painted.
    /// </summary>
    private static int BandsSouth(int nudgeY) =>
        nudgeY >= 0 ? 0 : (-nudgeY + IsoMath.TileH / 2 - 1) / (IsoMath.TileH / 2);

    /// <summary>
    /// The animation frame standing in for a character's rotation right now,
    /// or null when it is simply standing there.
    ///
    /// A cast beats a walk, so a card played at the end of a move shows the
    /// swing rather than the last step. A walk loops at the same frame rate as
    /// everything else, so the ~ menu speeds it up and slows it down too.
    /// </summary>
    private Texture2D? AnimationFrame(CharacterInstance c)
    {
        if (c.CastFrames is { Count: > 0 } cast)
            return cast[Math.Clamp((int)(c.CastAnimTime * DirectionalSprite.Fps), 0, cast.Count - 1)];
        if (MovingToward(c) != null &&
            _ctx.Sprites.MoveFrames(c, _mode != Mode.Explore) is { Count: > 0 } walk)
            return walk[(int)(_clock * DirectionalSprite.Fps) % walk.Count];

        // Doing nothing is still something to draw. An idle animation loops
        // in place of the still rotation for anybody who has one — an imp
        // hovers while it stands about and while it waits for its turn.
        // Everyone shares one clock, so a row of them does not beat in step
        // only because they were all drawn on the same frame.
        if (_ctx.Sprites.FirstFrames(c, DirectionalSprite.Idle) is { Count: > 0 } idle)
            return idle[(int)(_clock * DirectionalSprite.Fps + c.OccurrenceIndex * 3) % idle.Count];
        return null;
    }

    /// <summary>
    /// The picture to draw for somebody: their rotation for the way they are
    /// facing, or their placeholder cube if nobody has drawn them yet.
    /// </summary>
    private Texture2D ArtFor(CharacterInstance c) => _ctx.Sprites.Standing(c);

    /// <summary>Never called: this screen draws itself. See DrawSelf.</summary>
    public void Draw(SpriteBatch batch) { }

    /// <summary>
    /// Two passes, because the board and the HUD live in different worlds.
    ///
    /// The board is pixel art: it goes through the camera at a whole-number
    /// zoom with PointClamp, so one art pixel is exactly Zoom screen pixels and
    /// nothing is ever resampled. The HUD — cards, text, buttons — is still
    /// laid out in the 3840x2160 design space and still letterboxed onto the
    /// window, which is what keeps a fixed layout working at any window size.
    /// </summary>
    public void DrawSelf(SpriteBatch batch, GraphicsDevice device)
    {
        _windowSize = new Point(
            device.PresentationParameters.BackBufferWidth,
            device.PresentationParameters.BackBufferHeight);
        // the window's real size is only known here, so the opening view waits
        // for the first frame rather than guessing at load
        if (!_framed)
        {
            CentreOnFocus();
            _framed = true;
            if (Environment.GetEnvironmentVariable("TIMELINE_TRACE") == "1")
                foreach (var c in Everyone)
                    Console.WriteLine($"[trace] {c.Name} at {Tile(c)} window {_camera.ToScreen(FootOf(c).ToPoint())}");
        }

        device.Viewport = new Viewport(0, 0, _windowSize.X, _windowSize.Y);
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
            SamplerState.PointClamp, null, null, null, _camera.Matrix);
        DrawBoard(batch);
        batch.End();

        // The salts black out the WHOLE WINDOW, and this is the only pass that
        // can: the one below is letterboxed onto 3840x2160, so a black
        // rectangle there leaves the board showing in the bars down the sides.
        // Drawn with no camera matrix, so it is window pixels and nothing else.
        if (TripDarkness > 0f)
        {
            batch.Begin();
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(0, 0, _windowSize.X, _windowSize.Y),
                Color.Black * TripDarkness);
            batch.End();
        }

        _ctx.Viewport.Apply(device);
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
            SamplerState.LinearClamp, null, null, null, _ctx.Viewport.Matrix);
        foreach (var who in Everyone.Where(w => w.Alive))
            DrawCharacterNumbers(batch, who);
        DrawHud(batch);
        DrawLog(batch);
        if (DialogueActive) DrawDialogue(batch);
        // last, and over the top of everything: while the salts are working
        // there is nothing to see but the pictures
        DrawTrip(batch);
        DrawAsk(batch);
        DrawMenu(batch);
        DrawDevMenu(batch);
        batch.End();
        _tap = null;
    }

    private void DrawBoard(SpriteBatch batch)
    {
        // Everybody, filed under the DEPTH they stand at. Depth is (x + y):
        // every tile on one diagonal band across the screen shares it, and a
        // greater depth is nearer the viewer. A character goes down once its
        // whole band of ground is painted — the tiles beside it on that band
        // are level with it, not in front, so draining a band at a time is
        // what lets a raised block one step NEARER cover them.
        var byDepth = new Dictionary<int, List<CharacterInstance>>();
        foreach (var c in Everyone.Where(c => c.Alive && _level.Shown(Tile(c), _revealed)))
        {
            // Somebody mid-step stands BETWEEN two squares, so they are filed
            // under whichever of the two is NEARER the viewer — the greater
            // band. Whichever way they are walking, the ground they are half
            // standing on is then already painted when they go down, and
            // nothing level with them can paint over them.
            //
            // Filing them at the destination alone was only right walking
            // toward the viewer. Walking away, the square being LEFT is the
            // nearer one, and its ground was drawn after them: it covered
            // their feet for the length of the step.
            int depth = Math.Max(DepthOf(c, Tile(c)), DepthOf(c, MovingToward(c) ?? Tile(c)));
            if (!byDepth.TryGetValue(depth, out var list)) byDepth[depth] = list = new List<CharacterInstance>();
            list.Add(c);
        }

        bool armed = _cardArmed;
        // Ctrl fades everything standing on the ground so the grid reads clearly
        float alpha = _ctrl ? CtrlFade : 1f;

        // The square under the cursor is lit all the time, not just under Ctrl:
        // it is how you tell which square a click will land on, and that is
        // worth knowing whether or not anything is being aimed. FindTileAt
        // answers null off the edge of the level, so nothing lights up when the
        // cursor is over empty space.
        var hovered = FindTileAt(_worldPointer);

        // Every square anybody is covering right now, plus the patch the card
        // in hand would cover if it were played. Gathered once instead of
        // asked per tile, since the block loop runs over the whole level.
        _watchedGround.Clear();
        foreach (var g in Everyone.Where(g => g.Alive && g.IsGuarding))
            _watchedGround.UnionWith(g.Watch.Ground);
        foreach (var l in Everyone.Where(l => l.Alive && l.IsLurking))
            _watchedGround.UnionWith(l.Watch.Ground);
        if (armed && Acting is CharacterInstance planter &&
            (_selectedCard ?? HoveredCard()) is Card plant && (plant.IsGuard || plant.IsLurk))
            _watchedGround.UnionWith(WatchedGroundFor(planter, plant));

        // The ground every AMS is covering, worked out from where its owner
        // is standing THIS frame, which is what makes the ring travel with
        // them. Previewed from the hand the same way a guard zone is.
        _amsGround.Clear();
        foreach (var op in AmsOperators)
            _amsGround.UnionWith(AmsGround(op));
        if (armed && Acting is CharacterInstance oper &&
            (_selectedCard ?? HoveredCard()) is Card ring && ring.AmsRadius > 0)
            foreach (var block in _level.Blocks.Values)
            {
                var tile = new Point(block.X, block.Y);
                if (oper.Footprint.Any(f => IsoMath.GridDistance(f, tile) <= ring.AmsRadius))
                    _amsGround.Add(tile);
            }

        // Objects nudged south, waiting for the band of ground they have moved
        // onto to be painted. Almost always empty: an object with no nudge, or
        // one nudged north, is drawn with its own square as it always was.
        _pendingObjects.Clear();

        int band = int.MinValue;
        foreach (var block in _level.Blocks.Values
                     .Where(b => _level.Shown(new Point(b.X, b.Y), _revealed))
                     .OrderBy(b => b.X + b.Y).ThenBy(b => b.X))
        {
            // a new band starting means the last one is complete, so anybody
            // standing on it goes down now — over ground that is all painted,
            // and under everything nearer than they are
            if (block.X + block.Y != band)
            {
                DrawBandObjects(batch, band);
                DrawBandCast(batch, byDepth, band, alpha);
                band = block.X + block.Y;
            }

            DrawBlock(batch, block);
            var tile = new Point(block.X, block.Y);

            // each region gets a colour wash inside plus a border around the
            // outside; red replaces blue whenever a card is armed or hovered.
            // The wash strengths all come from Config.txt.
            if (armed)
            {
                if (_rangeSet.Contains(tile))
                    Region(batch, tile, block.Height, _rangeSet, new Color(255, 70, 70),
                        _rangeOpacityKey, 7f);
            }
            // Out of combat there is no blue: movement is not rationed there, so
            // painting where you "can" reach was colouring in the whole level
            // and saying nothing. In a fight the wash is the budget, and means
            // something again.
            else if (_mode != Mode.Explore && _moveSet.ContainsKey(tile))
                Region(batch, tile, block.Height, _moveSet.Keys, new Color(90, 150, 255),
                    "Movement", 7f);
            if (_blastSet.Contains(tile))
                Region(batch, tile, block.Height, _blastSet, new Color(190, 100, 255),
                    _blastOpacityKey, 9f);

            // Ground somebody is watching, and the same patch previewed while
            // the card is still in hand. A DASHED red border is what tells
            // the two reds apart: a solid red edge is "this card reaches
            // here", a broken one is "walk in here and something happens to
            // you". No wash inside it, so what is standing on the ground
            // stays readable.
            if (_watchedGround.Contains(tile))
                DashedOutline(batch, tile, block.Height, _watchedGround,
                    new Color(235, 50, 50), 8f);

            // Ground an AMS is covering: a solid CYAN edge, drawn thick.
            // Blue was the obvious colour and the wrong one — the movement
            // budget is already blue, and the two lines sat side by side
            // looking like one thing. Cyan is still read as blue and is not
            // mistaken for where you can walk.
            if (_amsGround.Contains(tile))
                Outline(batch, tile, block.Height, _amsGround,
                    new Color(60, 240, 255), 12f);

            // The squares a card wants its caster to stand on — behind
            // somebody's back — in the same broken red, since it is the same
            // kind of statement: this ground matters to the card in hand.
            if (_approachSet.Contains(tile))
                DashedOutline(batch, tile, block.Height, _approachSet,
                    new Color(235, 50, 50), 8f);

            if (_level.TriggerAt(tile) is { Fired: false })
            {
                Fill(batch, tile, block.Height, Color.Violet * _ctx.Config.Opacity("Trigger"));
                Edge(batch, tile, block.Height, Color.Violet * 0.8f);
            }

            if (hovered == tile)
            {
                Fill(batch, tile, block.Height, Color.Yellow * _ctx.Config.Opacity("Hover"));
                Edge(batch, tile, block.Height, Color.Yellow);
            }

            // A decoration on an opened area transition - a door, in
            // practice - shows its open picture. With no open picture it is
            // faded instead, so it never looks shut after it has been opened.
            if (_level.DecorationAt(tile) is LevelDecoration deco)
                Object(batch, new PendingObject(deco.File, tile, block.Height, alpha),
                    band + BandsSouth(_ctx.Anchors.For(deco.File).Y), band);
            if (_fires.ContainsKey(tile)) DrawFire(batch, tile, block.Height, alpha);
            if (IsGateTile(tile))
                Object(batch, new PendingObject(GateArtKey, tile, block.Height, alpha),
                    band + BandsSouth(_ctx.Anchors.For(GateArtKey).Y), band);
        }
        DrawBandObjects(batch, band);
        DrawBandCast(batch, byDepth, band, alpha);
        // anything nudged past the last band of ground still has to be drawn
        foreach (var late in _pendingObjects.Keys.OrderBy(b => b).ToList())
            DrawBandObjects(batch, late);

        // Anybody left is standing on ground the level never drew — off its
        // edge. They go down last rather than being dropped, so a character in
        // the wrong place is visible instead of quietly missing. People in a
        // room nobody has opened are not here at all: they were left out above,
        // with the ground they are standing on.
        foreach (var stranded in byDepth.Values)
            foreach (var c in stranded)
                DrawCharacter(batch, c, alpha);

        DrawProjectile(batch);
        DrawMower(batch);
    }

    /// <summary>Draws everybody standing at one depth, then forgets them.</summary>
    /// <summary>
    /// The depth band a body standing on a square occupies — its far corner,
    /// since that is the band its whole footprint is clear of.
    /// </summary>
    private int DepthOf(CharacterInstance c, Point at) =>
        at.X + c.SizeX - 1 + at.Y + c.SizeY - 1 +
        BandsSouth(_ctx.Anchors.For(c.Name, c.Art).Y);

    private void DrawBandCast(SpriteBatch batch,
        Dictionary<int, List<CharacterInstance>> byDepth, int depth, float alpha)
    {
        if (!byDepth.Remove(depth, out var standing)) return;
        // Green under everyone picked — not only the last one clicked, so a
        // group selection reads as a group. Drawn with the character rather
        // than with the ground, because it travels with them: a wash left on
        // the square they were last standing on trailed a whole tile behind
        // them all the way across the board.
        foreach (var c in standing.Where(p => Picked.Contains(p))) DrawPickedGround(batch, c);
        foreach (var c in standing) DrawCharacter(batch, c, alpha);
    }

    /// <summary>An object waiting for the band of ground it was nudged onto.</summary>
    private readonly record struct PendingObject(string Key, Point Tile, int Height, float Alpha);

    private readonly Dictionary<int, List<PendingObject>> _pendingObjects = new();

    /// <summary>
    /// Draws an object now if it belongs on the band being painted, or files it
    /// under the band it was nudged onto.
    /// </summary>
    private void Object(SpriteBatch batch, PendingObject obj, int wants, int band)
    {
        if (wants <= band) { DrawObject(batch, obj); return; }
        if (!_pendingObjects.TryGetValue(wants, out var list))
            _pendingObjects[wants] = list = new List<PendingObject>();
        list.Add(obj);
    }

    private void DrawBandObjects(SpriteBatch batch, int band)
    {
        if (!_pendingObjects.Remove(band, out var waiting)) return;
        foreach (var obj in waiting) DrawObject(batch, obj);
    }

    private void DrawObject(SpriteBatch batch, PendingObject obj)
    {
        if (obj.Key == GateArtKey) { DrawGate(batch, obj.Tile, obj.Height, obj.Alpha); return; }

        bool open = OpenedWayAt(obj.Tile);
        bool turns = BlockCatalog.Turns(obj.Key);
        // one that turns is drawn from the rotation it was placed at; the rest
        // have one picture and an open state
        string art = turns
            ? BlockCatalog.DecorationPath(obj.Key, _level.DecorationAt(obj.Tile)?.Facing ?? Facing8.SouthEast)
            : BlockCatalog.DecorationPath(obj.Key, open);
        Billboard(batch, art, obj.Tile, obj.Height,
            (open && !turns && !BlockCatalog.HasOpenArt(obj.Key)
                ? Color.White * 0.35f : Color.White) * obj.Alpha,
            _ctx.Anchors.For(obj.Key));
    }

    /// <summary>The green diamond under a selected character, sliding with them.</summary>
    private void DrawPickedGround(SpriteBatch batch, CharacterInstance c)
    {
        var slide = WalkOffset(c);
        foreach (var tile in c.Footprint)
        {
            var box = DiamondRect(tile, HeightAt(tile));
            box.Offset((int)slide.X, (int)slide.Y);
            batch.Draw(_ctx.Assets.LoadTexture("Content/Images/Blocks/OverlayTop.png"), box,
                Color.LimeGreen * _ctx.Config.Opacity("Selected"));
            batch.Draw(_ctx.Assets.LoadTexture("Content/Images/Blocks/OverlayEdge.png"), box,
                Color.LimeGreen);
        }
    }

    private void DrawBlock(SpriteBatch batch, LevelBlock block) =>
        BlockCatalog.Draw(batch, _ctx.Assets, block.Type,
            IsoMath.ToScreen(block.X, block.Y, block.Height, Origin), Color.White);

    /// <summary>
    /// Burning ground. The art is a looping sprite sheet at
    /// Content/Images/Effects/FireTile.png with the usual companion .txt; if it
    /// isn't there the square still burns, drawn as a pulsing orange wash, so
    /// the mechanic works before the art exists rather than after.
    /// </summary>
    private void DrawFire(SpriteBatch batch, Point tile, int height, float alpha)
    {
        if (!_fireAnimTried)
        {
            _fireAnimTried = true;
            if (AssetLoader.Exists(FireArtPath))
                _fireAnim = SpriteAnimation.Load(_ctx.Assets, FireArtPath);
        }

        var c = IsoMath.ToScreen(tile.X, tile.Y, height, Origin);
        if (_fireAnim is SpriteAnimation anim && anim.FrameCount > 0)
        {
            // loops for as long as the square burns
            int frame = anim.FrameAt(_clock % Math.Max(0.001f, anim.Duration));
            var src = anim.SourceRect(frame);
            int w = IsoMath.TileW;
            int h = (int)(w * src.Height / (float)Math.Max(1, src.Width));
            batch.Draw(anim.Sheet,
                new Rectangle((int)(c.X - w / 2f), (int)(c.Y + IsoMath.TileH / 2f - h), w, h),
                src, Color.White * alpha);
            return;
        }

        // placeholder: a wash that breathes, so a burning square is unmistakable
        float pulse = 0.42f + 0.18f * (float)Math.Sin(_clock * 5.0 + (tile.X + tile.Y));
        Fill(batch, tile, height, new Color(255, 120, 30) * pulse * alpha);
        Edge(batch, tile, height, new Color(255, 190, 60) * alpha);
    }

    private const string FireArtPath = "Content/Images/Effects/FireTile.png";

    /// <summary>
    /// One end of a pair of gates: a square that breathes violet, with its rim
    /// lit. Both ends pulse on the same clock so the two read as one thing.
    /// </summary>
    private void DrawGate(SpriteBatch batch, Point tile, int height, float alpha)
    {
        // It lies FLAT, on the square rather than standing on it, so it is
        // drawn into the diamond instead of billboarded like a tree.
        //
        // Drawn SOLID. It used to breathe on a clock, fading in and out, which
        // read as a thing half there rather than a thing you can step into.
        // The only fade left is the one Ctrl puts over the whole board.
        if (AssetLoader.Exists(GateArtPath))
        {
            var tex = _ctx.Assets.LoadTexture(GateArtPath);
            var c = IsoMath.ToScreen(tile.X, tile.Y, height, Origin);
            var by = _ctx.Anchors.For(GateArtKey);
            int w = IsoMath.TileW;
            int h = (int)(w * tex.Height / (float)tex.Width);
            batch.Draw(tex, new Rectangle(
                (int)(c.X - w / 2f) + by.X, (int)(c.Y - h / 2f) - by.Y, w, h),
                Color.White * alpha);
            return;
        }
        Fill(batch, tile, height, new Color(150, 70, 230) * alpha);
        Edge(batch, tile, height, new Color(220, 170, 255) * alpha);
    }

    /// <summary>The Vessel's gate, lying open on a square. A wash when it is not drawn yet.</summary>
    private const string GateArtPath = "Content/Images/Pixel/Effects/VesselGate.png";

    /// <summary>What the gate is called in the anchor menu and in Anchors.txt.</summary>
    private const string GateArtKey = "VesselGate";

    /// <summary>How high a shot out of the sky starts, in cubes — the editor's cubes.</summary>
    private const int SkyCubes = 12;

    /// <summary>
    /// How far back along its angle a sky shot starts, in art pixels.
    ///
    /// It is a HEIGHT first: twelve cubes above the square it lands on, which
    /// the angle then turns into a distance along the incoming line. A shot
    /// coming in nearly flat would want an enormous run-up for the same
    /// height, so the angle is held away from the horizontal.
    ///
    /// It used to be a flat 3000 pixels, which at the tomahawk's speed is
    /// twenty-three seconds of falling — most of it off the top of the screen
    /// with nothing to see. That does not read as slow, it reads as stuck.
    /// </summary>
    private static float SkyRunUp(float angleDegrees)
    {
        float drop = Math.Abs((float)Math.Sin(MathHelper.ToRadians(angleDegrees)));
        return SkyCubes * IsoMath.TileH / Math.Max(0.25f, drop);
    }

    private Rectangle DiamondRect(Point tile, int height)
    {
        var c = IsoMath.ToScreen(tile.X, tile.Y, height, Origin);
        return new Rectangle((int)(c.X - IsoMath.TileW / 2f), (int)(c.Y - IsoMath.TileH / 2f),
            IsoMath.TileW, IsoMath.TileH);
    }

    /// <summary>
    /// A small skull lying flat in the middle of a square. Deliberately much
    /// smaller than the tile: it marks the ground as watched without hiding
    /// what is standing on it.
    /// </summary>
    private void DrawSkull(SpriteBatch batch, Point tile, int height)
    {
        var tex = _ctx.Assets.LoadTexture("Content/Images/Pixel/Effects/Skull.png");
        var c = IsoMath.ToScreen(tile.X, tile.Y, height, Origin);
        int size = IsoMath.Px(12);
        batch.Draw(tex, new Rectangle((int)(c.X - size / 2f), (int)(c.Y - size / 2f), size, size),
            Color.White * 0.85f);
    }

    private void Fill(SpriteBatch batch, Point tile, int height, Color color)
    {
        if (color.A == 0) return;
        batch.Draw(_ctx.Assets.LoadTexture("Content/Images/Blocks/OverlayTop.png"),
            DiamondRect(tile, height), color);
    }

    /// <summary>
    /// One tile of a highlighted region: a colour wash across its top face at
    /// the strength Config.txt asks for, plus the region's border where this
    /// tile faces out of it. Painting every tile this way leaves a solid area
    /// with a clean edge and no grid lines through the middle.
    /// </summary>
    private void Region(SpriteBatch batch, Point tile, int height, ICollection<Point> region,
        Color color, string opacityKey, float thickness)
    {
        Fill(batch, tile, height, color * _ctx.Config.Opacity(opacityKey));
        Outline(batch, tile, height, region, color, thickness);
    }

    /// <summary>
    /// Draws only the sides of this tile's diamond that face OUT of the region —
    /// do it for every tile in a region and what's left is its border alone,
    /// with none of the inner grid running through it.
    /// </summary>
    private void Outline(SpriteBatch batch, Point tile, int height,
        ICollection<Point> region, Color color, float thickness)
    {
        var c = IsoMath.ToScreen(tile.X, tile.Y, height, Origin);
        var top = c + new Vector2(0, -IsoMath.TileH / 2f);
        var right = c + new Vector2(IsoMath.TileW / 2f, 0);
        var bottom = c + new Vector2(0, IsoMath.TileH / 2f);
        var left = c + new Vector2(-IsoMath.TileW / 2f, 0);

        // +X lies down-right on screen, +Y down-left
        if (!region.Contains(new Point(tile.X + 1, tile.Y)))
            Ui.Line(batch, _ctx.Pixel, right, bottom, thickness, color);
        if (!region.Contains(new Point(tile.X - 1, tile.Y)))
            Ui.Line(batch, _ctx.Pixel, left, top, thickness, color);
        if (!region.Contains(new Point(tile.X, tile.Y + 1)))
            Ui.Line(batch, _ctx.Pixel, bottom, left, thickness, color);
        if (!region.Contains(new Point(tile.X, tile.Y - 1)))
            Ui.Line(batch, _ctx.Pixel, top, right, thickness, color);
    }

    /// <summary>
    /// The same border Outline draws, broken into dashes.
    ///
    /// A region needs to be able to say two different things in the same red,
    /// and a broken edge is the one difference that survives being small,
    /// being over busy ground, and being looked at out of the corner of an
    /// eye. It used to be said with skulls scattered inside the area, which
    /// covered the thing standing on the square you were trying to read.
    /// </summary>
    private void DashedOutline(SpriteBatch batch, Point tile, int height,
        ICollection<Point> region, Color color, float thickness)
    {
        var c = IsoMath.ToScreen(tile.X, tile.Y, height, Origin);
        var top = c + new Vector2(0, -IsoMath.TileH / 2f);
        var right = c + new Vector2(IsoMath.TileW / 2f, 0);
        var bottom = c + new Vector2(0, IsoMath.TileH / 2f);
        var left = c + new Vector2(-IsoMath.TileW / 2f, 0);

        if (!region.Contains(new Point(tile.X + 1, tile.Y))) Dashes(batch, right, bottom, color, thickness);
        if (!region.Contains(new Point(tile.X - 1, tile.Y))) Dashes(batch, left, top, color, thickness);
        if (!region.Contains(new Point(tile.X, tile.Y + 1))) Dashes(batch, bottom, left, color, thickness);
        if (!region.Contains(new Point(tile.X, tile.Y - 1))) Dashes(batch, top, right, color, thickness);
    }

    /// <summary>
    /// One edge, as a row of dashes. The dash length is fixed in art pixels
    /// so the pattern looks the same on every side of the diamond rather than
    /// stretching along the longer ones.
    /// </summary>
    private void Dashes(SpriteBatch batch, Vector2 a, Vector2 b, Color color, float thickness)
    {
        float dash = IsoMath.Px(7), gap = IsoMath.Px(5);
        var span = b - a;
        float len = span.Length();
        if (len < 1f) return;
        var dir = span / len;
        for (float at = 0; at < len; at += dash + gap)
            Ui.Line(batch, _ctx.Pixel, a + dir * at,
                a + dir * Math.Min(at + dash, len), thickness, color);
    }

    /// <summary>Grid lines: the diamond's outline, so the highlight reads as a mesh.</summary>
    private void Edge(SpriteBatch batch, Point tile, int height, Color color) =>
        batch.Draw(_ctx.Assets.LoadTexture("Content/Images/Blocks/OverlayEdge.png"),
            DiamondRect(tile, height), color);

    private void Billboard(SpriteBatch batch, string path, Point tile, int height, Color tint,
        ArtAnchor? nudge = null)
    {
        var tex = _ctx.Assets.LoadTexture(path);
        var c = IsoMath.ToScreen(tile.X, tile.Y, height, Origin);
        // a door or a tree is drawn at its own size when it is pixel art, and
        // squeezed to a tile's width when it is still an old painted picture
        int w = Math.Min(tex.Width, IsoMath.TileW);
        int h = (int)(w * tex.Height / (float)tex.Width);
        var by = nudge ?? ArtAnchors.None;
        batch.Draw(tex, new Rectangle(
            (int)(c.X - w / 2f) + by.X, (int)(c.Y + IsoMath.Px(5) - h) - by.Y, w, h), tint);
    }

    /// <param name="alpha">
    /// Faded right down while Ctrl is held, so the ground under everyone can be
    /// seen and aimed at. The health bar above the head keeps full strength —
    /// it is the information you are targeting with, not something in the way.
    /// </param>
    private void DrawCharacter(SpriteBatch batch, CharacterInstance c, float alpha = 1f)
    {
        var art = ArtFor(c);
        var rect = SpriteRect(c);

        // A cast or a walk stands in for the standing pose while it runs.
        var picture = AnimationFrame(c) ?? art;
        var shown = ReferenceEquals(picture, art) ? rect : RectFor(c, picture);

        // Lying in wait is drawn half there. It is the only thing on the board
        // that says so: a lurker is still standing where they were, and
        // nothing else about them changes.
        float solidity = c.IsLurking ? 0.5f : 1f;
        batch.Draw(picture, shown, Color.White * alpha * solidity);

        // A placeholder cube has no front, so the yellow triangle is the only
        // thing saying which way it is turned. It goes on AFTER the cube: the
        // cube's base sits on the middle of the square, which is exactly where
        // the triangle is, so drawing it underneath hid it completely.
        if (_ctx.Sprites.For(c) == null) DrawFacingMark(batch, c, alpha);

        // About to be hit: a red line round them. Drawn for anything the aimed
        // card would actually catch, which with Friendly Fire on means your own
        // people light up alongside the enemies — the point being that you find
        // that out while aiming rather than afterwards.
        //
        // Traced off the frame BEING DRAWN, not off the standing pose. An imp
        // hovers on the spot, and an outline drawn round the still picture sat
        // a few pixels off whatever the wings were doing — a red smear beside
        // the thing it was supposed to be marking.
        if (_doomed.Contains(c)) OutlineSprite(batch, picture, shown);

        // Within a bite of death, while the card that eats them is in hand.
        // Their bar grows too — see BarRect.
        if (_edible.Contains(c)) OutlineSprite(batch, picture, shown, new Color(255, 160, 40));

        var back = BarRect(c);
        if (_targets.Contains(c))
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(rect.X, back.Y - 4, rect.Width, 2), Color.OrangeRed);

        // the bars, and everything hung off them, only when asked for
        if (!ShowsBars(c)) return;
        DrawBars(batch, c, back, MindRect(c));

        // whoever is selected wears a small arrow pointing down at their bar
        if (IsSelected(c)) DrawSelectionArrow(batch, back);

        if (c.CurseBonus > 0)
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(back.X, MindRect(c).Bottom + 1, back.Width, 1), new Color(180, 90, 230));

        // Marks under the bar, below the curse stripe so nothing overlaps.
        // Side by side when a character has both, rather than stacked.
        var marks = new List<string>();
        if (c.IsVulnerable) marks.Add("Bullseye");
        if (c.IsStunned) marks.Add("Stun");
        for (int i = 0; i < marks.Count; i++)
        {
            int row = marks.Count * MarkPx + (marks.Count - 1);
            int x = back.Center.X - row / 2 + i * (MarkPx + 1);
            batch.Draw(_ctx.Assets.LoadTexture($"Content/Images/Pixel/Effects/{marks[i]}.png"),
                new Rectangle(x, MindRect(c).Bottom + 3, MarkPx, MarkPx), Color.White);
        }

        // burning: one flame per stack, sitting on the bar
        if (c.BurningStacks > 0)
        {
            var flame = _ctx.Assets.LoadTexture("Content/Images/Pixel/Effects/Flame.png");
            for (int i = 0; i < Math.Min(c.BurningStacks, 4); i++)
                batch.Draw(flame,
                    new Rectangle(back.X - MarkPx + 1 + i * (MarkPx - 2), back.Y - 2,
                        MarkPx, MarkPx), Color.White);
        }
    }

    /// <summary>
    /// Whose picture the mouse is over: the drawn part of it, not the empty
    /// canvas round it. Where two overlap, the one nearer the viewer wins,
    /// since that is the one drawn on top.
    /// </summary>
    private CharacterInstance? CastUnderPointer()
    {
        var at = _worldPointer.ToPoint();
        return Everyone
            .Where(c => c.Alive && _level.Shown(Tile(c), _revealed))
            .Where(c =>
            {
                // the frame on screen, not the standing pose: pointing at a
                // hovering imp has to hit the imp you can see
                var picture = AnimationFrame(c) ?? ArtFor(c);
                var solid = ArtBounds.Solid(picture);
                var rect = RectFor(c, picture);
                solid.Offset(rect.X, rect.Y);
                return solid.Contains(at);
            })
            .OrderByDescending(c => DepthOf(c, Tile(c)))
            .FirstOrDefault();
    }

    /// <summary>
    /// Whether somebody's bars are up: the selected party, whoever the mouse
    /// is over on the board or in the turn strip, and everybody under Ctrl.
    /// </summary>
    private bool ShowsBars(CharacterInstance c)
    {
        // Ctrl and the mouse always win: holding Ctrl to read the board, or
        // pointing at somebody, is asking for that one's bars whatever the
        // setting says.
        if (_ctrl || c == _hoveredCast || c == _stripHover) return true;
        return _ctx.Settings.Bars switch
        {
            BarVisibility.Always => true,
            BarVisibility.Damaged => Picked.Contains(c) || Hurt(c),
            _ => Picked.Contains(c),
        };
    }

    /// <summary>
    /// Whether either bar is short of full — including the pale ghost still
    /// sliding down, so a bar does not blink out halfway through its own
    /// animation on the hit that took it back to full.
    /// </summary>
    private static bool Hurt(CharacterInstance c) =>
        c.Hp < c.MaxHp || c.ShownHp > c.Hp ||
        (!c.MindImmune && (c.Mind < c.MaxMind || c.ShownMind > c.Mind));

    /// <summary>
    /// Health and mind bars filled into two boxes. Used over heads on the board
    /// and under faces in the turn strip, so the two always read the same.
    /// </summary>
    private void DrawBars(SpriteBatch batch, CharacterInstance c, Rectangle back, Rectangle mindBar)
    {
        // Armour extends the health bar in metallic grey and a pad of
        // temporary health extends it in pale pink, so 10 health plus 5 of
        // either makes that colour a third of the width.
        Ui.FillRect(batch, _ctx.Pixel, back, Color.Black * 0.72f);

        int span = Math.Max(1, c.MaxHp + c.Armor + c.TempHp);
        int hpW = (int)(back.Width * Math.Clamp(c.Hp / (float)span, 0f, 1f));
        int padW = (int)(back.Width * Math.Clamp(c.TempHp / (float)span, 0f, 1f));
        int armW = (int)(back.Width * Math.Clamp(c.Armor / (float)span, 0f, 1f));

        // The part that has been lost but not yet caught up shows pale behind
        // the bar, so a hit reads as a chunk sliding off rather than a number
        // that was simply different last time you looked.
        int ghostW = (int)(back.Width * Math.Clamp(Math.Max(c.ShownHp, c.Hp) / span, 0f, 1f));
        if (ghostW > hpW)
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(back.X + hpW, back.Y, ghostW - hpW, back.Height),
                new Color(235, 225, 120));

        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(back.X, back.Y, hpW, back.Height),
            c.IsPlayer ? new Color(70, 190, 70) : new Color(200, 60, 60));
        // the pad first, then the plate under it, in the order damage eats them
        if (padW > 0)
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(back.X + hpW, back.Y, padW, back.Height),
                new Color(255, 170, 200));
        if (armW > 0)
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(back.X + hpW + padW, back.Y, armW, back.Height),
                new Color(168, 172, 180));

        Outline(batch, back);

        // The mind bar under it. Purple for anything with nerve to lose; grey
        // for a Living Stone, which has none to lose and is drawn full.
        Ui.FillRect(batch, _ctx.Pixel, mindBar, Color.Black * 0.72f);
        int mindW = (int)(mindBar.Width * c.MindFraction);
        // the same pale ghost the health bar uses, so a fright reads as a
        // chunk sliding off rather than a bar that is simply shorter now
        if (!c.MindImmune && c.ShownMind > c.Mind)
        {
            int ghostMind = (int)(mindBar.Width *
                Math.Clamp(c.ShownMind / Math.Max(1f, c.MaxMind), 0f, 1f));
            if (ghostMind > mindW)
                Ui.FillRect(batch, _ctx.Pixel,
                    new Rectangle(mindBar.X + mindW, mindBar.Y, ghostMind - mindW, mindBar.Height),
                    new Color(225, 200, 245));
        }
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(mindBar.X, mindBar.Y, mindW, mindBar.Height),
            c.MindImmune ? new Color(130, 130, 138) : new Color(150, 60, 200));
        Outline(batch, mindBar);
    }

    /// <summary>A one-pixel white edge, drawn last so nothing paints over it.</summary>
    private void Outline(SpriteBatch batch, Rectangle r)
    {
        if (r.Width <= 2 || r.Height <= 2) return;
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(r.X, r.Y, r.Width, 1), Color.White);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), Color.White);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(r.X, r.Y, 1, r.Height), Color.White);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(r.Right - 1, r.Y, 1, r.Height), Color.White);
    }

    /// <summary>How big a status icon is, in art pixels.</summary>
    private static int MarkPx => IsoMath.Px(8);

    /// <summary>
    /// The health bar over somebody's head, in art pixels. Its width follows
    /// the picture so a two-tile enemy gets a wider bar than a person, within
    /// limits that keep a narrow sprite's bar readable and a wide one's from
    /// running across the board.
    /// </summary>
    private Rectangle BarRect(CharacterInstance c)
    {
        var art = ArtFor(c);
        var solid = ArtBounds.Solid(art);
        var rect = SpriteRect(c);
        int w = Math.Clamp(solid.Width + IsoMath.Px(8), IsoMath.Px(32), IsoMath.Px(64)) / 2;
        // Anything a card in hand could swallow whole gets a bigger bar while
        // that card is up: the whole decision is "is there enough left in it",
        // so the number you are deciding on is the thing to make easier to read.
        if (_edible.Contains(c)) w = w * 3 / 2;
        return new Rectangle(
            rect.X + (solid.Left + solid.Right) / 2 - w / 2,
            rect.Y + solid.Top - (BarH + MindH + 5), w, BarH);
    }

    /// <summary>How tall the health and mind bars are, in art pixels.</summary>
    private static int BarH => IsoMath.Px(7) / 2;
    private static int MindH => IsoMath.Px(4) / 2;

    /// <summary>
    /// The mind bar: nerve, directly under the health bar and a little
    /// shorter, so the two read as one block without being mistaken for each
    /// other. Anything that cannot be frightened gets a grey one.
    /// </summary>
    private Rectangle MindRect(CharacterInstance c)
    {
        var bar = BarRect(c);
        return new Rectangle(bar.X, bar.Bottom + 1, bar.Width, MindH);
    }

    /// <summary>The triangle on the floor showing which way a placeholder faces.</summary>
    private void DrawFacingMark(SpriteBatch batch, CharacterInstance c, float alpha)
    {
        // on the middle of the BODY, so a two-square Gator's arrow sits under
        // the middle of the Gator rather than off one end of it
        var centre = FootOf(c);
        batch.Draw(FacingMark.For(_ctx.Game.GraphicsDevice, c.Facing),
            new Rectangle(
                (int)centre.X - FacingMark.Width / 2,
                (int)centre.Y - FacingMark.Height / 2,
                FacingMark.Width, FacingMark.Height),
            Color.White * (0.85f * alpha));
    }

    /// <summary>
    /// Damage numbers and the health total, drawn in the HUD pass rather than
    /// on the board. They are text, and the font is baked for the 3840-wide
    /// design space — putting it through the pixel camera would either shrink
    /// it to mush or blow it up over the whole level. So the bar's position is
    /// carried across into design space and the numbers are drawn there, sharp.
    /// </summary>
    private void DrawCharacterNumbers(SpriteBatch batch, CharacterInstance c)
    {
        var back = ToDesign(BarRect(c));
        if (back.Width <= 0) return;
        if (ShowsBars(c)) DrawBarNumbers(batch, c, back, ToDesign(MindRect(c)));

        for (int i = 0; i < c.Popups.Count; i++)
        {
            var (amount, _, life) = c.Popups[i];
            float gone = 1f - life / PopupSeconds;
            var where = new Vector2(back.Center.X, back.Y - 18 - gone * 70f - i * 44f);
            string text = $"-{amount}";
            var size = _ctx.Font.MeasureString(text) * 0.42f;
            // a dark copy behind it, so a number over pale art is still readable
            batch.DrawString(_ctx.Font, text, where - size / 2 + new Vector2(3, 3),
                Color.Black * (0.75f * life / PopupSeconds), 0f, Vector2.Zero, 0.42f,
                SpriteEffects.None, 0f);
            batch.DrawString(_ctx.Font, text, where - size / 2,
                new Color(255, 236, 120) * Math.Clamp(life / PopupSeconds * 1.6f, 0f, 1f),
                0f, Vector2.Zero, 0.42f, SpriteEffects.None, 0f);
        }
    }

    /// <summary>
    /// The health and nerve totals written inside their bars, in design space.
    /// The numbers fill the bars they sit in, so they follow the bar's size.
    /// </summary>
    private void DrawBarNumbers(SpriteBatch batch, CharacterInstance c, Rectangle back, Rectangle mindBox)
    {
        Ui.DrawTextCentered(batch, _ctx.Font,
            c.Armor + c.TempHp > 0 ? $"{c.Hp}+{c.Armor + c.TempHp}" : c.Hp.ToString(), back, Color.White,
            back.Height * BarTextScale);

        // Nerve, in its own bar. An infinity sign for anything that cannot be
        // frightened: a Living Stone showing "100" would look like a number
        // somebody could get down.
        if (mindBox.Width > 0)
            Ui.DrawTextCentered(batch, _ctx.Font,
                c.MindImmune ? "\u221e" : c.Mind.ToString(), mindBox,
                c.MindImmune ? new Color(210, 210, 218) : Color.White,
                mindBox.Height * BarTextScale);
    }

    /// <summary>Font scale per design pixel of bar height.</summary>
    private const float BarTextScale = 0.0125f;

    /// <summary>
    /// A rectangle on the board, expressed in the design space the HUD uses.
    /// Goes world -> window -> design, undoing the letterbox on the way.
    /// </summary>
    private Rectangle ToDesign(Rectangle world)
    {
        float scale = Math.Max(0.0001f, _ctx.Viewport.Scale);
        var topLeft = _camera.ToScreen(new Point(world.X, world.Y));
        var bottomRight = _camera.ToScreen(new Point(world.Right, world.Bottom));
        var a = new Point(
            (int)((topLeft.X - _ctx.Viewport.Offset.X) / scale),
            (int)((topLeft.Y - _ctx.Viewport.Offset.Y) / scale));
        var b = new Point(
            (int)((bottomRight.X - _ctx.Viewport.Offset.X) / scale),
            (int)((bottomRight.Y - _ctx.Viewport.Offset.Y) / scale));
        return new Rectangle(a.X, a.Y, b.X - a.X, b.Y - a.Y);
    }

    /// <summary>How long a damage number stays in the air.</summary>
    private const float PopupSeconds = 1.1f;

    /// <summary>Health points the bar closes per second while catching up.</summary>
    private const float BarCatchUpPerSecond = 26f;

    /// <summary>
    /// Slides every health bar towards the number it is meant to show, and
    /// ages the damage numbers floating off people. Runs on real time rather
    /// than on turns, so it keeps going while a card is mid-flight.
    /// </summary>
    /// <summary>Nerve the mind bar closes per second while catching up.</summary>
    private const float MindCatchUpPerSecond = 90f;

    private void UpdateHealthBars(float dt)
    {
        // the mind bar chases its number the way the health bar does, so a
        // fright reads as nerve draining rather than a bar that jumped
        foreach (var c in Everyone)
        {
            if (c.ShownMind < 0) c.ShownMind = c.Mind;
            else if (c.ShownMind > c.Mind)
                c.ShownMind = Math.Max(c.Mind, c.ShownMind - MindCatchUpPerSecond * dt);
            else c.ShownMind = c.Mind;
        }
        foreach (var c in Everyone)
        {
            if (c.ShownHp < 0f) c.ShownHp = c.Hp;          // first sight of them
            else if (c.ShownHp > c.Hp)
                c.ShownHp = Math.Max(c.Hp, c.ShownHp - BarCatchUpPerSecond * dt);
            else if (c.ShownHp < c.Hp)
                c.ShownHp = Math.Min(c.Hp, c.ShownHp + BarCatchUpPerSecond * dt);

            for (int i = c.Popups.Count - 1; i >= 0; i--)
            {
                var p = c.Popups[i];
                p.Life -= dt;
                if (p.Life <= 0f) c.Popups.RemoveAt(i); else c.Popups[i] = p;
            }
        }
    }

    /// <summary>Everyone marked right now: the picked group, or whoever's turn it is.</summary>
    private IEnumerable<CharacterInstance> Picked =>
        _mode is Mode.Explore
            ? _picked.Where(p => p.Alive)
            : Chosen is CharacterInstance one ? new[] { one } : Enumerable.Empty<CharacterInstance>();

    private bool IsSelected(CharacterInstance c) => _mode is Mode.Explore
        ? _picked.Contains(c)
        : c == Current && c.IsPlayer;

    /// <summary>How thick the "about to be hit" outline is, in the art's own pixels.</summary>
    /// <summary>How thick the red line round a doomed sprite is, in art pixels.</summary>
    private static int DoomedOutline => IsoMath.Px(1);

    /// <summary>
    /// Traces a red line round the ART of a sprite — the drawn pixels, soft
    /// edges included — rather than round its canvas.
    ///
    /// A character sits in a PNG that is mostly empty, so boxing the canvas
    /// drew a rectangle floating well clear of them and, with several marked at
    /// once, a row of boxes that told you nothing about who was where. The
    /// outline is a texture built once per sprite and stretched over it, so
    /// this is one draw call however complicated the silhouette.
    /// </summary>
    private void OutlineSprite(SpriteBatch batch, Texture2D art, Rectangle rect, Color? ink = null) =>
        batch.Draw(_ctx.Assets.Outline(art, DoomedOutline), rect, ink ?? new Color(255, 45, 45));

    /// <summary>A small solid triangle pointing down, sitting on top of the health bar.</summary>
    private void DrawSelectionArrow(SpriteBatch batch, Rectangle bar)
    {
        int width = IsoMath.Px(9), height = IsoMath.Px(5);
        int baseY = bar.Y - 2 - height;
        for (int row = 0; row < height; row++)
        {
            int w = Math.Max(2, width - row * width / height);
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(bar.Center.X - w / 2, baseY + row, w, 1), Color.Gold);
        }
    }

    /// <summary>
    /// The machine, sitting on whichever square of its run it has reached. It
    /// slides between the last square and the next rather than jumping, so a
    /// run at eleven squares a second still reads as something driving.
    /// </summary>
    private void DrawMower(SpriteBatch batch)
    {
        if (_mode != Mode.Acting || _act != Act.Mowing || _mower == null || _actingCard == null)
            return;
        int i = Math.Clamp(_mowerBeat - 1, 0, _mower.Beats.Count - 1);
        var here = _mower.Beats[i].Tile;
        var last = i > 0 ? _mower.Beats[i - 1].Tile : here;
        // _mowerTimer counts DOWN across one square, so this runs 0 -> 1
        float t = MathHelper.Clamp(1f - _mowerTimer / MowerTileTime, 0f, 1f);

        var a = IsoMath.ToScreen(last.X, last.Y, HeightAt(last), Origin);
        var b = IsoMath.ToScreen(here.X, here.Y, HeightAt(here), Origin);
        var pos = Vector2.Lerp(a, b, t);

        // Which way it is POINTED is a separate question from where it slides
        // between: the first beat and the blast at the end both share a square
        // with their neighbour, and asking those for a direction gave none.
        var facing = Facings.Towards(_mower.Approach(i), here);

        // art drawn per direction is already turned; one picture spins instead
        if (EffectArt.Turned(_actingCard.ProjectileArt, facing) is string turned)
        {
            var art = _ctx.Assets.LoadTexture(turned);
            batch.Draw(art, pos - Standing(art), null, Color.White, 0f,
                Vector2.Zero, 1f, SpriteEffects.None, 0f);
            return;
        }

        var tex = ProjectileArt(_actingCard.ProjectileArt);
        batch.Draw(tex, pos, null, Color.White, _clock * 14f, Middle(tex), 1f, SpriteEffects.None, 0f);
    }

    /// <summary>
    /// How far to shift a picture so it STANDS on a square rather than being
    /// skewered by it.
    ///
    /// The mower drives along the ground, so the middle of its wheels wants to
    /// be on the middle of the square. Hung by its middle like a fireball it
    /// sank half a sprite into the floor and read as being fired through the
    /// tiles. Measured off the solid pixels, so the empty margin a sprite was
    /// exported with never shows up as a hover.
    /// </summary>
    private static Vector2 Standing(Texture2D art)
    {
        var solid = ArtBounds.Solid(art);
        return new Vector2((solid.Left + solid.Right) / 2f, solid.Bottom);
    }

    /// <summary>The middle of a picture, which is what every effect hangs by.</summary>
    private static Vector2 Middle(Texture2D tex) => new(tex.Width / 2f, tex.Height / 2f);

    private void DrawProjectile(SpriteBatch batch)
    {
        if (_mode != Mode.Acting || _act != Act.Projectile || _actingCard == null) return;
        float t = _actDur <= 0f ? 1f : MathHelper.Clamp(_actT / _actDur, 0f, 1f);
        if (_shots.Count == 0)
            DrawShot(batch, _projTo, _projRotation, _projSeed, t);
        foreach (var (to, seed) in _shots)
            DrawShot(batch, to,
                (float)Math.Atan2(to.Y - _projFrom.Y, to.X - _projFrom.X), seed, t);
    }

    /// <summary>
    /// One shot, t of the way from the caster to where it lands.
    ///
    /// A rocket does not fly straight. It weaves across its own line and
    /// settles onto the target as it arrives — the wander is widest in the
    /// middle of the flight and nothing at either end, so it always leaves
    /// the caster and lands on the square it was aimed at.
    /// </summary>
    private void DrawShot(SpriteBatch batch, Vector2 to, float angle, float seed, float t)
    {
        var pos = Vector2.Lerp(_projFrom, to, t);
        if (_projWander != 0f)
        {
            var line = to - _projFrom;
            if (line != Vector2.Zero)
            {
                var across = Vector2.Normalize(new Vector2(-line.Y, line.X));
                float taper = (float)Math.Sin(t * Math.PI);
                float sway = (float)Math.Sin(t * Math.PI * 3.0 + seed) * _projWander * taper;
                pos += across * sway;
                // point where it is actually going, not where it was aimed
                angle += sway * 0.02f;
            }
        }
        // art drawn per direction is already turned; one picture gets spun to
        // point along the flight
        var flight = to - _projFrom;
        if (EffectArt.Turned(_actingCard!.ProjectileArt, Facings.OnScreen(flight.X, flight.Y))
            is string turned)
        {
            var art = _ctx.Assets.LoadTexture(turned);
            batch.Draw(art, pos, null, Color.White, 0f, Middle(art), 1f, SpriteEffects.None, 0f);
            return;
        }

        var tex = ProjectileArt(_actingCard.ProjectileArt);
        batch.Draw(tex, pos, null, Color.White, angle, Middle(tex), 1f, SpriteEffects.None, 0f);
    }

    /// <summary>
    /// The INFO WINDOW: the box beside the hand, on its left, that explains
    /// whatever is being looked at — a card's own words, or everything
    /// currently happening to the character under the pointer.
    ///
    /// It takes whatever room there is between the screen edge and the first
    /// card. A hand wide enough to leave less than InfoMinW gets no window at
    /// all — null — because a column three words wide is worse than none.
    ///
    /// It grows UPWARD from its floor to whatever height is asked for, since
    /// it is anchored to the cards along the bottom of the screen; a list of
    /// a dozen effects runs up the side rather than off the end.
    /// </summary>
    private Rectangle? InfoWindow(int wantedHeight)
    {
        var rects = HandRects();
        int handLeft = rects.Count > 0 ? rects[0].X : VirtualViewport.Width / 2;
        int right = handLeft - InfoGap;
        int x = Math.Max(60, right - InfoW);
        int w = right - x;
        if (w < InfoMinW) return null;

        // lifted clear of the very bottom of the screen: the cards rest half
        // off it, but words have to be read
        int floor = VirtualViewport.Height - 24;
        int h = Math.Clamp(wantedHeight, InfoMinH, floor - 40);
        return new Rectangle(x, floor - h, w, h);
    }

    /// <summary>
    /// What a card throws. A card with pixel art of its own gets it; everything
    /// else gets the ball, which is the placeholder every projectile starts as.
    /// </summary>
    private Texture2D ProjectileArt(string file) =>
        _ctx.Assets.LoadTexture(EffectArt.Single(file));

    private List<Rectangle> HandRects()
    {
        int total = _hand.Count * (CardW + CardGap) - CardGap;
        int x0 = (VirtualViewport.Width - total) / 2;
        var rects = new List<Rectangle>();
        for (int i = 0; i < _hand.Count; i++)
            rects.Add(new Rectangle(x0 + i * (CardW + CardGap), CardRestY, CardW, CardH));
        return rects;
    }
}
