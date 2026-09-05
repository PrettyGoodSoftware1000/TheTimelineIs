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
                     .OrderByDescending(b => b.X + b.Y))
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
    private Rectangle SpriteRect(CharacterInstance c)
    {
        var art = ArtFor(c);
        var solid = ArtBounds.Solid(art);
        var foot = FootOf(c);
        return new Rectangle(
            (int)foot.X - (solid.Left + solid.Right) / 2,
            (int)foot.Y - solid.Bottom,
            art.Width, art.Height);
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
        foreach (var c in Everyone.Where(c => c.Alive))
        {
            // the FAR corner of the body, so every square it stands on is
            // already painted by the time the sprite goes down over them
            var anchor = c == _walker && _walkPath.Count > 0 ? _walkPath[0] : Tile(c);
            int depth = anchor.X + c.SizeX - 1 + anchor.Y + c.SizeY - 1;
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
        if (armed && Acting is CharacterInstance planter &&
            (_selectedCard ?? HoveredCard()) is { IsGuard: true } plant)
            _watchedGround.UnionWith(GuardZoneAround(Tile(planter), plant.GuardReach));

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

            // Ground somebody is watching, and the same patch previewed in red
            // while the card is still in hand. Skulls are what tells the two
            // reds apart: plain red is "this card reaches here", red with a
            // skull is "walk here and get shot".
            if (_watchedGround.Contains(tile))
            {
                Region(batch, tile, block.Height, _watchedGround, new Color(210, 40, 40),
                    "Guard", 8f);
                DrawSkull(batch, tile, block.Height);
            }

            if (_level.TriggerAt(tile) is { Fired: false })
            {
                Fill(batch, tile, block.Height, Color.Violet * _ctx.Config.Opacity("Trigger"));
                Edge(batch, tile, block.Height, Color.Violet * 0.8f);
            }

            // Whose turn it is, or who is picked in Explore: green under their
            // feet. Drawn before the yellow so the cursor still reads clearly
            // when it is over the selected character's own square.
            // green under everyone picked, not only the last one clicked, so a
            // group selection is visible as a group
            if (Picked.Any(p => p.Covers(tile)))
            {
                Fill(batch, tile, block.Height, Color.LimeGreen * _ctx.Config.Opacity("Selected"));
                Edge(batch, tile, block.Height, Color.LimeGreen);
            }

            if (hovered == tile)
            {
                Fill(batch, tile, block.Height, Color.Yellow * _ctx.Config.Opacity("Hover"));
                Edge(batch, tile, block.Height, Color.Yellow);
            }

            if (_level.DoorAt(tile) is LevelDoor door)
                Billboard(batch, "Content/Images/Decorations/Door.png", tile, block.Height,
                    (door.Open ? Color.White * 0.35f : Color.White) * alpha);
            if (_level.DecorationAt(tile) is LevelDecoration deco)
                Billboard(batch, BlockCatalog.DecorationPath(deco.File), tile, block.Height,
                    Color.White * alpha);
            if (_fires.ContainsKey(tile)) DrawFire(batch, tile, block.Height, alpha);
        }
        DrawBandCast(batch, byDepth, band, alpha);

        // Anybody left is standing where no ground was drawn — off the edge of
        // the level, or in a room nobody has revealed. They go down last rather
        // than being dropped, so a character in the wrong place is visible
        // instead of quietly missing.
        foreach (var stranded in byDepth.Values)
            foreach (var c in stranded)
                DrawCharacter(batch, c, alpha);

        DrawProjectile(batch);
        DrawMower(batch);
    }

    /// <summary>Draws everybody standing at one depth, then forgets them.</summary>
    private void DrawBandCast(SpriteBatch batch,
        Dictionary<int, List<CharacterInstance>> byDepth, int depth, float alpha)
    {
        if (!byDepth.Remove(depth, out var standing)) return;
        foreach (var c in standing) DrawCharacter(batch, c, alpha);
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
    /// How far back along its angle a sky shot starts, in virtual pixels. Well
    /// past the top of the screen, so it is already falling when it appears.
    /// </summary>
    private const float SkyRunUp = 3000f;

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
        const int size = 12;
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

    /// <summary>Grid lines: the diamond's outline, so the highlight reads as a mesh.</summary>
    private void Edge(SpriteBatch batch, Point tile, int height, Color color) =>
        batch.Draw(_ctx.Assets.LoadTexture("Content/Images/Blocks/OverlayEdge.png"),
            DiamondRect(tile, height), color);

    private void Billboard(SpriteBatch batch, string path, Point tile, int height, Color tint)
    {
        var tex = _ctx.Assets.LoadTexture(path);
        var c = IsoMath.ToScreen(tile.X, tile.Y, height, Origin);
        // a door or a tree is drawn at its own size when it is pixel art, and
        // squeezed to a tile's width when it is still an old painted picture
        int w = Math.Min(tex.Width, IsoMath.TileW);
        int h = (int)(w * tex.Height / (float)tex.Width);
        batch.Draw(tex, new Rectangle((int)(c.X - w / 2f), (int)(c.Y + 5 - h), w, h), tint);
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

        // While a cast is running its frames stand in for the sprite, hung by
        // the same feet, so nothing jumps when it starts or stops.
        if (c.CastFrames is { Count: > 0 } frames)
        {
            int i = Math.Clamp((int)(c.CastAnimTime * DirectionalSprite.Fps), 0, frames.Count - 1);
            var frame = frames[i];
            var solid = ArtBounds.Solid(frame);
            var foot = FootOf(c);
            batch.Draw(frame, new Rectangle(
                (int)foot.X - (solid.Left + solid.Right) / 2,
                (int)foot.Y - solid.Bottom, frame.Width, frame.Height), Color.White * alpha);
        }
        else
            batch.Draw(art, rect, Color.White * alpha);

        // A placeholder cube has no front, so the yellow triangle is the only
        // thing saying which way it is turned. It goes on AFTER the cube: the
        // cube's base sits on the middle of the square, which is exactly where
        // the triangle is, so drawing it underneath hid it completely.
        if (_ctx.Sprites.For(c) == null) DrawFacingMark(batch, c, alpha);

        // About to be hit: a red line round them. Drawn for anything the aimed
        // card would actually catch, which with Friendly Fire on means your own
        // people light up alongside the enemies — the point being that you find
        // that out while aiming rather than afterwards. Traced off the standing
        // sprite even mid-cast, so the shape does not flicker frame to frame.
        if (_doomed.Contains(c)) OutlineSprite(batch, art, rect);

        var back = BarRect(c);
        if (_targets.Contains(c))
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(rect.X, back.Y - 4, rect.Width, 2), Color.OrangeRed);

        // health bar above the head. Armor extends the bar in metallic grey, so
        // 10 health plus 5 armor makes the grey a third of its width.
        Ui.FillRect(batch, _ctx.Pixel, back, Color.Black * 0.72f);

        int span = Math.Max(1, c.MaxHp + c.Armor);
        int hpW = (int)(back.Width * Math.Clamp(c.Hp / (float)span, 0f, 1f));
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
        if (armW > 0)
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(back.X + hpW, back.Y, armW, back.Height),
                new Color(168, 172, 180));
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(back.X, back.Y, back.Width, 1),
            Color.Black * 0.5f);

        // whoever is selected wears a small arrow pointing down at their bar
        if (IsSelected(c)) DrawSelectionArrow(batch, back);

        if (c.CurseBonus > 0)
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(back.X, back.Bottom + 1, back.Width, 1), new Color(150, 60, 200));

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
                new Rectangle(x, back.Bottom + 3, MarkPx, MarkPx), Color.White);
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


    /// <summary>How big a status icon is, in art pixels.</summary>
    private const int MarkPx = 8;

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
        int w = Math.Clamp(solid.Width, 20, 48);
        return new Rectangle(
            rect.X + (solid.Left + solid.Right) / 2 - w / 2,
            rect.Y + solid.Top - 8, w, 4);
    }

    /// <summary>The triangle on the floor showing which way a placeholder faces.</summary>
    private void DrawFacingMark(SpriteBatch batch, CharacterInstance c, float alpha)
    {
        var centre = IsoMath.ToScreen(c.GX, c.GY, HeightAt(Tile(c)), Origin);
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
        Ui.DrawTextCentered(batch, _ctx.Font,
            c.Armor > 0 ? $"{c.Hp}+{c.Armor}" : c.Hp.ToString(), back, Color.White,
            back.Height * 0.0075f);

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
    private void UpdateHealthBars(float dt)
    {
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
    private const int DoomedOutline = 1;

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
    private void OutlineSprite(SpriteBatch batch, Texture2D art, Rectangle rect) =>
        batch.Draw(_ctx.Assets.Outline(art, DoomedOutline), rect, new Color(255, 45, 45));

    /// <summary>A small solid triangle pointing down, sitting on top of the health bar.</summary>
    private void DrawSelectionArrow(SpriteBatch batch, Rectangle bar)
    {
        const int width = 9, height = 5;
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

        var tex = ProjectileArt(_actingCard.ProjectileArt);
        batch.Draw(tex, pos, null, Color.White, _clock * 14f,
            new Vector2(tex.Width / 2f, tex.Height / 2f), 1f, SpriteEffects.None, 0f);
    }

    private void DrawProjectile(SpriteBatch batch)
    {
        if (_mode != Mode.Acting || _act != Act.Projectile || _actingCard == null) return;
        float t = _actDur <= 0f ? 1f : MathHelper.Clamp(_actT / _actDur, 0f, 1f);
        var tex = ProjectileArt(_actingCard.ProjectileArt);
        var pos = Vector2.Lerp(_projFrom, _projTo, t);
        batch.Draw(tex, pos, null, Color.White, _projRotation,
            new Vector2(tex.Width / 2f, tex.Height / 2f), 1f, SpriteEffects.None, 0f);
    }

    /// <summary>
    /// What a card throws. A card with pixel art of its own gets it; everything
    /// else gets the ball, which is the placeholder every projectile starts as.
    /// </summary>
    private Texture2D ProjectileArt(string file)
    {
        string mine = $"Content/Images/Pixel/Effects/{file}";
        return _ctx.Assets.LoadTexture(
            AssetLoader.Exists(mine) ? mine : "Content/Images/Pixel/Effects/Ball.png");
    }

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
