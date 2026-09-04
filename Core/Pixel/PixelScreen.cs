using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Iso;
using TheTimelineIs.Core.Screens;

namespace TheTimelineIs.Core.Pixel;

/// <summary>
/// The pixel build: the same isometric level data, drawn so that every pixel
/// on screen is the same size as every other one.
///
/// It is a test bench rather than the game — no cards, no turns, no rules —
/// because what is being tested is how the art reads: tiles that meet without
/// seams, characters that turn to face where they are going, and a zoom that
/// does not blur anything.
///
/// Run it with: dotnet run --project Desktop -- --pixel
/// </summary>
public class PixelScreen : IScreen, IDrawsItself
{
    private readonly GameContext _ctx;
    private readonly LevelData _level;
    private readonly PixelCamera _camera = new();
    private readonly List<PixelActor> _cast = new();

    private PixelActor? _picked;
    private Point _windowSize = new(1920, 1080);
    private bool _placed;
    private string _note = "";
    private float _noteTimer;

    /// <summary>Which level the pixel build opens.</summary>
    public const string TestLevel = "PixelTest";

    public PixelScreen(GameContext ctx, string levelName = TestLevel)
    {
        _ctx = ctx;
        _level = LevelData.Load(levelName);
        Populate();
    }

    /// <summary>
    /// The middle of the board in world pixels, so the view opens looking at
    /// the level rather than at a corner of it.
    /// </summary>
    private Point BoardMiddle()
    {
        var blocks = _level.Blocks.Values.ToList();
        if (blocks.Count == 0) return Point.Zero;
        int minX = blocks.Min(b => b.X), maxX = blocks.Max(b => b.X);
        int minY = blocks.Min(b => b.Y), maxY = blocks.Max(b => b.Y);
        return PixelIso.FootOf((minX + maxX) / 2, (minY + maxY) / 2, 0);
    }

    /// <summary>
    /// Puts the cast on the board: the party on the player starts, and an
    /// enemy wherever the level says. The pixel build carries a cut-down cast
    /// on purpose — it only needs enough to look at.
    /// </summary>
    private void Populate()
    {
        var starts = _level.PlayerStarts.ToList();
        var party = new[] { "Gun-O-Mancer", "Werewitch" };
        for (int i = 0; i < party.Length && i < starts.Count; i++)
        {
            string name = party[i];
            string folder = $"Content/Cast/PlayerCharacters/{name}";
            string? state = StateFolder(folder);
            if (state == null)
            {
                _ctx.ReportProblem(folder,
                    $"no pixel art for '{name}' — expected a folder with " +
                    "rotations/south-east.png inside it. Drawing a marker instead.");
                continue;
            }
            _cast.Add(new PixelActor
            {
                Name = name,
                IsPlayer = true,
                Tile = starts[i],
                Sprite = DirectionalSprite.Load(_ctx.Assets, _ctx.ContentIndex, folder, state),
            });
        }
        foreach (var e in _level.Enemies)
            _cast.Add(new PixelActor
            {
                Name = e.Name,
                IsPlayer = false,
                Tile = new Point(e.X, e.Y),
                Cube = _ctx.Assets.LoadTexture("Content/Images/Pixel/GoblinCube.png"),
            });

        foreach (var who in _cast.Where(c => c.Sprite is { HasArt: false }))
            _ctx.ReportProblem($"{who.Sprite!.Folder}/{who.Sprite.State}",
                $"no rotations found for '{who.Name}' — expected " +
                $"rotations/south-east.png and friends. Drawing a marker instead.");
    }

    /// <summary>
    /// The state folder to draw a character from: the first one under their
    /// folder that actually has rotations in it.
    ///
    /// The art tool names that folder after the state, and the state is named
    /// whatever the artist typed — "WitchForm" for one character, the
    /// character's own name for another. Looking for the rotations is the only
    /// thing that holds for both, and it keeps a new export working without a
    /// code change.
    /// </summary>
    private string? StateFolder(string characterFolder) =>
        _ctx.ContentIndex.Folders(characterFolder)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(state =>
                AssetLoader.Exists($"{characterFolder}/{state}/rotations/" +
                    $"{Facings.Default.FileName()}.png"));

    // ---------------- update ----------------

