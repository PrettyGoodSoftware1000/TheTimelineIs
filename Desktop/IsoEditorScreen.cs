using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using TheTimelineIs.Core;
using TheTimelineIs.Core.Data;
using TheTimelineIs.Core.Input;
using TheTimelineIs.Core.Iso;
using TheTimelineIs.Core.Render;
using TheTimelineIs.Core.Screens;

namespace TheTimelineIs.Desktop;

/// <summary>
/// The isometric level editor (dotnet run --project Desktop -- --editor).
/// Desktop-only; writes Content/Levels/TestLevel.txt in the source tree.
///
///   1/2/3 .. block palette   B next block type    left click  place
///   D deco  O door  E enemy  P start  G trigger    Delete key  erase
///   scroll wheel or +/-      placement height (feet)
///   R then typing            set current room label (Enter to accept)
///   N then typing            set the dialogue name new triggers call
///   WASD/arrows              pan     S save     F5 play-test the level
/// </summary>
public class IsoEditorScreen : IScreen
{
    private enum Tool { Block, Decoration, Door, Enemy, PlayerStart, Trigger }

    private readonly GameContext _ctx;
    private readonly LevelData _level;
    private readonly string _savePath;

    private Tool _tool = Tool.Block;
    private int _blockIndex, _decoIndex, _enemyIndex;
    private int _height;
    private string _room = "Main";
    private string _trigger = "Intro";      // dialogue block a placed trigger calls
    private bool _typingRoom, _typingTrigger;
    private string _roomBuffer = "";
    private Vector2 _camera;
    private Vector2 _origin = new(VirtualViewport.Width / 2f, 500);
    private Point _pointer;
    private string _status = "";
    private float _statusTimer;

    public IsoEditorScreen(GameContext ctx)
    {
        _ctx = ctx;
        _level = LevelData.Load("TestLevel");
        _savePath = FindSavePath();
    }

