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
using TheTimelineIs.Core.Pixel;
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
public partial class IsoEditorScreen : IScreen, IDrawsItself
{
    private enum Tool { Block, Decoration, Enemy, PlayerStart, Trigger, Room, Transition }

    /// <summary>Tools you drag across the ground; the rest are one click each.</summary>
    /// <summary>
    /// Tools you drag across the ground; the rest are one click each.
    ///
    /// Decorations and enemies are deliberately NOT draggable. A stroke is one
    /// undo step, and taking back a dragged line of trees with one Ctrl+Z was
    /// never what anybody meant by it — a tree at a time is.
    /// </summary>
    private static bool Paints(Tool t) =>
        t is Tool.Block or Tool.Trigger or Tool.Room or Tool.Transition;

    private readonly GameContext _ctx;
    private LevelData _level;
    private readonly string _levelsDir;
    private string _levelName = "PixelFlat";

    private Tool _tool = Tool.Block;
    private int _decoIndex, _enemyIndex;

    /// <summary>
    /// The Door dropdown's last entry. It is not a door at all — it is here
    /// because it is the other way to get from one room to another, and that
    /// is where somebody would look for it.
    /// </summary>
    private const string TransitionEntry = "Area transition";

    /// <summary>
    /// The pad waiting to be linked: right-click one pad to arm it, right-click
    /// a second to join them. Held as one of the pad's squares, because the pad
    /// itself is rebuilt from the map every time it is asked for.
    /// </summary>
    private Point? _linkFrom;

    /// <summary>Pair numbers are handed out in order; 0 means unlinked.</summary>
    private int NextPair() =>
        _level.Transitions.Count == 0 ? 1 : _level.Transitions.Max(t => t.Pair) + 1;

    // the block brush: which family, and which piece inside it. -1 = Random,
    // which is settled per square as it is painted.
    private int _familyIndex;
    private int _pieceIndex;
    private static readonly Random Rng = new();

    /// <summary>
    /// The placement height, in the small steps the level file stores.
    /// Everything the editor SHOWS goes through Tall(), which reads it as
    /// blocks — one cube is 1, a step is 0.25.
    /// </summary>
    private int _height;

    /// <summary>A stored height read as blocks: four steps to a block, so 2 is 0.5.</summary>
    private static string Tall(int steps) =>
        (steps / (float)IsoMath.StepsPerBlock).ToString("0.##");
    private string _room = "Main";
    private string _trigger = "Intro";      // dialogue block a placed trigger calls
    private bool _typingRoom, _typingTrigger, _typingSaveAs, _typingRename, _typingNewMap;
    private string _roomBuffer = "";
    /// <summary>The window onto the board: whole art pixels at a whole-number zoom.</summary>
    private readonly PixelCamera _camera = new();
    /// <summary>The pointer in design space, for the toolbar and panels.</summary>
    private Point _pointer;
    /// <summary>The pointer on the board, in art pixels.</summary>
    private Vector2 _worldPointer;
    private bool _framed;
    private Point _windowSize = new(1920, 1080);

    /// <summary>
    /// Text that belongs to a square — heights, room names, trigger names —
    /// gathered while the board is drawn and written in the HUD pass, where
    /// the font is sharp. Drawn on the board it would be pixel-scaled mush.
    /// </summary>
    private readonly List<(Vector2 World, string Text, Color Ink, float Scale, bool Plate)> _labels = new();
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
    /// <summary>
    /// The press in progress started on the toolbar or a dropdown, so nothing
    /// it does afterwards touches the level.
    /// </summary>
    private bool _uiPress;

    /// <summary>Alt: write every block's height across the level. Tap to latch, hold for a glance.</summary>
    private readonly TapHold _heights = new();

    /// <summary>Space: paint every block with its room's colour, on the same rule.</summary>
    private readonly TapHold _rooms = new();

    /// <summary>
    /// Room colours, in the order rooms are handed them. Chosen to stay apart
    /// from each other and from the overlays already on the ground — red is the
    /// box delete, orange the box fill, cyan the selection, green a player
    /// start, violet a trigger — so a room wash is never mistaken for one.
    /// </summary>
    private static readonly Color[] RoomColors =
    {
        new(80, 150, 255), new(255, 190, 60), new(120, 220, 120), new(255, 120, 200),
        new(160, 130, 255), new(90, 220, 220), new(240, 130, 90), new(200, 220, 110),
    };

    /// <summary>
    /// Which colour each room wears. Keyed off the room names in alphabetical
    /// order so a room keeps its colour as blocks are added and removed, rather
    /// than shuffling every time the level's block order changes.
    /// </summary>
    private List<string> RoomOrder() =>
        _level.RoomNames.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();

