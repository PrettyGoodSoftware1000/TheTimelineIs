using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// Desktop-only; writes Content/Levels/{Level}.txt in the source tree.
///
/// Every tool has a button in the strip across the top AND a hotkey; the two
/// stay in step. Palettes with more than one entry (blocks, decorations,
/// enemies) hang a dropdown off their button instead of spending a button per
/// entry.
///
///   1/2/3 .. block palette   B next block type    hold left  paint
///   D deco  O door  E enemy  P start  G trigger   hold Del   rub out
///   Ctrl+Del                 drag a box, everything inside it goes
///   Ctrl+Z                   undo the last stroke
///   right click a trigger    open that level's dialogue file in a text editor
///   scroll wheel or +/-      placement height (feet)
///   R then typing            set current room label (Enter to accept)
///   N then typing            set the dialogue name new triggers call
///   V then typing            save as a new level name, and keep editing it
///   WASD/arrows              pan     S save     T play-test the level
/// </summary>
public class IsoEditorScreen : IScreen
{
    private enum Tool { Block, Decoration, Door, Enemy, PlayerStart, Trigger }

    /// <summary>Tools you drag across the ground; the rest are one click each.</summary>
    private static bool Paints(Tool t) =>
        t is Tool.Block or Tool.Decoration or Tool.Trigger;

    private readonly GameContext _ctx;
    private LevelData _level;
    private readonly string _levelsDir;
    private string _levelName = "TestLevel";

    private Tool _tool = Tool.Block;
    private int _blockIndex, _decoIndex, _enemyIndex;
    private int _height;
    private string _room = "Main";
    private string _trigger = "Intro";      // dialogue block a placed trigger calls
    private bool _typingRoom, _typingTrigger, _typingSaveAs;
    private string _roomBuffer = "";
    private Vector2 _camera;
    private Vector2 _origin = new(VirtualViewport.Width / 2f, 620);
    private Point _pointer;
    private string _status = "";
    private float _statusTimer;

    // drag state: the tile last painted or rubbed out, so one stroke does each
    // square once instead of once per frame
    private Point? _paintedLast, _erasedLast;
    private bool _strokeOpen;               // an undo snapshot has been taken for this stroke

    // Ctrl+Delete box
    private bool _boxMode;
    private Point? _boxStart, _boxEnd;

    private string? _openMenu;              // which button's dropdown is showing
    private readonly List<LevelData> _undo = new();
    private const int UndoDepth = 40;

    private const int BarY = 30, BarH = 108, BarGap = 12;
    private static readonly Rectangle ToolbarBand = new(0, 0, VirtualViewport.Width, 260);

    public IsoEditorScreen(GameContext ctx)
    {
        _ctx = ctx;
        _level = LevelData.Load(_levelName);
        _levelsDir = FindLevelsDir();
    }