    private static string FindSavePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheTimelineIs.sln")))
                return Path.Combine(dir.FullName, "Content", "Levels", "TestLevel.txt");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "Content", "Levels", "TestLevel.txt");
    }

    private void Status(string text) { _status = text; _statusTimer = 3f; }

    public void Update(InputState input, float dt)
    {
        _pointer = input.PointerPos;
        _camera += input.PanDelta;
        if (_statusTimer > 0) _statusTimer -= dt;

        if (_typingRoom || _typingTrigger)
        {
            _roomBuffer += input.TypedChars;
            if (input.Backspace && _roomBuffer.Length > 0) _roomBuffer = _roomBuffer[..^1];
            if (input.Cancel) { _typingRoom = _typingTrigger = false; }
            if (input.Submit && _roomBuffer.Trim().Length > 0)
            {
                if (_typingRoom) { _room = _roomBuffer.Trim(); Status($"room = {_room}"); }
                else { _trigger = _roomBuffer.Trim(); Status($"trigger dialogue = {_trigger}"); }
                _typingRoom = _typingTrigger = false;
            }
            return;
        }

        foreach (char c in input.TypedChars.ToLowerInvariant())
            switch (c)
            {
                case '1' or '2' or '3':
                    _tool = Tool.Block;
                    _blockIndex = Math.Min(c - '1', Math.Max(0, BlockCatalog.BlockTypes.Count - 1));
                    break;
                case 'b':
                    _tool = Tool.Block;
                    if (BlockCatalog.BlockTypes.Count > 0)
                        _blockIndex = (_blockIndex + 1) % BlockCatalog.BlockTypes.Count;
                    break;
                case 'd':
                    _tool = Tool.Decoration;
                    if (BlockCatalog.Decorations.Count > 0)
                        _decoIndex = (_decoIndex + 1) % BlockCatalog.Decorations.Count;
                    break;
                case 'o': _tool = Tool.Door; break;
                case 'e':
                    _tool = Tool.Enemy;
                    if (_ctx.Enemies.EnemyNames.Count > 0)
                        _enemyIndex = (_enemyIndex + 1) % _ctx.Enemies.EnemyNames.Count;
                    break;
                case 'p': _tool = Tool.PlayerStart; break;
                case 'g': _tool = Tool.Trigger; break;
                case 'r': _typingRoom = true; _roomBuffer = _room; break;
                case 'n': _typingTrigger = true; _roomBuffer = _trigger; break;
                case '+' or '=': _height = Math.Min(_height + 1, 12); break;
                case '-': _height = Math.Max(_height - 1, 0); break;
                case 's': Save(); break;
                case 't': PlayTest(); break;
            }

        _height = Math.Clamp(_height + input.ScrollDelta, 0, 12);

        var origin = _origin - _camera;
        if (input.Tap is Point place)
            Place(Target(place.ToVector2(), origin).Tile);
        // right-drag pans the view, so erasing is the Delete key at the cursor
        if (input.Delete)
            Delete(PickTile(_pointer.ToVector2(), origin)
                   ?? IsoMath.ToGrid(_pointer.ToVector2(), origin));
    }

    private Point? PickTile(Vector2 screen, Vector2 origin)
    {
        foreach (var b in _level.Blocks.Values.OrderByDescending(b => b.X + b.Y))
            if (IsoMath.HitsTop(screen, b.X, b.Y, b.Height, origin))
                return new Point(b.X, b.Y);
        return null;
    }

    /// <summary>
    /// The cell under the cursor, and the height the cursor square should be
    /// drawn at. A block goes down at the placement height over the flat grid;
    /// everything else lands ON an existing block, so it has to pick that
    /// block's raised top rather than reading the ground plane underneath it.
    /// </summary>
    private (Point Tile, int Height) Target(Vector2 screen, Vector2 origin)
    {
        var flat = IsoMath.ToGrid(screen, origin);
        if (_tool == Tool.Block) return (flat, _height);
        if (PickTile(screen, origin) is Point picked)
            return (picked, _level.BlockAt(picked)?.Height ?? 0);
        return (flat, 0);
    }

    private void Place(Point tile)
    {
        switch (_tool)
        {
            case Tool.Block:
                string type = BlockCatalog.BlockTypes.Count > 0
                    ? BlockCatalog.BlockTypes[_blockIndex] : "Grass";
                _level.Blocks[tile] = new LevelBlock
                    { X = tile.X, Y = tile.Y, Height = _height, Type = type, Room = _room };
                break;
            case Tool.Decoration when _level.BlockAt(tile) != null && BlockCatalog.Decorations.Count > 0:
                _level.Decorations.RemoveAll(d => d.X == tile.X && d.Y == tile.Y);
                _level.Decorations.Add(new LevelDecoration
                    { X = tile.X, Y = tile.Y, File = BlockCatalog.Decorations[_decoIndex] });
                break;
            case Tool.Door when _level.BlockAt(tile) is LevelBlock db:
                _level.Doors.RemoveAll(d => d.X == tile.X && d.Y == tile.Y);
                // the door joins the block's own room to the editor's current room
                _level.Doors.Add(new LevelDoor
                    { X = tile.X, Y = tile.Y, RoomA = db.Room, RoomB = _room });
                Status($"door joins {db.Room} <-> {_room}");
                break;
            case Tool.Enemy when _level.BlockAt(tile) != null && _ctx.Enemies.EnemyNames.Count > 0:
                _level.Enemies.RemoveAll(e => e.X == tile.X && e.Y == tile.Y);
                _level.Enemies.Add(new LevelEnemy
                    { X = tile.X, Y = tile.Y, Name = _ctx.Enemies.EnemyNames[_enemyIndex] });
                break;
            case Tool.Trigger when _level.BlockAt(tile) != null:
                _level.Triggers.RemoveAll(t => t.X == tile.X && t.Y == tile.Y);
                _level.Triggers.Add(new LevelTrigger { X = tile.X, Y = tile.Y, Dialogue = _trigger });
                Status($"trigger -> {_trigger}");
                break;
            case Tool.PlayerStart when _level.BlockAt(tile) != null:
                _level.PlayerStarts.Remove(tile);
                _level.PlayerStarts.Add(tile);
                while (_level.PlayerStarts.Count > 4) _level.PlayerStarts.RemoveAt(0);
                break;
        }
    }

    private void Delete(Point tile)
    {
        if (_level.Decorations.RemoveAll(d => d.X == tile.X && d.Y == tile.Y) > 0) return;
        if (_level.Doors.RemoveAll(d => d.X == tile.X && d.Y == tile.Y) > 0) return;
        if (_level.Enemies.RemoveAll(e => e.X == tile.X && e.Y == tile.Y) > 0) return;
        if (_level.Triggers.RemoveAll(t => t.X == tile.X && t.Y == tile.Y) > 0) return;
        if (_level.PlayerStarts.Remove(tile)) return;
        _level.Blocks.Remove(tile);
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_savePath)!);
        File.WriteAllText(_savePath, _level.Serialize());
        Status($"saved -> {_savePath}");
    }

    private void PlayTest()
    {
        Save();
        _ctx.State.Reset(_ctx.State.PartyOrDefault());
        _ctx.SwitchTo(new IsoLevelScreen(_ctx, "TestLevel"));
    }

    public void Draw(SpriteBatch batch)
    {
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height), Color.Black);
        var origin = _origin - _camera;

        // faint base grid so there's something to aim at over the void
        var hover = IsoMath.ToGrid(_pointer.ToVector2(), origin);
        for (int gx = hover.X - 14; gx <= hover.X + 14; gx++)
            for (int gy = hover.Y - 14; gy <= hover.Y + 14; gy++)
                if (!_level.Blocks.ContainsKey(new Point(gx, gy)))
                    DrawTop(batch, gx, gy, 0, origin, Color.White * 0.06f);

        foreach (var b in _level.Blocks.Values.OrderBy(b => b.X + b.Y).ThenBy(b => b.X))
        {
            var top = IsoMath.ToScreen(b.X, b.Y, b.Height, origin);
            var side = _ctx.Assets.LoadTexture(BlockCatalog.SidePath(b.Type));
            for (int f = 0; f < b.Height; f++)
                batch.Draw(side, new Rectangle((int)(top.X - IsoMath.TileW / 2f),
                    (int)(top.Y + f * IsoMath.FootPx), IsoMath.TileW, IsoMath.FootPx), Color.White);
            batch.Draw(_ctx.Assets.LoadTexture(BlockCatalog.TopPath(b.Type)),
                new Rectangle((int)(top.X - IsoMath.TileW / 2f), (int)(top.Y - IsoMath.TileH / 2f),
                    IsoMath.TileW, IsoMath.TileH), Color.White);

            var tile = new Point(b.X, b.Y);
            if (_level.DoorAt(tile) != null)
                Billboard(batch, "Content/Images/Decorations/Door.png", tile, b.Height, origin, Color.White);
            if (_level.DecorationAt(tile) is LevelDecoration deco)
                Billboard(batch, BlockCatalog.DecorationPath(deco.File), tile, b.Height, origin, Color.White);
            foreach (var e in _level.Enemies.Where(e => e.X == b.X && e.Y == b.Y))
                if (_ctx.Enemies.Get(e.Name) is EnemyDef def)
                    Billboard(batch, $"{def.Folder}/{def.SpriteFiles[0]}", tile, b.Height, origin, Color.White * 0.9f);
            if (_level.PlayerStarts.Contains(tile))
                DrawTop(batch, b.X, b.Y, b.Height, origin, Color.LimeGreen * 0.35f);
            if (_level.TriggerAt(tile) is LevelTrigger trig)
            {
                DrawTop(batch, b.X, b.Y, b.Height, origin, Color.Violet * 0.4f);
                var tc = IsoMath.ToScreen(b.X, b.Y, b.Height, origin);
                batch.DrawString(_ctx.Font, trig.Dialogue, new Vector2(tc.X - 70, tc.Y - 40),
                    Color.Violet, 0f, Vector2.Zero, 0.26f, SpriteEffects.None, 0f);
            }
        }

        // hovered cell, sitting at the height it would actually place at, with
        // that height written in the middle so it can be read against the blocks
        var (cursor, cursorHeight) = Target(_pointer.ToVector2(), origin);
        DrawTop(batch, cursor.X, cursor.Y, cursorHeight, origin, Color.Yellow * 0.3f);
        var mid = IsoMath.ToScreen(cursor.X, cursor.Y, cursorHeight, origin);
        Ui.DrawTextCentered(batch, _ctx.Font, cursorHeight.ToString(),
            new Rectangle((int)(mid.X - IsoMath.TileW / 2f), (int)(mid.Y - IsoMath.TileH / 2f),
                IsoMath.TileW, IsoMath.TileH), Color.Yellow, 0.36f);

        DrawHudText(batch);
        DrawRoomLabels(batch, origin);
    }

    private void DrawTop(SpriteBatch batch, int gx, int gy, int height, Vector2 origin, Color tint)
    {
        var c = IsoMath.ToScreen(gx, gy, height, origin);
        batch.Draw(_ctx.Assets.LoadTexture("Content/Images/Blocks/OverlayTop.png"),
            new Rectangle((int)(c.X - IsoMath.TileW / 2f), (int)(c.Y - IsoMath.TileH / 2f),
                IsoMath.TileW, IsoMath.TileH), tint);
    }

    private void Billboard(SpriteBatch batch, string path, Point tile, int height, Vector2 origin, Color tint)
    {
        var tex = _ctx.Assets.LoadTexture(path);
        var c = IsoMath.ToScreen(tile.X, tile.Y, height, origin);
        int w = Math.Min(tex.Width, 420);
        int h = (int)(w * tex.Height / (float)tex.Width);
        batch.Draw(tex, new Rectangle((int)(c.X - w / 2f), (int)(c.Y + 30 - h), w, h), tint);
    }

    private void DrawRoomLabels(SpriteBatch batch, Vector2 origin)
    {
        foreach (var group in _level.Blocks.Values.GroupBy(b => b.Room, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.OrderBy(b => b.X + b.Y).First();
            var c = IsoMath.ToScreen(first.X, first.Y, first.Height, origin);
            batch.DrawString(_ctx.Font, group.Key, new Vector2(c.X - 60, c.Y - 220),
                Color.Cyan * 0.8f, 0f, Vector2.Zero, 0.32f, SpriteEffects.None, 0f);
        }
    }

    private void DrawHudText(SpriteBatch batch)
    {
        // editor is a dev tool: literal strings, not Strings.txt
        string block = BlockCatalog.BlockTypes.Count > 0 ? BlockCatalog.BlockTypes[_blockIndex] : "-";
        string deco = BlockCatalog.Decorations.Count > 0 ? BlockCatalog.Decorations[_decoIndex] : "-";
        string enemy = _ctx.Enemies.EnemyNames.Count > 0 ? _ctx.Enemies.EnemyNames[_enemyIndex] : "-";
        string tool = _tool switch
        {
            Tool.Block => $"BLOCK {block}",
            Tool.Decoration => $"DECO {deco}",
            Tool.Door => $"DOOR -> room {_room}",
            Tool.Enemy => $"ENEMY {enemy}",
            Tool.Trigger => $"TRIGGER -> {_trigger}",
            _ => "PLAYER START",
        };
        string line1 = $"EDITOR   tool: {tool}   height: {_height} ft   room: {_room}";
        string line2 = "1-3/B blocks  D deco  O door  E enemy  P start  G trigger  R room  N dialogue  " +
                       "scroll/+- height  click place  DEL erase  S save  T test";
        batch.DrawString(_ctx.Font, line1, new Vector2(60, 40), Color.Yellow,
            0f, Vector2.Zero, 0.4f, SpriteEffects.None, 0f);
        batch.DrawString(_ctx.Font, line2, new Vector2(60, 120), Color.White * 0.75f,
            0f, Vector2.Zero, 0.3f, SpriteEffects.None, 0f);
        if (_typingRoom || _typingTrigger)
            batch.DrawString(_ctx.Font,
                (_typingRoom ? "room name: " : "dialogue name: ") + _roomBuffer + "_",
                new Vector2(60, 200), Color.Cyan, 0f, Vector2.Zero, 0.4f, SpriteEffects.None, 0f);
        if (_statusTimer > 0)
            batch.DrawString(_ctx.Font, _status, new Vector2(60, 280), Color.LightGreen,
                0f, Vector2.Zero, 0.34f, SpriteEffects.None, 0f);
    }
}
