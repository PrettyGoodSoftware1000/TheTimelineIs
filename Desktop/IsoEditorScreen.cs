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
/// The full control list lives in ControlLines, shown in the middle of the
/// screen by the Controls button or the Insert key and hidden by default —
/// the strip across the top is what is on screen the rest of the time.
/// </summary>
public partial class IsoEditorScreen : IScreen
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
    private int _decoIndex, _enemyIndex;

    // the block brush: which family, and which piece inside it. -1 = Random,
    // which is settled per square as it is painted.
    private int _familyIndex;
    private int _pieceIndex;
    private static readonly Random Rng = new();
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

    // box drags. _boxMode is armed by Ctrl+Delete; the other two read their
    // modifier live, so they need no arming
    private bool _boxMode;
    private Point? _boxStart, _boxEnd;
    private Point? _fillStart, _fillEnd;      // Shift+drag: box place
    private Point? _selStart, _selEnd;        // Ctrl+drag: a selection that sticks
    private Point? _selA, _selB;              // the committed selection
    private List<LevelBlock> _clipboard = new();
    private Point _clipOrigin;

    private bool _showControls;             // the Controls panel, off by default
    private string? _openMenu;              // which button's dropdown is showing
    private readonly List<LevelData> _undo = new();
    private const int UndoDepth = 40;

    private int _problems;                    // validator complaints about this level
    private const int BarY = 24, BarH = 84, BarGap = 10;
    /// <summary>Text size on the buttons, and the padding around it.</summary>
    private const float BtnText = 0.24f;
    private const int BtnPad = 30, BtnArrow = 26, BarX0 = 40;
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

    /// <summary>Rescans for problems a few times a second rather than every frame.</summary>
    private float _problemTimer;

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

    private string FamilyName => BlockCatalog.Families.Count > 0
        ? BlockCatalog.Families[Math.Clamp(_familyIndex, 0, BlockCatalog.Families.Count - 1)] : "-";

    private IReadOnlyList<GroundPiece> FamilyPieces => BlockCatalog.PiecesIn(FamilyName);

    private bool RandomBrush => _pieceIndex < 0;

    private string PieceLabel =>
        RandomBrush ? "Random"
        : FamilyPieces.Count > 0
            ? Strip(FamilyPieces[Math.Clamp(_pieceIndex, 0, FamilyPieces.Count - 1)].File)
            : "-";

    /// <summary>The piece currently under the brush, or null when the family is empty.</summary>
    private GroundPiece? SelectedPiece
    {
        get
        {
            var pieces = FamilyPieces;
            if (pieces.Count == 0) return null;
            return pieces[RandomBrush ? 0 : Math.Clamp(_pieceIndex, 0, pieces.Count - 1)];
        }
    }

    /// <summary>
    /// Which piece to paint this square with. Random settles HERE, as the
    /// square is painted, and only ever inside the chosen family — grass never
    /// comes out stone, a block never comes out a surface. The choice is then
    /// written into the level, so a level looks the same every time it loads.
    /// </summary>
    private string BrushPiece()
    {
        var pieces = FamilyPieces;
        if (pieces.Count == 0) return "";
        return (RandomBrush ? pieces[Rng.Next(pieces.Count)]
                            : pieces[Math.Clamp(_pieceIndex, 0, pieces.Count - 1)]).File;
    }
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
        int x = BarX0;

        void Add(string label, string id, bool active, bool menu = false, int pad = BtnPad)
        {
            int w = (int)(_ctx.Font.MeasureString(label).X * BtnText) + pad + (menu ? BtnArrow : 0);
            list.Add(new Btn(new Rectangle(x, BarY, w, BarH), label, id, active, menu));
            x += w + BarGap;
        }

        Add($"Level: {_levelName}", "level", false, menu: true);
        Add($"Ground: {FamilyName}", "family", _tool == Tool.Block, menu: true);
        Add($"Piece: {PieceLabel}", "piece", _tool == Tool.Block, menu: true);
        Add("Anchor", "anchor", _anchoring);
        Add("Reload", "reload", false);
        Add("Controls", "controls", _showControls);
        Add($"Deco: {Strip(DecoName)}", "deco", _tool == Tool.Decoration, menu: true);
        Add("Door", "door", _tool == Tool.Door);
        Add($"Enemy: {EnemyName}", "enemy", _tool == Tool.Enemy, menu: true);
        Add("Start", "start", _tool == Tool.PlayerStart);
        Add($"Trigger: {_trigger}", "trigger", _tool == Tool.Trigger);
        Add($"Room: {_room}", "room", false);
        Add("Dialogue", "dialogue", false);
        Add(_problems == 0 ? "OK" : $"! {_problems}", "problems", false, pad: 34);
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
        "family" => BlockCatalog.Families.ToList(),
        // Random heads the list so it is one click away whatever the family holds
        "piece" => new List<string> { "Random" }
            .Concat(FamilyPieces.Select(gp => Strip(gp.File))).ToList(),
        "deco" => BlockCatalog.Decorations.Select(Strip).ToList(),
        "enemy" => _ctx.Enemies.EnemyNames.ToList(),
        "level" => LevelNames(),
        _ => new List<string>(),
    };

    /// <summary>
    /// Every level file in the repo's Content/Levels, so one can be opened
    /// without restarting. Dialogue files sit in the same folder and are not
    /// levels. Directory enumeration is fine here: the editor is desktop-only
    /// and already reads and writes the source tree directly.
    /// </summary>
    private List<string> LevelNames()
    {
        try
        {
            return Directory.GetFiles(_levelsDir, "*.txt")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n != null && !n.EndsWith("Dialogue", StringComparison.OrdinalIgnoreCase))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new List<string> { _levelName }; }
    }

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
            case "level": OpenLevel(MenuItems("level")[index]); break;
            case "family": _familyIndex = index; _pieceIndex = 0; _tool = Tool.Block; break;
            case "piece": _pieceIndex = index - 1; _tool = Tool.Block; break;
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
            case "level":
                _openMenu = _openMenu == "level" ? null : "level";
                return true;
            case "family" or "piece" or "deco" or "enemy":
                // the button both selects its tool and opens its palette
                _tool = id is "family" or "piece" ? Tool.Block
                    : id == "deco" ? Tool.Decoration : Tool.Enemy;
                _openMenu = _openMenu == id ? null : id;
                return true;
            case "anchor": ToggleAnchorTool(); return true;
            case "reload": ReloadFromDisk(); return true;
            case "door": _tool = Tool.Door; return true;
            case "start": _tool = Tool.PlayerStart; return true;
            case "trigger": _tool = Tool.Trigger; return true;
            case "controls": _showControls = !_showControls; return true;
            case "room": _typingRoom = true; _roomBuffer = _room; return true;
            case "dialogue": _typingTrigger = true; _roomBuffer = _trigger; return true;
            case "problems": RecountProblems(); Status(ProblemText()); return true;
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
        _camera += input.PanDeltaNoLetters;   // WASD are tool keys here
        if (_statusTimer > 0) _statusTimer -= dt;
        _problemTimer -= dt;
        if (_problemTimer <= 0f) { RecountProblems(); _problemTimer = 0.4f; }

        if (input.ToggleControls) _showControls = !_showControls;

        if (Typing) { UpdateTyping(input); return; }

        // the anchor tool takes over the ground entirely; only the toolbar
        // above it keeps working, so its own button can close it again
        if (UpdateAnchorTool(input)) return;

        var origin = _origin - _camera;

        if (input.CtrlHeld && input.Delete) { _boxMode = true; _boxStart = _boxEnd = null; }
        if (input.Undo) Undo();
        if (input.Copy) CopySelection();
        if (input.Paste && !ToolbarBand.Contains(_pointer))
            PasteAt(Target(_pointer.ToVector2(), origin).Tile);
        if (input.MiddleTap is Point drop && !ToolbarBand.Contains(drop))
            Eyedropper(PickTile(drop.ToVector2(), origin) ?? IsoMath.ToGrid(drop.ToVector2(), origin));

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
        if (UpdateFill(input, origin)) return;
        if (UpdateSelect(input, origin)) return;

        UpdatePaint(input, origin);
        UpdateErase(input, origin);

        // right-clicking a dialogue square opens that level's dialogue file
        if (input.AltTap is Point rc && !ToolbarBand.Contains(rc))
        {
            var t = PickTile(rc.ToVector2(), origin) ?? IsoMath.ToGrid(rc.ToVector2(), origin);
            if (_level.TriggerAt(t) is LevelTrigger trig) OpenDialogueFile(trig.Dialogue);
        }
    }

    private bool Typing => _typingRoom || _typingTrigger || _typingSaveAs;

    /// <summary>
    /// Text entry for a room, dialogue or level name.
    ///
    /// This runs before the toolbar and returns, so while it is up nothing else
    /// in the editor responds. It used to end only on Esc or on Enter with
    /// something typed — so entering it by accident, which is easy when a
    /// button sits half off the edge of the screen, left the editor apparently
    /// dead: the mouse moved, no button did anything, and the only way out was
    /// a key nothing told you to press. A click anywhere now ends it too,
    /// taking what was typed if there is any and abandoning it otherwise.
    /// </summary>
    private void UpdateTyping(InputState input)
    {
        _roomBuffer += input.TypedChars;
        if (input.Backspace && _roomBuffer.Length > 0) _roomBuffer = _roomBuffer[..^1];
        if (input.Cancel) { StopTyping(); Status("cancelled"); return; }
        if (input.Tap.HasValue || input.AltTap.HasValue)
        {
            if (_roomBuffer.Trim().Length > 0) CommitTyping();
            else { StopTyping(); Status("cancelled"); }
            return;
        }
        if (input.Submit)
        {
            if (_roomBuffer.Trim().Length > 0) CommitTyping();
            else { StopTyping(); Status("cancelled"); }
        }
    }

    private void StopTyping() => _typingRoom = _typingTrigger = _typingSaveAs = false;

    private void CommitTyping()
    {
        if (_typingRoom) { _room = _roomBuffer.Trim(); Status($"room = {_room}"); }
        else if (_typingSaveAs) SaveAs(_roomBuffer);
        else
        {
            _trigger = _roomBuffer.Trim();
            _tool = Tool.Trigger;
            Status($"trigger dialogue = {_trigger}");
        }
        StopTyping();
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
                    _familyIndex = Math.Min(c - '1', Math.Max(0, BlockCatalog.Families.Count - 1));
                    _pieceIndex = 0;
                    break;
                case 'b':
                    _tool = Tool.Block;
                    // B walks the pieces of the current family and then Random
                    if (FamilyPieces.Count > 0)
                        _pieceIndex = _pieceIndex + 1 >= FamilyPieces.Count ? -1 : _pieceIndex + 1;
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
                // with a selection up, +/- move the ground rather than the cursor
                case '+' or '=':
                    if (_selA != null) RaiseSelection(1);
                    else _height = Math.Min(_height + 1, 12);
                    break;
                case '-':
                    if (_selA != null) RaiseSelection(-1);
                    else _height = Math.Max(_height - 1, 0);
                    break;
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
    /// Shift+drag fills a box with the current block at the placement height.
    /// Returns true while it owns the pointer.
    /// </summary>
    private bool UpdateFill(InputState input, Vector2 origin)
    {
        if (!input.ShiftHeld)
        {
            if (_fillStart != null) { _fillStart = _fillEnd = null; }
            return false;
        }
        if (ToolbarBand.Contains(_pointer)) return false;

        var under = Target(_pointer.ToVector2(), origin).Tile;
        if (input.Tap.HasValue) _fillStart = under;
        if (_fillStart != null) _fillEnd = under;

        if (input.Released.HasValue && _fillStart is Point a && _fillEnd is Point b)
        {
            FillBox(a, b);
            _fillStart = _fillEnd = null;
        }
        return _fillStart != null || input.Tap.HasValue;
    }

    /// <summary>
    /// Ctrl+drag marks out a selection that stays put once the button comes up:
    /// +/- raise and lower the blocks in it, Ctrl+C copies, Del empties it.
    /// </summary>
    private bool UpdateSelect(InputState input, Vector2 origin)
    {
        if (_selA != null && (input.Cancel || input.AltTap.HasValue))
        {
            _selA = _selB = null;
            Status("selection dropped");
            return true;
        }
        // Delete with a selection up empties the selection instead of the tile
        if (_selA is Point sa && _selB is Point sb && input.Delete && !input.CtrlHeld)
        {
            BeginEdit();
            var (x0, y0, x1, y1) = Span(sa, sb);
            int n = 0;
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    n += DeleteAll(new Point(x, y));
            Status($"emptied the selection ({n} thing(s))");
            return true;
        }

        if (!input.CtrlHeld || input.Delete)
        {
            if (_selStart != null) _selStart = _selEnd = null;
            return false;
        }
        if (ToolbarBand.Contains(_pointer)) return false;

        var under = PickTile(_pointer.ToVector2(), origin)
                    ?? IsoMath.ToGrid(_pointer.ToVector2(), origin);
        if (input.Tap.HasValue) _selStart = under;
        if (_selStart != null) _selEnd = under;

        if (input.Released.HasValue && _selStart is Point a && _selEnd is Point b)
        {
            _selA = a; _selB = b;
            _selStart = _selEnd = null;
            var (x0, y0, x1, y1) = Span(a, b);
            Status($"selected {(x1 - x0 + 1)}x{(y1 - y0 + 1)} — +/- raise, Ctrl+C copy, Del empty, Esc drop");
        }
        return _selStart != null || input.Tap.HasValue;
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
                string type = BrushPiece();
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

    /// <summary>Opens another level for editing, dropping undo and the clipboard.</summary>
    private void OpenLevel(string name)
    {
        if (name.Equals(_levelName, StringComparison.OrdinalIgnoreCase)) return;
        _level = LevelData.Load(name);
        _levelName = name;
        _level.Name = name;
        _undo.Clear();
        _selA = _selB = _selStart = _selEnd = null;
        _boxMode = false;
        RecountProblems();
        Status($"opened {name}.txt ({_level.Blocks.Count} blocks)");
    }

    /// <summary>
    /// The same things the startup validator complains about, counted for the
    /// level in hand so a mistake shows while you are still building rather
    /// than at the next launch.
    /// </summary>
    private void RecountProblems()
    {
        int n = 0;
        foreach (var t in _level.Triggers)
            if (_level.BlockAt(new Point(t.X, t.Y)) == null) n++;
        foreach (var d in _level.Decorations)
            if (_level.BlockAt(new Point(d.X, d.Y)) == null) n++;
        foreach (var e in _level.Enemies)
            if (_level.BlockAt(new Point(e.X, e.Y)) == null) n++;
        foreach (var d in _level.Doors)
            if (_level.BlockAt(new Point(d.X, d.Y)) == null) n++;
        n += _level.PlayerStarts.Count(p => _level.BlockAt(p) == null);
        if (_level.PlayerStarts.Count < 4) n++;
        if (_level.Blocks.Count == 0) n++;
        _problems = n;
    }

    /// <summary>A short account of what is wrong, for the toolbar button.</summary>
    private string ProblemText()
    {
        var bits = new List<string>();
        int orphans = _level.Triggers.Count(t => _level.BlockAt(new Point(t.X, t.Y)) == null)
            + _level.Decorations.Count(d => _level.BlockAt(new Point(d.X, d.Y)) == null)
            + _level.Enemies.Count(e => _level.BlockAt(new Point(e.X, e.Y)) == null)
            + _level.Doors.Count(d => _level.BlockAt(new Point(d.X, d.Y)) == null)
            + _level.PlayerStarts.Count(p => _level.BlockAt(p) == null);
        if (orphans > 0) bits.Add($"{orphans} thing(s) floating with no block under them");
        if (_level.PlayerStarts.Count < 4)
            bits.Add($"only {_level.PlayerStarts.Count} player start(s) of 4");
        if (_level.Blocks.Count == 0) bits.Add("no blocks at all");
        return bits.Count == 0 ? "no problems" : string.Join("; ", bits);
    }

    /// <summary>Middle click: adopt the type, height and room of the square under it.</summary>
    private void Eyedropper(Point tile)
    {
        if (_level.BlockAt(tile) is not LevelBlock b) { Status("nothing to sample there"); return; }
        _height = b.Height;
        _room = b.Room;
        // adopt the sampled square's family AND its exact piece, not Random
        if (BlockCatalog.Find(b.Type) is GroundPiece sampled)
        {
            int fam = BlockCatalog.Families
                .ToList().FindIndex(f => f.Equals(sampled.Family, StringComparison.OrdinalIgnoreCase));
            if (fam >= 0)
            {
                _familyIndex = fam;
                _pieceIndex = Math.Max(0, BlockCatalog.PiecesIn(sampled.Family)
                    .ToList().FindIndex(gp => gp.File.Equals(sampled.File, StringComparison.OrdinalIgnoreCase)));
                _tool = Tool.Block;
            }
        }
        Status($"picked up {b.Type} at {b.Height} ft in room {b.Room}");
    }

    private static (int X0, int Y0, int X1, int Y1) Span(Point a, Point b) =>
        (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    /// <summary>Shift+drag: fill the box with the current block at the placement height.</summary>
    private void FillBox(Point a, Point b)
    {
        BeginEdit();
        var (x0, y0, x1, y1) = Span(a, b);
        // BrushPiece() is called per square, so a Random brush scatters the
        // family across the box instead of stamping one piece over all of it
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                _level.Blocks[new Point(x, y)] = new LevelBlock
                    { X = x, Y = y, Height = _height, Type = BrushPiece(), Room = _room };
        Status($"filled {(x1 - x0 + 1) * (y1 - y0 + 1)} squares with " +
               $"{(RandomBrush ? $"random {FamilyName}" : PieceLabel)} at {_height} ft");
    }

    /// <summary>+/- with a selection: shift every block inside it up or down.</summary>
    private void RaiseSelection(int by)
    {
        if (_selA is not Point a || _selB is not Point b) return;
        BeginEdit();
        var (x0, y0, x1, y1) = Span(a, b);
        int n = 0;
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                if (_level.Blocks.TryGetValue(new Point(x, y), out var blk))
                {
                    blk.Height = Math.Clamp(blk.Height + by, 0, 12);
                    n++;
                }
        Status($"{(by > 0 ? "raised" : "lowered")} {n} block(s)");
    }

    private void CopySelection()
    {
        if (_selA is not Point a || _selB is not Point b) { Status("nothing selected"); return; }
        var (x0, y0, x1, y1) = Span(a, b);
        _clipOrigin = new Point(x0, y0);
        _clipboard = new List<LevelBlock>();
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                if (_level.Blocks.TryGetValue(new Point(x, y), out var blk))
                    _clipboard.Add(new LevelBlock
                        { X = blk.X, Y = blk.Y, Height = blk.Height, Type = blk.Type, Room = blk.Room });
        Status($"copied {_clipboard.Count} block(s)");
    }

    /// <summary>Pastes with the selection's top-left corner landing on the cursor.</summary>
    private void PasteAt(Point tile)
    {
        if (_clipboard.Count == 0) { Status("clipboard is empty"); return; }
        BeginEdit();
        foreach (var b in _clipboard)
        {
            var at = new Point(tile.X + b.X - _clipOrigin.X, tile.Y + b.Y - _clipOrigin.Y);
            _level.Blocks[at] = new LevelBlock
                { X = at.X, Y = at.Y, Height = b.Height, Type = b.Type, Room = _room };
        }
        Status($"pasted {_clipboard.Count} block(s) into room {_room}");
    }

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
            BlockCatalog.Draw(batch, _ctx.Assets, b.Type,
                IsoMath.ToScreen(b.X, b.Y, b.Height, origin), Color.White);

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
            if (InSpan(_boxStart, _boxEnd, tile))
                DrawTop(batch, b.X, b.Y, b.Height, origin, Color.Red * 0.45f);
            if (InSpan(_fillStart, _fillEnd, tile))
                DrawTop(batch, b.X, b.Y, b.Height, origin, Color.Orange * 0.45f);
            if (InSpan(_selStart, _selEnd, tile) || InSpan(_selA, _selB, tile))
                DrawTop(batch, b.X, b.Y, b.Height, origin, Color.Cyan * 0.35f);
        }

        // a fill box covers squares that have no block yet, so it is painted
        // over the bare grid as well as over what is already there
        if (_fillStart is Point fa && _fillEnd is Point fb)
        {
            var (x0, y0, x1, y1) = Span(fa, fb);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    if (!_level.Blocks.ContainsKey(new Point(x, y)))
                        DrawTop(batch, x, y, _height, origin, Color.Orange * 0.45f);
        }

        DrawCursor(batch, origin);
        // the anchor tool covers the level but sits under the toolbar, so its
        // own button stays reachable to close it
        DrawAnchorTool(batch);
        DrawToolbar(batch);
        DrawHudText(batch);
        DrawRoomLabels(batch, origin);
        // last, so both cover whatever they hang over
        DrawControlsPanel(batch);
        DrawOpenMenu(batch);
    }

    private static bool InSpan(Point? from, Point? to, Point tile) =>
        from is Point a && to is Point b &&
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
            bool alarm = b.Id == "problems" && _problems > 0;
            Ui.FillRect(batch, _ctx.Pixel, b.Rect,
                alarm ? new Color(120, 30, 30)
                : b.Active ? new Color(120, 96, 20)
                : hot ? new Color(52, 52, 66) : new Color(32, 32, 42));
            var edge = alarm ? Color.Red
                : b.Active ? Color.Yellow : hot ? Color.White : Color.White * 0.35f;
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(b.Rect.X, b.Rect.Y, b.Rect.Width, 3), edge);
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(b.Rect.X, b.Rect.Bottom - 3, b.Rect.Width, 3), edge);
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(b.Rect.X, b.Rect.Y, 3, b.Rect.Height), edge);
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(b.Rect.Right - 3, b.Rect.Y, 3, b.Rect.Height), edge);
            Ui.DrawTextCentered(batch, _ctx.Font, b.Label,
                b.Menu ? new Rectangle(b.Rect.X, b.Rect.Y, b.Rect.Width - BtnArrow, b.Rect.Height) : b.Rect,
                b.Active ? Color.White : Color.White * 0.9f, BtnText);

            // a little triangle marks the buttons that drop a palette down
            if (!b.Menu) continue;
            int cx = b.Rect.Right - 18, cy = b.Rect.Center.Y - 3;
            for (int r = 0; r < 8; r++)
                Ui.FillRect(batch, _ctx.Pixel,
                    new Rectangle(cx - 8 + r, cy + r, 17 - r * 2, 1), Color.White * 0.8f);
        }

    }

    /// <summary>
    /// An open dropdown, drawn after everything else so it covers whatever it
    /// hangs over — the level, the HUD, the room labels — until it closes.
    /// It is opaque, so a list over a busy level stays readable.
    /// </summary>
    private void DrawOpenMenu(SpriteBatch batch)
    {
        if (_openMenu == null) return;
        var items = MenuItems(_openMenu);
        var rects = MenuRects(_openMenu);
        for (int i = 0; i < items.Count; i++)
        {
            bool hot = rects[i].Contains(_pointer);
            Ui.FillRect(batch, _ctx.Pixel, rects[i],
                hot ? new Color(70, 70, 90, 255) : new Color(26, 26, 34, 255));
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(rects[i].X, rects[i].Y, rects[i].Width, 2), Color.White * 0.3f);
            Ui.DrawTextCentered(batch, _ctx.Font, items[i], rects[i], Color.White, BtnText);
        }
    }

    /// <summary>
    /// Every control, in the middle of the screen, off by default. The Controls
    /// button and the Insert key both toggle it. It used to be two permanent
    /// lines of small text under the toolbar, which cost that space on every
    /// frame to say things you need once.
    /// </summary>
    private static readonly string[] ControlLines =
    {
        "hold left            paint",
        "hold Del             rub out",
        "Shift+drag           fill a box at the placement height",
        "Ctrl+Del, then drag  empty a box",
        "Ctrl+drag            select a box: +/- raise and lower, Ctrl+C copy,",
        "                     Ctrl+V paste at the cursor, Del empty, Esc drop",
        "middle click         eyedropper: adopt that square's piece/height/room",
        "Ctrl+Z               undo the last stroke",
        "right-click trigger  open that level's dialogue file",
        "scroll wheel         placement height in feet",
        "ARROWS               pan (WASD are tool keys)",
        "",
        "1..9  ground family     B  next piece, then Random",
        "D deco   O door   E enemy   P start   G trigger",
        "R room name    N dialogue name    V save as",
        "S save    T test",
        "",
        "Insert or the Controls button hides this again",
    };

    private void DrawControlsPanel(SpriteBatch batch)
    {
        if (!_showControls) return;
        const float scale = 0.30f;
        int lineH = (int)(_ctx.Font.LineSpacing * scale);
        int h = ControlLines.Length * lineH + 90;
        int w = 1900;
        var box = new Rectangle((VirtualViewport.Width - w) / 2,
            (VirtualViewport.Height - h) / 2, w, h);

        Ui.FillRect(batch, _ctx.Pixel, box, new Color(10, 10, 16, 248));
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(box.X, box.Y, box.Width, 4), Color.Yellow);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(box.X, box.Bottom - 4, box.Width, 4), Color.Yellow);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(box.X, box.Y, 4, box.Height), Color.Yellow);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(box.Right - 4, box.Y, 4, box.Height), Color.Yellow);

        int y = box.Y + 46;
        foreach (var line in ControlLines)
        {
            batch.DrawString(_ctx.Font, line, new Vector2(box.X + 60, y),
                Color.White * 0.85f, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            y += lineH;
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
        string mode = _boxMode ? "  BOX DELETE: drag a box, Esc to cancel"
            : _selA != null ? "  SELECTION: +/- raise, Ctrl+C copy, Ctrl+V paste, Del empty, Esc drop"
            : "";
        // height lives here now that the -/0 ft/+ buttons are gone; the wheel
        // still sets it, so it needs somewhere to be read
        batch.DrawString(_ctx.Font,
            $"{_levelName}.txt   room: {_room}   blocks: {_level.Blocks.Count}   " +
            $"height: {_height} ft{mode}",
            new Vector2(BarX0, y), _boxMode ? Color.Red : Color.Yellow,
            0f, Vector2.Zero, 0.30f, SpriteEffects.None, 0f);

        // Text entry stops the rest of the editor responding, so it says so
        // where it cannot be missed rather than as one more line of HUD.
        if (Typing)
        {
            var box = new Rectangle(VirtualViewport.Width / 2 - 900, 300, 1800, 200);
            Ui.FillRect(batch, _ctx.Pixel, box, new Color(10, 10, 16, 245));
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(box.X, box.Y, box.Width, 4), Color.Cyan);
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(box.X, box.Bottom - 4, box.Width, 4), Color.Cyan);
            Ui.DrawTextCentered(batch, _ctx.Font,
                (_typingRoom ? "room name: " : _typingSaveAs ? "save as: " : "dialogue name: ")
                + _roomBuffer + "_",
                new Rectangle(box.X, box.Y + 40, box.Width, 70), Color.Cyan, 0.42f);
            Ui.DrawTextCentered(batch, _ctx.Font,
                "Enter accepts  ·  Esc or a click anywhere abandons it",
                new Rectangle(box.X, box.Y + 120, box.Width, 50), Color.White * 0.7f, 0.26f);
        }
        else if (_statusTimer > 0)
            batch.DrawString(_ctx.Font, _status, new Vector2(60, y + 100), Color.LightGreen,
                0f, Vector2.Zero, 0.32f, SpriteEffects.None, 0f);
    }
}