    public void Update(InputState input, float dt)
    {
        if (_noteTimer > 0) _noteTimer -= dt;
        foreach (var a in _cast) a.Advance(dt);

        // whole-step zoom about the cursor, so pixels stay square and the
        // thing under the pointer stays under the pointer
        if (input.ScrollDelta != 0)
        {
            _camera.ZoomBy(Math.Sign(input.ScrollDelta), input.RawPointer);
            Say($"zoom {_camera.Zoom}x — every pixel is {_camera.Zoom} screen pixels square");
        }

        // arrows and WASD scroll, in whole world pixels
        var pan = input.PanDelta;
        if (pan != Vector2.Zero)
            _camera.Scroll(new Point(
                (int)Math.Round(pan.X / _camera.Zoom), (int)Math.Round(pan.Y / _camera.Zoom)));

        if (input.Cancel) _ctx.SwitchTo(new TitleScreen(_ctx));

        if (input.RawTap is Point tap) Click(tap);
        if (input.RawAltTap is Point alt) Shoot(alt);
    }

    /// <summary>Left click picks somebody up, or walks whoever is picked.</summary>
    private void Click(Point screen)
    {
        var tile = PixelIso.GridAt(_camera.ToWorld(screen).ToVector2());
        if (_cast.FirstOrDefault(c => c.Tile == tile) is PixelActor who)
        {
            _picked = who.IsPlayer ? who : _picked;
            if (who.IsPlayer) Say($"{who.Name} picked — click the ground to walk, right-click to shoot");
            return;
        }
        if (_picked == null || _level.BlockAt(tile) == null) return;

        // Walking settles the facing. A step along a grid axis is a screen
        // diagonal, which is why walking only ever ends on one of the four.
        _picked.Facing = Facings.Walking(_picked.Tile, tile, _picked.Facing);
        _picked.Tile = tile;
        Say($"{_picked.Name} walks — now facing {_picked.Facing.FileName()}");
    }

    /// <summary>
    /// Right click is a ranged attack: the shooter turns to face the target
    /// and plays its shooting animation if it has one for that direction.
    ///
    /// This is where the cardinal directions come from. Walking cannot produce
    /// them, because a walk always ends on a screen diagonal; a shot can be
    /// aimed anywhere, so it uses all eight.
    /// </summary>
    private void Shoot(Point screen)
    {
        if (_picked == null) return;
        var tile = PixelIso.GridAt(_camera.ToWorld(screen).ToVector2());
        _picked.Facing = Facings.Towards(_picked.Tile, tile);

        string shot = "GunShot";
        if (_picked.Sprite?.Animation(shot, _picked.Facing) is { Count: > 0 } frames)
        {
            _picked.Play(frames);
            Say($"{_picked.Name} fires {shot} facing {_picked.Facing.FileName()}");
        }
        else
        {
            Say($"{_picked.Name} aims {_picked.Facing.FileName()} — " +
                (_picked.Sprite?.HasAnimation(shot) == true
                    ? $"no {shot} frames drawn for that direction yet"
                    : $"no {shot} animation in this character's folder yet"));
        }
    }

    private void Say(string text)
    {
        _note = text;
        _noteTimer = 4f;
    }

    // ---------------- drawing ----------------

    /// <summary>Never called: this screen draws itself. See DrawSelf.</summary>
    public void Draw(SpriteBatch batch) { }

    public void DrawSelf(SpriteBatch batch, GraphicsDevice device)
    {
        _windowSize = new Point(
            device.PresentationParameters.BackBufferWidth,
            device.PresentationParameters.BackBufferHeight);
        device.Viewport = new Viewport(0, 0, _windowSize.X, _windowSize.Y);

        // the window's real size is only known here, so the opening view waits
        // for the first frame rather than guessing
        if (!_placed)
        {
            _camera.CentreOn(BoardMiddle(), _windowSize.X, _windowSize.Y);
            _placed = true;
        }

        // PointClamp is the whole thing. Linear filtering is what turns pixel
        // art into mush the moment it is drawn at anything but 1:1.
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
            SamplerState.PointClamp, null, null, null, _camera.Matrix);
        DrawGround(batch);
        DrawCast(batch);
        batch.End();