    /// <summary>The repo's Content/Levels, so a save lands in the source tree.</summary>
    private static string FindLevelsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TheTimelineIs.sln")))
                return Path.Combine(dir.FullName, "Content", "Levels");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "Content", "Levels");
    }

    private string SavePath => Path.Combine(_levelsDir, _levelName + ".txt");
    private string DialoguePath => Path.Combine(_levelsDir, _levelName + "Dialogue.txt");

    private void Status(string text) { _status = text; _statusTimer = 4f; }

    // ---------------- undo ----------------

    /// <summary>
    /// Snapshots the level before an edit. A drag stroke calls this once, on its
    /// first square, so one undo takes back the whole stroke rather than one
    /// tile of it.
    /// </summary>
    private void BeginEdit()
    {
        _undo.Add(_level.Clone());
        if (_undo.Count > UndoDepth) _undo.RemoveAt(0);
    }

    private void Undo()
    {
        if (_undo.Count == 0) { Status("nothing to undo"); return; }
        _level.CopyFrom(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        Status($"undo ({_undo.Count} left)");
    }

    // ---------------- toolbar ----------------

    private readonly record struct Btn(Rectangle Rect, string Label, string Id, bool Active, bool Menu);

    private string BlockName => BlockCatalog.BlockTypes.Count > 0
        ? BlockCatalog.BlockTypes[Math.Clamp(_blockIndex, 0, BlockCatalog.BlockTypes.Count - 1)] : "-";
    private string DecoName => BlockCatalog.Decorations.Count > 0
        ? BlockCatalog.Decorations[Math.Clamp(_decoIndex, 0, BlockCatalog.Decorations.Count - 1)] : "-";
    private string EnemyName => _ctx.Enemies.EnemyNames.Count > 0
        ? _ctx.Enemies.EnemyNames[Math.Clamp(_enemyIndex, 0, _ctx.Enemies.EnemyNames.Count - 1)] : "-";

    /// <summary>
    /// The button strip, rebuilt every frame so Update and Draw can never
    /// disagree about where anything is. Widths follow the labels.
    /// </summary>
    private List<Btn> Buttons()
    {
        var list = new List<Btn>();
        int x = 60;

        void Add(string label, string id, bool active, bool menu = false, int pad = 44)
        {
            int w = (int)(_ctx.Font.MeasureString(label).X * 0.30f) + pad + (menu ? 34 : 0);
            list.Add(new Btn(new Rectangle(x, BarY, w, BarH), label, id, active, menu));
            x += w + BarGap;
        }

        Add($"Block: {BlockName}", "block", _tool == Tool.Block, menu: true);
        Add($"Deco: {Strip(DecoName)}", "deco", _tool == Tool.Decoration, menu: true);
        Add("Door", "door", _tool == Tool.Door);
        Add($"Enemy: {EnemyName}", "enemy", _tool == Tool.Enemy, menu: true);
        Add("Start", "start", _tool == Tool.PlayerStart);
        Add($"Trigger: {_trigger}", "trigger", _tool == Tool.Trigger);
        Add("-", "hminus", false, pad: 30);
        Add($"{_height} ft", "height", false, pad: 30);
        Add("+", "hplus", false, pad: 30);
        Add($"Room: {_room}", "room", false);
        Add("Dialogue", "dialogue", false);
        Add("Undo", "undo", false);
        Add("Save", "save", false);
        Add("Save As", "saveas", false);
        Add("Test", "test", false);
        return list;
    }

    private static string Strip(string file) =>
        file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;

    /// <summary>The entries a dropdown shows, and what picking one does.</summary>
    private List<string> MenuItems(string id) => id switch
    {
        "block" => BlockCatalog.BlockTypes.ToList(),
        "deco" => BlockCatalog.Decorations.Select(Strip).ToList(),
        "enemy" => _ctx.Enemies.EnemyNames.ToList(),
        _ => new List<string>(),
    };

    private List<Rectangle> MenuRects(string id)
    {
        var btn = Buttons().FirstOrDefault(b => b.Id == id);
        var items = MenuItems(id);
        var rects = new List<Rectangle>();
        for (int i = 0; i < items.Count; i++)
            rects.Add(new Rectangle(btn.Rect.X, btn.Rect.Bottom + 6 + i * (BarH - 12),
                Math.Max(btn.Rect.Width, 460), BarH - 14));
        return rects;
    }

    private void PickMenu(string id, int index)
    {
        switch (id)
        {
            case "block": _blockIndex = index; _tool = Tool.Block; break;
            case "deco": _decoIndex = index; _tool = Tool.Decoration; break;
            case "enemy": _enemyIndex = index; _tool = Tool.Enemy; break;
        }
        _openMenu = null;
    }

    /// <summary>A toolbar button was clicked. Returns true if it was handled.</summary>
    private bool HitButton(string id)
    {
        switch (id)
        {
            case "block" or "deco" or "enemy":
                // the button both selects its tool and opens its palette
                _tool = id == "block" ? Tool.Block : id == "deco" ? Tool.Decoration : Tool.Enemy;
                _openMenu = _openMenu == id ? null : id;
                return true;
            case "door": _tool = Tool.Door; return true;
            case "start": _tool = Tool.PlayerStart; return true;
            case "trigger": _tool = Tool.Trigger; return true;
            case "hminus": _height = Math.Max(_height - 1, 0); return true;
            case "hplus": _height = Math.Min(_height + 1, 12); return true;
            case "height": return true;
            case "room": _typingRoom = true; _roomBuffer = _room; return true;
            case "dialogue": _typingTrigger = true; _roomBuffer = _trigger; return true;
            case "undo": Undo(); return true;
            case "save": Save(); return true;
            case "saveas": _typingSaveAs = true; _roomBuffer = _levelName; return true;
            case "test": PlayTest(); return true;
        }
        return false;
    }

    // ---------------- update ----------------

    public void Update(InputState input, float dt)
    {
        _pointer = input.PointerPos;
        _camera += input.PanDelta;
        if (_statusTimer > 0) _statusTimer -= dt;

        if (_typingRoom || _typingTrigger || _typingSaveAs) { UpdateTyping(input); return; }

        var origin = _origin - _camera;

        if (input.CtrlHeld && input.Delete) { _boxMode = true; _boxStart = _boxEnd = null; }
        if (input.Undo) Undo();

        // a dropdown swallows the next click wherever it lands
        if (_openMenu != null && UpdateMenu(input)) return;

        if (input.Tap is Point tap && ToolbarBand.Contains(tap))
        {
            foreach (var b in Buttons())
                if (b.Rect.Contains(tap) && HitButton(b.Id))
                    return;
            return;   // clicks on the bar never fall through to the ground
        }

        UpdateHotkeys(input);
        _height = Math.Clamp(_height + input.ScrollDelta, 0, 12);

        if (_boxMode) { UpdateBox(input, origin); return; }

        UpdatePaint(input, origin);
        UpdateErase(input, origin);

        // right-clicking a dialogue square opens that level's dialogue file
        if (input.AltTap is Point rc && !ToolbarBand.Contains(rc))
        {
            var t = PickTile(rc.ToVector2(), origin) ?? IsoMath.ToGrid(rc.ToVector2(), origin);
            if (_level.TriggerAt(t) is LevelTrigger trig) OpenDialogueFile(trig.Dialogue);
        }
    }

    private void UpdateTyping(InputState input)
    {
        _roomBuffer += input.TypedChars;
        if (input.Backspace && _roomBuffer.Length > 0) _roomBuffer = _roomBuffer[..^1];
        if (input.Cancel) _typingRoom = _typingTrigger = _typingSaveAs = false;
        if (input.Submit && _roomBuffer.Trim().Length > 0)
        {
            if (_typingRoom) { _room = _roomBuffer.Trim(); Status($"room = {_room}"); }
            else if (_typingSaveAs) SaveAs(_roomBuffer);
            else
            {
                _trigger = _roomBuffer.Trim();
                _tool = Tool.Trigger;
                Status($"trigger dialogue = {_trigger}");
            }
            _typingRoom = _typingTrigger = _typingSaveAs = false;
        }
    }

    /// <summary>Returns true when the click belonged to the open dropdown.</summary>
    private bool UpdateMenu(InputState input)
    {
        if (input.Cancel || input.AltTap.HasValue) { _openMenu = null; return true; }
        if (input.Tap is not Point tap) return false;

        var rects = MenuRects(_openMenu!);
        for (int i = 0; i < rects.Count; i++)
            if (rects[i].Contains(tap)) { PickMenu(_openMenu!, i); return true; }

        // a click anywhere else just dismisses it, without also placing a tile
        _openMenu = null;
        return true;
    }

    private void UpdateHotkeys(InputState input)
    {
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
                case 'v': _typingSaveAs = true; _roomBuffer = _levelName; break;
                case 't': PlayTest(); break;
            }
    }

    /// <summary>
    /// Holding the left button paints continuously: each square the cursor
    /// crosses is placed once, and the whole stroke is a single undo step.
    /// Tools where repeats are meaningless (doors, enemies, starts) stay on
    /// one click each.
    /// </summary>
    private void UpdatePaint(InputState input, Vector2 origin)
    {
        if (!input.PointerHeld)
        {
            _paintedLast = null;
            if (!input.DeleteHeld) _strokeOpen = false;
            return;
        }
        if (ToolbarBand.Contains(_pointer)) return;

        var tile = Target(_pointer.ToVector2(), origin).Tile;
        if (!Paints(_tool))
        {
            // one-shot tools act on the press frame only
            if (input.Tap.HasValue) { BeginEdit(); Place(tile); }
            return;
        }
        if (_paintedLast == tile) return;
        if (!_strokeOpen) { BeginEdit(); _strokeOpen = true; }
        _paintedLast = tile;
        Place(tile);
    }

    /// <summary>Holding Delete rubs out everything the cursor crosses.</summary>
    private void UpdateErase(InputState input, Vector2 origin)
    {
        if (!input.DeleteHeld)
        {
            _erasedLast = null;
            if (!input.PointerHeld) _strokeOpen = false;
            return;
        }
        if (ToolbarBand.Contains(_pointer)) return;

        var tile = PickTile(_pointer.ToVector2(), origin)
                   ?? IsoMath.ToGrid(_pointer.ToVector2(), origin);
        if (_erasedLast == tile) return;
        if (!_strokeOpen) { BeginEdit(); _strokeOpen = true; }
        _erasedLast = tile;
        Delete(tile);
    }

    /// <summary>
    /// Ctrl+Delete arms a box: drag one out and everything inside goes at once.
    /// The cursor returns to normal as soon as the box is drawn.
    /// </summary>
    private void UpdateBox(InputState input, Vector2 origin)
    {
        if (input.Cancel || input.AltTap.HasValue)
        {
            _boxMode = false; _boxStart = _boxEnd = null;
            Status("box delete cancelled");
            return;
        }

        var under = PickTile(_pointer.ToVector2(), origin)
                    ?? IsoMath.ToGrid(_pointer.ToVector2(), origin);
        if (input.Tap.HasValue && !ToolbarBand.Contains(_pointer)) _boxStart = under;
        if (_boxStart != null) _boxEnd = under;

        if (input.Released.HasValue && _boxStart is Point a && _boxEnd is Point b)
        {
            BeginEdit();
            int x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X);
            int y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
            int n = 0;
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    n += DeleteAll(new Point(x, y));
            _boxMode = false; _boxStart = _boxEnd = null;
            Status($"box deleted {n} thing(s)");
        }
    }

    // ---------------- the level ----------------

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
                break;
            case Tool.PlayerStart when _level.BlockAt(tile) != null:
                _level.PlayerStarts.Remove(tile);
                _level.PlayerStarts.Add(tile);
                while (_level.PlayerStarts.Count > 4) _level.PlayerStarts.RemoveAt(0);
                break;
        }
    }

    /// <summary>Rubs out the topmost thing on a square: contents first, block last.</summary>
    private void Delete(Point tile)
    {
        if (_level.Decorations.RemoveAll(d => d.X == tile.X && d.Y == tile.Y) > 0) return;
        if (_level.Doors.RemoveAll(d => d.X == tile.X && d.Y == tile.Y) > 0) return;
        if (_level.Enemies.RemoveAll(e => e.X == tile.X && e.Y == tile.Y) > 0) return;
        if (_level.Triggers.RemoveAll(t => t.X == tile.X && t.Y == tile.Y) > 0) return;
        if (_level.PlayerStarts.Remove(tile)) return;
        _level.Blocks.Remove(tile);
    }

    /// <summary>Everything on a square at once — what the box delete does.</summary>
    private int DeleteAll(Point tile)
    {
        int n = 0;
        n += _level.Decorations.RemoveAll(d => d.X == tile.X && d.Y == tile.Y);
        n += _level.Doors.RemoveAll(d => d.X == tile.X && d.Y == tile.Y);
        n += _level.Enemies.RemoveAll(e => e.X == tile.X && e.Y == tile.Y);
        n += _level.Triggers.RemoveAll(t => t.X == tile.X && t.Y == tile.Y);
        if (_level.PlayerStarts.Remove(tile)) n++;
        if (_level.Blocks.Remove(tile)) n++;
        return n;
    }

    // ---------------- files ----------------

    private void Save()
    {
        Directory.CreateDirectory(_levelsDir);
        File.WriteAllText(SavePath, _level.Serialize());
        Status($"saved -> {SavePath}");
    }

    /// <summary>
    /// Save As: writes the level under a new name and keeps editing THAT file,
    /// so plain S from then on saves the copy and the original is left as it
    /// was. The name is a bare level name — no folders, no extension — because
    /// levels are loaded by name from Content/Levels.
    /// </summary>
    private void SaveAs(string typed)
    {
        string name = typed.Trim();
        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        // a level name is used to build a path and a dialogue filename, so it
        // has to stay a plain name
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\'))
        {
            Status($"'{typed.Trim()}' is not a usable level name — letters and digits, no folders");
            return;
        }
        _levelName = name;
        _level.Name = name;
        Save();
    }

    /// <summary>
    /// Opens this level's dialogue file in whatever the OS uses for .txt. If
    /// the file doesn't exist yet, or doesn't contain the block the trigger
    /// names, the missing block is stubbed in first — so right-clicking a fresh
    /// trigger square lands you on the lines you need to write.
    /// </summary>
    private void OpenDialogueFile(string blockName)
    {
        try
        {
            Directory.CreateDirectory(_levelsDir);
            string text = File.Exists(DialoguePath) ? File.ReadAllText(DialoguePath) : "";
            if (text.Length == 0)
                text = $"# Dialogue for {_levelName}. A trigger square placed in the editor (G tool)\n" +
                       "# names one of these blocks; stepping on it plays the block once.\n" +
                       "# Format: \"Speaker: text\", one line each.\n";

            bool present = text.Split('\n').Any(l =>
                l.TrimStart().StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) &&
                l.Split(':', 2)[1].Trim().Equals(blockName, StringComparison.OrdinalIgnoreCase));
            if (!present)
            {
                text = text.TrimEnd() + $"\n\nDialogue: {blockName}\nDirtbag: (write this scene)\n";
                Status($"added an empty '{blockName}' block and opened the file");
            }
            else Status($"opened {Path.GetFileName(DialoguePath)} at '{blockName}'");

            File.WriteAllText(DialoguePath, text);
            Process.Start(new ProcessStartInfo(DialoguePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status($"could not open {DialoguePath}: {ex.Message}");
        }
    }

    private void PlayTest()
    {
        Save();
        _ctx.State.Reset(_ctx.State.PartyOrDefault());
        _ctx.SwitchTo(new IsoLevelScreen(_ctx, _levelName));
    }

    // ---------------- drawing ----------------

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
                if (_ctx.Enemies.Get(e.Name) is EnemyDef def && def.SpriteFiles.Count > 0)
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
            if (InBox(tile))
                DrawTop(batch, b.X, b.Y, b.Height, origin, Color.Red * 0.45f);
        }

        DrawCursor(batch, origin);
        DrawToolbar(batch);
        DrawHudText(batch);
        DrawRoomLabels(batch, origin);
    }

    private bool InBox(Point tile) =>
        _boxStart is Point a && _boxEnd is Point b &&
        tile.X >= Math.Min(a.X, b.X) && tile.X <= Math.Max(a.X, b.X) &&
        tile.Y >= Math.Min(a.Y, b.Y) && tile.Y <= Math.Max(a.Y, b.Y);

    private void DrawCursor(SpriteBatch batch, Vector2 origin)
    {
        if (ToolbarBand.Contains(_pointer)) return;

        // the box tool has its own cursor: red, and it shows the span it covers
        if (_boxMode)
        {
            var at = PickTile(_pointer.ToVector2(), origin)
                     ?? IsoMath.ToGrid(_pointer.ToVector2(), origin);
            int h = _level.BlockAt(at)?.Height ?? 0;
            DrawTop(batch, at.X, at.Y, h, origin, Color.Red * 0.55f);
            return;
        }

        // hovered cell, sitting at the height it would actually place at, with
        // that height written in the middle so it can be read against the blocks
        var (cursor, cursorHeight) = Target(_pointer.ToVector2(), origin);
        DrawTop(batch, cursor.X, cursor.Y, cursorHeight, origin, Color.Yellow * 0.3f);
        var mid = IsoMath.ToScreen(cursor.X, cursor.Y, cursorHeight, origin);
        Ui.DrawTextCentered(batch, _ctx.Font, cursorHeight.ToString(),
            new Rectangle((int)(mid.X - IsoMath.TileW / 2f), (int)(mid.Y - IsoMath.TileH / 2f),
                IsoMath.TileW, IsoMath.TileH), Color.Yellow, 0.36f);
    }

    private void DrawToolbar(SpriteBatch batch)
    {
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, BarY + BarH + 18), new Color(14, 14, 20, 235));

        foreach (var b in Buttons())
        {
            bool hot = b.Rect.Contains(_pointer);
            Ui.FillRect(batch, _ctx.Pixel, b.Rect,
                b.Active ? new Color(120, 96, 20) : hot ? new Color(52, 52, 66) : new Color(32, 32, 42));
            var edge = b.Active ? Color.Yellow : hot ? Color.White : Color.White * 0.35f;
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(b.Rect.X, b.Rect.Y, b.Rect.Width, 3), edge);
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(b.Rect.X, b.Rect.Bottom - 3, b.Rect.Width, 3), edge);
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(b.Rect.X, b.Rect.Y, 3, b.Rect.Height), edge);
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(b.Rect.Right - 3, b.Rect.Y, 3, b.Rect.Height), edge);
            Ui.DrawTextCentered(batch, _ctx.Font, b.Label,
                b.Menu ? new Rectangle(b.Rect.X, b.Rect.Y, b.Rect.Width - 30, b.Rect.Height) : b.Rect,
                b.Active ? Color.White : Color.White * 0.9f, 0.30f);

            // a little triangle marks the buttons that drop a palette down
            if (!b.Menu) continue;
            int cx = b.Rect.Right - 22, cy = b.Rect.Center.Y - 4;
            for (int r = 0; r < 10; r++)
                Ui.FillRect(batch, _ctx.Pixel,
                    new Rectangle(cx - 10 + r, cy + r, 21 - r * 2, 1), Color.White * 0.8f);
        }

        if (_openMenu == null) return;
        var items = MenuItems(_openMenu);
        var rects = MenuRects(_openMenu);
        for (int i = 0; i < items.Count; i++)
        {
            bool hot = rects[i].Contains(_pointer);
            Ui.FillRect(batch, _ctx.Pixel, rects[i],
                hot ? new Color(70, 70, 90, 250) : new Color(26, 26, 34, 250));
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(rects[i].X, rects[i].Y, rects[i].Width, 2), Color.White * 0.3f);
            Ui.DrawTextCentered(batch, _ctx.Font, items[i], rects[i], Color.White, 0.30f);
        }
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
        int y = BarY + BarH + 26;
        string mode = _boxMode ? "  BOX DELETE: drag a box, Esc to cancel" : "";
        batch.DrawString(_ctx.Font,
            $"{_levelName}.txt   room: {_room}   blocks: {_level.Blocks.Count}{mode}",
            new Vector2(60, y), _boxMode ? Color.Red : Color.Yellow,
            0f, Vector2.Zero, 0.32f, SpriteEffects.None, 0f);
        batch.DrawString(_ctx.Font,
            "hold left: paint   hold Del: erase   Ctrl+Del: box delete   Ctrl+Z: undo   " +
            "right-click a trigger: open its dialogue file   scroll: height   WASD: pan",
            new Vector2(60, y + 46), Color.White * 0.6f,
            0f, Vector2.Zero, 0.26f, SpriteEffects.None, 0f);

        if (_typingRoom || _typingTrigger || _typingSaveAs)
            batch.DrawString(_ctx.Font,
                (_typingRoom ? "room name: " : _typingSaveAs ? "save as: " : "dialogue name: ")
                + _roomBuffer + "_",
                new Vector2(60, y + 100), Color.Cyan, 0f, Vector2.Zero, 0.4f, SpriteEffects.None, 0f);
        else if (_statusTimer > 0)
            batch.DrawString(_ctx.Font, _status, new Vector2(60, y + 100), Color.LightGreen,
                0f, Vector2.Zero, 0.32f, SpriteEffects.None, 0f);
    }
}