    private Color RoomColor(string room)
    {
        int i = RoomOrder().FindIndex(r => r.Equals(room, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? Color.Gray : RoomColors[i % RoomColors.Length];
    }

    private string? _openMenu;              // which button's dropdown is showing
    private readonly List<LevelData> _undo = new();
    private const int UndoDepth = 40;

    private int _problems;                    // validator complaints about this level
    private const int BarY = 24, BarH = 84, BarGap = 10;
    /// <summary>Text size on the buttons, and the padding around it.</summary>
    private const float BtnText = 0.24f;
    private const int BtnPad = 30, BtnArrow = 26, BarX0 = 40;
    private const int BarRight = VirtualViewport.Width - BarX0;

    /// <summary>
    /// How many rows the buttons wrapped onto last frame. The strip grows
    /// downward rather than running off the right edge, so the band the ground
    /// must not be clicked through has to grow with it.
    /// </summary>
    private int _barRows = 1;

    private int BarBottom => BarY + _barRows * (BarH + BarGap) + 8;

    /// <summary>The area the toolbar owns; clicks here never reach the level.</summary>
    private Rectangle ToolbarBand => new(0, 0, VirtualViewport.Width, BarBottom);

    public IsoEditorScreen(GameContext ctx)
    {
        _ctx = ctx;
        _level = LevelData.Load(_levelName);
        _levelsDir = FindLevelsDir();
        RebuildCorners();
        // Ground art is terrain sets now, so the terrain brush is the one that
        // works out of the box. Blocks.txt is for one-off pieces and is empty
        // until somebody puts one there.
        _terrain = TerrainCatalog.Terrains.FirstOrDefault() ?? "";
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

    /// <summary>
    /// Every square the window can currently see, with a square of margin so
    /// nothing pops in at the edges.
    ///
    /// The four corners of the window are turned into grid squares and the box
    /// around them is swept. A diamond grid is turned 45 degrees to the window,
    /// so that box is about twice the squares you can actually see — cheap
    /// enough, and it means the grid never has a visible edge. Zoomed all the
    /// way out it is capped, because at 1x the box would run to tens of
    /// thousands of squares and none of them would be worth drawing.
    /// </summary>
    private IEnumerable<Point> VisibleGrid(Vector2 origin)
    {
        var corners = new[]
        {
            _camera.ToWorld(Point.Zero),
            _camera.ToWorld(new Point(_windowSize.X, 0)),
            _camera.ToWorld(new Point(0, _windowSize.Y)),
            _camera.ToWorld(new Point(_windowSize.X, _windowSize.Y)),
        }.Select(w => IsoMath.ToGrid(w.ToVector2(), origin)).ToList();

        int x0 = corners.Min(c => c.X) - 1, x1 = corners.Max(c => c.X) + 1;
        int y0 = corners.Min(c => c.Y) - 1, y1 = corners.Max(c => c.Y) + 1;

        const int Most = 120;        // per side, so at most 14400 squares
        if (x1 - x0 > Most) x1 = x0 + Most;
        if (y1 - y0 > Most) y1 = y0 + Most;

        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                yield return new Point(x, y);
    }

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
        Dirty = true;
    }

    private void Undo()
    {
        if (_undo.Count == 0) { Status("nothing to undo"); return; }
        _level.CopyFrom(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        Dirty = true;                       // undone back to saved is still a change on screen
        RebuildCorners();
        Status($"undo ({_undo.Count} left)");
    }

    // ---------------- toolbar ----------------

    private readonly record struct Btn(Rectangle Rect, string Label, string Id, bool Active, bool Menu);

    private string FamilyName => BlockCatalog.Families.Count > 0
        ? BlockCatalog.Families[Math.Clamp(_familyIndex, 0, BlockCatalog.Families.Count - 1)] : "-";

    private IReadOnlyList<GroundPiece> FamilyPieces => BlockCatalog.PiecesIn(FamilyName);

    private bool RandomBrush => _pieceIndex < 0;

    private string PieceLabel =>
        BlockCatalog.IsCheckerboard(FamilyName) ? "Checkerboard"
        : RandomBrush ? "Random"
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
    private string BrushPiece(Point tile)
    {
        // A checkerboard family ignores the piece picker entirely: the square's
        // position decides which half it draws from, so a dark piece can never
        // land beside another dark one however the brush was set.
        if (BlockCatalog.IsCheckerboard(FamilyName))
        {
            var half = BlockCatalog.PiecesIn(FamilyName, BlockCatalog.ShadeAt(tile));
            if (half.Count == 0) return "";
            return half[Rng.Next(half.Count)].File;
        }

        var pieces = FamilyPieces;
        if (pieces.Count == 0) return "";
        return (RandomBrush ? pieces[Rng.Next(pieces.Count)]
                            : pieces[Math.Clamp(_pieceIndex, 0, pieces.Count - 1)]).File;
    }
    /// <summary>
    /// Which way the decoration brush is turned. One setting for the brush,
    /// not per square: you point it the way you want and then place as many
    /// as you like, the way the height works.
    /// </summary>
    private Facing8 _decoFacing = Facings.Default;

    /// <summary>
    /// Which way the enemy brush is turned. Its own setting, kept apart from
    /// the decoration's: pointing a portal one way and then placing a goblin
    /// should not point the goblin that way too.
    /// </summary>
    private Facing8 _enemyFacing = Facings.Default;

    /// <summary>Whether the wheel should be turning something rather than raising it.</summary>
    private bool RotatingDeco =>
        _tool == Tool.Decoration && BlockCatalog.Decorations.Count > 0 && BlockCatalog.Turns(DecoFile);

    /// <summary>
    /// Everybody is drawn from eight rotations, so the enemy brush always
    /// turns — there is no such thing as an enemy that cannot face a way.
    /// </summary>
    private bool RotatingEnemy => _tool == Tool.Enemy && _ctx.Enemies.EnemyNames.Count > 0;

    /// <summary>Whichever way the brush in hand is pointing.</summary>
    private Facing8 BrushFacing => RotatingEnemy ? _enemyFacing : _decoFacing;

    /// <summary>The decoration the brush will place, as Decorations.txt writes it.</summary>
    private string DecoFile => BlockCatalog.Decorations.Count > 0
        ? BlockCatalog.Decorations[Math.Clamp(_decoIndex, 0, BlockCatalog.Decorations.Count - 1)] : "";

    /// <summary>Wheel up turns the brush one eighth one way, wheel down the other.</summary>
    private void TurnBrush(int steps)
    {
        Facing8 Turned(Facing8 from) =>
            Facings.All[((Array.IndexOf(Facings.All, from) + steps) % 8 + 8) % 8];
        if (RotatingEnemy) _enemyFacing = Turned(_enemyFacing);
        else _decoFacing = Turned(_decoFacing);
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
        int x = BarX0, row = 0;

        void Add(string label, string id, bool active, bool menu = false, int pad = BtnPad)
        {
            int w = (int)(_ctx.Font.MeasureString(label).X * BtnText) + pad + (menu ? BtnArrow : 0);
            // wrap onto the next row rather than run off the right edge, so a
            // long level name or a new tool can never push a button off screen
            if (x > BarX0 && x + w > BarRight) { row++; x = BarX0; }
            list.Add(new Btn(new Rectangle(x, BarY + row * (BarH + BarGap), w, BarH),
                label, id, active, menu));
            x += w + BarGap;
        }

        // Next row, deliberately: the two rows mean different things. The top
        // one is the BRUSH — what a click on the ground would put there. The
        // bottom one is the MAP: which file, which room, and is it alright.
        void Break() { row++; x = BarX0; }

        Add($"Terrain: {TerrainLabel}", "terrain", _tool == Tool.Block && TerrainBrush, menu: true);
        Add($"Ground art: {FamilyName}", "family", _tool == Tool.Block && !TerrainBrush, menu: true);
        Add($"Ground piece: {PieceLabel}", "piece", _tool == Tool.Block, menu: true);
        Add($"Decoration: {Strip(DecoName)}", "deco", _tool == Tool.Decoration, menu: true);
        Add($"Doorway: {DoorLabel}", "door", _tool == Tool.Transition, menu: true);
        Add($"Enemy: {EnemyName}", "enemy", _tool == Tool.Enemy, menu: true);
        Add("Player start", "start", _tool == Tool.PlayerStart);
        Add($"Dialogue: {_trigger}", "trigger", _tool == Tool.Trigger, menu: true);
        Add("Paint room", "placeroom", _tool == Tool.Room);

        Break();
        Add("File", "file", false, menu: true);
        Add($"Current Level: {_levelName}{(Dirty ? "  (Not Saved)" : "")}", "level", false, menu: true);
        Add($"Room: {_room}", "room", false, menu: true);
        Add("Controls", "controls", _showControls);
        // the content check: no problems, or a red count of them. Clicking it
        // looks again and names the first one.
        Add(_problems == 0 ? "Check for Errors" : $"! {_problems}", "problems", false, pad: 34);
        _barRows = row + 1;
        return list;
    }

    private static string Strip(string file) =>
        file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;

    /// <summary>What the Doorway button is set to paint.</summary>
    private string DoorLabel => TransitionEntry;

    /// <summary>What a click on the ground would do right now, in two words.</summary>
    private string ToolLabel => _tool switch
    {
        Tool.Block => TerrainBrush ? _terrain : PieceLabel,
        Tool.Decoration => Strip(DecoName),
        Tool.Enemy => EnemyName,
        Tool.PlayerStart => "player start",
        Tool.Trigger => $"dialogue {_trigger}",
        Tool.Room => $"room {_room}",
        _ => TransitionEntry,
    };

    /// <summary>
    /// One line about the button under the cursor. The Controls panel teaches
    /// the editor; this is for the button you have forgotten.
    /// </summary>
    private string? HoverHint()
    {
        if (!ToolbarBand.Contains(_pointer)) return null;
        foreach (var b in Buttons())
            if (b.Rect.Contains(_pointer))
                return Hints.TryGetValue(b.Id, out var hint) ? hint : null;
        return null;
    }

    private static readonly Dictionary<string, string> Hints = new()
    {
        ["file"] = "import, reload, save, save as, rename this map, playtest it",
        ["level"] = "open another map, or start a new one",
        ["terrain"] = "paint ground that blends into its neighbours",
        ["family"] = "pick a set of one-off ground art, for squares terrain cannot do",
        ["piece"] = "which piece of that set, or Random to scatter them",
        ["deco"] = "a tree, a rock: something standing on a square, blocking it. "
                   + "The wheel turns the ones that turn",
        ["door"] = "paint two pads, then right-click one and its far end to join them",
        ["enemy"] = "drop one enemy per click on a square that has ground. The wheel turns it",
        ["start"] = "where the party stands when the level opens, up to four",
        ["trigger"] = "a square that plays a dialogue when somebody steps on it",
        ["room"] = "which room the Paint room tool writes onto squares",
        ["placeroom"] = "drag over squares to put them in the room named beside this",
        ["controls"] = "every key and button, in the middle of the screen",
        ["problems"] = "look again for things this map gets wrong, and name the first",
    };

    /// <summary>
    /// Whether the level has been changed since the last save.
    ///
    /// Set wherever an edit begins, which is the same place the undo stack is
    /// pushed, so nothing can change the level without saying so. The toolbar
    /// says "(Not Saved)" while this is true, and opening another map asks
    /// first.
    /// </summary>
    private bool Dirty { get; set; }

    /// <summary>The first entry of the Room menu: a name that is not in use yet.</summary>
    private const string NewRoomEntry = "Create a New Room";

    /// <summary>The first entry of the Dialogue menu.</summary>
    private const string NewDialogueEntry = "Create New Dialogue Tile";

    /// <summary>
    /// Every dialogue block this level has, read from its OWN file in the
    /// source tree rather than through the built copy — the editor writes
    /// that file, so it is the one that is up to date.
    /// </summary>
    private List<string> DialogueNames()
    {
        var names = new List<string>();
        try
        {
            if (!File.Exists(DialoguePath)) return names;
            foreach (string raw in File.ReadAllLines(DialoguePath))
            {
                string line = raw.Trim();
                if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
                string name = line[9..].Trim();
                if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[editor] dialogue list: {ex.Message}"); }
        return names;
    }

    /// <summary>Opening, saving and leaving — everything that is about the FILE.</summary>
    private static readonly string[] FileMenu =
    {
        "Import a PixelLab export",
        "Reload from disk",
        "Save",
        "Save As",
        "Rename Map",
        "Playtest the level",
    };

    /// <summary>The first entry of the level list: a blank map rather than an old one.</summary>
    private const string NewMapEntry = "Make a New Map";

    /// <summary>The entries a dropdown shows, and what picking one does.</summary>
    private List<string> MenuItems(string id) => id switch
    {
        "file" => FileMenu.ToList(),
        // "Off" heads the list so the old piece brush is one click away
        "terrain" => TerrainMenuRows().SelectMany(r => r).ToList(),
        "family" => BlockCatalog.Families.ToList(),
        // Random heads the list so it is one click away whatever the family holds
        "piece" => new List<string> { "Random" }
            .Concat(FamilyPieces.Select(gp => Strip(gp.File))).ToList(),
        "deco" => BlockCatalog.Decorations.Select(Strip).ToList(),
        // the name on the button is always one of the entries, even before
        // anything on the map uses it
        "room" => new List<string> { NewRoomEntry }
            .Concat(RoomOrder()).Append(_room)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        "trigger" => new List<string> { NewDialogueEntry }
            .Concat(DialogueNames()).Append(_trigger)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        "enemy" => _ctx.Enemies.EnemyNames.ToList(),
        "door" => new List<string> { TransitionEntry },
        "level" => LevelNames(),
        _ => new List<string>(),
    };

    /// <summary>
    /// Every level file in the repo's Content/Levels, so one can be opened
    /// without restarting, with "Make a New Map" at the top.
    ///
    /// Two files in that folder are not levels and are left out: a level's
    /// Dialogue, and Destinations, which is the world map's list of pins.
    /// Opening either of those in the level editor only ever produced an
    /// empty map and a puzzled face.
    ///
    /// Directory enumeration is fine here: the editor is desktop-only and
    /// already reads and writes the source tree directly.
    /// </summary>
    private List<string> LevelNames()
    {
        var names = new List<string> { NewMapEntry };
        try
        {
            names.AddRange(Directory.GetFiles(_levelsDir, "*.txt")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n != null
                    && !n.EndsWith("Dialogue", StringComparison.OrdinalIgnoreCase)
                    && !n.Equals("Destinations", StringComparison.OrdinalIgnoreCase))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }
        catch { names.Add(_levelName); }
        return names;
    }

    private List<Rectangle> MenuRects(string id)
    {
        var btn = Buttons().FirstOrDefault(b => b.Id == id);
        // the terrain menu pairs its entries two to a row; everything else is a column
        if (id == "terrain") return TerrainMenuRects(btn.Rect);
        var items = MenuItems(id);
        var rects = new List<Rectangle>();
        int width = Math.Max(btn.Rect.Width, 460), step = BarH - 12;
        int top = btn.Rect.Bottom + 6;

        // A list too long for the screen carries on in a second column beside
        // the first, rather than running off the bottom with its last entries
        // out of reach — twenty-nine decorations did exactly that.
        int perColumn = Math.Max(1, (VirtualViewport.Height - top - 8) / step);
        int columns = (items.Count + perColumn - 1) / perColumn;
        int left = Math.Min(btn.Rect.X, BarRight - columns * (width + 6) + 6);
        for (int i = 0; i < items.Count; i++)
            rects.Add(new Rectangle(left + (i / perColumn) * (width + 6),
                top + (i % perColumn) * step, width, BarH - 14));
        return rects;
    }

    private void PickMenu(string id, int index)
    {
        switch (id)
        {
            case "file": DoFileMenu(index); break;
            case "level":
                string picked = MenuItems("level")[index];
                if (picked == NewMapEntry)
                    Confirm($"{_levelName} has changes that are not saved. Start a new map anyway?",
                        () => { _typingNewMap = true; _roomBuffer = ""; });
                else OpenLevel(picked);
                break;
            case "terrain":
                _terrain = index == 0 ? "" : MenuItems("terrain")[index];
                _tool = Tool.Block;
                break;
            case "family":
                _familyIndex = index; _pieceIndex = 0; _terrain = ""; _tool = Tool.Block;
                break;
            case "piece": _pieceIndex = index - 1; _terrain = ""; _tool = Tool.Block; break;
            case "deco": _decoIndex = index; _tool = Tool.Decoration; break;
            case "room":
                if (index == 0) { _typingRoom = true; _roomBuffer = ""; }
                else { _room = MenuItems("room")[index]; _tool = Tool.Room; }
                break;
            case "trigger":
                if (index == 0) { _typingTrigger = true; _roomBuffer = ""; }
                else { _trigger = MenuItems("trigger")[index]; }
                _tool = Tool.Trigger;
                break;
            case "enemy": _enemyIndex = index; _tool = Tool.Enemy; break;
            case "door": _tool = Tool.Transition; break;
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
            case "terrain":
                // the terrain palette, and picking from it paints
                _tool = Tool.Block;
                _openMenu = _openMenu == id ? null : id;
                return true;
            case "file":
                _openMenu = _openMenu == id ? null : id;
                return true;
            case "family" or "piece" or "deco" or "enemy" or "door" or "room" or "trigger":
                // the button both selects its tool and opens its palette
                _tool = id is "family" or "piece" ? Tool.Block
                    : id == "deco" ? Tool.Decoration
                    : id == "door" ? Tool.Transition
                    : id == "room" ? Tool.Room
                    : id == "trigger" ? Tool.Trigger
                    : Tool.Enemy;
                _openMenu = _openMenu == id ? null : id;
                return true;
            case "start": _tool = Tool.PlayerStart; return true;
            case "controls": _showControls = !_showControls; return true;
            case "placeroom": _tool = Tool.Room; return true;
            case "problems": RecountProblems(); Status(ProblemText()); return true;
        }
        return false;
    }

    /// <summary>One entry of the File menu, in the order FileMenu lists them.</summary>
    private void DoFileMenu(int index)
    {
        switch (index)
        {
            case 0: ImportWithPicker(); break;
            case 1: ReloadFromDisk(); break;
            case 2: Save(); break;
            case 3: _typingSaveAs = true; _roomBuffer = _levelName; break;
            case 4: _typingRename = true; _roomBuffer = _levelName; break;
            case 5: PlayTest(); break;
        }
    }

    // ---------------- update ----------------

    public void Update(InputState input, float dt)
    {
        _pointer = input.PointerPos;
        _worldPointer = _camera.ToWorld(input.RawPointer).ToVector2();
        // WASD pan exactly as the arrows do, so a hand on the keys never has
        // to go looking for them. Nothing in the editor spends W, A, S or D on
        // anything else. Design-space units back to window pixels, then to art
        // pixels, so a drag moves the board the same amount at any zoom.
        _camera.Scroll(new Point(
            (int)Math.Round(input.PanDelta.X * _ctx.Viewport.Scale / _camera.Zoom),
            (int)Math.Round(input.PanDelta.Y * _ctx.Viewport.Scale / _camera.Zoom)));
        Buttons();                            // refreshes the row count the band uses
        if (_statusTimer > 0) _statusTimer -= dt;

        // A click on the toolbar or a dropdown owns the whole press. Without
        // this the NEXT frame, with the button still down, would see the
        // pointer over the ground and paint a block there.
        if (!input.PointerHeld) _uiPress = false;
        _problemTimer -= dt;
        if (_problemTimer <= 0f) { RecountProblems(); _problemTimer = 0.4f; }

        if (input.ToggleControls) _showControls = !_showControls;

        // A question about losing work owns the whole editor until it is
        // answered, the same way typing a name does.
        if (_confirmDo != null) { UpdateConfirm(input); return; }
        if (Typing) { UpdateTyping(input); return; }
        if (input.Save) Save();

        // after the typing guard: Space belongs to the room name being typed,
        // not to the overlay, while a name is being entered
        _heights.Update(input.AltHeld, dt);
        _rooms.Update(input.SpaceHeld, dt);

        var origin = Vector2.Zero;

        if (input.CtrlHeld && input.Delete) { _boxMode = true; _boxStart = _boxEnd = null; }
        if (input.Undo) Undo();
        if (input.Copy) CopySelection();
        if (input.Paste && !ToolbarBand.Contains(_pointer))
            PasteAt(Target(_worldPointer, origin).Tile);
        if (input.MiddleTap is Point drop && !ToolbarBand.Contains(drop))
            Eyedropper(PickTile(_worldPointer, origin) ?? IsoMath.ToGrid(_worldPointer, origin));

        // a dropdown swallows the next click wherever it lands
        if (_openMenu != null)
        {
            if (input.Tap.HasValue) _uiPress = true;
            if (UpdateMenu(input)) return;
        }

        if (input.Tap is Point tap && ToolbarBand.Contains(tap))
        {
            _uiPress = true;
            foreach (var b in Buttons())
                if (b.Rect.Contains(tap) && HitButton(b.Id))
                    return;
            return;   // clicks on the bar never fall through to the ground
        }
        UpdateHotkeys(input);
        // The wheel sets the placement height; with Ctrl it zooms instead, in
        // whole steps, so a pixel stays a whole number of screen pixels.
        //
        // While a turning decoration or an enemy is the brush it TURNS that
        // instead. A thing that stands on a square cannot be raised off it
        // anyway, so the wheel has nothing else to do there.
        if (input.ScrollDelta != 0)
        {
            if (input.CtrlHeld) _camera.ZoomBy(Math.Sign(input.ScrollDelta), input.RawPointer);
            else if (RotatingDeco || RotatingEnemy) TurnBrush(input.ScrollDelta);
            else _height = Math.Clamp(_height + input.ScrollDelta, 0, 12);
        }

        // keys and the wheel still work while a UI button is held down; only
        // the tools that touch the level are held off until it is released
        if (_uiPress) return;

        if (_boxMode) { UpdateBox(input, origin); return; }
        if (UpdateFill(input, origin)) return;
        if (UpdateSelect(input, origin)) return;

        UpdatePaint(input, origin);
        UpdateErase(input, origin);

        // right-click: link two transition pads, or open a dialogue square's file
        if (input.AltTap is Point rc && !ToolbarBand.Contains(rc))
        {
            var t = PickTile(_worldPointer, origin) ?? IsoMath.ToGrid(_worldPointer, origin);
            if (_level.TransitionAt(t) != null) LinkTransition(t);
            else if (_level.TriggerAt(t) is LevelTrigger trig) OpenDialogueFile(trig.Dialogue);
            else if (_linkFrom != null) { _linkFrom = null; Status("link cancelled"); }
        }
    }

    private bool Typing =>
        _typingRoom || _typingTrigger || _typingSaveAs || _typingRename || _typingNewMap;

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
    /// <summary>
    /// Yes or no, and nothing else until one of them. Enter or Y goes ahead,
    /// Esc or N or a click backs out — a click is a no here, not a yes, since
    /// the question is always about throwing work away.
    /// </summary>
    private void UpdateConfirm(InputState input)
    {
        bool yes = input.Submit || input.TypedChars.ToLowerInvariant().Contains('y');
        bool no = input.Cancel || input.Tap.HasValue || input.AltTap.HasValue
                  || input.TypedChars.ToLowerInvariant().Contains('n');
        if (!yes && !no) return;

        var go = _confirmDo;
        _confirmDo = null;
        _confirmAsk = "";
        if (yes) go?.Invoke();
        else Status("left as it was");
    }

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

    private void StopTyping() =>
        _typingRoom = _typingTrigger = _typingSaveAs = _typingRename = _typingNewMap = false;

    private void CommitTyping()
    {
        if (_typingRoom) { _room = _roomBuffer.Trim(); Status($"room = {_room}"); }
        else if (_typingSaveAs) SaveAs(_roomBuffer);
        else if (_typingRename) RenameMap(_roomBuffer);
        else if (_typingNewMap) NewMap(_roomBuffer);
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
                case 'o': _tool = Tool.Transition; break;
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

        var tile = Target(_worldPointer, origin).Tile;
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

        var tile = PickTile(_worldPointer, origin)
                   ?? IsoMath.ToGrid(_worldPointer, origin);
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

        var under = Target(_worldPointer, origin).Tile;
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

        var under = PickTile(_worldPointer, origin)
                    ?? IsoMath.ToGrid(_worldPointer, origin);
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

        var under = PickTile(_worldPointer, origin)
                    ?? IsoMath.ToGrid(_worldPointer, origin);
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
        // nearest square first, and inside a square the top of the stack
        foreach (var b in _level.Blocks.Values
                     .OrderByDescending(b => b.X + b.Y).ThenByDescending(b => b.Height))
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
            case Tool.Block when TerrainBrush:
                PaintTerrain(tile);
                break;
            case Tool.Block:
                string type = BrushPiece(tile);
                _level.Blocks[tile] = new LevelBlock
                    { X = tile.X, Y = tile.Y, Height = _height, Type = type, Room = _room };
                break;
            case Tool.Decoration when _level.BlockAt(tile) != null && BlockCatalog.Decorations.Count > 0:
                _level.Decorations.RemoveAll(d => d.X == tile.X && d.Y == tile.Y);
                _level.Decorations.Add(new LevelDecoration
                {
                    X = tile.X, Y = tile.Y,
                    File = BlockCatalog.Decorations[_decoIndex],
                    Facing = _decoFacing,
                });
                break;
            case Tool.Enemy when _level.BlockAt(tile) != null && _ctx.Enemies.EnemyNames.Count > 0:
                _level.Enemies.RemoveAll(e => e.X == tile.X && e.Y == tile.Y);
                _level.Enemies.Add(new LevelEnemy
                {
                    X = tile.X, Y = tile.Y,
                    Name = _ctx.Enemies.EnemyNames[_enemyIndex],
                    Facing = _enemyFacing,
                });
                break;
            // painted like any other overlay; which squares form one pad is
            // decided by which of them touch, not by anything stored
            case Tool.Transition when _level.BlockAt(tile) != null:
                if (_level.TransitionAt(tile) == null)
                    _level.Transitions.Add(new LevelTransition { X = tile.X, Y = tile.Y });
                break;
            case Tool.Trigger when _level.BlockAt(tile) != null:
                _level.Triggers.RemoveAll(t => t.X == tile.X && t.Y == tile.Y);
                _level.Triggers.Add(new LevelTrigger { X = tile.X, Y = tile.Y, Dialogue = _trigger });
                break;
            // paints the room label onto blocks that already exist, so a room
            // can be marked out after the ground is built
            case Tool.Room when _level.BlockAt(tile) is LevelBlock rb:
                rb.Room = _room;
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
        if (_level.Enemies.RemoveAll(e => e.X == tile.X && e.Y == tile.Y) > 0) return;
        if (_level.Transitions.RemoveAll(t => t.X == tile.X && t.Y == tile.Y) > 0) return;
        if (_level.Triggers.RemoveAll(t => t.X == tile.X && t.Y == tile.Y) > 0) return;
        if (_level.PlayerStarts.Remove(tile)) return;
        if (_level.Blocks.TryGetValue(tile, out var gone) && _level.Blocks.Remove(tile))
            ForgetTerrain(tile, gone.Height);
    }

    /// <summary>Everything on a square at once — what the box delete does.</summary>
    private int DeleteAll(Point tile)
    {
        int n = 0;
        n += _level.Decorations.RemoveAll(d => d.X == tile.X && d.Y == tile.Y);
        n += _level.Enemies.RemoveAll(e => e.X == tile.X && e.Y == tile.Y);
        n += _level.Transitions.RemoveAll(t => t.X == tile.X && t.Y == tile.Y);
        n += _level.Triggers.RemoveAll(t => t.X == tile.X && t.Y == tile.Y);
        if (_level.PlayerStarts.Remove(tile)) n++;
        // a box empties a square outright, stack and all
        foreach (var gone in _level.Blocks.RemoveColumn(tile))
            { ForgetTerrain(tile, gone.Height); n++; }
        return n;
    }

    // ---------------- files ----------------

    /// <summary>
    /// What to do if the person says yes, and the question to ask them.
    ///
    /// One question at a time, and only ever about throwing work away.
    /// Anything that would lose unsaved edits goes through Confirm() rather
    /// than doing it and leaving a note on the status line that has already
    /// scrolled away by the time it is read.
    /// </summary>
    private Action? _confirmDo;
    private string _confirmAsk = "";

    /// <summary>Runs an action now if nothing is at stake, or asks first if there is.</summary>
    private void Confirm(string ask, Action then)
    {
        if (!Dirty) { then(); return; }
        _confirmAsk = ask;
        _confirmDo = then;
    }

    /// <summary>Opens another level for editing, dropping undo and the clipboard.</summary>
    private void OpenLevel(string name)
    {
        if (name.Equals(_levelName, StringComparison.OrdinalIgnoreCase)) return;
        Confirm($"{_levelName} has changes that are not saved. Open {name} anyway?",
            () => OpenLevelNow(name));
    }

    private void OpenLevelNow(string name)
    {
        _level = LevelData.Load(name);
        _levelName = name;
        _level.Name = name;
        _undo.Clear();
        _selA = _selB = _selStart = _selEnd = null;
        _boxMode = false;
        // a latched overlay belongs to the level it was turned on over
        _heights.Clear();
        _rooms.Clear();
        RebuildCorners();
        RecountProblems();
        Dirty = false;
        CentreOnLevel();
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
        foreach (var t in _level.Transitions)
            if (_level.BlockAt(new Point(t.X, t.Y)) == null) n++;
        // a pad that leads nowhere is a level that cannot be finished
        n += _level.TransitionPads().Count(p => p.Pair == 0 ||
            _level.TransitionPads().Count(o => o.Pair == p.Pair) != 2);
        foreach (var d in _level.Decorations)
            if (_level.BlockAt(new Point(d.X, d.Y)) == null) n++;
        // a body bigger than one tile needs its whole footprint, same as the
        // startup validator demands
        foreach (var e in _level.Enemies)
            n += FootprintGaps(e);
        n += _level.PlayerStarts.Count(p => _level.BlockAt(p) == null);
        if (_level.PlayerStarts.Count < 4) n++;
        if (_level.Blocks.Count == 0) n++;
        _problems = n;
    }

    /// <summary>
    /// How many squares of an enemy's body have no block under them. One for a
    /// normal enemy standing over the void; up to four for a Living Stone.
    /// </summary>
    private int FootprintGaps(LevelEnemy e)
    {
        var def = _ctx.Enemies.Get(e.Name);
        return Pathfinder.Footprint(new Point(e.X, e.Y), def?.SizeX ?? 1, def?.SizeY ?? 1)
            .Count(t => _level.BlockAt(t) == null);
    }

    /// <summary>A short account of what is wrong, for the toolbar button.</summary>
    private string ProblemText()
    {
        var bits = new List<string>();
        int orphans = _level.Triggers.Count(t => _level.BlockAt(new Point(t.X, t.Y)) == null)
            + _level.Decorations.Count(d => _level.BlockAt(new Point(d.X, d.Y)) == null)
            + _level.Enemies.Sum(FootprintGaps)
            + _level.PlayerStarts.Count(p => _level.BlockAt(p) == null);
        if (orphans > 0) bits.Add($"{orphans} thing(s) floating with no block under them");
        if (_level.PlayerStarts.Count < 4)
            bits.Add($"only {_level.PlayerStarts.Count} player start(s) of 4");
        if (_level.Blocks.Count == 0) bits.Add("no blocks at all");
        return bits.Count == 0 ? "no problems" : string.Join("; ", bits);
    }

    /// <summary>
    /// Middle click: picks up whatever is topmost on a square and sets the
    /// brush to it, so the next click puts the same thing down again.
    ///
    /// TOPMOST, in the same order Delete works in: a body standing on a
    /// square is what you get, then a decoration, then the ground. Picking up
    /// a goblin does NOT also pick up the ground it is standing on - that
    /// would quietly change the ground brush under you, and the next click
    /// somewhere else would drop a block nobody asked for.
    /// </summary>
    private void Eyedropper(Point tile)
    {
        if (EnemyCovering(tile) is LevelEnemy standing)
        {
            int which = _ctx.Enemies.EnemyNames
                .ToList().FindIndex(n => n.Equals(standing.Name, StringComparison.OrdinalIgnoreCase));
            if (which >= 0)
            {
                _enemyIndex = which;
                _enemyFacing = standing.Facing;   // the way it is turned comes too
                _tool = Tool.Enemy;
                Status($"picked up {standing.Name} - the ground under it is untouched");
                return;
            }
        }

        if (_level.DecorationAt(tile) is LevelDecoration deco)
        {
            int which = BlockCatalog.Decorations
                .ToList().FindIndex(d => d.Equals(deco.File, StringComparison.OrdinalIgnoreCase));
            if (which >= 0)
            {
                _decoIndex = which;
                // the eyedropper takes the way it is turned too, so copying
                // one portal and placing another gives you the same portal
                if (BlockCatalog.Turns(deco.File)) _decoFacing = deco.Facing;
                _tool = Tool.Decoration;
                Status($"picked up {Strip(deco.File)} - the ground under it is untouched");
                return;
            }
        }

        if (_level.BlockAt(tile) is not LevelBlock b) { Status("nothing to sample there"); return; }
        _height = b.Height;
        _room = b.Room;

        // Terrain first, because BlockCatalog.Find resolves a terrain token to
        // a piece as well - and a square painted with terrain is one somebody
        // means to carry on painting with terrain, not with the piece its
        // picture happens to be.
        if (TerrainCatalog.Resolve(b.Type) is ValueTuple<TerrainSet, int> found)
        {
            var (set, mask) = found;
            // the terrain holding most of the square's corners is the one
            // somebody pointing at it means
            _terrain = Corners.All.GroupBy(c => set.TerrainAt(mask, c))
                .OrderByDescending(g => g.Count()).First().Key;
            _tool = Tool.Block;
        }
        // adopt the sampled square's family AND its exact piece, not Random
        else if (BlockCatalog.Find(b.Type) is GroundPiece sampled)
        {
            int fam = BlockCatalog.Families
                .ToList().FindIndex(f => f.Equals(sampled.Family, StringComparison.OrdinalIgnoreCase));
            if (fam >= 0)
            {
                _familyIndex = fam;
                _pieceIndex = Math.Max(0, BlockCatalog.PiecesIn(sampled.Family)
                    .ToList().FindIndex(gp => gp.File.Equals(sampled.File, StringComparison.OrdinalIgnoreCase)));
                _terrain = "";
                _tool = Tool.Block;
            }
        }

        int deep = _level.Blocks.Column(tile).Count;
        Status($"picked up {b.Type} at height {Tall(b.Height)} in room {b.Room}" +
               (deep > 1 ? $", {deep} cubes deep" : ""));
    }

    /// <summary>The enemy whose body covers this square, if one does.</summary>
    private LevelEnemy? EnemyCovering(Point tile)
    {
        foreach (var e in _level.Enemies)
        {
            var def = _ctx.Enemies.Get(e.Name);
            int w = def?.SizeX ?? 1, h = def?.SizeY ?? 1;
            if (tile.X >= e.X && tile.X < e.X + w && tile.Y >= e.Y && tile.Y < e.Y + h)
                return e;
        }
        return null;
    }

    private static (int X0, int Y0, int X1, int Y1) Span(Point a, Point b) =>
        (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    /// <summary>Shift+drag: fill the box with the current block at the placement height.</summary>
    private void FillBox(Point a, Point b)
    {
        BeginEdit();
        var (x0, y0, x1, y1) = Span(a, b);
        int squares = (x1 - x0 + 1) * (y1 - y0 + 1);

        // A box fills with whatever the BRUSH is, which is not always ground.
        // Painting a room used to drop blocks at the placement height over the
        // whole box, which is the one thing somebody marking out a room is
        // certainly not asking for.
        if (_tool == Tool.Room)
        {
            int painted = 0;
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    if (_level.BlockAt(new Point(x, y)) is LevelBlock rb) { rb.Room = _room; painted++; }
            Status($"put {painted} square(s) in room {_room}");
            return;
        }
        if (_tool != Tool.Block)
        {
            Status($"a box only fills with ground - the brush is {ToolLabel}");
            return;
        }

        // BrushPiece is called per square, so a Random brush scatters the
        // family across the box instead of stamping one piece over all of it
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                var at = new Point(x, y);
                if (TerrainBrush) PaintTerrain(at);
                else _level.Blocks[at] = new LevelBlock
                    { X = x, Y = y, Height = _height, Type = BrushPiece(at), Room = _room };
            }
        Status($"filled {squares} squares with " +
               $"{(TerrainBrush ? _terrain : RandomBrush ? $"random {FamilyName}" : PieceLabel)} " +
               $"at height {Tall(_height)}");
    }

    /// <summary>+/- with a selection: shift every block inside it up or down.</summary>
    private void RaiseSelection(int by)
    {
        if (_selA is not Point a || _selB is not Point b) return;
        // moving ground up and down is the ground brush's job, nobody else's
        if (_tool != Tool.Block)
        {
            Status($"heights only move with the ground brush - this one paints {ToolLabel}");
            return;
        }
        BeginEdit();
        var (x0, y0, x1, y1) = Span(a, b);
        int n = 0;
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                var at = new Point(x, y);
                var column = _level.Blocks.RemoveColumn(at);
                if (column.Count == 0) continue;
                // the whole column moves together, keeping the gaps between
                // its cubes; re-placed so the grid stays in order
                foreach (var blk in column)
                {
                    blk.Height = Math.Clamp(blk.Height + by, 0, 12);
                    _level.Blocks.Place(at, blk);
                    n++;
                }
            }
        RebuildCorners();
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
        // so playtesting, and opening it again, read what was just written
        MirrorLevel(_levelName + ".txt", _levelName + ".txt");
        Dirty = false;
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
        if (UsableName(typed) is not string name) return;
        _levelName = name;
        _level.Name = name;
        Save();
    }

    /// <summary>
    /// A level name that can be used as a file name, or null with the reason
    /// already on the status line. Names build paths and the dialogue file's
    /// name, so they stay plain: no folders, no extension.
    /// </summary>
    private string? UsableName(string typed)
    {
        string name = typed.Trim();
        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\'))
        {
            Status($"'{typed.Trim()}' is not a usable level name — letters and digits, no folders");
            return null;
        }
        return name;
    }

    /// <summary>
    /// Renames the map in hand: the new file replaces the old one, which goes.
    ///
    /// Save As leaves the original behind; this does not, which is the whole
    /// difference between them. The level's own dialogue file is named after
    /// it, so that moves too — otherwise every trigger would point at a file
    /// nobody could find.
    /// </summary>
    private void RenameMap(string typed)
    {
        if (UsableName(typed) is not string name) return;
        if (name.Equals(_levelName, StringComparison.Ordinal)) { Status("same name"); return; }
        if (File.Exists(Path.Combine(_levelsDir, name + ".txt")))
        {
            Status($"there is already a map called {name} — pick another name");
            return;
        }

        string was = _levelName;
        string wasLevel = SavePath, wasDialogue = DialoguePath;
        _levelName = name;
        _level.Name = name;
        Save();
        try
        {
            if (File.Exists(wasDialogue))
            {
                File.Move(wasDialogue, DialoguePath, overwrite: true);
                MirrorLevel(name + "Dialogue.txt", name + "Dialogue.txt");
                MirrorLevel(was + "Dialogue.txt", null);
            }
            if (File.Exists(wasLevel)) File.Delete(wasLevel);
            MirrorLevel(was + ".txt", null);
        }
        catch (Exception ex)
        {
            Status($"saved as {name}, but the old files are still there: {ex.Message}");
            return;
        }
        Status($"renamed to {name}.txt");
    }

    /// <summary>
    /// An empty map under a new name, saved at once so it is a real file and
    /// shows up in the level list. Nothing is placed on it: the first square
    /// painted is where the map starts.
    /// </summary>
    private void NewMap(string typed)
    {
        if (UsableName(typed) is not string name) return;
        if (File.Exists(Path.Combine(_levelsDir, name + ".txt")))
        {
            Status($"there is already a map called {name} — open it, or pick another name");
            return;
        }

        _level = new LevelData { Name = name };
        _levelName = name;
        Dirty = false;
        _undo.Clear();
        _selA = _selB = _selStart = _selEnd = null;
        _boxMode = false;
        _heights.Clear();
        _rooms.Clear();
        RebuildCorners();
        Save();
        RecountProblems();
        CentreOnLevel();
        Status($"new map {name}.txt — paint a square to start it");
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

    /// <summary>Never called: this screen draws itself. See DrawSelf.</summary>
    public void Draw(SpriteBatch batch) { }

    /// <summary>
    /// Two passes, like the game: the board through the camera at a whole-number
    /// zoom with PointClamp, then the toolbar, panels and every label in the
    /// letterboxed design space where the font is sharp.
    /// </summary>
    public void DrawSelf(SpriteBatch batch, GraphicsDevice device)
    {
        _windowSize = new Point(
            device.PresentationParameters.BackBufferWidth,
            device.PresentationParameters.BackBufferHeight);
        if (!_framed) { CentreOnLevel(); _framed = true; }

        device.Viewport = new Viewport(0, 0, _windowSize.X, _windowSize.Y);
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
            SamplerState.PointClamp, null, null, null, _camera.Matrix);
        _labels.Clear();
        var origin = Vector2.Zero;

        // Faint base grid so there's something to aim at over the void. It
        // covers WHAT YOU CAN SEE, worked out from the window's four corners —
        // not a patch around the pointer, which slid about as the mouse moved
        // and ran off the screen entirely if you moved far enough, leaving
        // black.
        foreach (var tile in VisibleGrid(origin))
            if (!_level.Blocks.ContainsKey(tile))
                DrawTop(batch, tile.X, tile.Y, 0, origin, Color.White * 0.06f);

        foreach (var b in _level.Blocks.Values.OrderBy(b => b.X + b.Y).ThenBy(b => b.X))
        {
            BlockCatalog.Draw(batch, _ctx.Assets, b.Type,
                IsoMath.ToScreen(b.X, b.Y, b.Height, origin), Color.White);

            // Everything that stands ON a square belongs to the square, not to
            // each cube of its stack: a tree on a two-high column is one tree,
            // on the top of it.
            var tile = new Point(b.X, b.Y);
            if (_level.BlockAt(tile) != b) continue;

            if (_level.DecorationAt(tile) is LevelDecoration deco)
                Billboard(batch, BlockCatalog.Turns(deco.File)
                        ? BlockCatalog.DecorationPath(deco.File, deco.Facing)
                        : BlockCatalog.DecorationPath(deco.File),
                    tile, b.Height, origin, Color.White, deco.File);
            // An enemy goes down when the LAST square of its body is painted,
            // not the first. Drawn on its anchor, a body covering more than one
            // tile was buried by its own other squares — every one of them has
            // a greater X+Y, so every one of them painted over its legs.
            foreach (var e in EnemiesEndingAt(tile))
                if (_ctx.Enemies.Get(e.Name) is EnemyDef def)
                    Cast(batch, new Point(e.X, e.Y), b.Height, origin, Color.White * 0.9f, def, e.Facing);
            if (_level.PlayerStarts.Contains(tile))
                DrawTop(batch, b.X, b.Y, b.Height, origin, Color.LimeGreen * 0.35f);
            if (_level.TriggerAt(tile) is LevelTrigger trig)
            {
                DrawTop(batch, b.X, b.Y, b.Height, origin, Color.Violet * 0.4f);
                var tc = IsoMath.ToScreen(b.X, b.Y, b.Height, origin);
                Label(tc + new Vector2(0, -10), trig.Dialogue, Color.Violet, 0.26f);
            }
            if (InSpan(_boxStart, _boxEnd, tile))
                DrawTop(batch, b.X, b.Y, b.Height, origin, Color.Red * 0.45f);
            if (InSpan(_fillStart, _fillEnd, tile))
                DrawTop(batch, b.X, b.Y, b.Height, origin, Color.Orange * 0.45f);
            if (InSpan(_selStart, _selEnd, tile) || InSpan(_selA, _selB, tile))
                DrawTop(batch, b.X, b.Y, b.Height, origin, Color.Cyan * 0.35f);
            if (_level.TransitionAt(tile) != null)
            {
                bool linked = _level.TransitionAt(tile)!.Pair != 0;
                // orange for a live pad, grey for one that leads nowhere yet,
                // so a half-built link is obvious without opening the file
                DrawTop(batch, b.X, b.Y, b.Height, origin,
                    (linked ? new Color(255, 150, 40) : new Color(150, 150, 150)) * 0.5f);
            }
            if (_rooms.On)
                DrawTop(batch, b.X, b.Y, b.Height, origin, RoomColor(b.Room) * 0.55f);

            // The height goes down INSIDE this loop, not in a pass of its own
            // afterwards. Drawn afterwards, the number for a block hidden
            // behind a taller neighbour still showed — landing on the face of
            // the block in front of it and reading as that block's height.
            // Here a nearer block paints over it, exactly as it does the art.
            if (_heights.On) DrawHeightLabel(b, origin);
        }

        // A body whose far corner has no block under it was never reached by
        // the loop above, so it would vanish while being placed. It is drawn
        // here instead — the problem counter is already complaining about it,
        // and an invisible enemy is a worse way to find that out.
        foreach (var e in _level.Enemies)
        {
            if (_ctx.Enemies.Get(e.Name) is not EnemyDef def) continue;
            var far = new Point(e.X + def.SizeX - 1, e.Y + def.SizeY - 1);
            if (_level.BlockAt(far) != null) continue;
            Cast(batch, new Point(e.X, e.Y),
                _level.BlockAt(new Point(e.X, e.Y))?.Height ?? 0, origin, Color.Red * 0.8f,
                def, e.Facing);
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

        DrawTransitionLinks(batch, origin);
        DrawCursor(batch, origin);
        DrawHoveredEnemy(batch, origin);
        DrawRoomLabels(origin);
        batch.End();

        _ctx.Viewport.Apply(device);
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
            SamplerState.LinearClamp, null, null, null, _ctx.Viewport.Matrix);
        DrawLabels(batch);
        if (_rooms.On) DrawRoomKey(batch);
        DrawToolbar(batch);
        DrawHudText(batch);
        // last, so both cover whatever they hang over
        DrawControlsPanel(batch);
        DrawOpenMenu(batch);
        batch.End();
    }

    /// <summary>Opens looking at the middle of the level, or at the origin of an empty one.</summary>
    private void CentreOnLevel()
    {
        var blocks = _level.Blocks.Values.ToList();
        Point middle = Point.Zero;
        if (blocks.Count > 0)
            middle = IsoMath.ToScreen(
                (blocks.Min(b => b.X) + blocks.Max(b => b.X)) / 2,
                (blocks.Min(b => b.Y) + blocks.Max(b => b.Y)) / 2, 0, Vector2.Zero).ToPoint();
        _camera.CentreOn(middle, _windowSize.X, _windowSize.Y);
    }

    private void Label(Vector2 world, string text, Color ink, float scale, bool plate = false) =>
        _labels.Add((world, text, ink, scale, plate));

    /// <summary>A board point in the design space the HUD uses: world -> window -> design.</summary>
    private Vector2 ToDesign(Vector2 world)
    {
        var window = _camera.ToScreen(world.ToPoint());
        float scale = Math.Max(0.0001f, _ctx.Viewport.Scale);
        return new Vector2((window.X - _ctx.Viewport.Offset.X) / scale,
            (window.Y - _ctx.Viewport.Offset.Y) / scale);
    }

    private void DrawLabels(SpriteBatch batch)
    {
        foreach (var (world, text, ink, scale, plate) in _labels)
        {
            var at = ToDesign(world);
            var size = _ctx.Font.MeasureString(text) * scale;
            var box = new Rectangle((int)(at.X - size.X / 2), (int)(at.Y - size.Y / 2),
                (int)size.X, (int)size.Y);
            if (box.Right < 0 || box.X > VirtualViewport.Width ||
                box.Bottom < 0 || box.Y > VirtualViewport.Height) continue;
            // a dark plate under the digits, so a number on pale ground still reads
            if (plate)
                Ui.FillRect(batch, _ctx.Pixel,
                    new Rectangle(box.X - 10, box.Y - 6, box.Width + 20, box.Height + 12), Color.Black * 0.55f);
            batch.DrawString(_ctx.Font, text, new Vector2(box.X, box.Y), ink,
                0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        }
    }

    /// <summary>
    /// Right-clicking a transition pad. The first one is remembered; the second
    /// joins the two, giving both a fresh pair number. Right-clicking the pad
    /// that is already armed cancels, and right-clicking a pad already linked to
    /// something breaks that link first, so relinking never leaves a third pad
    /// wearing the same number.
    /// </summary>
    private void LinkTransition(Point tile)
    {
        var pads = _level.TransitionPads();
        var pad = pads.FirstOrDefault(p => p.Covers(tile));
        if (pad == null) return;

        if (_linkFrom == null)
        {
            _linkFrom = tile;
            Status($"linking from the pad at {pad.Key.X},{pad.Key.Y} — right-click its far end");
            return;
        }

        var from = pads.FirstOrDefault(p => p.Covers(_linkFrom.Value));
        if (from == null) { _linkFrom = tile; Status("that pad is gone; starting again here"); return; }
        if (from.Key == pad.Key) { _linkFrom = null; Status("link cancelled"); return; }

        // whatever either end used to be joined to is let go, so no third pad
        // is left sharing the number
        foreach (var stale in new[] { from.Pair, pad.Pair }.Where(n => n != 0).Distinct())
            foreach (var t in _level.Transitions.Where(t => t.Pair == stale))
                t.Pair = 0;

        int pair = NextPair();
        foreach (var t in _level.Transitions)
            if (from.Covers(new Point(t.X, t.Y)) || pad.Covers(new Point(t.X, t.Y)))
                t.Pair = pair;

        Status($"pair {pair}: {from.Key.X},{from.Key.Y} <-> {pad.Key.X},{pad.Key.Y}");
        _linkFrom = null;
    }

    /// <summary>
    /// The enemies whose body finishes on this square — its far corner, the one
    /// with the greatest X+Y. That is the point in the painter's order after
    /// which nothing the body stands on can be drawn over it.
    /// </summary>
    private IEnumerable<LevelEnemy> EnemiesEndingAt(Point tile) =>
        _level.Enemies.Where(e =>
        {
            var d = _ctx.Enemies.Get(e.Name);
            return e.X + (d?.SizeX ?? 1) - 1 == tile.X && e.Y + (d?.SizeY ?? 1) - 1 == tile.Y;
        });

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
            var at = PickTile(_worldPointer, origin)
                     ?? IsoMath.ToGrid(_worldPointer, origin);
            int h = _level.BlockAt(at)?.Height ?? 0;
            DrawTop(batch, at.X, at.Y, h, origin, Color.Red * 0.55f);
            return;
        }

        // hovered cell, sitting at the height it would actually place at, with
        // that height written in the middle so it can be read against the blocks
        var (cursor, cursorHeight) = Target(_worldPointer, origin);

        // The blue line sits on what is UNDER the cursor — the top of the
        // ground already there, or the flat grid where there is none — while
        // the yellow square lifts to the placement height. Working inside a
        // room built two high, it lands on that room's floor rather than
        // sinking to zero and saying nothing about where the block will go.
        DrawTopOutline(batch, cursor.X, cursor.Y, GroundUnder(cursor), origin,
            new Color(80, 170, 255));
        DrawTop(batch, cursor.X, cursor.Y, cursorHeight, origin, Color.Yellow * 0.3f);
        Label(IsoMath.ToScreen(cursor.X, cursor.Y, cursorHeight, origin),
            Tall(cursorHeight), Color.Yellow, 0.36f);

        if (RotatingDeco || RotatingEnemy) DrawRotateHint(batch, cursor, cursorHeight, origin);
    }

    /// <summary>
    /// What the brush is about to place, and the two curved arrows that say
    /// the wheel will turn it.
    ///
    /// The ghost matters more than the arrows: a portal has a front and a
    /// back, and being told which way it will face BEFORE it goes down is the
    /// whole reason the wheel does this. The arrows are there so nobody has
    /// to be told the wheel does it.
    /// </summary>
    private void DrawRotateHint(SpriteBatch batch, Point cursor, int height, Vector2 origin)
    {
        if (RotatingEnemy && _ctx.Enemies.Get(EnemyName) is EnemyDef ghost)
            Cast(batch, cursor, height, origin, Color.White * 0.55f, ghost, _enemyFacing);
        else
            Billboard(batch, BlockCatalog.DecorationPath(DecoFile, _decoFacing),
                cursor, height, origin, Color.White * 0.55f, DecoFile);

        var c = IsoMath.ToScreen(cursor.X, cursor.Y, height, origin);
        var ink = new Color(190, 140, 255);
        // an ellipse on the ground, not a circle: the grid is seen at an angle
        float rx = IsoMath.TileW * 0.72f, ry = IsoMath.TileH * 0.72f;
        TurnArrow(batch, c, rx, ry, 200f, 340f, ink);
        TurnArrow(batch, c, rx, ry, 20f, 160f, ink);

        Label(c + new Vector2(0, IsoMath.TileH * 1.1f), BrushFacing.FileName(), ink, 0.30f);
    }

    /// <summary>
    /// One curved arrow lying on the ground, swept between two angles with a
    /// head on the end it is pointing at.
    /// </summary>
    private void TurnArrow(SpriteBatch batch, Vector2 centre, float rx, float ry,
        float fromDeg, float toDeg, Color color)
    {
        Vector2 At(float deg)
        {
            float r = MathHelper.ToRadians(deg);
            return centre + new Vector2((float)Math.Cos(r) * rx, (float)Math.Sin(r) * ry);
        }

        const int Steps = 16;
        var prev = At(fromDeg);
        for (int i = 1; i <= Steps; i++)
        {
            var next = At(MathHelper.Lerp(fromDeg, toDeg, i / (float)Steps));
            Line(batch, prev, next, color, 3f);
            prev = next;
        }
        // the head points along the sweep, so the pair read as turning rather
        // than as a ring drawn round the square
        var tip = At(toDeg);
        var back = At(MathHelper.Lerp(fromDeg, toDeg, (Steps - 2) / (float)Steps));
        Arrow(batch, back, tip, color);
    }

    private void DrawToolbar(SpriteBatch batch)
    {
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, BarBottom), new Color(14, 14, 20, 235));

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
    /// <summary>
    /// One row of the Controls panel: the key you press, and what it does.
    /// Split in two so the keys can run down their own column in blue and be
    /// picked out at a glance. An empty Key is a line that is only prose.
    /// </summary>
    private readonly record struct ControlRow(string Key, string What);

    private static readonly ControlRow[] ControlLines =
    {
        new("hold left",       "paint"),
        new("hold Del",        "rub out"),
        new("Shift+drag",      "fill a box with the ground brush, at the placement height"),
        new("Ctrl+Del, drag",  "empty a box"),
        new("Ctrl+drag",       "select a box"),
        new("+ / -",           "raise and lower the selection (ground brush only)"),
        new("Ctrl+C",          "copy the selection"),
        new("Ctrl+V",          "paste it onto the square under the cursor"),
        new("Del",             "empty the selection"),
        new("Esc",             "drop the selection"),
        new("middle click",    "eyedropper: the topmost thing on that square"),
        new("right-click",     "on a trigger, opens that level's dialogue file"),
        new("Ctrl+Z",          "undo the last stroke"),
        new("Ctrl+S",          "save"),
        new("scroll wheel",    "placement height, in blocks: one cube tall is 1 - but turns an enemy, or a decoration that turns"),
        new("Ctrl+wheel",      "zoom, in whole pixels"),
        new("WASD, ARROWS",    "pan the view"),
        new("ALT",             "block heights: hold for a glance, tap to leave it up"),
        new("SPACE",           "room colours: hold for a glance, tap to leave it up"),
        new("", ""),
        new("1 2 3",           "ground family"),
        new("B",               "next piece of that family, then Random"),
        new("O",               "doorway"),
        new("E",               "next enemy"),
        new("P",               "player start"),
        new("G",               "dialogue square"),
        new("R",               "name a new room"),
        new("N",               "name a new dialogue"),
        new("V",               "save as"),
        new("T",               "playtest the level"),
        new("", ""),
        new("top row",         "the BRUSH: what a click on the ground would leave there"),
        new("bottom row",      "the MAP: which file, which room, is it alright"),
        new("File",            "import, reload, save, save as, rename, playtest"),
        new("Current Level",   "open another map, or make a new one"),
        new("Terrain",         "pick a terrain; painting blends it into its neighbours"),
        new("Terrain menu",    "terrains that can blend sit side by side in it"),
        new("Doorway",         "paint two pads, then right-click one and its far end"),
        new("the arrow",       "points from pad A to pad B; the trip works both ways"),
        new("Room",            "pick a room to paint with, or name a new one"),
        new("Paint room",      "drag over squares to put them in that room"),
        new("Check for Errors", "look again for what this map gets wrong"),
        new("hover a button",  "says what it does, above the status line"),
        new("", ""),
        new("Insert",          "or the Controls button hides this again"),
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

        // Keys down the left in blue, what they do in a column of their own.
        // The column is as wide as the longest key actually needs, measured
        // rather than guessed, so it stays right if a key is renamed.
        var rows = ControlLines;
        float keyColumn = rows.Max(r => _ctx.Font.MeasureString(r.Key).X) * scale + 40;
        var keyInk = new Color(120, 180, 255);

        int y = box.Y + 46;
        foreach (var row in rows)
        {
            if (row.Key.Length > 0)
                batch.DrawString(_ctx.Font, row.Key, new Vector2(box.X + 60, y),
                    keyInk, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            if (row.What.Length > 0)
                batch.DrawString(_ctx.Font, row.What, new Vector2(box.X + 60 + keyColumn, y),
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

    /// <summary>
    /// The top of whatever is already on a square, or the flat grid at zero
    /// when nothing is. What a block placed there would be standing on.
    /// </summary>
    private int GroundUnder(Point square) => _level.BlockAt(square)?.Height ?? 0;

    /// <summary>
    /// The outline of a square's top — one pixel of art, traced round the
    /// diamond rather than drawn as a box, so it follows the shape the tiles
    /// actually are.
    /// </summary>
    private void DrawTopOutline(SpriteBatch batch, int gx, int gy, int height,
        Vector2 origin, Color ink)
    {
        var top = _ctx.Assets.LoadTexture("Content/Images/Blocks/OverlayTop.png");
        var c = IsoMath.ToScreen(gx, gy, height, origin);
        batch.Draw(_ctx.Assets.Outline(top, 1),
            new Rectangle((int)(c.X - IsoMath.TileW / 2f), (int)(c.Y - IsoMath.TileH / 2f),
                IsoMath.TileW, IsoMath.TileH), ink);
    }

    /// <summary>
    /// An enemy on the ground, exactly as the game draws it: its front
    /// rotation, or its cube, hung by the lowest solid pixel on the middle of
    /// its footprint.
    /// </summary>
    private void Cast(SpriteBatch batch, Point tile, int height, Vector2 origin, Color tint,
        EnemyDef def, Facing8 facing)
    {
        var art = _ctx.Sprites.Standing(new CharacterInstance
        {
            Name = def.Name, IsPlayer = false, SizeX = def.SizeX, SizeY = def.SizeY,
            Facing = facing,
        });
        var solid = ArtBounds.Solid(art);
        var foot = IsoMath.ToScreen(tile.X, tile.Y, height, origin);
        foot.X += (def.SizeX - def.SizeY) * (IsoMath.TileW / 2f) / 2f;
        foot.Y += (def.SizeX + def.SizeY - 2) * (IsoMath.TileH / 2f) / 2f;
        var rect = new Rectangle(
            (int)foot.X - (solid.Left + solid.Right) / 2,
            (int)foot.Y - solid.Bottom, art.Width, art.Height);
        batch.Draw(art, rect, tint);
    }

    /// <summary>
    /// A red line round the ART of whichever enemy the cursor is over.
    ///
    /// Delete takes the topmost thing on a square, and on a square with an
    /// enemy standing on it that is the enemy, not the ground. Drawn last, on
    /// top of the cursor square, because the cursor washes everything under it
    /// yellow and a warning you have to squint at is not a warning.
    /// </summary>
    private void DrawHoveredEnemy(SpriteBatch batch, Vector2 origin)
    {
        if (ToolbarBand.Contains(_pointer) || _confirmDo != null || Typing) return;
        var over = PickTile(_worldPointer, origin) ?? IsoMath.ToGrid(_worldPointer, origin);

        foreach (var e in _level.Enemies)
        {
            if (_ctx.Enemies.Get(e.Name) is not EnemyDef def) continue;
            if (over.X < e.X || over.X >= e.X + def.SizeX ||
                over.Y < e.Y || over.Y >= e.Y + def.SizeY) continue;

            var who = new CharacterInstance
            {
                Name = def.Name, IsPlayer = false, SizeX = def.SizeX, SizeY = def.SizeY,
            };
            var art = _ctx.Sprites.Standing(who);
            var solid = ArtBounds.Solid(art);
            var far = new Point(e.X + def.SizeX - 1, e.Y + def.SizeY - 1);
            int height = _level.BlockAt(new Point(e.X, e.Y))?.Height
                         ?? _level.BlockAt(far)?.Height ?? 0;
            var foot = IsoMath.ToScreen(e.X, e.Y, height, origin);
            foot.X += (def.SizeX - def.SizeY) * (IsoMath.TileW / 2f) / 2f;
            foot.Y += (def.SizeX + def.SizeY - 2) * (IsoMath.TileH / 2f) / 2f;
            batch.Draw(_ctx.Assets.Outline(art, IsoMath.Px(2)), new Rectangle(
                    (int)foot.X - (solid.Left + solid.Right) / 2,
                    (int)foot.Y - solid.Bottom, art.Width, art.Height),
                new Color(255, 45, 45));
            return;
        }
    }

    /// <summary>
    /// A decoration on its square. Nudged by the same Anchors.txt the game
    /// reads, so a trash can anchored in the ~ menu sits where the editor
    /// shows it rather than somewhere else once the level is played.
    /// </summary>
    private void Billboard(SpriteBatch batch, string path, Point tile, int height, Vector2 origin,
        Color tint, string? anchorKey = null)
    {
        var tex = _ctx.Assets.LoadTexture(path);
        var c = IsoMath.ToScreen(tile.X, tile.Y, height, origin);
        var by = anchorKey == null ? ArtAnchors.None : ArtAnchors.Current.For(anchorKey);
        int w = Math.Min(tex.Width, IsoMath.TileW);
        int h = (int)(w * tex.Height / (float)tex.Width);
        batch.Draw(tex, new Rectangle(
            (int)(c.X - w / 2f) + by.X, (int)(c.Y + 5 - h) - by.Y, w, h), tint);
    }

    /// <summary>
    /// A teal line between the two ends of every linked pair, plus a dashed one
    /// from the pad waiting to be linked to the cursor. Editor only — the game
    /// draws none of this.
    /// </summary>
    private void DrawTransitionLinks(SpriteBatch batch, Vector2 origin)
    {
        var pads = _level.TransitionPads();
        var teal = new Color(0, 200, 190);

        foreach (var group in pads.Where(p => p.Pair != 0).GroupBy(p => p.Pair))
        {
            // A and B by the pad's own Key, which is its lowest square and
            // does not move about as the map is edited - so the arrow points
            // the same way every time the level is opened.
            var ends = group.OrderBy(p => p.Key.Y).ThenBy(p => p.Key.X).ToList();
            // a pair with anything other than two ends is broken; the validator
            // says so, and drawing every leg makes it visible here too
            var ink = ends.Count == 2 ? teal : Color.Red;

            for (int i = 0; i < ends.Count; i++)
            {
                Label(Middle(ends[i], origin) + new Vector2(0, -34),
                    $"{ends[i].Pair}{(char)('A' + Math.Min(i, 25))}", ink, 0.34f);
                for (int j = i + 1; j < ends.Count; j++)
                    Arrow(batch, Middle(ends[i], origin), Middle(ends[j], origin), ink);
            }
        }

        if (_linkFrom is Point from && pads.FirstOrDefault(p => p.Covers(from)) is TransitionPad armed)
            Arrow(batch, Middle(armed, origin), _worldPointer, teal * 0.6f);
    }

    /// <summary>
    /// A line from one pad to the other with a head on the far end, so which
    /// way round a pair is linked can be read at a glance instead of guessed
    /// at. The head is deliberately chunky: at the zoom somebody builds a map
    /// at, a neat little one is three pixels and says nothing.
    /// </summary>
    private void Arrow(SpriteBatch batch, Vector2 a, Vector2 b, Color color)
    {
        const float HeadLength = 26f, HeadHalfWidth = 13f;
        var span = b - a;
        if (span.LengthSquared() < 1f) return;
        var dir = Vector2.Normalize(span);

        // the shaft stops where the head starts, so the two do not pile up
        Line(batch, a, b - dir * (HeadLength * 0.9f), color, 3f);

        // filled by laying strokes across it, close enough together that
        // rounding each one to whole pixels cannot leave gaps between them
        var across = new Vector2(-dir.Y, dir.X);
        for (float d = 0; d <= HeadLength; d += 0.34f)
        {
            float half = HeadHalfWidth * (d / HeadLength);
            var centre = b - dir * d;
            Line(batch, centre - across * half, centre + across * half, color, 2f);
        }
    }

    /// <summary>The screen point at the middle of a pad's top faces.</summary>
    private Vector2 Middle(TransitionPad pad, Vector2 origin)
    {
        var sum = Vector2.Zero;
        foreach (var t in pad.Tiles)
            sum += IsoMath.ToScreen(t.X, t.Y, _level.BlockAt(t)?.Height ?? 0, origin);
        return sum / Math.Max(1, pad.Tiles.Count);
    }

    /// <summary>A straight line, drawn by stretching and rotating the 1x1 pixel.</summary>
    private void Line(SpriteBatch batch, Vector2 a, Vector2 b, Color color, float thickness)
    {
        var span = b - a;
        float length = span.Length();
        if (length < 0.5f) return;
        batch.Draw(_ctx.Pixel, new Rectangle((int)a.X, (int)a.Y, (int)length, (int)thickness),
            null, color, (float)Math.Atan2(span.Y, span.X), new Vector2(0, 0.5f), SpriteEffects.None, 0f);
    }

    /// <summary>
    /// One block's height, written on its top face. Odd numbers are magenta so
    /// a run of steps can be counted without reading every digit — the eye
    /// picks up the alternation on its own.
    /// </summary>
    private void DrawHeightLabel(LevelBlock b, Vector2 origin) =>
        Label(IsoMath.ToScreen(b.X, b.Y, b.Height, origin), Tall(b.Height),
            b.Height % IsoMath.StepsPerBlock != 0 ? Color.Magenta : Color.White,
            0.34f, plate: true);

    /// <summary>
    /// The legend for the room overlay: which colour is which room, and how
    /// many blocks each one holds. Without it the wash is pretty but unreadable.
    /// </summary>
    private void DrawRoomKey(SpriteBatch batch)
    {
        var rooms = RoomOrder();
        if (rooms.Count == 0) return;

        int y = BarBottom + 20;
        var panel = new Rectangle(BarX0, y, 620, 56 + rooms.Count * 52);
        Ui.FillRect(batch, _ctx.Pixel, panel, Color.Black * 0.72f);
        batch.DrawString(_ctx.Font, "rooms", new Vector2(panel.X + 20, panel.Y + 12),
            Color.White, 0f, Vector2.Zero, 0.3f, SpriteEffects.None, 0f);

        for (int i = 0; i < rooms.Count; i++)
        {
            int row = panel.Y + 56 + i * 52;
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(panel.X + 20, row, 40, 36), RoomColor(rooms[i]));
            int count = _level.Blocks.Values.Count(b =>
                b.Room.Equals(rooms[i], StringComparison.OrdinalIgnoreCase));
            batch.DrawString(_ctx.Font, $"{rooms[i]}   {count}",
                new Vector2(panel.X + 76, row), Color.White, 0f, Vector2.Zero, 0.3f, SpriteEffects.None, 0f);
        }
    }

    private void DrawRoomLabels(Vector2 origin)
    {
        foreach (var group in _level.Blocks.Values.GroupBy(b => b.Room, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.OrderBy(b => b.X + b.Y).First();
            var c = IsoMath.ToScreen(first.X, first.Y, first.Height, origin);
            Label(c + new Vector2(0, -40), group.Key, Color.Cyan * 0.8f, 0.32f);
        }
    }

    private void DrawHudText(SpriteBatch batch)
    {
        // editor is a dev tool: literal strings, not Strings.txt.
        // The status line sits along the BOTTOM so it is never mistaken for
        // part of the toolbar and never covers the ground being built.
        int y = VirtualViewport.Height - 64;
        string mode = _boxMode ? "  BOX DELETE: drag a box, Esc to cancel"
            : _selA != null ? "  SELECTION: +/- raise, Ctrl+C copy, Ctrl+V paste, Del empty, Esc drop"
            : "";
        // height lives here now that the -/0 ft/+ buttons are gone; the wheel
        // still sets it, so it needs somewhere to be read
        batch.DrawString(_ctx.Font,
            $"{_levelName}.txt{(Dirty ? " (Not Saved)" : "")}   room: {_room}   " +
            $"blocks: {_level.Blocks.Count}   painting: {ToolLabel}   " +
            $"height: {Tall(_height)}{mode}",
            new Vector2(BarX0, y), _boxMode ? Color.Red : Color.Yellow,
            0f, Vector2.Zero, 0.30f, SpriteEffects.None, 0f);

        // What the button under the cursor would do, said once, where the
        // status already is. The Controls panel is for learning the editor;
        // this is for the moment you have forgotten one button.
        if (HoverHint() is string hint)
            batch.DrawString(_ctx.Font, hint, new Vector2(BarX0, y - 52), Color.SkyBlue,
                0f, Vector2.Zero, 0.30f, SpriteEffects.None, 0f);

        // A question about losing work, in the same place and the same shape
        // as text entry, because it stops the editor the same way.
        if (_confirmDo != null)
        {
            var ask = new Rectangle(VirtualViewport.Width / 2 - 1000, 300, 2000, 200);
            Ui.FillRect(batch, _ctx.Pixel, ask, new Color(16, 10, 10, 245));
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(ask.X, ask.Y, ask.Width, 4), Color.Orange);
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(ask.X, ask.Bottom - 4, ask.Width, 4), Color.Orange);
            Ui.DrawTextCentered(batch, _ctx.Font, _confirmAsk,
                new Rectangle(ask.X, ask.Y + 40, ask.Width, 70), Color.Orange, 0.34f);
            Ui.DrawTextCentered(batch, _ctx.Font,
                "Y or Enter goes ahead and loses them  ·  N, Esc or a click keeps them",
                new Rectangle(ask.X, ask.Y + 120, ask.Width, 50), Color.White * 0.7f, 0.26f);
        }
        // Text entry stops the rest of the editor responding, so it says so
        // where it cannot be missed rather than as one more line of HUD.
        else if (Typing)
        {
            var box = new Rectangle(VirtualViewport.Width / 2 - 900, 300, 1800, 200);
            Ui.FillRect(batch, _ctx.Pixel, box, new Color(10, 10, 16, 245));
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(box.X, box.Y, box.Width, 4), Color.Cyan);
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(box.X, box.Bottom - 4, box.Width, 4), Color.Cyan);
            Ui.DrawTextCentered(batch, _ctx.Font,
                (_typingRoom ? "room name: "
                    : _typingSaveAs ? "save as: "
                    : _typingRename ? "rename this map to: "
                    : _typingNewMap ? "name the new map: "
                    : "dialogue name: ")
                + _roomBuffer + "_",
                new Rectangle(box.X, box.Y + 40, box.Width, 70), Color.Cyan, 0.42f);
            Ui.DrawTextCentered(batch, _ctx.Font,
                "Enter accepts  ·  Esc or a click anywhere abandons it",
                new Rectangle(box.X, box.Y + 120, box.Width, 50), Color.White * 0.7f, 0.26f);
        }
        else if (_statusTimer > 0 && HoverHint() == null)
            batch.DrawString(_ctx.Font, _status, new Vector2(BarX0, y - 52), Color.LightGreen,
                0f, Vector2.Zero, 0.32f, SpriteEffects.None, 0f);
    }
}