        // the note is UI, drawn straight to the window at a whole-number size
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
        DrawNote(batch);
        batch.End();
    }

    private void DrawGround(SpriteBatch batch)
    {
        var grass = _ctx.Assets.LoadTexture("Content/Images/Pixel/TileGrass.png");
        var stone = _ctx.Assets.LoadTexture("Content/Images/Pixel/TileStone.png");

        foreach (var block in _level.Blocks.Values
                     .OrderBy(b => b.X + b.Y).ThenBy(b => b.X))
        {
            var cell = PixelIso.CellAt(block.X, block.Y, block.Height);
            // the level's block type only decides which of the two placeholder
            // tiles it gets; the pixel build has no palette of its own yet
            var tex = block.Type.Contains("grass", StringComparison.OrdinalIgnoreCase)
                   || block.Type.Contains("green", StringComparison.OrdinalIgnoreCase)
                ? grass : stone;
            batch.Draw(tex, new Rectangle(cell.X, cell.Y, PixelIso.TileW, PixelIso.TileH),
                Color.White);
        }
    }

    private void DrawCast(SpriteBatch batch)
    {
        foreach (var a in _cast.OrderBy(c => c.Tile.X + c.Tile.Y).ThenBy(c => c.Tile.X))
        {
            var foot = FootOf(a);
            var art = a.CurrentFrame();

            if (art == null)
            {
                // no art: a marker rather than nothing, so a missing character
                // is visible on the board and not just in the log
                var box = new Rectangle(foot.X - 8, foot.Y - 24, 16, 24);
                batch.Draw(_ctx.Pixel, box, Color.Magenta);
                continue;
            }

            // Drawn at its own size, always — never stretched to fit a tile.
            // The feet go on the middle of the square by the bottom-centre of
            // the PICTURE, not of the file, so a character exported onto a
            // roomy canvas stands on the ground like everyone else.
            var solid = ArtBounds.Solid(art);
            var at = new Rectangle(
                foot.X - (solid.Left + solid.Right) / 2,
                foot.Y - solid.Bottom,
                art.Width, art.Height);
            batch.Draw(art, at, Color.White);

            if (a == _picked)
                Outline(batch, new Rectangle(at.X, at.Y, at.Width, at.Height), Color.Gold);
        }
    }

    /// <summary>A one-pixel box, which stays one pixel at any zoom.</summary>
    private void Outline(SpriteBatch batch, Rectangle r, Color c)
    {
        batch.Draw(_ctx.Pixel, new Rectangle(r.X, r.Y, r.Width, 1), c);
        batch.Draw(_ctx.Pixel, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), c);
        batch.Draw(_ctx.Pixel, new Rectangle(r.X, r.Y, 1, r.Height), c);
        batch.Draw(_ctx.Pixel, new Rectangle(r.Right - 1, r.Y, 1, r.Height), c);
    }

    /// <summary>
    /// The font is baked for a 3840-wide design space, so it has to be shrunk
    /// hard to read as small text on a real window. It is the one thing here
    /// drawn at a fractional size — it is type, not art, and blocky type at
    /// this size is unreadable rather than characterful.
    /// </summary>
    private const float NotePt = 0.14f;

    private void DrawNote(SpriteBatch batch)
    {
        string help = "wheel zooms  ·  arrows scroll  ·  click a character then the ground  ·  " +
                      "right-click to aim  ·  Esc leaves";
        batch.DrawString(_ctx.Font, help, new Vector2(10, 8), Color.White * 0.55f,
            0f, Vector2.Zero, NotePt, SpriteEffects.None, 0f);
        batch.DrawString(_ctx.Font, $"zoom {_camera.Zoom}x", new Vector2(10, 30),
            Color.White * 0.45f, 0f, Vector2.Zero, NotePt, SpriteEffects.None, 0f);
        if (_noteTimer > 0)
            batch.DrawString(_ctx.Font, _note, new Vector2(10, 52), Color.Gold,
                0f, Vector2.Zero, NotePt * 1.2f, SpriteEffects.None, 0f);
    }

    private static Point FootOf(PixelActor a) => PixelIso.FootOf(a.Tile.X, a.Tile.Y, 0);
}

/// <summary>One body on the pixel board. Deliberately thin: a position, a way of facing, and art.</summary>
public class PixelActor
{
    public string Name = "";
    public bool IsPlayer;
    public Point Tile;
    public Facing8 Facing = Facings.Default;

    /// <summary>Eight-rotation art, for a character that has it.</summary>
    public DirectionalSprite? Sprite;

    /// <summary>A single picture, for a placeholder like the goblin cube.</summary>
    public Texture2D? Cube;

    private IReadOnlyList<Texture2D>? _playing;
    private float _clock;

    /// <summary>Frames a second for a played animation.</summary>
    private const float Fps = 12f;

    public void Play(IReadOnlyList<Texture2D> frames)
    {
        _playing = frames;
        _clock = 0f;
    }

    public void Advance(float dt)
    {
        if (_playing == null) return;
        _clock += dt;
        if (_clock >= _playing.Count / Fps) _playing = null;   // back to standing
    }

    /// <summary>What to draw right now: an animation frame, a rotation, or a cube.</summary>
    public Texture2D? CurrentFrame()
    {
        if (_playing != null)
            return _playing[Math.Clamp((int)(_clock * Fps), 0, _playing.Count - 1)];
        return Sprite?.Rotation(Facing) ?? Cube;
    }
}
