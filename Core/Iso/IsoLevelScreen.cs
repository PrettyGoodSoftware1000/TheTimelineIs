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

/// <summary>
/// The isometric test level over a black void. The party explores freely and
/// individually; walking within 15 tiles of a revealed enemy springs combat —
/// the rest of the party first gets a free positioning move, then turn order
/// rolls (sides shuffled, alternating).
///
/// Each highlighted region is a colour wash with a border around its outer
/// edge, at the strengths Config.txt gives: blue for where the selected
/// character can walk, red for an armed card's reach, purple for an area. Red
/// replaces blue while a card is up, and none of it shows until somebody is
/// selected.
///
/// One click does everything. Clicking an enemy targets it, and a card that
/// wants several targets fires the moment its last one is clicked; if the
/// caster has to close the distance first it walks there itself. Right-click
/// cancels the armed card.
///
/// Both sides act through the same card pipeline — the party's from
/// PlayerCards.txt, enemies' from EnemyCards.txt — so hit sequences, sounds,
/// projectiles and effects behave identically whoever plays them.
///
/// Nothing about damage is printed over the level. Every blow, burn, theft and
/// turn event goes into the log behind the + button at the top left.
///
/// Stepping on a trigger square plays its dialogue block once.
/// </summary>
public partial class IsoLevelScreen : IScreen, IDrawsItself
{
    private enum Mode
    {
        Explore, FreeMove, PlayerTurn, PlayerTarget, StealPick, EnemyTurn, Acting, Victory,
        /// <summary>Watching a recording. Nothing is decided here, only shown.</summary>
        Replay,
    }
    private enum Act { Casting, Projectile, MeleeWait, Hits, Mowing, Tripping }

    private const int AggroTiles = 15;

    /// <summary>How much of a sprite survives while Ctrl is held: 80% transparent.</summary>
    private const float CtrlFade = 0.2f;
    private const float WalkTilesPerSec = 5f;

    private readonly GameContext _ctx;
    private readonly LevelData _level;
    private readonly DialogueLibrary _dialogue;
    private readonly List<CharacterInstance> _party = new();
    private readonly List<CharacterInstance> _enemies = new();
    private readonly HashSet<string> _revealed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<CharacterInstance> _aggroed = new();
    private readonly List<CharacterInstance> _order = new();
    private static readonly Random Rng = new();

    private Mode _mode = Mode.Explore;
    private int _turn = -1;

    /// <summary>
    /// The running record of this mission, written to a file by the Save Replay
    /// button or automatically when the mission ends. Recording costs a list
    /// entry per event and nothing else, so it is always on — a replay nobody
    /// asked for is far cheaper than one somebody wanted and did not get.
    /// </summary>
    private readonly Replay _replay = new();

    /// <summary>Which turn number the recorder is filing events under.</summary>
    private int _replayTurn;

    /// <summary>
    /// Whether anything is being written down. Off until asked for: most
    /// missions are not worth keeping, and a folder filling with recordings
    /// nobody wanted is its own kind of mess.
    /// </summary>
    private bool _recording;

    /// <summary>Set once the mission's ending has been recorded, so it is only written once.</summary>
    private bool _ended;
    /// <summary>
    /// Who is picked out of combat. Several at once: shift adds, ctrl toggles
    /// one, tab or the middle button takes everybody — the way files are picked
    /// in a file manager. In combat it only ever holds whoever's turn it is.
    /// </summary>
    private readonly List<CharacterInstance> _picked = new();

    /// <summary>The one whose movement is shown, which is the last one added.</summary>
    private CharacterInstance? _selected
    {
        get => _picked.LastOrDefault(p => p.Alive);
        set { _picked.Clear(); if (value != null) _picked.Add(value); }
    }
    private readonly HashSet<CharacterInstance> _freeMovers = new();
    private List<Card> _hand = new();
    private Card? _selectedCard;
    private readonly List<CharacterInstance> _targets = new();  // chosen targets, in click order
    private HashSet<Point> _blastSet = new();

    /// <summary>Ground the card in flight will set alight when it lands.</summary>
    private HashSet<Point> _burnArea = new();

    /// <summary>The square a sky-angled shot is falling onto.</summary>
    private Point _skyTarget;

    /// <summary>
    /// The square the card in flight was aimed at. Kept because the effects run
    /// long after the aim has been cleared, and a summon needs to know where
    /// the player pointed.
    /// </summary>
    private Point _aimPoint;

    /// <summary>
    /// Burning ground: a square, and how many more turns it stays alight.
    /// Anyone who STARTS their turn on one takes damage, so walking across a
    /// fire and off it again is free — standing in it is not.
    /// </summary>
    private readonly Dictionary<Point, int> _fires = new();

    /// <summary>Runs the fire art, and everything else that loops on the ground.</summary>
    private float _clock;

    /// <summary>The looping animation burning squares are drawn with, if the art exists.</summary>
    private SpriteAnimation? _fireAnim;
    private bool _fireAnimTried;

    // a steal waiting for the thief to choose what to take
    private CharacterInstance? _stealVictim;
    private int _stealTurns;
    private List<Card> _stealOptions = new();
    private string _stealForm = "";              // set on the shapeshift bonus pick

    // overlays, recomputed only when the mover, position, or card changes
    private Dictionary<Point, int> _moveSet = new();
    private HashSet<Point> _rangeSet = new();

    /// <summary>
    /// Squares under somebody's guard, rebuilt each frame for drawing: the live
    /// zones plus whatever the card in hand would add.
    /// </summary>
    private readonly HashSet<Point> _watchedGround = new();

    /// <summary>
    /// Everyone the card being aimed would hit, outlined in red while you aim.
    /// Rebuilt every frame from the same rule the card uses when it lands, so
    /// what lights up is exactly what gets hurt — friendly fire included.
    /// </summary>
    private readonly HashSet<CharacterInstance> _doomed = new();

    private bool _cardArmed;          // a card is selected or hovered: red replaces blue
    private string _rangeOpacityKey = "Range";   // "Leap" when the card jumps
    private string _blastOpacityKey = "AoE";     // "Cone" for a wedge
    private object? _overlayKey;

    /// <summary>
    /// The window onto the board, in whole art pixels at a whole-number zoom.
    /// Everything in the world is drawn at its own coordinates and this moves
    /// instead, which is why <see cref="Origin"/> is nothing.
    /// </summary>
    private readonly PixelCamera _camera = new();

    /// <summary>Where the pointer is on the BOARD, in art pixels.</summary>
    private Vector2 _worldPointer;

    /// <summary>Set once the real window size is known, on the first frame.</summary>
    private bool _framed;
    private Point _windowSize = new(1920, 1080);

    /// <summary>The square the view opens on, and returns to when recentred.</summary>
    private Point _focus;

    /// <summary>Rotations and placeholder cubes for everybody on the board.</summary>
    private readonly CastSprites _sprites;

    /// <summary>
    /// Transition pads the party is standing on, by the pad's lowest square.
    /// A pad arrived on cannot fire until every party member is off it, or the
    /// party would bounce straight back where they came from.
    /// </summary>
    private readonly HashSet<Point> _disarmed = new();
    private Point _pointer;
    private Point? _tap;
    private bool _ctrl;              // held: fades the board so the grid reads
    private bool _shift;             // held: adds to the out-of-combat selection
    private string _toast = "";
    private float _toastTimer;

    /// <summary>
    /// Counts down after a replay is written. While it runs, the Save Replay
    /// button says so itself — feedback belongs where the button is, not in the
    /// far corner of a 3840-wide screen where nobody is looking.
    /// </summary>
    private float _replaySavedTimer;

    // the combat log: every blow, burn and turn event, hidden behind a + button
    private readonly List<string> _log = new();
    private bool _logOpen;
    private int _logScroll;           // lines scrolled back from the newest

    // walking
    private CharacterInstance? _walker;
    private List<Point> _walkPath = new();
    private Point _walkFrom;
    private float _walkT;

    /// <summary>
    /// Everyone else walking alongside the main walker, when a group was moved
    /// as a set. They share the one clock, so the party crosses the ground
    /// together instead of filing past one at a time.
    /// </summary>
    private readonly List<Escort> _escorts = new();

    /// <summary>One extra body on the same walk clock.</summary>
    private sealed class Escort
    {
        public required CharacterInstance Who;
        public required List<Point> Path;
        public Point From;
    }

    /// <summary>
    /// Seconds the walk is holding still for. A guard's volley stops the walker
    /// where they stand for a moment so the shots read as landing on them, and
    /// then they carry on to where they were going.
    /// </summary>
    private float _walkPause;
    private Action? _afterWalk;

    // card / enemy action timing
    private Act _act;
    private float _actT, _actDur;
    private Card? _actingCard;
    private CharacterInstance? _actor;
    private List<CharacterInstance> _victims = new();
    private int _hitIndex;
    private float _hitTimer;
    private Vector2 _projFrom, _projTo;
    private float _projRotation;

    // dialogue playback
    private List<DialogueLine>? _lines;
    private int _lineIndex;

    // the turn strip starts clear of the log button and runs right
    private static readonly Rectangle TurnStrip = new(220, 34, 2900, 200);
    private const int TurnFaceActive = 190, TurnFace = 130, TurnFaceGap = 18;

    private static readonly Rectangle LogToggleRect = new(60, 40, 96, 96);
    private static readonly Rectangle LogPanel = new(60, 156, 1500, 1180);
    private const float LogTextScale = 0.30f;
    private const int LogLineH = 46;
    private static readonly Rectangle EndTurnRect = new(3280, 60, 500, 160);

    /// <summary>
    /// Recording, to the LEFT of End Turn on the same row. It was underneath at
    /// 500 wide, which "Stop Saving Replay" ran straight out of — the label is
    /// the longest thing on the screen, so it gets the width to say it.
    /// </summary>
    private static readonly Rectangle SaveReplayRect = new(2380, 60, 840, 160);

    /// <summary>In a replay, the same spot advances a turn.</summary>
    private static readonly Rectangle NextTurnRect = new(3280, 60, 500, 160);

    /// <summary>Seconds the Save Replay button stays turned green and saying so.</summary>
    private const float ReplaySavedShown = 2.5f;
    private static readonly Rectangle DoneRect = new(3280, 60, 500, 160);
    private static readonly Rectangle WinRect = new(1620, 1250, 600, 180);
    private static readonly Rectangle DialogueBox = new(60, 1560, 3720, 420);
    private const int CardW = 400, CardH = 560, CardGap = 26;

    // Card text sizes in POINTS. Courier.spritefont is baked at 96pt, so the
    // scale a draw call wants is points/96 — which is what makes "2pt smaller"
    // an exact number here rather than a guess at a scale factor.
    private const float FontBakedPt = 96f;
    private const float CardNamePt = 36.4f;    // was 38.4
    private const float CardBodyPt = 26.8f;    // was 28.8
    private const float CardTotalPt = 32.6f;   // was 34.6
    private const float CardRangePt = 26.8f;   // was 28.8
    private static float Pt(float points) => points / FontBakedPt;
    private static readonly int CardRestY = VirtualViewport.Height - CardH / 2;
    private const float HoverScale = 1.3f;

    /// <summary>
    /// Set when the screen is showing a recording rather than being played. In
    /// that state nothing decides anything: no AI runs, no card resolves, no
    /// turn is taken. The screen only does what the record says was done, one
    /// turn per press.
    /// </summary>
    private readonly bool _replayMode;
    private Replay? _watching;
    private int _replayAt;              // how far through the event list
    private string _replayName = "";

    public IsoLevelScreen(GameContext ctx, string levelName)
        : this(ctx, levelName, LevelData.Load(levelName), null, "") { }

    /// <summary>Watch a recorded mission, in the level as it was at the time.</summary>
    public IsoLevelScreen(GameContext ctx, string name, Replay replay, string levelText)
        : this(ctx, replay.Level,
            LevelData.FromText(levelText, replay.Level, $"Replays/{name}.level.txt"),
            replay, name) { }

    private IsoLevelScreen(GameContext ctx, string levelName, LevelData level,
        Replay? watching, string replayName)
    {
        _ctx = ctx;
        _sprites = new CastSprites(ctx.Assets, ctx.ContentIndex, ctx.Game.GraphicsDevice);
        _level = level;
        _watching = watching;
        _replayMode = watching != null;
        _replayName = replayName;
        _dialogue = DialogueLibrary.Load(levelName);
        _replay.Level = levelName;
        SpawnParty();
        SpawnEnemies();
        foreach (var start in _level.PlayerStarts.Take(_party.Count))
            if (_level.BlockAt(start) is LevelBlock b)
                _revealed.Add(b.Room);
        if (_revealed.Count == 0 && _level.Blocks.Count > 0)
            _revealed.Add(_level.Blocks.Values.First().Room);

        // where the view opens; the camera is centred on it once the real
        // window size is known, on the first frame drawn
        _focus = _party.Count > 0 ? Tile(_party[0]) : Point.Zero;
        _replay.Party = _party.Select(p => p.Name).ToList();
        Log(_ctx.Strings.Get(_replayMode ? "replay_watching" : "iso_enter"));

        if (_replayMode)
        {
            // a recording shows the whole level: nothing was hidden from the
            // person who played it by the time they finished
            foreach (var b in _level.Blocks.Values) _revealed.Add(b.Room);
            _mode = Mode.Replay;
        }
    }

    private void SpawnParty()
    {
        var names = _ctx.State.PartyOrDefault();
        var starts = _level.PlayerStarts;
        for (int i = 0; i < names.Count; i++)
        {
            var cls = _ctx.Classes.Get(names[i]);
            var at = i < starts.Count ? starts[i]
                : starts.Count > 0 ? new Point(starts[0].X + i, starts[0].Y) : new Point(i, 0);
            _party.Add(new CharacterInstance
            {
                Name = names[i],
                OccurrenceIndex = _party.Count(p => p.Name.Equals(names[i], StringComparison.OrdinalIgnoreCase)),
                IsPlayer = true,
                SpriteFile = cls is { Forms.Count: > 0 }
                    ? cls.SpriteForForm(cls.StartingForm)
                    : PickSprite(cls?.SpriteFiles, names[i], _party),
                Form = cls?.StartingForm ?? "",
                MaxHp = cls?.Hp ?? 20,
                Hp = cls?.Hp ?? 20,
                MoveMax = cls?.Movement ?? 5,
                ActionsPerTurn = cls?.Actions ?? CharacterInstance.DefaultActionsPerTurn,
                GX = at.X, GY = at.Y,
            });
        }
    }

    private void SpawnEnemies()
    {
        foreach (var spawn in _level.Enemies)
        {
            var def = _ctx.Enemies.Get(spawn.Name);
            if (def == null) continue;
            _enemies.Add(new CharacterInstance
            {
                Name = def.Name,
                OccurrenceIndex = _enemies.Count(e => e.Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase)),
                IsPlayer = false,
                SpriteFile = PickSprite(def.SpriteFiles, def.Name, _enemies),
                MaxHp = def.Hp,
                Hp = def.Hp,
                MoveMax = def.Movement,
                ActionsPerTurn = def.Actions,
                SizeX = def.SizeX, SizeY = def.SizeY,
                GX = spawn.X, GY = spawn.Y,
            });
        }
    }

    private static string PickSprite(IReadOnlyList<string>? variants, string name,
        List<CharacterInstance> existing)
    {
        var list = variants is { Count: > 0 } ? variants : new List<string> { $"{name}.png" };
        return list.OrderBy(v => existing.Count(e => e.SpriteFile.Equals(v, StringComparison.OrdinalIgnoreCase)))
                   .First();
    }

    // ---------------- helpers ----------------

    private static Point Tile(CharacterInstance c) => new(c.GX, c.GY);
    private int HeightAt(Point p) => _level.BlockAt(p)?.Height ?? 0;

    /// <summary>
    /// Nothing. The board is drawn where it is and the camera does the moving,
    /// so a sprite's position never depends on where the view happens to be —
    /// which is what keeps it on a whole pixel.
    /// </summary>
    private static Vector2 Origin => Vector2.Zero;

    private IEnumerable<CharacterInstance> Everyone => _party.Concat(_enemies);
    private List<CharacterInstance> LivingParty => _party.Where(p => p.Alive).ToList();

    /// <summary>
    /// The mission is lost. A summoned pet is not a survivor: it only ever acts
    /// inside its summoner's turn, so a gator standing over a dead party has no
    /// turn to take. The party is its real members.
    /// </summary>
    private bool PartyWiped => !CharacterInstance.AnyoneStanding(_party);
    private List<CharacterInstance> VisibleEnemies => _enemies.Where(e =>
        e.Alive && _level.Shown(Tile(e), _revealed)).ToList();

    /// <summary>Every square a character's body covers — one, unless it is a big one.</summary>
    private static IEnumerable<Point> Occupied(CharacterInstance c) => c.Footprint;

    private HashSet<Point> OccupiedExcept(CharacterInstance? except) =>
        Everyone.Where(c => c.Alive && c != except).SelectMany(Occupied).ToHashSet();

    /// <summary>
    /// Squares a mover may walk THROUGH but not stop on. Party members squeeze
    /// past each other freely; enemies block the way as before.
    /// </summary>
    private HashSet<Point> PassThroughFor(CharacterInstance mover) => mover.IsPlayer
        ? _party.Where(p => p.Alive && p != mover).SelectMany(Occupied).ToHashSet()
        : new HashSet<Point>();

    /// <summary>Whoever is standing on a square, big bodies included.</summary>
    private CharacterInstance? WhoIsOn(Point tile) =>
        Everyone.FirstOrDefault(c => c.Alive && c.Covers(tile));

    private CharacterInstance? Current => _turn >= 0 && _turn < _order.Count ? _order[_turn] : null;
    private bool DialogueActive => _lines != null;

    /// <summary>
    /// Records what happened. Everything about damage, burning, shapes and
    /// turns goes here and nowhere else — the log panel is the only place the
    /// player reads it, so the level itself stays uncluttered.
    /// </summary>
    /// <summary>
    /// Begins writing things down, and pins where everybody is standing right
    /// now. Without that snapshot a recording begun part-way through a fight
    /// would play back with the party at the level's entrance, since the only
    /// other thing that says where anyone is is a Move.
    /// </summary>
    private void StartRecording()
    {
        _recording = true;
        _replay.Events.Clear();
        _replayTurn = _turn >= 0 ? 1 : 0;
        foreach (var c in Everyone.Where(c => c.Alive))
            _replay.Events.Add(new ReplayEvent
            {
                Kind = ReplayEventKind.Place, Turn = _replayTurn, Who = c.Name,
                From = Tile(c), To = Tile(c), Amount = c.Hp,
                Text = c.IsPlayer ? "party" : "enemy",
            });
        Toast(_ctx.Strings.Get("replay_started"));
        Log(_ctx.Strings.Get("replay_started"));
    }

    /// <summary>
    /// Writes the record so far, plus the level it was played in, under one
    /// name. The level is copied rather than pointed at: levels get edited, and
    /// a replay that read the live file would start showing people walking
    /// through walls the first time somebody moved one.
    /// </summary>
    private void SaveReplay(string why)
    {
        if (_replayMode || !_recording) return;
        _recording = false;
        _replay.Saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        string name = Replay.NameFor(_replay.Level, DateTime.Now);
        string? where = _ctx.ReplayStore.Save(name, _replay.Serialize(), _level.Serialize());
        Toast(where == null
            ? _ctx.Strings.Get("replay_failed")
            : _ctx.Strings.Format("replay_saved", ("name", name)));
        _replaySavedTimer = where == null ? 0f : ReplaySavedShown;
        Log(_ctx.Strings.Format("replay_saved", ("name", name)) + $" ({why})");
    }

    /// <summary>
    /// The mission is over. Recorded once and saved once, however many ways the
    /// end is noticed — a party wipe reaches this from more than one place.
    /// </summary>
    private void FinishMission(string how)
    {
        if (_replayMode || _ended || !_recording) return;
        _ended = true;
        Record(ReplayEventKind.End, note: how);
        SaveReplay(how);
    }

    // ---------------- watching a recording ----------------

    /// <summary>
    /// One press of Next Turn, or the spacebar, plays one whole turn of the
    /// record: the walk, the card, and what it did, in the order they happened.
    ///
    /// Pressing again while that is still playing does not queue a second turn.
    /// It cuts the current one short — the walk snaps to its destination, the
    /// shot lands, the damage shows — and only then does the next press move on.
    /// Waiting through an animation you have already seen is not watching.
    /// </summary>
    private void UpdateReplay(InputState input, float dt)
    {
        bool advance = input.Confirm ||
                       (_tap is Point p && NextTurnRect.Contains(p));
        if (_tap != null && NextTurnRect.Contains(_tap.Value)) _tap = null;

        if (_walker != null)
        {
            if (advance) SnapWalk();
            else { UpdateWalk(dt); return; }
        }
        if (_act == Act.Projectile && !advance) { UpdateAction(dt); return; }
        if (!advance) return;

        // a press during an animation spent itself cutting that short
        if (_act != Act.Hits && _actingCard != null) { EndReplayAction(); return; }
        PlayReplayTurn();
    }

    /// <summary>Drops a walk in progress straight onto its last square.</summary>
    private void SnapWalk()
    {
        if (_walker == null) return;
        if (_walkPath.Count > 0)
        {
            var last = _walkPath[^1];
            _walker.GX = last.X;
            _walker.GY = last.Y;
        }
        foreach (var e in _escorts)
            if (e.Path.Count > 0) { e.Who.GX = e.Path[^1].X; e.Who.GY = e.Path[^1].Y; }
        _escorts.Clear();
        _walkPath.Clear();
        _walker = null;
        _afterWalk = null;
        _overlayKey = null;
    }

    private void EndReplayAction()
    {
        _actingCard = null;
        _projFrom = _projTo = Vector2.Zero;
        _act = Act.Hits;
        _actT = _actDur;
        foreach (var c in Everyone) { c.CastAnim = null; c.CastAnimTime = 0f; }
    }

    /// <summary>
    /// Applies every event of the next turn. Movement and shots are shown with
    /// their animations; damage and deaths are applied outright, because a
    /// recording is a statement of what happened rather than a re-simulation —
    /// nothing here is allowed to work out a different answer.
    /// </summary>
    private void PlayReplayTurn()
    {
        if (_watching == null) return;
        if (_replayAt >= _watching.Events.Count) { _mode = Mode.Victory; return; }

        // the turn number of the event we are about to play; everything sharing
        // it belongs to this press
        int turn = _watching.Events[_replayAt].Turn;
        Point? walkTo = null;
        CharacterInstance? walker = null;

        while (_replayAt < _watching.Events.Count && _watching.Events[_replayAt].Turn == turn)
        {
            var e = _watching.Events[_replayAt++];
            var who = Everyone.FirstOrDefault(c =>
                c.Name.Equals(e.Who, StringComparison.OrdinalIgnoreCase));
            var target = Everyone.FirstOrDefault(c =>
                c.Name.Equals(e.Target, StringComparison.OrdinalIgnoreCase));

            switch (e.Kind)
            {
                // where everyone stood when recording began
                case ReplayEventKind.Place when who != null:
                    who.GX = e.From.X;
                    who.GY = e.From.Y;
                    who.Hp = Math.Max(0, e.Amount);
                    who.Alive = e.Amount > 0;
                    break;

                case ReplayEventKind.Turn:
                    _replayTurn = e.Turn;
                    if (who != null) _replayActor = who;
                    Log(_ctx.Strings.Format("battle_turn", ("name", e.Who)));
                    break;

                case ReplayEventKind.Move when who != null:
                    // put them back where the walk started, then walk it
                    who.GX = e.From.X; who.GY = e.From.Y;
                    walker = who; walkTo = e.To;
                    break;

                case ReplayEventKind.Card when who != null:
                    Log(_ctx.Strings.Format("replay_card", ("name", e.Who), ("card", e.Card)));
                    StartCastAnimation(who);
                    break;

                case ReplayEventKind.Hit when target != null:
                    target.Hp = Math.Max(0, target.Hp - e.Amount);
                    target.ShakeTimer = Recoil.Duration;
                    Log(_ctx.Strings.Format("battle_hit",
                        ("target", e.Target), ("dmg", e.Amount.ToString()), ("type", e.Text)));
                    break;

                case ReplayEventKind.Down when target != null:
                    target.Hp = 0;
                    target.Alive = false;
                    Log(_ctx.Strings.Format("battle_down", ("name", e.Target)));
                    break;

                case ReplayEventKind.End:
                    Log(_ctx.Strings.Format("replay_over", ("how", e.Text)));
                    break;
            }
        }

        // the walk is shown last, so the whole turn is on screen while it runs
        if (walker != null && walkTo is Point goal)
        {
            _walker = walker;
            _walkFrom = Tile(walker);
            _walkPath = new List<Point> { goal };
            _walkT = 0f;
            _walkPause = 0f;
            _afterWalk = null;
        }
        _overlayKey = null;
    }

    /// <summary>Whose turn the recording is currently showing.</summary>
    private CharacterInstance? _replayActor;

    private void DrawReplayHud(SpriteBatch batch)
    {
        Ui.DrawTextCentered(batch, _ctx.Font,
            _ctx.Strings.Format("replay_title", ("name", _replayName)),
            new Rectangle(0, 40, VirtualViewport.Width, 90), Color.Gold, 0.4f);

        int turns = _watching?.Turns ?? 0;
        Ui.DrawTextCentered(batch, _ctx.Font,
            _replayAt >= (_watching?.Events.Count ?? 0)
                ? _ctx.Strings.Get("replay_end")
                : _ctx.Strings.Format("replay_turn",
                    ("turn", _replayTurn.ToString()), ("total", turns.ToString())),
            new Rectangle(0, 140, VirtualViewport.Width, 70), Color.White * 0.8f, 0.34f);

        if (_replayActor != null)
            Ui.DrawTextCentered(batch, _ctx.Font, _replayActor.Name,
                new Rectangle(0, 210, VirtualViewport.Width, 70), Color.LightGreen, 0.36f);

        if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, NextTurnRect,
                _ctx.Strings.Get("replay_next"), _tap))
            PlayReplayTurn();
    }

    /// <summary>Files one thing that happened under the turn it happened in.</summary>
    private void Record(ReplayEventKind kind, CharacterInstance? who = null,
        string card = "", string target = "", Point from = default, Point to = default,
        int amount = 0, string note = "")
    {
        if (_replayMode || !_recording) return;   // off, or watching one already
        _replay.Events.Add(new ReplayEvent
        {
            Kind = kind, Turn = _replayTurn, Who = who?.Name ?? "",
            Card = card, Target = target, From = from, To = to, Amount = amount, Text = note,
        });
    }

    private void Log(string text)
    {
        foreach (var line in text.Split('\n'))
            if (line.Trim().Length > 0)
                _log.Add(line.Trim());
        if (_logScroll > 0) _logScroll++;      // keep the reader's place while pinned back
    }

    /// <summary>
    /// Immediate feedback on something the player just tried to do — out of
    /// range, no movement left. Flashes on screen AND joins the log.
    /// </summary>
    private void Toast(string text)
    {
        _toast = text;
        _toastTimer = 3f;
        Log(text);
    }

    /// <summary>The deck a character's own cards come from.</summary>
    private CardLibrary DeckOf(CharacterInstance c) => c.IsPlayer ? _ctx.Cards : _ctx.EnemyCards;

    /// <summary>Looks a card up by name in whichever deck it came from.</summary>
    private Card? FindCard(string name, bool enemyDeck) =>
        (enemyDeck ? _ctx.EnemyCards : _ctx.Cards).All
            .FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Everything a character can play right now: their own cards, minus any
    /// the Dirtbag is currently holding off them, plus anything they have
    /// stolen from somebody else.
    /// </summary>
    private List<Card> HandOf(CharacterInstance who)
    {
        var tags = who.IsPlayer
            ? _ctx.Classes.CardTagsFor(who.Name)
            : _ctx.Enemies.CardTagsFor(who.Name);
        var hand = who.IsPlayer
            ? DeckOf(who).HandFor(tags, who.Form)
            : DeckOf(who).HandFor(tags);

        // an enemy nobody wrote a card for is dealt the default one, so it can
        // still swing. Once it has one of its own, the default drops away.
        if (!who.IsPlayer && hand.Count == 0)
            hand = DeckOf(who).DefaultHand();

        // Loaded different shells: the swapped-out card is gone from this
        // character's hand and its replacement stands in the same slot, so the
        // hand keeps its order and the number keys keep meaning what they did.
        if (who.Swapped.Count > 0)
        {
            var deck = DeckOf(who);
            hand = hand.Select(c =>
                who.Swapped.TryGetValue(c.Name, out string? into) &&
                deck.All.FirstOrDefault(x => x.Name.Equals(into, StringComparison.OrdinalIgnoreCase))
                    is Card loaded
                    ? loaded : c).ToList();
        }

        // A summon card is out of the hand while its creature is on the board:
        // one Gator at a time. It comes back the moment that one dies, and a
        // new level starts with a fresh party and no pets at all.
        hand = hand.Where(c => !c.IsSummon || !SummonAlive(who, c.Summons)).ToList();

        if (who.Lost.Count > 0)
            hand = hand.Where(c => !who.Lost.Any(l =>
                l.CardName.Equals(c.Name, StringComparison.OrdinalIgnoreCase))).ToList();

        foreach (var loot in who.Stolen)
            if (FindCard(loot.CardName, loot.FromEnemyDeck) is Card borrowed)
                hand.Add(borrowed);

        // mid-channel there is exactly one thing to do: let go of it
        if (who.IsChannelling)
            hand = hand.Where(c =>
                c.Name.Equals(who.ChannellingCard, StringComparison.OrdinalIgnoreCase)).ToList();
        return hand;
    }

    /// <summary>Gives a borrowed card back and clears it from both sides.</summary>
    private void ReturnStolen(StolenCard loot, CharacterInstance thief)
    {
        thief.Stolen.Remove(loot);
        loot.From?.Lost.Remove(loot);
    }

    /// <summary>Who the current mode lets the player move.</summary>
    /// <summary>
    /// The party member the green square sits under: whoever is being moved
    /// right now, and in a fight the one whose turn it is even while a card is
    /// resolving. Enemies never get it.
    /// </summary>
    private CharacterInstance? Chosen =>
        ActiveMover is CharacterInstance m && m.IsPlayer ? m :
        Current is { IsPlayer: true, Alive: true } c ? c : null;

    /// <summary>
    /// Whoever the player is moving right now. In a fight that is normally the
    /// character whose turn it is — but a summoner and its pets share one turn,
    /// so it is whichever of that group has been picked.
    /// </summary>
    private CharacterInstance? ActiveMover => _mode switch
    {
        Mode.Explore => _selected,
        Mode.FreeMove => _selected != null && _freeMovers.Contains(_selected) ? _selected : null,
        Mode.PlayerTurn or Mode.PlayerTarget =>
            _petControl is { Alive: true } p && ActsWith(p, Current) ? p : Current,
        _ => null,
    };

    /// <summary>
    /// The pet the player has clicked, when a summoner's turn is running. Null
    /// means the summoner themselves is being moved.
    /// </summary>
    private CharacterInstance? _petControl;

    /// <summary>
    /// Whether these two take their turn together: a pet and its owner, or two
    /// pets of the same owner. Everything else has its own place in the order.
    /// </summary>
    private static bool ActsWith(CharacterInstance a, CharacterInstance? b)
    {
        if (a == null || b == null) return false;
        if (a == b) return true;
        return a.Owner == b || b.Owner == a || (a.Owner != null && a.Owner == b.Owner);
    }

    /// <summary>
    /// Whose cards and points a play spends: the pet if one has the controls,
    /// otherwise whoever's turn it is. Everything about playing a card goes
    /// through this rather than through Current.
    /// </summary>
    private CharacterInstance? Acting => ActiveMover ?? Current;

    /// <summary>Everyone acting on the current turn: whoever it is, plus their pets.</summary>
    private List<CharacterInstance> ActingGroup() =>
        Current == null ? new List<CharacterInstance>()
            : LivingParty.Concat(_enemies.Where(e => e.Alive))
                .Where(c => ActsWith(c, Current)).ToList();

    private Vector2 FootOf(CharacterInstance c)
    {
        var at = IsoMath.ToScreen(c.GX, c.GY, HeightAt(Tile(c)), Origin);
        var leg = c == _walker && _walkPath.Count > 0 ? (_walkFrom, _walkPath[0])
            : _escorts.FirstOrDefault(e => e.Who == c) is Escort esc && esc.Path.Count > 0
                ? (esc.From, esc.Path[0])
                : ((Point, Point)?)null;
        if (leg is var (legFrom, legTo) && leg != null)
        {
            var from = IsoMath.ToScreen(legFrom.X, legFrom.Y, HeightAt(legFrom), Origin);
            var to = IsoMath.ToScreen(legTo.X, legTo.Y, HeightAt(legTo), Origin);
            at = Vector2.Lerp(from, to, _walkT);
        }
        // a body wider than one square stands in the middle of its footprint,
        // which in this projection is straight down the screen from its corner
        // the middle of the footprint, which in this projection is straight
        // down-screen for a square body and offset sideways for a long one
        at.X += (c.SizeX - c.SizeY) * (IsoMath.TileW / 2f) / 2f;
        at.Y += (c.SizeX + c.SizeY - 2) * (IsoMath.TileH / 2f) / 2f;
        return at + new Vector2(0, 26) + Recoil.Offset(c);
    }

    // ---------------- update ----------------

    public void Update(InputState input, float dt)
    {
        _pointer = input.PointerPos;
        _tap = input.Tap;
        _ctrl = input.CtrlHeld;
        _shift = input.ShiftHeld;

        // The HUD is laid out in the 3840x2160 design space and reads _pointer.
        // The board is drawn in art pixels through the camera, so pointing at a
        // square has to start from the RAW window position — converting through
        // the design space first would round the answer to the wrong tile.
        _worldPointer = _camera.ToWorld(input.RawPointer).ToVector2();
        // PanDelta arrives in design-space units; back to window pixels, then to
        // art pixels, so a drag moves the board by the same amount at any zoom
        _camera.Scroll(new Point(
            (int)Math.Round(input.PanDelta.X * _ctx.Viewport.Scale / _camera.Zoom),
            (int)Math.Round(input.PanDelta.Y * _ctx.Viewport.Scale / _camera.Zoom)));
        if (_toastTimer > 0) _toastTimer -= dt;
        if (_replaySavedTimer > 0) _replaySavedTimer -= dt;
        Recoil.Update(Everyone, dt);
        UpdateHealthBars(dt);
        _clock += dt;
        UpdateCastAnimations(dt);

        // the ~ menu answers before anything else, and swallows the frame
        if (UpdateDevMenu(input)) return;

        if (_tap is Point logTap && LogToggleRect.Contains(logTap))
        {
            _logOpen = !_logOpen;
            _logScroll = 0;
            _tap = null;
            return;
        }

        // The record button is answered here, before anything else looks at the
        // tap. It used to be answered where it is DRAWN — after the board had
        // already eaten the click — so it never fired on any screen you play
        // on. Handling it up here also means it works in every mode it appears
        // in, including while an enemy is taking its turn.
        if (_tap is Point recTap && ReplayButtonUp && SaveReplayRect.Contains(recTap))
        {
            _tap = null;
            if (_recording) SaveReplay("asked for"); else StartRecording();
            return;
        }
        // the wheel scrolls the log back through history while it is open, and
        // zooms the board everywhere else — in whole steps, so a pixel is still
        // a whole number of screen pixels afterwards
        if (input.ScrollDelta != 0)
        {
            if (_logOpen && LogPanel.Contains(_pointer))
                _logScroll = Math.Clamp(_logScroll + input.ScrollDelta,
                    0, Math.Max(0, _log.Count - LogLines));
            else
                _camera.ZoomBy(Math.Sign(input.ScrollDelta), input.RawPointer);
        }

        if (DialogueActive)
        {
            if (_tap.HasValue || input.Confirm) AdvanceDialogue();
            _tap = null;
            return;
        }

        // right-click always drops the armed card
        if (input.AltTap.HasValue) CancelCard();

        if (_replayMode) { RefreshOverlays(); UpdateReplay(input, dt); return; }

        // Tab or the middle button takes the whole party. Out of combat only:
        // in a fight it is one character's turn and there is nothing to pick.
        if (_mode is Mode.Explore or Mode.FreeMove &&
            (input.SelectAll || input.MiddleTap.HasValue))
        {
            _picked.Clear();
            _picked.AddRange(_mode == Mode.FreeMove
                ? _freeMovers.Where(p => p.Alive)
                : LivingParty);
            _overlayKey = null;
        }

        // a stunned character's turn: the camera has gone to them, and nothing
        // happens until the pause runs out
        if (_stunHold > 0f)
        {
            _stunHold -= dt;
            if (_stunHold <= 0f) { _stunHold = 0f; NextTurn(); }
            return;
        }

        // End or Space finishes the turn, and a number plays that card. Both are
        // ignored while somebody is talking or a card is mid-flight, since a key
        // pressed then was meant for the thing on screen, not for the turn.
        if (!DialogueActive && _walker == null && _mode is Mode.PlayerTurn or Mode.PlayerTarget)
        {
            if (input.EndTurn) { NextTurn(); return; }
            if (input.CardKey is int slot) { PlayCardByNumber(slot); return; }
        }
        if (_walker != null) { UpdateWalk(dt); return; }

        if (_mode == Mode.Explore) RearmTransitions();
        RefreshOverlays();

        switch (_mode)
        {
            case Mode.StealPick: UpdateStealPick(input); break;
            case Mode.Acting: UpdateAction(dt); break;
            case Mode.EnemyTurn: EnemyAct(); break;
            case Mode.Explore:
            case Mode.FreeMove:
            case Mode.PlayerTurn:
            case Mode.PlayerTarget:
                UpdateAim();
                HandleClicks();
                break;
        }
    }

    /// <summary>
    /// Casting animations run on their own clock, at their own frame rate, and
    /// stop when their last frame has been shown — the casting time never
    /// stretches or trims them. A cast shorter than its animation therefore
    /// launches the projectile with the caster still mid-swing, which is what
    /// the art is drawn to do.
    ///
    /// This is ticked before every early return in Update so a cast that
    /// outlives its own phase keeps running through dialogue and walking.
    /// </summary>
    private void UpdateCastAnimations(float dt)
    {
        foreach (var c in Everyone)
        {
            if (c.CastAnim == null) continue;
            c.CastAnimTime += dt;
            if (c.CastAnimTime >= c.CastAnim.Duration)
            {
                c.CastAnim = null;
                c.CastAnimTime = 0f;
            }
        }
    }

    /// <summary>
    /// Swaps the caster's sprite for its casting sheet, from the first frame.
    /// A shapeshifter picks up the sheet for the shape it is wearing right now,
    /// so a card that changes form still casts in the form it started in.
    /// Anyone with no animation declared simply stands there as before.
    /// </summary>
    private void StartCastAnimation(CharacterInstance actor)
    {
        actor.CastAnim = actor.CastAnimationPath is string path
            ? SpriteAnimation.Load(_ctx.Assets, path)
            : null;
        actor.CastAnimTime = 0f;
    }

    private void CancelCard()
    {
        if (_selectedCard == null && _targets.Count == 0) return;
        _selectedCard = null;
        _targets.Clear();
        _blastSet.Clear();
        _overlayKey = null;
        if (_mode == Mode.PlayerTarget) _mode = Mode.PlayerTurn;
    }

    private void UpdateWalk(float dt)
    {
        if (_walker == null) return;
        // held still while a guard's shots land on us; the walk resumes after
        if (_walkPause > 0f)
        {
            _walkPause -= dt;
            if (_walkPause > 0f) return;
            _walkPause = 0f;
        }
        _walkT += dt * WalkTilesPerSec;
        while (_walkT >= 1f && (_walkPath.Count > 0 || _escorts.Count > 0))
        {
            _walkT -= 1f;

            // everyone travelling together takes their step on the same beat
            StepEscorts();
            if (_walkPath.Count == 0) continue;

            var arrived = _walkPath[0];
            _walkPath.RemoveAt(0);
            _walker.Face(Tile(_walker), arrived);
            _walkFrom = arrived;
            _walker.GX = arrived.X;
            _walker.GY = arrived.Y;
            _overlayKey = null;

            // crossing burning ground catches you as surely as standing in it
            Ignite(_walker);
            if (!_walker.Alive) { StopWalk(); break; }

            // standing beside a door opens it: no clicking, no reach to learn
            if (_walker.IsPlayer) OpenDoorsBeside(_walker);

            // Stepping onto watched ground draws a volley. It only ENDS the
            // walk if it kills; otherwise the walker stands still for a moment
            // and then carries on to where it was going, which is what the
            // pause below is for.
            if (CheckGuards(_walker)) { StopWalk(); break; }
            if (_walkPause > 0f) break;

            if (_walker.IsPlayer && FireTrigger(arrived)) { StopWalk(); break; }

            // an area transition takes the whole party somewhere else, so the
            // walk that set it off has nowhere left to go
            if (_mode == Mode.Explore && _walker.IsPlayer && TakeTransition(arrived))
            {
                StopWalk();
                _walker = null;
                _afterWalk = null;
                return;
            }

            if (_mode == Mode.Explore && _walker.IsPlayer && CheckAggro(_walker))
            {
                StopWalk();
                break;
            }
        }
        // a volley landing on the last step still gets its moment before
        // whatever the walk was leading up to happens
        if (_walkPause > 0f) return;
        if (_walkPath.Count == 0 && _escorts.Count == 0)
        {
            var done = _afterWalk;
            _walker = null;
            _afterWalk = null;
            done?.Invoke();
        }
    }

    /// <summary>
    /// Cuts the whole walk short — the leader and everyone with them.
    ///
    /// Anything that interrupts a walk (a fire, a volley, a conversation, a
    /// fight starting) interrupts it for the group. Stopping only the leader
    /// would leave the rest strolling on into whatever it was.
    /// </summary>
    private void StopWalk()
    {
        _walkPath.Clear();
        _escorts.Clear();
    }

    /// <summary>
    /// One step for everybody walking alongside the main walker. They run the
    /// same per-step checks it does — fire catches them, doors open for them,
    /// and watched ground shoots at them — because they are walking too.
    /// </summary>
    private void StepEscorts()
    {
        for (int i = _escorts.Count - 1; i >= 0; i--)
        {
            var e = _escorts[i];
            if (!e.Who.Alive || e.Path.Count == 0) { _escorts.RemoveAt(i); continue; }

            var arrived = e.Path[0];
            e.Path.RemoveAt(0);
            e.From = arrived;
            e.Who.Face(Tile(e.Who), arrived);
            e.Who.GX = arrived.X;
            e.Who.GY = arrived.Y;

            Ignite(e.Who);
            if (!e.Who.Alive) { _escorts.RemoveAt(i); continue; }
            if (e.Who.IsPlayer) OpenDoorsBeside(e.Who);
            CheckGuards(e.Who);
            if (e.Path.Count == 0) _escorts.RemoveAt(i);
        }
        _overlayKey = null;
    }

    /// <summary>
    /// Stepping onto a linked transition pad, out of combat: the whole party
    /// moves to the pad at the other end, that room becomes the only one lit,
    /// and the room they left goes dark again. Returns false when the square is
    /// not a pad, leads nowhere, or is the one they just arrived on.
    ///
    /// A pad the party is standing on is disarmed, or arriving would send them
    /// straight back. It re-arms once EVERY party member is clear of it, and
    /// from then on one member stepping back on is enough.
    /// </summary>
    private bool TakeTransition(Point tile)
    {
        var pads = _level.TransitionPads();
        var here = pads.FirstOrDefault(p => p.Covers(tile));
        if (here == null || here.Pair == 0) return false;
        if (_disarmed.Contains(here.Key)) return false;

        var there = pads.FirstOrDefault(p => p != here && p.Pair == here.Pair);
        if (there == null)
        {
            // half a link: report it rather than swallowing the step
            _ctx.ReportProblem(LevelData.PathFor(_level.Name),
                $"the transition at {tile.X},{tile.Y} is pair {here.Pair}, but nothing else " +
                "in this level carries that number, so it leads nowhere");
            return false;
        }

        MoveParty(there);
        return true;
    }

    /// <summary>Whether anyone in the party is still standing on a pad.</summary>
    private bool PartyOn(TransitionPad pad) =>
        LivingParty.Any(p => p.Footprint.Any(pad.Covers));

    /// <summary>
    /// Puts the party down on a pad and lights only the room it sits in. Pads
    /// are usually one square per party member; anyone who doesn't fit takes
    /// the nearest free square instead, so a small pad still works.
    /// </summary>
    private void MoveParty(TransitionPad destination)
    {
        // the destination's room has to be lit before anything can be placed in
        // it — Standable refuses a square in a room nobody has revealed
        var room = destination.Tiles
            .Select(t => _level.BlockAt(t)?.Room)
            .FirstOrDefault(r => r != null) ?? _level.Blocks.Values.First().Room;
        _revealed.Clear();
        _revealed.Add(room);

        var taken = new HashSet<Point>();
        var pads = destination.Tiles.OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
        int next = 0;
        foreach (var member in LivingParty)
        {
            Point? spot = null;
            while (next < pads.Count && spot == null)
            {
                var candidate = pads[next++];
                if (!taken.Contains(candidate) && Fits(member, candidate, taken))
                    spot = candidate;
            }
            spot ??= NearestFree(destination.Center, member, taken);
            if (spot is not Point at) continue;   // nowhere at all: leave them put

            foreach (var t in member.Footprint) taken.Add(t);
            member.GX = at.X;
            member.GY = at.Y;
            foreach (var t in member.Footprint) taken.Add(t);
        }

        // the pad they land on must not throw them straight back
        _disarmed.Add(destination.Key);
        RecenterOn(LivingParty.FirstOrDefault());
        _overlayKey = null;
        Log(_ctx.Strings.Format("iso_transition", ("room", room)));

        // a new room can hold a fight
        foreach (var p in LivingParty)
            if (CheckAggro(p)) break;
    }

    private bool Fits(CharacterInstance who, Point at, IReadOnlySet<Point> taken) =>
        Pathfinder.Fits(_level, at, who.SizeX, who.SizeY, _revealed,
            OccupiedExcept(who).Concat(taken).ToHashSet());

    /// <summary>The closest square to a pad that this character actually fits on.</summary>
    private Point? NearestFree(Point around, CharacterInstance who, IReadOnlySet<Point> taken)
    {
        var candidates = _level.Blocks.Keys
            .Where(t => Fits(who, t, taken))
            .OrderBy(t => IsoMath.GridDistance(t, around));
        foreach (var t in candidates) return t;
        return null;
    }

    /// <summary>Drops the camera on somebody, for when the party is moved under it.</summary>
    private void RecenterOn(CharacterInstance? who)
    {
        if (who == null) return;
        _focus = Tile(who);
        CentreOnFocus();
    }

    /// <summary>Puts the camera over <see cref="_focus"/> at the current zoom.</summary>
    private void CentreOnFocus() =>
        _camera.CentreOn(
            IsoMath.ToScreen(_focus.X, _focus.Y, HeightAt(_focus), Origin).ToPoint(),
            _windowSize.X, _windowSize.Y);

    /// <summary>
    /// Pads the party is standing on, which cannot fire again until everybody
    /// is off. Cleared here rather than on the step off, so a member wandering
    /// back on before the last one leaves does not set it off.
    /// </summary>
    private void RearmTransitions()
    {
        if (_disarmed.Count == 0) return;
        foreach (var pad in _level.TransitionPads())
            if (_disarmed.Contains(pad.Key) && !PartyOn(pad))
                _disarmed.Remove(pad.Key);
    }

    /// <summary>The ground a planted character covers: every revealed square within reach.</summary>
    /// <summary>
    /// The ground a planted character covers: every square of real ground
    /// within reach.
    ///
    /// Deliberately NOT filtered by what is revealed. It used to be, and that
    /// quietly cut the zone off at the edge of the room you could see — so an
    /// enemy coming through a door that opened afterwards walked in over
    /// ground the watch had never been told about, and nothing fired. The zone
    /// is a patch of dirt; what you can see of it is a drawing question, and
    /// the drawing loop only visits revealed squares anyway.
    /// </summary>
    private HashSet<Point> GuardZoneAround(Point centre, int reach)
    {
        var zone = new HashSet<Point>();
        foreach (var block in _level.Blocks.Values)
        {
            var tile = new Point(block.X, block.Y);
            if (IsoMath.GridDistance(tile, centre) <= reach) zone.Add(tile);
        }
        return zone;
    }

    /// <summary>Whether any part of a body is standing on a guard's ground.</summary>
    private static bool InGuardZone(CharacterInstance guard, CharacterInstance who) =>
        guard.Watch.Covers(who.Footprint);

    /// <summary>
    /// Anyone who steps onto ground somebody is covering gets shot for it —
    /// their own side included, since a planted gun does not check badges.
    ///
    /// Stepping IN is what fires it, and the watch itself keeps track of who is
    /// already standing there. Returns true only if the walk should stop for
    /// good, which is when the walker dies; otherwise it pauses for the volley
    /// and carries on.
    /// </summary>
    private bool CheckGuards(CharacterInstance walker)
    {
        foreach (var guard in Everyone.Where(g => g.Alive && g.IsGuarding && g != walker).ToList())
        {
            if (!guard.Watch.Entered(Key(walker), walker.Footprint)) continue;

            var report = new StringBuilder();
            report.AppendLine(_ctx.Strings.Format("iso_guard_fires",
                ("name", guard.Name), ("target", walker.Name),
                ("shots", guard.Watch.Shots.ToString())));

            var was = _actor;
            _actor = guard;                       // so the shots are credited to the guard
            for (int i = 0; i < guard.Watch.Shots && walker.Alive; i++)
                ApplyHit(walker, guard.Watch.Damage, "Gunfire", report);
            _actor = was;

            _ctx.Sounds.Play("hitbasic.wav");
            Log(report.ToString().TrimEnd());
            // the volley reads as a pause in the walk rather than a stop: hold
            // the walker still long enough to see it land, then let them finish
            _walkPause = GuardPause;
            if (!walker.Alive) return true;
        }
        return false;
    }

    /// <summary>How long a walk holds still while a guard's volley lands on it.</summary>
    private const float GuardPause = 0.45f;

    /// <summary>Seconds a stunned character's turn is held on screen doing nothing.</summary>
    private const float StunHoldSeconds = 1.5f;

    /// <summary>Time left on that hold, or 0.</summary>
    private float _stunHold;

    // ---------------- the lawnmower ----------------

    /// <summary>The run being played back, a square at a time.</summary>
    private MowerRun? _mower;
    private int _mowerBeat;
    private float _mowerTimer;

    /// <summary>Seconds the machine spends on each square it crosses.</summary>
    private const float MowerTileTime = 0.11f;

    /// <summary>
    /// Works out the whole run up front, then hands it to the update loop to
    /// play back. Deciding it all at once means the damage is settled before
    /// any of it is drawn, so nothing can go differently depending on frame
    /// rate — and the rules live in MowerRun, where a test can reach them.
    /// </summary>
    private void StartMower()
    {
        var card = _actingCard!;
        var driver = _actor!;
        var from = Tile(driver);
        var report = new StringBuilder();

        _mower = MowerRun.Drive(
            from,
            MowerRun.HeadingToward(from, _aimPoint),
            card.MowerTiles,
            ground: t => _level.Shown(t, _revealed),
            // Only somebody this card is allowed to touch counts as something
            // to hit. With Friendly Fire that is everyone, the driver included:
            // a bounce can send the thing back through the man who started it,
            // which is the whole character of the card.
            occupant: t => WhoIsOn(t) is CharacterInstance c && MayTarget(driver, card, c)
                ? Key(c) : null,
            strike: (t, key) =>
            {
                var victim = FindByKey(key);
                if (victim == null) return (0, false);
                int dmg = RollDamage(card, victim);
                ApplyHit(victim, dmg, card.DamageType, report);
                return (dmg, !victim.Alive);
            },
            Rng);

        if (report.Length > 0) Log(report.ToString().TrimEnd());
        _mowerBeat = 0;
        _mowerTimer = 0f;
        EnterAct(Act.Mowing, 0f);
    }

    /// <summary>
    /// A name that picks out one body on the board, since two goblins share a
    /// name. The mower only needs to hand a reference back to itself, and this
    /// keeps MowerRun free of any knowledge of what a character is.
    /// </summary>
    private static string Key(CharacterInstance c) => $"{c.Name}#{c.OccurrenceIndex}";

    private CharacterInstance? FindByKey(string key) =>
        Everyone.FirstOrDefault(c => Key(c) == key);

    /// <summary>
    /// Plays the run back one square at a time. All the damage on the way has
    /// already been dealt; this is the picture of it. The blast at the end is
    /// the exception — it is rolled and applied when the machine gets there,
    /// so that anything killed on the way is already down and not counted.
    /// </summary>
    private void UpdateMower(float dt)
    {
        if (_mower == null) { FinishAction(); return; }
        _mowerTimer -= dt;
        if (_mowerTimer > 0f) return;

        if (_mowerBeat >= _mower.Beats.Count)
        {
            _mower = null;
            FinishAction();
            return;
        }

        var beat = _mower.Beats[_mowerBeat++];
        _mowerTimer = MowerTileTime;

        switch (beat.What)
        {
            case MowerStep.Through:
            case MowerStep.Bounced:
                _ctx.Sounds.Play("hitbasic.wav");
                break;
            case MowerStep.Exploded:
                BlowUpMower(beat.Tile);
                break;
        }
    }

    // ---------------- bath salts ----------------

    /// <summary>The trip being played, or null. Owns the running order of the pictures.</summary>
    private BathSaltsTrip? _trip;
    private float _tripT;
    private bool _tripPaidOut;

    /// <summary>Where this trip's pictures live, kept for as long as they are on screen.</summary>
    private string _tripFolder = "";
    private readonly List<Vector2> _tripPlaces = new();

    /// <summary>Where the pictures come from, relative to whoever took them.</summary>
    private const string BathSaltsFolder = "BathSalts";

    /// <summary>What each side takes, as a fraction of their own maximum health.</summary>
    private const float EnemyTollMax = 1.30f, PartyTollMax = 0.80f;

    /// <summary>How far the caster can come round from where they went under.</summary>
    private const int BathSaltsScatter = 15;

    /// <summary>The most of the screen one picture may take up, on its longer side.</summary>
    private const float TripPictureShare = 0.55f;

    /// <summary>
    /// Blacks the screen out and starts the pictures. Nothing is hurt yet: the
    /// damage lands at the far end, while the screen is still dark, so the
    /// board you come back to is already the board you have to deal with.
    /// </summary>
    private void StartTrip()
    {
        var taker = _actor!;
        // Remembered rather than looked up again while drawing: _actor is
        // cleared the moment the card finishes, and the pictures are still
        // fading out at that point.
        _tripFolder = $"{taker.Folder}/{BathSaltsFolder}";
        var files = _ctx.ContentIndex.Images(_tripFolder);

        if (files.Count == 0)
        {
            // Nothing to show. The card still does what it does — being told
            // there are no pictures is far better than a black screen that
            // never comes back, and better than the card silently doing nothing.
            _ctx.ReportProblem(_tripFolder,
                $"'{_actingCard!.Name}' found no pictures here, so there is nothing to see " +
                "— the damage still lands");
            Toast(_ctx.Strings.Get("iso_trip_empty"));
        }

        _trip = BathSaltsTrip.From(files, Rng);
        _tripT = 0f;
        _tripPaidOut = false;

        // One resting place per shot, picked now so a picture does not jitter
        // around the screen while it is fading. Kept as a 0..1 fraction and
        // turned into pixels at draw time, once the picture's real size is
        // known — a centre chosen in pixels would hang a big picture half off
        // the edge of the screen.
        _tripPlaces.Clear();
        for (int i = 0; i < _trip.Shots.Count; i++)
            _tripPlaces.Add(new Vector2((float)Rng.NextDouble(), (float)Rng.NextDouble()));

        Log(_ctx.Strings.Format("iso_trip_start", ("name", taker.Name)));
        EnterAct(Act.Tripping, 0f);
    }

    private void UpdateTrip(float dt)
    {
        if (_trip == null) { FinishAction(); return; }
        _tripT += dt;

        // the reckoning happens before the lights come back up
        float payout = _trip.Duration - BathSaltsTrip.FadeSeconds;
        if (!_tripPaidOut && _tripT >= payout)
        {
            _tripPaidOut = true;
            BathSaltsToll();
        }

        if (_tripT >= _trip.Duration)
        {
            _trip = null;
            FinishAction();
        }
    }

    /// <summary>
    /// What the salts actually do, settled while the screen is still black.
    ///
    /// Everyone on the board pays, both sides — enemies up to more than their
    /// whole health, so a lucky roll clears a room and an unlucky one wastes a
    /// turn. The one certainty is what it does to the man who took them: down
    /// to one, and standing somewhere he did not choose.
    /// </summary>
    private void BathSaltsToll()
    {
        var taker = _actor!;
        var report = new StringBuilder();

        foreach (var c in Everyone.Where(c => c.Alive && c != taker).ToList())
        {
            float most = c.IsPlayer ? PartyTollMax : EnemyTollMax;
            int dmg = (int)Math.Round(c.MaxHp * most * Rng.NextDouble());
            if (dmg > 0) ApplyHit(c, dmg, "Bath Salts", report);
        }

        // he does not roll. He always comes out of it on one health.
        if (taker.Alive)
        {
            taker.Hp = 1;
            report.AppendLine(_ctx.Strings.Format("iso_trip_survivor", ("name", taker.Name)));
            if (ScatterWithin(taker, BathSaltsScatter) is Point woke)
                report.AppendLine(_ctx.Strings.Format("iso_trip_woke",
                    ("name", taker.Name), ("x", woke.X.ToString()), ("y", woke.Y.ToString())));
        }

        if (report.Length > 0) Log(report.ToString().TrimEnd());
        _overlayKey = null;
    }

    /// <summary>
    /// Puts a character down on a random square within reach of where they
    /// were, on ground their body actually fits on. Returns where they landed,
    /// or null if there was nowhere to put them — in which case they stay put,
    /// which is a better answer than dropping them into a wall.
    /// </summary>
    private Point? ScatterWithin(CharacterInstance who, int reach)
    {
        var from = Tile(who);
        var taken = OccupiedExcept(who);
        var spots = _level.Blocks.Keys
            .Where(t => IsoMath.GridDistance(t, from) <= reach)
            .Where(t => Pathfinder.Fits(_level, t, who.SizeX, who.SizeY, _revealed, taken))
            .ToList();
        if (spots.Count == 0) return null;

        var landed = spots[Rng.Next(spots.Count)];
        who.GX = landed.X;
        who.GY = landed.Y;
        Record(ReplayEventKind.Move, who, from: from, to: landed);
        return landed;
    }

    /// <summary>
    /// The trip itself, drawn over everything. Black, then whatever is in the
    /// folder, then black again — the fade at each end is why the board never
    /// snaps back into view with the bodies already rearranged.
    /// </summary>
    private void DrawTrip(SpriteBatch batch)
    {
        if (_trip == null) return;
        var full = new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height);

        // in over the first moments, out over the last, solid in between
        float fade = BathSaltsTrip.FadeSeconds;
        float dark = _tripT < fade ? _tripT / fade
            : _tripT > _trip.Duration - fade ? Math.Max(0f, (_trip.Duration - _tripT) / fade)
            : 1f;
        Ui.FillRect(batch, _ctx.Pixel, full, Color.Black * Math.Clamp(dark, 0f, 1f));

        // which shot are we in, and how far into it
        float t = _tripT - fade;
        if (t < 0f) return;
        for (int i = 0; i < _trip.Shots.Count; i++)
        {
            var shot = _trip.Shots[i];
            if (t > shot.Duration) { t -= shot.Duration; continue; }

            var tex = _ctx.Assets.LoadTexture($"{_tripFolder}/{shot.FrameAt(t)}");
            // Fitted to a share of the screen, aspect kept. Sizing these the way
            // backgrounds are sized made a square picture fill the screen and a
            // tall one come out a third of the height, from the same folder.
            float fit = Math.Min(
                VirtualViewport.Width * TripPictureShare / tex.Width,
                VirtualViewport.Height * TripPictureShare / tex.Height);
            var size = new Vector2(tex.Width * fit, tex.Height * fit);
            // the fraction picks a corner somewhere in the room the picture
            // leaves over, so it always lands fully on screen
            var at = _tripPlaces[i];
            var corner = new Vector2(
                at.X * Math.Max(0f, VirtualViewport.Width - size.X),
                at.Y * Math.Max(0f, VirtualViewport.Height - size.Y));
            batch.Draw(tex,
                new Rectangle((int)corner.X, (int)corner.Y, (int)size.X, (int)size.Y),
                Color.White * (BathSaltsTrip.Opacity(shot, t) * dark));
            return;
        }
    }

    /// <summary>The blast at the end of the run: its own roll, over its own little area.</summary>
    private void BlowUpMower(Point where)
    {
        var card = _actingCard!;
        var report = new StringBuilder();
        var area = new HashSet<Point>();
        foreach (var block in _level.Blocks.Values)
        {
            var tile = new Point(block.X, block.Y);
            if (_level.Shown(tile, _revealed) &&
                IsoMath.GridDistance(tile, where) <= Math.Max(1, card.ExplosionRange))
                area.Add(tile);
        }

        _ctx.Sounds.Play(card.HitEvents.FirstOrDefault()?.Sound);
        foreach (var victim in CatchableBy(_actor, card)
                     .Where(c => c.Alive && c.Footprint.Any(area.Contains)).ToList())
        {
            // a marked target takes the top of the range, like any other roll
            int dmg = victim.IsVulnerable
                ? card.BlastMax
                : Rng.Next(card.BlastMin, card.BlastMax + 1);
            ApplyHit(victim, dmg, card.DamageType, report);
        }
        if (report.Length > 0) Log(report.ToString().TrimEnd());
        _blastSet = area;      // leave the purple up while the last beat plays
    }

    /// <summary>
    /// What one blow from this card is worth against this target. A card with a
    /// fixed number always does it; one written as a range rolls. A vulnerable
    /// target turns that roll into its highest value — the extra half is added
    /// afterwards, in ApplyHit, so it applies to fixed damage too.
    /// </summary>
    private int RollDamage(Card card, CharacterInstance target)
    {
        if (!card.VariableDamage) return card.Damage;
        return target.IsVulnerable ? card.Damage : Rng.Next(card.DamageMin, card.Damage + 1);
    }

    /// <summary>
    /// Stepping on a trigger square plays its dialogue, once.
    ///
    /// Once per DIALOGUE, not once per square. Painting the same conversation
    /// across a doorway is the normal way to catch a party however it walks in,
    /// and every one of those squares firing meant hearing the same speech four
    /// times. Every square carrying that name is spent the first time any of
    /// them goes off.
    /// </summary>
    private bool FireTrigger(Point tile)
    {
        if (_level.TriggerAt(tile) is not LevelTrigger trigger || trigger.Fired) return false;
        foreach (var t in _level.Triggers.Where(t =>
                     t.Dialogue.Equals(trigger.Dialogue, StringComparison.OrdinalIgnoreCase)))
            t.Fired = true;
        var lines = _dialogue.Get(trigger.Dialogue);
        if (lines == null || lines.Count == 0)
        {
            _ctx.ReportProblem(DialogueLibrary.PathFor(_level.Name),
                $"trigger at {tile.X},{tile.Y} calls dialogue '{trigger.Dialogue}', which has no lines");
            return false;
        }
        _lines = lines;
        _lineIndex = 0;
        return true;
    }

    private void AdvanceDialogue()
    {
        _lineIndex++;
        if (_lines != null && _lineIndex < _lines.Count) return;
        _lines = null;
        _lineIndex = 0;
        // walking into a fight and into a conversation on the same step is possible
        if (_mode == Mode.Explore)
            foreach (var p in LivingParty)
                if (CheckAggro(p)) break;
    }

    /// <summary>
    /// Who gets a free move when a fight starts. Only the Dirtbag, who cheats.
    /// Named rather than flagged in Classes.txt because it is a joke about one
    /// character, not a stat anybody else should be able to buy.
    /// </summary>
    private static bool IsCheat(CharacterInstance c) =>
        c.Name.Equals("Dirtbag", StringComparison.OrdinalIgnoreCase);

    private bool CheckAggro(CharacterInstance mover)
    {
        var seen = VisibleEnemies.Where(e =>
            _party.Any(p => p.Alive && e.DistanceTo(p) <= AggroTiles)).ToList();
        if (seen.Count == 0) return false;

        foreach (var e in seen) _aggroed.Add(e);
        if (_mode is Mode.Explore)
        {
            // Nobody gets a free shuffle when a fight starts any more: you
            // fight from where you were caught. The Dirtbag cheats, and gets
            // one whatever happened — including when it was him who walked
            // into them.
            _freeMovers.Clear();
            foreach (var p in LivingParty.Where(IsCheat))
            {
                p.MovePoints = p.MoveMax;
                _freeMovers.Add(p);
            }
            if (_freeMovers.Count > 0)
            {
                _mode = Mode.FreeMove;
                _selected = _freeMovers.FirstOrDefault();
                _overlayKey = null;
                Log(_ctx.Strings.Format("iso_spotted",
                    ("name", _freeMovers.First().Name)));
            }
            else
            {
                StartCombat();
            }
        }
        return true;
    }

    /// <summary>
    /// Points saved up belong to one fight. Cleared when combat opens so a
    /// previous battle cannot bankroll this one, and cleared again when it ends
    /// so the walk to the next fight is not spent hoarding.
    /// </summary>
    private void ClearSavedActions()
    {
        foreach (var c in Everyone)
        {
            c.ResetActionPoints();
            StopGuarding(c);
        }
    }

    /// <summary>Lifts a guard zone: the ground stops being watched and the marks come off.</summary>
    private static void StopGuarding(CharacterInstance c) => c.Watch.Stand_Down();

    private void StartCombat()
    {
        ClearSavedActions();
        _order.Clear();
        var players = LivingParty.Where(p => !p.IsPet).OrderBy(_ => Rng.Next()).ToList();
        var foes = _aggroed.Where(e => e.Alive).OrderBy(_ => Rng.Next()).ToList();
        bool playersFirst = Rng.Next(2) == 0;
        var first = playersFirst ? players : foes;
        var second = playersFirst ? foes : players;
        for (int i = 0; i < Math.Max(first.Count, second.Count); i++)
        {
            if (i < first.Count) _order.Add(first[i]);
            if (i < second.Count) _order.Add(second[i]);
        }
        _turn = -1;
        NextTurn();
    }

    private void NextTurn()
    {
        CancelCard();
        // finishing a turn in the fire catches you, the same as starting one there
        if (Current is CharacterInstance leaving) Ignite(leaving);
        if (PartyWiped) { FinishMission("party down"); _ctx.SwitchTo(new DeathScreen(_ctx)); return; }
        if (!_aggroed.Any(e => e.Alive))
        {
            ClearSavedActions();
            _aggroed.Clear();
            _order.Clear();
            _turn = -1;
            _overlayKey = null;
            if (_enemies.All(e => !e.Alive)) { FinishMission("victory"); _mode = Mode.Victory; return; }
            _mode = Mode.Explore;
            Log(_ctx.Strings.Get("iso_clear"));
            return;
        }

        foreach (var e in _aggroed.Where(e => e.Alive && !_order.Contains(e)))
            _order.Add(e);

        for (int step = 0; step < _order.Count; step++)
        {
            _turn = (_turn + 1) % _order.Count;
            if (_order[_turn].Alive) break;
        }
        // A guard forgets anybody who is no longer standing on the ground, so
        // walking back in is a fresh approach. Somebody who died in the zone is
        // forgotten too, in case their name is reused.
        foreach (var g in Everyone.Where(g => g.IsGuarding))
            foreach (var t in Everyone.Where(t => !t.Alive || !InGuardZone(g, t)))
                g.Watch.Forget(Key(t));

        var current = Current!;
        _replayTurn++;
        Record(ReplayEventKind.Turn, current, amount: current.Hp,
            note: $"{current.Hp}/{current.MaxHp} hp");

        // Standing your ground lasts until your next turn comes round. It cost
        // you the rest of THAT turn's movement; it does not cost you every
        // turn after, so the zone lifts here and you walk again.
        if (current.IsGuarding) StopGuarding(current);
        current.MovePoints = current.MoveMax;
        current.RefreshActionPoints();
        AgeFires(current);
        _overlayKey = null;

        // a channelled card roots its caster: no movement until it is released
        if (current.IsChannelling)
        {
            if (current.ChannelTurnsLeft > 0) current.ChannelTurnsLeft--;
            current.MovePoints = 0;
            Log(_ctx.Strings.Format("iso_channelling",
                ("name", current.Name), ("card", current.ChannellingCard)));
        }

        if (!BurnAtTurnStart(current)) { NextTurn(); return; }

        // Stunned: the turn arrives and goes straight past. The points and
        // movement handed out above are spent doing nothing, which is the whole
        // cost of it. Checked after the burn so a stunned character still cooks.
        //
        // It is SHOWN rather than skipped in silence: the camera goes to them
        // and holds for a moment, so a turn that produces no action still reads
        // as somebody's turn rather than as the game having missed one out.
        if (current.IsStunned)
        {
            current.StunTurns--;
            current.MovePoints = 0;
            current.ActionPoints = 0;
            Log(_ctx.Strings.Format("iso_stun_skip",
                ("name", current.Name), ("turns", current.StunTurns.ToString())));
            RecenterOn(current);
            _stunHold = StunHoldSeconds;
            _mode = current.IsPlayer ? Mode.PlayerTurn : Mode.EnemyTurn;
            return;
        }

        if (current.IsPlayer)
        {
            // a summoner's turn is also its pets': they get their points and
            // movement now, and the player picks between them by clicking
            _petControl = null;
            foreach (var pet in LivingParty.Where(p => p.Owner == current))
            {
                if (pet.IsGuarding) StopGuarding(pet);
                pet.MovePoints = pet.MoveMax;
                pet.RefreshActionPoints();
            }
            _hand = HandOf(current);
            _mode = Mode.PlayerTurn;
        }
        else
        {
            _mode = Mode.EnemyTurn;
        }
    }

    /// <summary>
    /// Fires age once per round rather than once per character, so a three-turn
    /// fire lasts three rounds however many people are in the fight. The round
    /// is marked by the first character in the order taking their turn.
    /// </summary>
    private void AgeFires(CharacterInstance current)
    {
        if (_fires.Count == 0 || _order.Count == 0 || _order[_turn] != _order.First(o => o.Alive))
            return;
        foreach (var tile in _fires.Keys.ToList())
            if (--_fires[tile] <= 0)
                _fires.Remove(tile);
    }

    // ---------------- overlays ----------------

    /// <summary>
    /// Blue = where the selected character can walk. Red = how far the armed
    /// card reaches from where that character is standing right now — not from
    /// everywhere it could walk to first — and red replaces blue while it shows.
    /// </summary>
    private void RefreshOverlays()
    {
        var mover = ActiveMover;
        var card = _selectedCard ?? HoveredCard();
        var key = (mover, mover == null ? default : Tile(mover), mover?.MovePoints ?? 0,
            card, _revealed.Count, _targets.Count);
        if (Equals(_overlayKey, key)) return;
        _overlayKey = key;

        _moveSet = new Dictionary<Point, int>();
        _rangeSet = new HashSet<Point>();
        _cardArmed = false;
        if (mover == null) return;
        _cardArmed = card != null && _mode != Mode.Explore;

        // a Leap card reaches further and vaults terrain while closing in
        int budget = _mode == Mode.Explore ? 9999 : mover.MovePoints + (card?.LeapBonus ?? 0);
        _moveSet = Pathfinder.Reachable(_level, Tile(mover), budget, _revealed,
            OccupiedExcept(mover), card?.IgnoresHeight ?? false, PassThroughFor(mover),
            mover.SizeX, mover.SizeY).Cost;
        if (card == null || _mode == Mode.Explore) return;

        // a Leap card's reach covers a lot of ground, so it gets its own,
        // lighter wash rather than drowning the level in red
        _rangeOpacityKey = card.LeapBonus > 0 ? "Leap" : "Range";

        // a cone is shown by the purple wedge that follows the cursor, so a red
        // diamond around it would only be a second, wrong-shaped answer
        if (card.Delivery == Delivery.Cone) return;

        // A Leap card carries its own approach: the reach it advertises is the
        // card's range measured from anywhere the leap can put the caster, not
        // from the tile they happen to be standing on.
        var here = Tile(mover);
        var stands = card.LeapBonus > 0
            ? _moveSet.Keys.Append(here).ToList()
            : new List<Point> { here };
        // a card that plants its caster shows the ground it will WATCH, which
        // is measured by the Guard amount rather than by how far the card
        // reaches — the same number the zone itself is built from
        int reach = card.IsGuard ? card.GuardReach : card.Range;
        foreach (var block in _level.Blocks.Values)
        {
            var tile = new Point(block.X, block.Y);
            if (!_level.Shown(tile, _revealed)) continue;
            if (stands.Any(s => IsoMath.GridDistance(s, tile) <= reach))
                _rangeSet.Add(tile);
        }
    }

    private Card? HoveredCard()
    {
        if (_mode is not (Mode.PlayerTurn or Mode.PlayerTarget)) return null;
        var rects = HandRects();
        for (int i = 0; i < _hand.Count; i++)
            if (rects[i].Contains(_pointer)) return _hand[i];
        return null;
    }

    /// <summary>
    /// The purple area an armed area card would cover, following the cursor.
    /// </summary>
    private void UpdateAim()
    {
        _blastSet = new HashSet<Point>();
        _doomed.Clear();

        // While a channel is open the aim is already fixed, so the purple shows
        // where the shot is going to land instead of following the cursor. It
        // is the only way to see what was committed to a turn ago.
        if (Acting is CharacterInstance held && held.IsChannelling &&
            CardNamed(held.ChannellingCard) is Card waiting)
        {
            _blastOpacityKey = waiting.Delivery == Delivery.Cone ? "Cone" : "AoE";
            _blastSet = AreaOf(waiting, Tile(held), held.ChannelAim);
            MarkDoomed(waiting);
            return;
        }

        var aiming = _selectedCard ?? HoveredCard();
        if (aiming == null || Acting == null || _mode == Mode.Explore) return;

        if (!aiming.TargetsGround)
        {
            // A card aimed at one body marks whoever the yellow square is over,
            // so you can see who you are about to hit before you commit — and
            // see nothing when the square is empty.
            MarkDoomed(aiming);
            return;
        }

        _blastOpacityKey = aiming.Delivery == Delivery.Cone ? "Cone" : "AoE";

        if (FindTileAt(_worldPointer) is Point c && ReachableAim(Acting, c, aiming))
        {
            // For a summon the purple is the creature's own outline, and it is
            // only drawn where the creature will actually go. Painting it over
            // a square the body cannot fit on would promise a placement the
            // click then refuses, which reads as a bug.
            if (aiming.IsSummon && !SummonFits(aiming, c)) return;
            _blastSet = AreaOf(aiming, Tile(Acting), c);
        }
        MarkDoomed(aiming);
    }

    /// <summary>
    /// Everyone this card would hit if it went off right now, so they can be
    /// outlined in red before anybody commits to anything.
    ///
    /// It asks the same question the card itself asks when it lands —
    /// CatchableBy — so Friendly Fire is answered once, in one place. Turn the
    /// field on and your own people light up too, because they really are
    /// about to be hit.
    /// </summary>
    private void MarkDoomed(Card card)
    {
        // a summon puts a creature down; there is nobody to hurt
        if (card.IsSummon) return;

        var reachable = CatchableBy(Acting, card).Where(c => c.Alive);
        if (_blastSet.Count > 0)
        {
            foreach (var c in reachable.Where(c => c.Footprint.Any(_blastSet.Contains)))
                _doomed.Add(c);
            return;
        }
        // no area: it is whoever the cursor is sitting on, plus anybody already
        // chosen for a card that wants several
        foreach (var c in _targets) _doomed.Add(c);
        if (card.TargetsGround) return;
        if (FindTileAt(_worldPointer) is Point tile && WhoIsOn(tile) is CharacterInstance who
            && reachable.Contains(who) && ReachableAim(Acting!, tile, card))
            _doomed.Add(who);
    }

    /// <summary>Whether this character already has one of these on the board.</summary>
    private bool SummonAlive(CharacterInstance owner, string what) =>
        what.Length > 0 && _party.Any(p => p.Alive && p.Owner == owner &&
            p.Name.Equals(what, StringComparison.OrdinalIgnoreCase));

    /// <summary>The card of that name from whichever deck the holder draws from.</summary>
    private Card? CardNamed(string name) =>
        _hand.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? _ctx.Cards.All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? _ctx.EnemyCards.All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The tiles a card's area covers: a cone from the caster, or a blast radius.</summary>
    private HashSet<Point> AreaOf(Card card, Point from, Point aim)
    {
        // a summon paints the shape of the thing being summoned, so a body two
        // squares long shows both squares before it is committed to
        if (card.IsSummon)
        {
            var body = _ctx.Classes.Get(card.Summons);
            return new HashSet<Point>(Pathfinder.Footprint(aim,
                body?.SizeX ?? 1, body?.SizeY ?? 1));
        }

        // A mower shows the lane it is being pointed down: straight, no
        // diagonals, as far as it could go. Where it ACTUALLY ends up is
        // another matter — it wanders, and it bounces — but the lane is the
        // decision the player is being asked to make.
        if (card.IsMower)
        {
            var lane = new HashSet<Point>();
            var step = MowerRun.HeadingToward(from, aim);
            var at = from;
            for (int i = 0; i < card.MowerTiles; i++)
            {
                at = new Point(at.X + step.X, at.Y + step.Y);
                if (!_level.Shown(at, _revealed)) break;
                lane.Add(at);
            }
            return lane;
        }

        var set = new HashSet<Point>();
        foreach (var block in _level.Blocks.Values)
        {
            var tile = new Point(block.X, block.Y);
            if (!_level.Shown(tile, _revealed)) continue;
            bool hit = card.Delivery == Delivery.Cone
                ? IsoMath.InCone(from, aim, tile, card.Range)
                : IsoMath.GridDistance(tile, aim) <= card.ExplosionRange;
            if (hit) set.Add(tile);
        }
        return set;
    }

    /// <summary>
    /// Can the card be aimed at this tile? A cone only takes a heading from the
    /// aim point — its own Range caps how far the wedge runs — so any tile will
    /// do. Everything else has to be within reach of where the caster stands.
    /// </summary>
    private bool ReachableAim(CharacterInstance me, Point aim, Card card) =>
        card.Delivery == Delivery.Cone || me.DistanceTo(aim) <= card.Range;

    /// <summary>
    /// Where the caster acts from: where it already stands if that works, else
    /// the cheapest square it can afford with every chosen target in reach. The
    /// player never picks the angle — melee just closes in by the shortest walk.
    /// </summary>
    private Point? BestApproach(CharacterInstance me, List<CharacterInstance> targets, Card card)
    {
        var here = Tile(me);
        if (card.Delivery == Delivery.Cone) return here;
        // measured to the nearest part of the target, so a four-tile body is in
        // reach of anything standing against any of its sides
        bool InRange(Point from) => targets.All(t => t.DistanceTo(from) <= card.Range);
        if (InRange(here)) return here;
        return _moveSet.Keys.Where(InRange)
            .OrderBy(t => _moveSet[t]).Select(t => (Point?)t).FirstOrDefault();
    }

    // ---------------- input ----------------

    private void HandleClicks()
    {
        if (_tap is not Point press) return;
        _tap = null;

        if (_mode is Mode.PlayerTurn or Mode.PlayerTarget && HandleCardClick(press)) return;
        if (HitButton(press)) return;

        // Everything on the board is resolved by SQUARE, never by which sprite
        // the cursor happens to be over. The yellow square under the cursor is
        // what a click acts on, so what you see marked is what you get — and
        // somebody standing behind a tree or under a taller neighbour is
        // reachable, which clicking sprites could never manage. This used to be
        // what Ctrl did; now it is simply how clicking works, and Ctrl is left
        // to fade the board so the grid reads clearly.
        // a tap lands where the pointer is, so the board is asked in art
        // pixels rather than in the design space the HUD uses
        if (FindTileAt(_worldPointer) is Point square) ClickSquare(square);
    }

    /// <summary>
    /// Clicking a character out of combat, with the modifier keys behaving the
    /// way they do when picking files:
    ///
    /// - plain click replaces the selection with this one
    /// - shift adds to it
    /// - ctrl adds this one, or drops it if it was already picked
    /// </summary>
    private void PickCharacter(CharacterInstance who)
    {
        if (_ctrl)
        {
            if (!_picked.Remove(who)) _picked.Add(who);
            return;
        }
        if (_shift)
        {
            if (!_picked.Contains(who)) _picked.Add(who);
            return;
        }
        _picked.Clear();
        _picked.Add(who);
    }

    /// <summary>
    /// A click resolved by square — the one the yellow cursor is sitting on.
    /// With a card up it plays on whoever stands there; without one it is a
    /// move, or a selection when a party member is on the square.
    ///
    /// This is how every board click works now. It used to need Ctrl held, and
    /// a plain click hunted for a sprite under the cursor instead — which meant
    /// aiming at a head rather than at a square, and left anyone behind a tree
    /// or under a taller neighbour unclickable.
    /// </summary>
    private void ClickSquare(Point tile)
    {
        var who = WhoIsOn(tile);

        if (_mode == Mode.PlayerTarget && _selectedCard is Card aiming)
        {
            if (aiming.TargetsGround) { TryTargetGround(tile); return; }
            if (who == null) { Toast(_ctx.Strings.Get("iso_empty_square")); return; }
            if (aiming.TargetsAnyone)
            {
                if (who != Acting) TryTarget(who);
                else Toast(_ctx.Strings.Get("iso_needs_other"));
                return;
            }
            // with Friendly Fire this lets both sides through, so a card that
            // does not care whose side it hits can be pointed at your own
            if (MayTarget(Acting, aiming, who)) TryTarget(who);
            else Toast(_ctx.Strings.Get(aiming.TargetsAllies ? "iso_needs_ally" : "iso_needs_enemy"));
            return;
        }

        if (who is { IsPlayer: true })
        {
            if (_mode == Mode.Explore) { PickCharacter(who); _overlayKey = null; }
            else if (_mode == Mode.FreeMove && _freeMovers.Contains(who))
            { PickCharacter(who); _overlayKey = null; }
            else if (_mode is Mode.PlayerTurn or Mode.PlayerTarget) TakeControlOf(who);
            return;
        }

        HandleTileClick(tile);
    }

    /// <summary>
    /// Opens any door this character is now standing next to.
    ///
    /// Walking up to a door is the whole interaction. It used to want a click,
    /// from within two squares, which meant knowing there was a door there
    /// before you could see the room it opened onto.
    /// </summary>
    private void OpenDoorsBeside(CharacterInstance who)
    {
        foreach (var square in who.Footprint.SelectMany(LevelData.Beside).Distinct().ToList())
            if (_level.DoorAt(square) is { Open: false } door &&
                _level.RoomsBeside(square).Count >= 2)
                OpenDoor(door);
    }

    /// <summary>
    /// Opens a doorway and reveals the rooms on both sides of it. The rooms are
    /// read off the squares beside the door rather than stored on it, and the
    /// whole run of touching doorway squares opens together — a two-square gap
    /// is one door, not two.
    /// </summary>
    private void OpenDoor(LevelDoor door)
    {
        var group = _level.DoorGroup(door);
        foreach (var d in group)
        {
            d.Open = true;
            foreach (string room in _level.RoomsBeside(d.Tile))
                _revealed.Add(room);
        }
        _overlayKey = null;
        Log(_ctx.Strings.Get("iso_door_open"));
        var nearest = LivingParty.OrderBy(p => group.Min(d => p.DistanceTo(d.Tile))).First();
        CheckAggro(nearest);
    }

    private void HandleTileClick(Point tile)
    {
        var mover = ActiveMover;
        if (mover == null || !mover.Alive) return;
        // a card spends the turn's movement, but Nimble hands some back — so the
        // gate is the points on hand, never "has a card been played yet"
        if (mover.IsChannelling)
        {
            Toast(_ctx.Strings.Format("iso_channel_rooted", ("card", mover.ChannellingCard)));
            return;
        }
        if (_mode is Mode.PlayerTurn or Mode.PlayerTarget && mover.MovePoints <= 0)
        {
            Toast(_ctx.Strings.Get("iso_move_spent"));
            return;
        }
        // Out of combat the whole selection walks. Everybody heads for the free
        // square nearest where you clicked, so a click never simply refuses
        // because four people will not fit on one tile.
        if (_mode == Mode.Explore && _picked.Count > 1)
        {
            MarchTo(tile);
            return;
        }

        if (!_moveSet.TryGetValue(tile, out int spent)) return;

        if (_mode != Mode.Explore) mover.MovePoints -= spent;
        BeginWalk(mover, tile, null);
    }

    /// <summary>
    /// Sends everybody picked towards one square. The nearest walks onto it and
    /// the rest take the closest free ground they can reach, so a group move
    /// always happens rather than being refused for want of room.
    ///
    /// They are walked one after another — the engine animates one walker at a
    /// time — with each leg starting when the one before it lands.
    /// </summary>
    private void MarchTo(Point goal)
    {
        var going = _picked.Where(p => p.Alive).ToList();
        if (going.Count == 0) return;

        // nearest first, so the one already closest gets the square itself
        var order = going.OrderBy(p => p.DistanceTo(goal)).ToList();
        var claimed = new HashSet<Point>();
        var legs = new List<(CharacterInstance Who, Point Where)>();
        foreach (var who in order)
        {
            var taken = OccupiedExcept(who);
            foreach (var c in claimed) taken.Add(c);
            var spot = _level.Blocks.Keys
                .Where(t => Pathfinder.Fits(_level, t, who.SizeX, who.SizeY, _revealed, taken))
                .OrderBy(t => IsoMath.GridDistance(t, goal))
                .ThenBy(t => IsoMath.GridDistance(t, Tile(who)))
                .Cast<Point?>()
                .FirstOrDefault();
            if (spot is not Point at) continue;
            foreach (var t in Pathfinder.Footprint(at, who.SizeX, who.SizeY)) claimed.Add(t);
            legs.Add((who, at));
        }
        if (legs.Count == 0) return;

        // The first is the walker proper — it runs the clock and the per-step
        // checks that belong to a walk. The rest ride the same clock beside it.
        BeginWalk(legs[0].Who, legs[0].Where, null);
        foreach (var (who, where) in legs.Skip(1))
            AddEscort(who, where);
    }

    /// <summary>
    /// Puts another body on the current walk. It steps in time with the main
    /// walker rather than after it, which is what makes a group move read as
    /// one movement instead of a queue.
    /// </summary>
    private void AddEscort(CharacterInstance who, Point goal)
    {
        var (_, parent) = Pathfinder.Reachable(_level, Tile(who), 9999, _revealed,
            OccupiedExcept(who), false, PassThroughFor(who), who.SizeX, who.SizeY);
        var path = Pathfinder.PathTo(parent, Tile(who), goal);
        if (path.Count == 0) return;
        Record(ReplayEventKind.Move, who, from: Tile(who), to: goal, amount: path.Count);
        _escorts.Add(new Escort { Who = who, Path = path, From = Tile(who) });
    }

    private void BeginWalk(CharacterInstance mover, Point goal, Action? after, Card? via = null)
    {
        int budget = _mode == Mode.Explore ? 9999 : mover.MoveMax + (via?.LeapBonus ?? 0);
        var (_, parent) = Pathfinder.Reachable(_level, Tile(mover), budget, _revealed,
            OccupiedExcept(mover), via?.IgnoresHeight ?? false, PassThroughFor(mover), mover.SizeX, mover.SizeY);
        _walker = mover;
        _walkFrom = Tile(mover);
        _escorts.Clear();          // a new walk is not the old one's group
        _walkPath = Pathfinder.PathTo(parent, _walkFrom, goal);
        if (_walkPath.Count > 0)
            Record(ReplayEventKind.Move, mover, from: _walkFrom, to: goal,
                amount: _walkPath.Count);
        _walkT = 0f;
        _walkPause = 0f;
        _afterWalk = after;
        _overlayKey = null;
    }

    /// <summary>
    /// Hands the turn's controls to one of the characters sharing it — the
    /// summoner or one of its pets. Clicking anybody else does nothing, since
    /// they do not act on this turn.
    /// </summary>
    private void TakeControlOf(CharacterInstance who)
    {
        if (!ActsWith(who, Current)) return;
        _petControl = who == Current ? null : who;
        CancelCard();
        _hand = HandOf(ActiveMover ?? who);
        _overlayKey = null;
    }

    /// <summary>
    /// Plays the card in a numbered slot, exactly as clicking it would. The
    /// number is what is printed over the card, counting from 1.
    /// </summary>
    private void PlayCardByNumber(int slot)
    {
        if (slot < 1 || slot > _hand.Count) return;
        SelectCard(_hand[slot - 1]);
    }

    private bool HandleCardClick(Point press)
    {
        var rects = HandRects();
        for (int i = 0; i < _hand.Count; i++)
            if (rects[i].Contains(press))
            {
                SelectCard(_hand[i]);
                return true;
            }
        return false;
    }

    /// <summary>
    /// Arming a card, however it was chosen — clicked, or named by its number
    /// key. Everything the two ways in have in common lives here so they cannot
    /// drift apart: the same affordability check, the same channel release, the
    /// same shortcut for a card with nothing to aim at.
    /// </summary>
    private void SelectCard(Card card)
    {
        if (Acting is CharacterInstance holder && holder.ActionPoints < card.ActionCost)
        {
            Toast(_ctx.Strings.Format("iso_no_actions",
                ("cost", card.ActionCost.ToString()),
                ("points", holder.ActionPoints.ToString())));
            return;
        }
        _selectedCard = card;
        _targets.Clear();
        _overlayKey = null;

        // Releasing a channel does NOT ask where to aim. It was aimed on the
        // turn it was started, and it has been on its way ever since — being
        // asked again would be a second decision the caster never got to make.
        if (Acting is CharacterInstance caster && caster.IsChannelling &&
            card.Name.Equals(caster.ChannellingCard, StringComparison.OrdinalIgnoreCase))
        {
            if (caster.ChannelTurnsLeft > 0)
            {
                Toast(_ctx.Strings.Format("iso_channel_waiting",
                    ("card", caster.ChannellingCard),
                    ("turns", caster.ChannelTurnsLeft.ToString())));
                _selectedCard = null;
                return;
            }
            // what it catches is settled now, where it lands, not when it was
            // aimed — anyone who has since moved into the area is under it
            PlayArea(AreaOf(card, Tile(caster), caster.ChannelAim), caster.ChannelAim);
            return;
        }

        // A pure self-cast (changing shape, planting your feet) has nothing to
        // aim at. A summon is the exception: it acts on the caster, but WHERE
        // the creature lands is the player's call, so it still asks.
        //
        // A guard card goes off immediately even though it carries a damage
        // number, because that number is what the ground does to whoever walks
        // onto it later — there is nobody to point at now.
        if (card.IsGuard || card.IsBathSalts || (card.Damage <= 0 && !card.IsSummon &&
            card.Effects.All(e => Data.Effects.IsSelfCast(e.Name))))
        {
            PlayCard(new List<CharacterInstance>(), Tile(Acting!));
            return;
        }
        _mode = Mode.PlayerTarget;
    }

    /// <summary>
    /// One click per target. A single-target card fires on that click; a card
    /// wanting several collects one per click and fires on the last one.
    /// </summary>
    private void TryTarget(CharacterInstance enemy)
    {
        var card = _selectedCard!;
        var me = Acting!;
        int wanted = TargetsWanted(card);
        if (_targets.Contains(enemy)) return;   // already picked; ignore the repeat

        _targets.Add(enemy);
        if (BestApproach(me, _targets, card) == null)
        {
            _targets.Remove(enemy);
            Toast(_ctx.Strings.Get("iso_out_of_range"));
            return;
        }
        if (_targets.Count < wanted)
        {
            Toast(_ctx.Strings.Format("iso_pick_more", ("count", (wanted - _targets.Count).ToString())));
            _overlayKey = null;
            return;
        }
        Commit(me, card);
    }

    /// <summary>
    /// Ground aiming, for blasts and cones: one click fires at that tile, and
    /// anything the purple outline covers is hit.
    /// </summary>
    private void TryTargetGround(Point tile)
    {
        var card = _selectedCard!;
        var me = Acting!;
        if (!ReachableAim(me, tile, card)) { Toast(_ctx.Strings.Get("iso_out_of_range")); return; }
        // a creature needs somewhere to stand: say so on the spot rather than
        // spending the points and quietly putting it somewhere else
        if (card.IsSummon && !SummonFits(card, tile))
        {
            Toast(_ctx.Strings.Format("iso_summon_no_room", ("name", card.Summons)));
            return;
        }
        PlayArea(AreaOf(card, Tile(me), tile), tile);
    }

    /// <summary>Whether the creature a card summons has room to stand on that square.</summary>
    private bool SummonFits(Card card, Point at)
    {
        var body = _ctx.Classes.Get(card.Summons);
        return Pathfinder.Fits(_level, at, body?.SizeX ?? 1, body?.SizeY ?? 1,
            _revealed, OccupiedExcept(null));
    }

    /// <summary>
    /// Who a card is allowed to touch. Normally the other side; with Friendly
    /// Fire it is everyone standing there, the caster's own team included.
    ///
    /// Sides are read from the CASTER, not from the player, so an enemy's
    /// friendly-fire blast catches the goblin next to it exactly the way one
    /// of ours catches the Cyborg.
    /// </summary>
    private IEnumerable<CharacterInstance> CatchableBy(CharacterInstance? caster, Card card)
    {
        if (card.FriendlyFire)
            return LivingParty.Concat(VisibleEnemies);
        bool casterIsPlayer = caster?.IsPlayer ?? true;
        bool wantsPlayers = card.TargetsAllies == casterIsPlayer;
        return wantsPlayers ? LivingParty : VisibleEnemies;
    }

    /// <summary>Whether this card may be aimed at that character, given who is casting.</summary>
    private bool MayTarget(CharacterInstance? caster, Card card, CharacterInstance who) =>
        CatchableBy(caster, card).Contains(who);

    private void PlayArea(HashSet<Point> area, Point aim)
    {
        var card = _selectedCard;
        if (card == null) return;
        // A summon paints a square to stand on, not a blast: it hits nobody,
        // however many people happen to be near where the creature lands. A
        // mower catches people too, but only once it has driven into them —
        // who that turns out to be is settled by the run, not by the aim.
        var caught = card.IsSummon || card.IsMower
            ? new List<CharacterInstance>()
            : CatchableBy(Acting, card).Where(c => c.Footprint.Any(area.Contains)).ToList();
        // the ground the card covered is remembered here, because by the time
        // the hits resolve the aim and the area are gone
        _burnArea = card.FireTileTurns > 0 ? new HashSet<Point>(area) : new HashSet<Point>();
        _skyTarget = aim;
        PlayCard(caught, aim);
    }

    /// <summary>Sets ground alight, or tops up a square that is already burning.</summary>
    private void LightFires(IEnumerable<Point> tiles, int turns, StringBuilder report)
    {
        int lit = 0;
        foreach (var tile in tiles)
        {
            if (_level.BlockAt(tile) == null) continue;   // no ground, nothing to burn
            _fires.TryGetValue(tile, out int already);
            _fires[tile] = Math.Max(already, turns);
            lit++;
        }
        if (lit > 0)
            report.AppendLine(_ctx.Strings.Format("iso_fire_lit",
                ("count", lit.ToString()), ("turns", turns.ToString())));
    }

    /// <summary>How many bodies this card needs clicked before it can fire.</summary>
    private int TargetsWanted(Card card) => card.Kind == CardKind.MultiTarget
        ? Math.Max(1, Math.Min(card.Targets, CatchableBy(Acting, card).Count()))
        : 1;

    private void Commit(CharacterInstance me, Card card)
    {
        var square = BestApproach(me, _targets, card);
        if (square == null) { Toast(_ctx.Strings.Get("iso_out_of_range")); return; }

        var aimTile = Tile(_targets[0]);
        var shots = _targets.ToList();
        if (square.Value == Tile(me))
        {
            PlayCard(shots, aimTile);
            return;
        }
        me.MovePoints -= _moveSet.TryGetValue(square.Value, out int c) ? c : 0;
        BeginWalk(me, square.Value, () => PlayCard(shots, aimTile), card);
    }

    private bool HitButton(Point press)
    {
        switch (_mode)
        {
            case Mode.FreeMove when DoneRect.Contains(press):
                _freeMovers.Clear();
                StartCombat();
                return true;
            case Mode.PlayerTurn or Mode.PlayerTarget when EndTurnRect.Contains(press):
                NextTurn();
                return true;
        }
        return false;
    }

    /// <summary>Whether the record button is on screen, so drawing and clicking agree.</summary>
    private bool ReplayButtonUp => !_replayMode && _mode != Mode.Victory;

    // ---------------- card + enemy actions ----------------

    private void PlayCard(List<CharacterInstance> aimed, Point blastCenter)
    {
        var card = _selectedCard;
        if (card == null) return;
        _actor = Acting;
        _actingCard = card;
        _victims = aimed;
        _aimPoint = blastCenter;

        // Turn to face what is being aimed at. Unlike a walk this CAN point at
        // a screen cardinal — nothing stops you shooting straight up the screen
        // — and the drawing rounds that to the nearest pose there is art for.
        if (_actor != null && blastCenter != Tile(_actor))
            _actor.FaceTowards(Tile(_actor), blastCenter);
        _selectedCard = null;
        _targets.Clear();
        _blastSet.Clear();
        // playing a borrowed card uses it up and hands it straight back
        if (_actor != null && _actor.Stolen.FirstOrDefault(st =>
                st.CardName.Equals(card.Name, StringComparison.OrdinalIgnoreCase)) is StolenCard spent)
        {
            ReturnStolen(spent, _actor);
            Log(_ctx.Strings.Format("iso_steal_over",
                ("card", spent.CardName), ("owner", spent.From?.Name ?? "?")));
        }

        Record(ReplayEventKind.Card, _actor, card: card.Name, to: blastCenter,
            target: string.Join("/", aimed.Select(v => v.Name)), amount: card.ActionCost);
        _actor!.ActionPoints = Math.Max(0, _actor.ActionPoints - card.ActionCost);
        // changing shape is free of the movement penalty too: a shapeshifter can
        // shift and then still walk, though the shift itself costs its points
        if (card.BecomesForm == null)
            _actor.MovePoints = 0;   // a card ends this turn's movement, unless Nimble gives it back
        _overlayKey = null;

        // A channelled card's FIRST play only starts the channel: it is paid
        // for, the caster is rooted, and nothing else happens until a later
        // turn releases it. The release comes back through here with the
        // channel already open, and runs the card for real.
        if (card.IsChannelled && !_actor.IsChannelling)
        {
            _actor.ChannellingCard = card.Name;
            _actor.ChannelTurnsLeft = card.ChannelTurns;
            _actor.ChannelAim = blastCenter;      // aimed now, fired later
            _actor.MovePoints = 0;
            Log(_ctx.Strings.Format("iso_channel_start",
                ("name", _actor.Name), ("card", card.Name)));
            _ctx.Sounds.Play(card.CastingSound);
            StartCastAnimation(_actor);
            _actingCard = null;
            _victims.Clear();
            ResumeAfterAction();
            return;
        }
        if (card.IsChannelled) ClearChannel(_actor);

        _ctx.Sounds.Play(card.CastingSound);
        StartCastAnimation(_actor!);
        _mode = Mode.Acting;
        EnterAct(Act.Casting, card.CastingTime ?? _ctx.Sounds.Duration(card.CastingSound));
    }

    private static void ClearChannel(CharacterInstance c)
    {
        c.ChannellingCard = "";
        c.ChannelTurnsLeft = 0;
    }

    private void EnterAct(Act act, float duration)
    {
        _act = act;
        _actT = 0f;
        _actDur = Math.Max(0f, duration);
        if (act == Act.Hits)
            _hitTimer = _actingCard is { HitEvents.Count: > 0 } c ? c.HitEvents[0].Delay : 0f;
    }

    private void UpdateAction(float dt)
    {
        if (_act == Act.Hits) { UpdateHits(dt); return; }
        if (_act == Act.Mowing) { UpdateMower(dt); return; }
        if (_act == Act.Tripping) { UpdateTrip(dt); return; }

        _actT += dt;
        if (_actDur > 0 && _actT < _actDur) return;

        switch (_act)
        {
            // the machine is started once the casting is done, and then drives
            // itself: no projectile, no hit sequence, its own phase
            case Act.Casting when _actingCard is { IsMower: true }:
                StartMower();
                break;

            // the lights go out and the pictures start: also its own phase
            case Act.Casting when _actingCard is { IsBathSalts: true }:
                StartTrip();
                break;

            case Act.Casting when _actingCard is { Delivery: Delivery.Ranged } ranged:
                // a shot out of the sky needs no target on the ground and no
                // caster to leave from - it falls onto the square that was aimed at
                if (ranged.SkyAngle != 0f)
                {
                    _projTo = IsoMath.ToScreen(_skyTarget.X, _skyTarget.Y,
                        HeightAt(_skyTarget), Origin);
                    // walk back up the incoming line until the shot is off screen
                    float rad = MathHelper.ToRadians(ranged.SkyAngle);
                    var dir = new Vector2((float)Math.Cos(rad), (float)Math.Sin(rad));
                    _projFrom = _projTo - dir * SkyRunUp;
                    _projRotation = rad;
                    EnterAct(Act.Projectile, SkyRunUp / Math.Max(1f, ranged.Speed * IsoMath.TileW));
                    break;
                }

                var aim = _victims.FirstOrDefault();
                // a self-cast has nobody to fly at, but its effects still have
                // to resolve — skip the projectile, not the hit phase
                if (aim == null) { _hitIndex = 0; EnterAct(Act.Hits, 0f); return; }
                _projFrom = FootOf(_actor!) - new Vector2(0, 160);
                _projTo = FootOf(aim) - new Vector2(0, 160);
                _projRotation = (float)Math.Atan2(_projTo.Y - _projFrom.Y, _projTo.X - _projFrom.X);
                EnterAct(Act.Projectile,
                    IsoMath.GridDistance(Tile(_actor!), Tile(aim)) / Math.Max(1f, ranged.Speed));
                break;
            case Act.Casting when _actingCard is { Delivery: Delivery.Melee } melee:
                EnterAct(Act.MeleeWait, melee.MeleeTime);
                break;
            case Act.Casting:
            case Act.Projectile:
            case Act.MeleeWait:
                _hitIndex = 0;
                EnterAct(Act.Hits, 0f);
                break;
        }
    }

    private void UpdateHits(float dt)
    {
        _hitTimer -= dt;
        if (_hitTimer > 0f) return;
        var card = _actingCard!;
        var schedule = card.DamageSchedule();
        int dmg = _hitIndex < schedule.Length ? schedule[_hitIndex] : 0;
        _ctx.Sounds.Play(_hitIndex < card.HitEvents.Count ? card.HitEvents[_hitIndex].Sound : null);

        var report = new StringBuilder();
        var struck = _victims.Where(v => v.Alive).ToList();
        foreach (var v in struck)
        {
            // a card written as a range rolls separately for each target, so
            // one blast is not the same number to everybody under it
            int blow = card.VariableDamage
                ? RollDamage(card, v) / Math.Max(1, card.HitEvents.Count)
                : dmg;
            // a curse makes every melee blow land harder on its victim
            ApplyHit(v, blow + (card.Delivery == Delivery.Melee ? v.CurseBonus : 0),
                card.DamageType, report);
        }

        _hitIndex++;
        bool lastBlow = _hitIndex >= card.HitEvents.Count;
        if (lastBlow && card.Effects.Count > 0)
            ApplyEffects(card, struck, report);
        // the ground catches on the last blow, whether or not anyone was standing on it
        if (lastBlow && card.FireTileTurns > 0 && _burnArea.Count > 0)
        {
            LightFires(_burnArea, card.FireTileTurns, report);
            _burnArea.Clear();
        }
        if (report.Length > 0) Log(report.ToString().TrimEnd());

        if (!lastBlow)
        {
            _hitTimer = card.HitEvents[_hitIndex].Delay;
            return;
        }
        FinishAction();
    }

    private void FinishAction()
    {
        _actingCard = null;
        _victims.Clear();
        _overlayKey = null;
        if (PartyWiped) { FinishMission("party down"); _ctx.SwitchTo(new DeathScreen(_ctx)); return; }

        // a Steal held the thief's choice back until the card finished; make
        // them pick now, before the turn moves on. _actor stays set for it.
        if (_stealVictim != null) { BeginStealPick(); return; }

        _actor = null;
        ResumeAfterAction();
    }

    /// <summary>
    /// Back to the turn if anything is left to spend on it, else onward. An
    /// enemy comes back only when it could actually play another card — a
    /// Living Stone's Stone Slap costs five of its ten points, so it swings
    /// twice — because otherwise it would loop back only to stand there.
    /// </summary>
    private void ResumeAfterAction()
    {
        _actor = null;
        var mover = Current;
        if (mover is { Alive: true })
        {
            if (mover.IsPlayer &&
                ActingGroup().Any(c => c.ActionPoints > 0 || c.MovePoints > 0))
            {
                _mode = Mode.PlayerTurn;
                return;
            }
            if (!mover.IsPlayer && HasPlayableAttack(mover))
            {
                _mode = Mode.EnemyTurn;
                return;
            }
        }
        NextTurn();
    }

    /// <summary>Whether an enemy still holds an attack card it can pay for.</summary>
    private bool HasPlayableAttack(CharacterInstance e) =>
        HandOf(e).Any(c => !c.TargetsAllies && c.ActionCost <= e.ActionPoints);

    /// <summary>
    /// Armor is an extension of health: it soaks damage first and only what's
    /// left over reaches hit points, so 6 damage against 5 armor strips the
    /// armor and takes 1 off health.
    /// </summary>
    private void ApplyHit(CharacterInstance target, int dmg, string type, StringBuilder report)
    {
        if (dmg <= 0 || !target.Alive) return;
        target.ShakeTimer = Recoil.Duration;

        // Vulnerable pays out on the first blow to land and is then gone,
        // however many turns it had left. Armour is worked out afterwards, so
        // the bonus is soaked like any other damage rather than sneaking past.
        if (target.IsVulnerable)
        {
            int bonus = (int)Math.Round(dmg * Data.Effects.VulnerableBonus,
                MidpointRounding.AwayFromZero);
            dmg += bonus;
            target.VulnerableTurns = 0;
            report.AppendLine(_ctx.Strings.Format("iso_vulnerable_hit",
                ("target", target.Name), ("bonus", bonus.ToString())));
        }

        int soaked = Math.Min(target.Armor, dmg);
        target.Armor -= soaked;
        int through = dmg - soaked;
        target.Hp -= through;

        // the number that floats off them is what actually got through, plus
        // whatever the armour ate, since both came off the bar
        target.Popups.Add((through + soaked, type, PopupSeconds));

        report.AppendLine(soaked > 0
            ? _ctx.Strings.Format("iso_hit_armor", ("target", target.Name),
                ("dmg", through.ToString()), ("type", type), ("soaked", soaked.ToString()))
            : _ctx.Strings.Format("battle_hit", ("target", target.Name),
                ("dmg", through.ToString()), ("type", type)));

        Record(ReplayEventKind.Hit, _actor, target: target.Name, amount: through,
            note: type + (soaked > 0 ? $", {soaked} soaked" : ""));

        if (target.Hp <= 0)
        {
            target.Hp = 0;
            target.Alive = false;
            report.AppendLine(_ctx.Strings.Format("battle_down", ("name", target.Name)));
            Record(ReplayEventKind.Down, _actor, target: target.Name,
                to: Tile(target), note: _actingCard?.Name ?? "");
            // nobody is watching that ground any more
            StopGuarding(target);

            // a pet only acts on its summoner's turn, so one left behind would
            // never move again. It goes down with the hand that called it.
            foreach (var pet in _party.Where(p => p.Alive && p.Owner == target).ToList())
            {
                pet.Hp = 0;
                pet.Alive = false;
                report.AppendLine(_ctx.Strings.Format("battle_down", ("name", pet.Name)));
                Record(ReplayEventKind.Down, _actor, target: pet.Name, to: Tile(pet));
            }
        }
    }

    /// <summary>
    /// Puts a summoned creature on the board under the player's control. It
    /// joins the party rather than the enemy list, so everything that already
    /// knows about sides treats it correctly, but it never joins the turn
    /// ORDER — a pet acts inside its owner's turn.
    ///
    /// <paramref name="where"/> is the square the player aimed at. The first
    /// creature lands there; any others after it fill in around, and if that
    /// square has since been taken the nearest one that fits is used, so a
    /// summon never simply fails for want of an inch.
    /// </summary>
    private void SummonPet(CharacterInstance owner, Card card, int howMany, Point where,
        StringBuilder report)
    {
        var def = _ctx.Classes.Get(card.Summons);
        if (def is not { IsSummon: true })
        {
            _ctx.ReportProblem(CardLibrary.PlayerPath,
                $"'{card.Name}' summons '{card.Summons}', which is not a 'Summon:' block " +
                $"in {ClassLibrary.Path}");
            return;
        }

        if (SummonAlive(owner, card.Summons))
        {
            report.AppendLine(_ctx.Strings.Format("iso_summon_already", ("name", def.Name)));
            return;
        }

        for (int n = 0; n < Math.Max(1, howMany); n++)
        {
            var taken = OccupiedExcept(null);
            var spot = n == 0 && Pathfinder.Fits(_level, where, def.SizeX, def.SizeY, _revealed, taken)
                ? where
                : NearestFreeFor(where, def.SizeX, def.SizeY, taken);
            if (spot is not Point at)
            {
                report.AppendLine(_ctx.Strings.Format("iso_summon_no_room", ("name", def.Name)));
                return;
            }
            var pet = new CharacterInstance
            {
                Name = def.Name,
                OccurrenceIndex = _party.Count(p => p.Name.Equals(def.Name, StringComparison.OrdinalIgnoreCase)),
                IsPlayer = true,
                Owner = owner,
                SpriteFile = def.SpriteFiles[0],
                MaxHp = def.Hp, Hp = def.Hp,
                MoveMax = def.Movement, MovePoints = def.Movement,
                ActionsPerTurn = def.Actions,
                SizeX = def.SizeX, SizeY = def.SizeY,
                GX = at.X, GY = at.Y,
            };
            pet.RefreshActionPoints();
            _party.Add(pet);
            report.AppendLine(_ctx.Strings.Format("iso_summoned",
                ("owner", owner.Name), ("name", def.Name)));
        }
        _overlayKey = null;
    }

    /// <summary>
    /// Loads different shells: takes one card out of this character's hand and
    /// puts another in its place. Only their hand changes — the deck everyone
    /// reads from is untouched, so one Gun-O-Mancer swapping shells does not
    /// reach into another's pockets.
    ///
    /// Swapping back is just another Swap card pointing the other way, which is
    /// why there is no "unswap": Flaming Shells replaces Shock Shot with Hot
    /// Lead exactly as Lightning Shells did the reverse.
    /// </summary>
    private void SwapCard(CharacterInstance who, Card card, StringBuilder report)
    {
        if (card.Replaces.Length == 0 || card.With.Length == 0)
        {
            _ctx.ReportProblem(card.Source,
                $"'{card.Name}' swaps cards but is missing its 'Replaces:' or 'With:' line");
            return;
        }
        if (DeckOf(who).All.All(c => !c.Name.Equals(card.With, StringComparison.OrdinalIgnoreCase)))
        {
            _ctx.ReportProblem(card.Source,
                $"'{card.Name}' loads '{card.With}', which is not a card in {DeckOf(who).Source}");
            return;
        }

        // a swap already pointing at this card is replaced, not stacked, so
        // loading back and forth cannot leave a chain behind
        foreach (var stale in who.Swapped
                     .Where(kv => kv.Value.Equals(card.Replaces, StringComparison.OrdinalIgnoreCase))
                     .Select(kv => kv.Key).ToList())
            who.Swapped.Remove(stale);

        who.Swapped[card.Replaces] = card.With;
        _hand = HandOf(who);
        _overlayKey = null;
        report.AppendLine(_ctx.Strings.Format("iso_swapped",
            ("name", who.Name), ("old", card.Replaces), ("new", card.With)));
    }

    /// <summary>The closest square to a point where a body of this shape fits.</summary>
    private Point? NearestFreeFor(Point around, int sizeX, int sizeY, IReadOnlySet<Point> taken)
    {
        foreach (var t in _level.Blocks.Keys
                     .Where(t => Pathfinder.Fits(_level, t, sizeX, sizeY, _revealed, taken))
                     .OrderBy(t => IsoMath.GridDistance(t, around)))
            return t;
        return null;
    }

    /// <summary>Runs a card's Effects against everything it hit.</summary>
    private void ApplyEffects(Card card, IEnumerable<CharacterInstance> hit, StringBuilder report)
    {
        foreach (var effect in card.Effects)
        {
            if (Data.Effects.IsSelfCast(effect.Name))
            {
                if (_actor == null) continue;
                if (effect.Is(Data.Effects.Nimble))
                {
                    // Nimble hands movement back to the caster, not to the victims
                    _actor.MovePoints += effect.Amount;
                    report.AppendLine(_ctx.Strings.Format("iso_nimble",
                        ("name", _actor.Name), ("points", effect.Amount.ToString())));
                }
                else if (effect.Is(Data.Effects.Summon))
                {
                    SummonPet(_actor, card, effect.Amount, _aimPoint, report);
                }
                else if (effect.Is(Data.Effects.Guard))
                {
                    // Planting yourself costs the rest of your movement at
                    // once, and marks out the ground you are covering. The zone
                    // is worked out here and then left alone: it is a patch of
                    // dirt, not a bubble that follows anybody.
                    _actor.Watch.Cover(GuardZoneAround(Tile(_actor), card.GuardReach),
                        Math.Max(1, card.Hits), card.Damage);
                    _actor.MovePoints = 0;
                    // whoever is already standing in it does not get shot for
                    // having been there first; they are marked as inside so
                    // only stepping IN sets it off
                    foreach (var c in Everyone.Where(c => c.Alive && c != _actor &&
                                                         InGuardZone(_actor, c)))
                        _actor.Watch.AlreadyHere(Key(c));
                    report.AppendLine(_ctx.Strings.Format("iso_guarding",
                        ("name", _actor.Name), ("range", effect.Amount.ToString()),
                        ("shots", _actor.Watch.Shots.ToString()),
                        ("dmg", _actor.Watch.Damage.ToString())));
                }
                else if (effect.Is(Data.Effects.Swap))
                {
                    SwapCard(_actor, card, report);
                }
                else if (effect.Is(Data.Effects.Form))
                {
                    ChangeForm(_actor, effect.Text, report);
                }
                // Leap already did its work when the approach was planned, and
                // Channel is handled where the card is played rather than here
                continue;
            }
            foreach (var c in hit.Where(c => c.Alive))
            {
                if (effect.Is(Data.Effects.Burning))
                {
                    // each stack starts its own 2-turn life; existing ones are untouched
                    for (int i = 0; i < effect.Amount; i++)
                        c.Burns.Add(Data.Effects.BurnTurns);
                    report.AppendLine(_ctx.Strings.Format("iso_burning",
                        ("name", c.Name), ("stacks", c.BurningStacks.ToString())));
                }
                else if (effect.Is(Data.Effects.Armor))
                {
                    c.Armor += effect.Amount;
                    report.AppendLine(_ctx.Strings.Format("iso_armored",
                        ("name", c.Name), ("armor", c.Armor.ToString())));
                }
                else if (effect.Is(Data.Effects.Curse))
                {
                    // like burning, each curse keeps its own clock
                    c.Curses.Add((effect.Amount, Data.Effects.CurseTurns));
                    report.AppendLine(_ctx.Strings.Format("iso_cursed",
                        ("name", c.Name), ("bonus", c.CurseBonus.ToString())));
                }
                else if (effect.Is(Data.Effects.Stun))
                {
                    // the longer of the two rather than the sum: stunning
                    // somebody twice keeps them out until the later clock runs
                    c.StunTurns = Math.Max(c.StunTurns, effect.Amount);
                    report.AppendLine(_ctx.Strings.Format("iso_stunned",
                        ("name", c.Name), ("turns", c.StunTurns.ToString())));
                }
                else if (effect.Is(Data.Effects.Vulnerable))
                {
                    // marking somebody again just restarts the clock: there is
                    // one bullseye, and one blow spends it
                    c.VulnerableTurns = Math.Max(c.VulnerableTurns, effect.Amount);
                    report.AppendLine(_ctx.Strings.Format("iso_vulnerable",
                        ("name", c.Name), ("turns", c.VulnerableTurns.ToString())));
                }
                else if (effect.Is(Data.Effects.Steal))
                {
                    StealFrom(c, effect.Amount, report);
                }
            }
        }
    }

    /// <summary>
    /// Lifts one card off the victim — friend or foe — and hands it to the
    /// caster for the next few of their turns. The victim cannot play it while
    /// it is gone, which is how an enemy ends up with nothing to attack with.
    /// A card already stolen from somebody is not stolen again.
    /// </summary>
    private void StealFrom(CharacterInstance victim, int turns, StringBuilder report)
    {
        if (_actor == null || _actor == victim) return;
        var takeable = StealableFrom(victim, _actor);
        if (takeable.Count == 0)
        {
            report.AppendLine(_ctx.Strings.Format("iso_nothing_to_steal", ("name", victim.Name)));
            return;
        }
        // the thief chooses, so the pick waits until the card has finished
        // resolving and FinishAction can hand over to the picker
        _stealVictim = victim;
        _stealTurns = Math.Max(1, turns);
    }

    /// <summary>
    /// What can actually be lifted off somebody: cards that are genuinely
    /// theirs, so not ones they are themselves borrowing, and not the card
    /// being played to steal with.
    /// </summary>
    private List<Card> StealableFrom(CharacterInstance victim, CharacterInstance thief,
        string? asForm = null)
    {
        var borrowed = victim.Stolen
            .Select(st => st.CardName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var already = thief.Stolen
            .Select(st => st.CardName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // asForm looks into a shape the victim is NOT currently wearing, which
        // is how a stolen shapeshift card brings one of that shape's cards along
        var pool = asForm == null
            ? HandOf(victim)
            : DeckOf(victim).HandFor(
                victim.IsPlayer ? _ctx.Classes.CardTagsFor(victim.Name)
                                : _ctx.Enemies.CardTagsFor(victim.Name), asForm);

        return pool
            .Where(c => !borrowed.Contains(c.Name))
            .Where(c => !already.Contains(c.Name))
            .Where(c => c != _actingCard)
            .ToList();
    }

    /// <summary>Opens the picker, or closes the whole business if there is nothing to show.</summary>
    private void BeginStealPick(string? followUpForm = null)
    {
        if (_stealVictim == null || _actor == null) { EndStealPick(); return; }
        _stealOptions = StealableFrom(_stealVictim, _actor, followUpForm);
        _stealForm = followUpForm ?? "";
        if (_stealOptions.Count == 0) { EndStealPick(); return; }
        _mode = Mode.StealPick;
    }

    /// <summary>Takes the chosen card, then offers the shapeshift bonus if it earned one.</summary>
    private void TakeStolen(Card loot)
    {
        var victim = _stealVictim;
        var thief = _actor;
        if (victim == null || thief == null) { EndStealPick(); return; }

        var record = new StolenCard
        {
            CardName = loot.Name,
            From = victim,
            FromEnemyDeck = !victim.IsPlayer,
            TurnsLeft = _stealTurns,
        };
        thief.Stolen.Add(record);
        victim.Lost.Add(record);
        Log(_ctx.Strings.Format("iso_stole",
            ("thief", thief.Name), ("card", loot.Name), ("victim", victim.Name),
            ("turns", record.TurnsLeft.ToString())));

        // the one exception to one-card-per-steal: taking a shapeshift card
        // also lets you reach into the shape it would have turned them into
        if (_stealForm.Length == 0 && loot.BecomesForm is string shape)
        {
            BeginStealPick(shape);
            return;
        }
        EndStealPick();
    }

    private void EndStealPick()
    {
        _stealVictim = null;
        _stealOptions = new List<Card>();
        _stealForm = "";
        _hand = Current != null ? HandOf(Current) : _hand;
        ResumeAfterAction();
    }

    /// <summary>Picker clicks: one card, or right-click / Escape to take nothing.</summary>
    private void UpdateStealPick(InputState input)
    {
        if (input.AltTap.HasValue || input.Cancel) { EndStealPick(); return; }
        if (_tap is not Point press) return;
        _tap = null;
        var rects = StealRects();
        for (int i = 0; i < _stealOptions.Count && i < rects.Count; i++)
            if (rects[i].Contains(press))
            {
                TakeStolen(_stealOptions[i]);
                return;
            }
    }

    private List<Rectangle> StealRects()
    {
        int n = Math.Max(1, _stealOptions.Count);
        int total = n * (CardW + CardGap) - CardGap;
        // narrow the cards rather than run off the screen when a hand is large
        int w = CardW, gap = CardGap;
        if (total > VirtualViewport.Width - 200)
        {
            w = (VirtualViewport.Width - 200 - (n - 1) * gap) / n;
            total = n * (w + gap) - gap;
        }
        int x0 = (VirtualViewport.Width - total) / 2;
        int h = (int)(CardH * (w / (float)CardW));
        var rects = new List<Rectangle>();
        for (int i = 0; i < n; i++)
            rects.Add(new Rectangle(x0 + i * (w + gap), (VirtualViewport.Height - h) / 2, w, h));
        return rects;
    }

    private void DrawStealPick(SpriteBatch batch)
    {
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(0, 0, VirtualViewport.Width, VirtualViewport.Height), Color.Black * 0.72f);
        Ui.DrawTextCentered(batch, _ctx.Font,
            _stealForm.Length > 0
                ? _ctx.Strings.Format("iso_steal_pick_form", ("form", _stealForm))
                : _ctx.Strings.Format("iso_steal_pick", ("name", _stealVictim?.Name ?? "?")),
            new Rectangle(0, 320, VirtualViewport.Width, 120), Color.Gold, 0.56f);

        var rects = StealRects();
        for (int i = 0; i < _stealOptions.Count && i < rects.Count; i++)
            DrawCard(batch, _stealOptions[i], rects[i], rects[i].Contains(_pointer));
    }

    /// <summary>Swaps a shapeshifter's shape, and with it the cards in its hand.</summary>
    private void ChangeForm(CharacterInstance who, string form, StringBuilder report)
    {
        var cls = _ctx.Classes.Get(who.Name);
        if (cls?.FindForm(form) is not ClassForm target)
        {
            _ctx.ReportProblem(ClassLibrary.Path,
                $"'{who.Name}' has no form called '{form}', so the card could not change shape");
            return;
        }
        who.Form = target.Name;
        who.SpriteFile = target.Sprite;
        if (who == Current)
            _hand = HandOf(who);
        report.AppendLine(_ctx.Strings.Format("iso_form", ("name", who.Name), ("form", target.Name)));
    }
    /// <summary>
    /// Burning bites at the victim's own turn start: every live stack deals its
    /// damage, then each stack ages independently and the spent ones go out.
    /// Returns false if the fire killed them.
    /// </summary>
    private bool BurnAtTurnStart(CharacterInstance c)
    {
        // burning ground deals no damage itself — it sets you alight, and the
        // stacks it gives do the rest on your own clock
        Ignite(c);

        // curses tick down on their victim's turn too, independently of each other
        if (c.Curses.Count > 0)
        {
            for (int i = 0; i < c.Curses.Count; i++)
                c.Curses[i] = (c.Curses[i].Amount, c.Curses[i].Turns - 1);
            c.Curses.RemoveAll(x => x.Turns <= 0);
        }
        // a bullseye left unshot goes stale on the same clock
        if (c.VulnerableTurns > 0) c.VulnerableTurns--;
        // borrowed cards run on the THIEF's clock: the turn they were taken on
        // counts as the first, so Steal 3 is "now, or either of your next two"
        for (int i = c.Stolen.Count - 1; i >= 0; i--)
        {
            var loot = c.Stolen[i];
            if (--loot.TurnsLeft > 0) continue;
            ReturnStolen(loot, c);
            Log(_ctx.Strings.Format("iso_steal_over",
                ("card", loot.CardName), ("owner", loot.From?.Name ?? "?")));
        }
        if (c.Burns.Count == 0) return true;
        var report = new StringBuilder();
        ApplyHit(c, c.Burns.Count * Data.Effects.BurnDamagePerStack, "Fire", report);

        int before = c.Burns.Count;
        for (int i = 0; i < c.Burns.Count; i++) c.Burns[i]--;
        c.Burns.RemoveAll(turns => turns <= 0);
        if (c.Burns.Count < before)
            report.AppendLine(_ctx.Strings.Format("iso_burn_out",
                ("name", c.Name), ("gone", (before - c.Burns.Count).ToString()),
                ("left", c.Burns.Count.ToString())));

        Log(report.ToString().TrimEnd());
        return c.Alive;
    }

    /// <summary>
    /// Burning ground catching someone. It happens three ways — starting a turn
    /// standing in fire, walking through it, and ending a turn in it — so a
    /// character who crosses one square and stops there leaves with more stacks
    /// than one who only passes over it. The fire itself does no damage; the
    /// stacks it hands out do, at the victim's own turn start.
    /// </summary>
    private void Ignite(CharacterInstance c)
    {
        if (!c.Alive || !Occupied(c).Any(_fires.ContainsKey)) return;
        for (int i = 0; i < Data.Effects.FireTileStacks; i++)
            c.Burns.Add(Data.Effects.BurnTurns);
        Log(_ctx.Strings.Format("iso_fire_caught",
            ("name", c.Name), ("stacks", c.BurningStacks.ToString())));
    }

    /// <summary>
    /// What an enemy does with its turn, driven entirely by its cards in
    /// EnemyCards.txt:
    ///   1. A melee card it can actually land this turn wins — it walks at the
    ///      nearest player it can reach and swings.
    ///   2. Otherwise a ranged card: it closes only as far as it must to bring
    ///      the nearest player inside that card's range, and no further.
    ///   3. With no usable attack card — none authored, or the Dirtbag has
    ///      lifted the only one — it cannot attack at all, so it wanders to a
    ///      random square it can reach.
    /// </summary>
    private void EnemyAct()
    {
        var me = Current!;
        // a pet is a legitimate target even when it is the last one standing,
        // so enemies aim at the whole living party — but the mission is lost
        // once the real members are gone
        var players = LivingParty;
        if (PartyWiped) { FinishMission("party down"); _ctx.SwitchTo(new DeathScreen(_ctx)); return; }

        // an enemy is bound by action points exactly as the party is
        var hand = HandOf(me)
            .Where(c => !c.TargetsAllies && c.ActionCost <= me.ActionPoints).ToList();
        var reach = Pathfinder.Reachable(_level, Tile(me), me.MovePoints, _revealed,
            OccupiedExcept(me), sizeX: me.SizeX, sizeY: me.SizeY).Cost;
        var stands = reach.Keys.Append(Tile(me)).ToList();

        // how far a target would be if this enemy's body were anchored on a
        // given square — the whole body counts, not just its corner
        int GapFrom(Point square, CharacterInstance target) =>
            Pathfinder.Footprint(square, me.SizeX, me.SizeY).Min(t => IsoMath.GridDistance(t, Tile(target)));

        // longest reach first within each kind, so a spear beats a fist
        foreach (var card in hand.Where(c => c.Delivery == Delivery.Melee)
                                 .OrderByDescending(c => c.Range)
                                 .Concat(hand.Where(c => c.Delivery != Delivery.Melee)
                                             .OrderByDescending(c => c.Range)))
        {
            // the cheapest square that puts somebody in this card's range
            var shot = players
                .SelectMany(p => stands.Select(sq => (Square: sq, Target: p)))
                .Where(x => GapFrom(x.Square, x.Target) <= card.Range)
                .OrderBy(x => reach.TryGetValue(x.Square, out int c) ? c : 0)
                .ThenBy(x => GapFrom(x.Square, x.Target))
                .Select(x => ((Point, CharacterInstance)?)(x.Square, x.Target))
                .FirstOrDefault();
            if (shot == null) continue;

            var (square, victim) = shot.Value;
            if (square == Tile(me)) { EnemyPlay(me, card, victim); return; }
            me.MovePoints -= reach.TryGetValue(square, out int cost) ? cost : 0;
            var goal = square;
            BeginWalk(me, goal, () => EnemyPlay(me, card, victim));
            return;
        }

        // holding a weapon but out of reach of everyone: close the distance
        // and try again next turn
        if (hand.Count > 0)
        {
            var near = players.OrderBy(p => me.DistanceTo(p)).First();
            int wanted = hand.Max(c => c.Range);
            var goal = Pathfinder.StepToward(_level, Tile(me), Tile(near), me.MovePoints,
                wanted, _revealed, OccupiedExcept(me), out var path, me.SizeX, me.SizeY);
            me.MovePoints = 0;
            if (goal != null && path.Count > 0)
            {
                _walker = me;
                _walkFrom = Tile(me);
                _walkPath = path;
                _walkT = 0f;
                _walkPause = 0f;
                _afterWalk = NextTurn;
                return;
            }
            NextTurn();
            return;
        }

        EnemyWander(me, reach);
    }

    /// <summary>
    /// Nothing to attack with at all — no cards authored, or the Dirtbag is
    /// holding the only one. It cannot fight, so it picks a square inside its
    /// movement range at random and ambles there.
    /// </summary>
    private void EnemyWander(CharacterInstance me, Dictionary<Point, int> reach)
    {
        Log(_ctx.Strings.Format("iso_no_cards", ("name", me.Name)));
        me.MovePoints = 0;
        if (reach.Count == 0) { NextTurn(); return; }
        var where = reach.Keys.ElementAt(Rng.Next(reach.Count));
        BeginWalk(me, where, NextTurn);
    }

    /// <summary>
    /// Enemies fire cards through exactly the same pipeline the party uses, so
    /// hit sequences, projectiles, sounds and effects all behave identically.
    /// </summary>
    private void EnemyPlay(CharacterInstance me, Card card, CharacterInstance victim)
    {
        if (!victim.Alive || me.DistanceTo(victim) > card.Range)
        {
            NextTurn();
            return;
        }
        _selectedCard = card;

        // An area card goes off over the ground its target is standing on and
        // catches everyone the card is allowed to catch — the caster's own side
        // included when Friendly Fire says so.
        //
        // Enemies used to hand PlayCard the single body they had aimed at,
        // whatever the card was, which quietly turned every enemy blast and
        // every enemy cone into a one-target jab and left their Friendly Fire
        // line doing nothing at all.
        if (card.TargetsGround)
        {
            var aim = Tile(victim);
            PlayArea(AreaOf(card, Tile(me), aim), aim);
            return;
        }
        PlayCard(new List<CharacterInstance> { victim }, Tile(victim));
    }

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
    private Texture2D ArtFor(CharacterInstance c) =>
        _sprites.For(c)?.Rotation(c.Facing.Nearest()) ?? _sprites.Cube(c);

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
        if (!_framed) { CentreOnFocus(); _framed = true; }

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

        foreach (var block in _level.Blocks.Values
                     .Where(b => _level.Shown(new Point(b.X, b.Y), _revealed))
                     .OrderBy(b => b.X + b.Y).ThenBy(b => b.X))
        {
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

        // The cast goes down AFTER all of the ground, back to front.
        //
        // It cannot be interleaved with the tiles. A character is hung by their
        // feet on the middle of a square, and the square in FRONT of them draws
        // a diamond whose back half rises to that same middle — so any tile
        // ahead of them, however flat, paints over their legs. Ground first
        // fixes that for every sprite at once.
        //
        // The cost is that a raised block no longer hides somebody standing
        // behind it. That is worth paying: the old way clipped everybody all
        // the time, and this only shows on ground with height.
        foreach (var c in Everyone.Where(c => c.Alive)
                     .Where(c => _level.Shown(Tile(c), _revealed))
                     .OrderBy(c => Tile(c).X + c.SizeX - 1 + Tile(c).Y + c.SizeY - 1)
                     .ThenBy(c => Tile(c).X))
            DrawCharacter(batch, c, alpha);

        DrawProjectile(batch);
        DrawMower(batch);
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

        // While a cast is running its sheet stands in for the sprite, centred on
        // the same point, so nothing jumps when it starts or stops.
        if (c.CastAnim is SpriteAnimation anim)
            batch.Draw(anim.Sheet, anim.RectFor(rect),
                anim.SourceRect(anim.FrameAt(c.CastAnimTime)), Color.White * alpha);
        else
            batch.Draw(art, rect, Color.White * alpha);

        // A placeholder cube has no front, so the yellow triangle is the only
        // thing saying which way it is turned. It goes on AFTER the cube: the
        // cube's base sits on the middle of the square, which is exactly where
        // the triangle is, so drawing it underneath hid it completely.
        if (_sprites.For(c) == null) DrawFacingMark(batch, c, alpha);

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
        _mode is Mode.Explore or Mode.FreeMove
            ? _picked.Where(p => p.Alive)
            : Chosen is CharacterInstance one ? new[] { one } : Enumerable.Empty<CharacterInstance>();

    private bool IsSelected(CharacterInstance c) => _mode is Mode.Explore or Mode.FreeMove
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

    private void DrawDialogue(SpriteBatch batch)
    {
        var line = _lines![Math.Min(_lineIndex, _lines.Count - 1)];
        Ui.FillRect(batch, _ctx.Pixel, DialogueBox, new Color(0, 0, 0, 225));

        var speaker = Everyone.FirstOrDefault(c =>
            c.Name.Equals(line.Speaker, StringComparison.OrdinalIgnoreCase));
        var thumbRect = new Rectangle(DialogueBox.X + 36, DialogueBox.Y + 34, 350, 350);
        if (speaker != null)
        {
            var thumb = _ctx.Assets.LoadFirstAvailable(speaker.ThumbPath, speaker.SpritePath);
            batch.Draw(thumb, Ui.FitCentered(AssetLoader.DisplaySize(thumb, AssetKind.Thumb), thumbRect),
                Color.White);
        }
        batch.DrawString(_ctx.Font, line.Speaker,
            new Vector2(DialogueBox.X + 430, DialogueBox.Y + 36), Color.Gold,
            0f, Vector2.Zero, 0.5f, SpriteEffects.None, 0f);
        batch.DrawString(_ctx.Font, Ui.Wrap(_ctx.Font, line.Text, DialogueBox.Width - 520, 0.46f),
            new Vector2(DialogueBox.X + 430, DialogueBox.Y + 140), Color.White,
            0f, Vector2.Zero, 0.46f, SpriteEffects.None, 0f);
        Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("iso_dialogue_next"),
            new Rectangle(DialogueBox.X, DialogueBox.Bottom - 70, DialogueBox.Width - 40, 50),
            Color.White * 0.55f, 0.3f);
    }

    private void DrawHud(SpriteBatch batch)
    {
        // a dark plate behind the toast, because white text alone disappears
        // over pale ground and this is the game's only way of answering back
        if (_toastTimer > 0 && !DialogueActive)
        {
            var size = _ctx.Font.MeasureString(_toast) * 0.42f;
            var plate = new Rectangle(64, 244, (int)size.X + 32, (int)size.Y + 24);
            Ui.FillRect(batch, _ctx.Pixel, plate, new Color(12, 12, 20, 210));
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(plate.X, plate.Y, 6, plate.Height), Color.Gold);
            batch.DrawString(_ctx.Font, _toast, new Vector2(80, 260), Color.White,
                0f, Vector2.Zero, 0.42f, SpriteEffects.None, 0f);
        }

        // Save Replay is up from the moment the level loads, not only at the
        // end: the interesting part of a mission is often over well before the
        // mission is, and a fight you want to keep is one you want to keep now.
        //
        // The button reports its own result for a few seconds. It used to say
        // nothing at all and leave the news to the toast, which prints small and
        // plain in the opposite corner of the screen — technically feedback,
        // practically invisible.
        if (ReplayButtonUp)
        {
            // Three things this button can be saying: it has just written a
            // file, it is writing things down, or it is doing nothing.
            bool justSaved = _replaySavedTimer > 0;
            string label = justSaved ? "replay_done"
                : _recording ? "replay_stop" : "replay_start";
            Color? tint = justSaved ? new Color(24, 86, 34, 235)
                : _recording ? new Color(110, 30, 30, 235) : null;

            // the press is handled in HitButton; this only paints it
            Ui.Button(batch, _ctx.Pixel, _ctx.Font, SaveReplayRect,
                _ctx.Strings.Get(label), null, tint);

            // a dot beside it while it is running, so a recording left on is
            // obvious without reading the button
            if (_recording && !justSaved)
                Ui.FillRect(batch, _ctx.Pixel,
                    new Rectangle(SaveReplayRect.X - 46, SaveReplayRect.Y + 44, 32, 32),
                    Color.Red);
        }

        if (_replayMode) { DrawReplayHud(batch); return; }

        switch (_mode)
        {
            case Mode.Explore:
                Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("iso_explore_hint"),
                    new Rectangle(0, 40, VirtualViewport.Width, 90), Color.White * 0.7f, 0.34f);
                break;
            case Mode.FreeMove:
                Ui.DrawTextCentered(batch, _ctx.Font,
                    _ctx.Strings.Format("iso_spotted",
                        ("name", _freeMovers.FirstOrDefault()?.Name ?? "")),
                    new Rectangle(0, 40, VirtualViewport.Width, 90), Color.OrangeRed, 0.42f);
                if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, DoneRect, _ctx.Strings.Get("iso_done"), _tap))
                { _freeMovers.Clear(); StartCombat(); }
                break;
            case Mode.PlayerTurn:
            case Mode.PlayerTarget:
                DrawTurnStrip(batch);
                if (Acting is CharacterInstance me)
                {
                    Ui.DrawTextCentered(batch, _ctx.Font,
                        me.MovePoints <= 0
                            ? _ctx.Strings.Get("iso_move_spent")
                            : _ctx.Strings.Format("iso_move_left", ("points", me.MovePoints.ToString())),
                        new Rectangle(0, 300, VirtualViewport.Width, 80),
                        me.MovePoints <= 0 ? Color.Gray : Color.LightBlue, 0.36f);
                    DrawActionPoints(batch, me);
                }
                if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, EndTurnRect, _ctx.Strings.Get("iso_end_turn"), _tap))
                    NextTurn();
                if (_mode == Mode.PlayerTarget)
                {
                    int wanted = _selectedCard == null ? 1 : TargetsWanted(_selectedCard);
                    string prompt = _targets.Count == 0
                        ? _ctx.Strings.Get("iso_pick_target")
                        : _ctx.Strings.Format("iso_pick_more",
                            ("count", Math.Max(1, wanted - _targets.Count).ToString()));
                    Ui.DrawTextCentered(batch, _ctx.Font, prompt,
                        new Rectangle(0, 1330, VirtualViewport.Width, 100), Color.OrangeRed, 0.44f);
                }
                DrawHand(batch);
                break;
            case Mode.EnemyTurn:
            case Mode.Acting:
                DrawTurnStrip(batch);
                break;
            case Mode.StealPick:
                DrawTurnStrip(batch);
                DrawStealPick(batch);
                break;
            case Mode.Victory:
                Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("iso_victory"),
                    new Rectangle(0, 900, VirtualViewport.Width, 250), Color.Gold, 1.0f);
                if (Ui.Button(batch, _ctx.Pixel, _ctx.Font, WinRect, _ctx.Strings.Get("battle_win"), _tap))
                {
                    _ctx.State.CompletedMissions.Add(_level.Name);
                    _ctx.State.EndMission(completed: false);
                    _ctx.SwitchTo(new MapScreen(_ctx));
                }
                break;
        }
    }

    /// <summary>How many lines the panel shows at once.</summary>
    private static int LogLines => LogPanel.Height / LogLineH - 1;

    /// <summary>
    /// The + button, and the log panel behind it. Collapsed by default so the
    /// level is clean; open, it shows the most recent entries with the newest
    /// at the bottom, and the wheel scrolls back through the rest.
    /// </summary>
    private void DrawLog(SpriteBatch batch)
    {
        if (_logOpen)
        {
            Ui.FillRect(batch, _ctx.Pixel, LogPanel, new Color(0, 0, 0, 205));
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(LogPanel.X, LogPanel.Y, LogPanel.Width, 3), Color.White * 0.3f);

            int shown = Math.Min(LogLines, _log.Count);
            int end = _log.Count - _logScroll;          // exclusive
            int start = Math.Max(0, end - shown);
            for (int i = start; i < end; i++)
            {
                // the newest entries read brightest, older ones fade back
                float age = (end - 1 - i) / (float)Math.Max(1, shown);
                batch.DrawString(_ctx.Font, Ui.Wrap(_ctx.Font, _log[i], LogPanel.Width - 56, LogTextScale),
                    new Vector2(LogPanel.X + 28, LogPanel.Y + 26 + (i - start) * LogLineH),
                    Color.White * (1f - age * 0.45f),
                    0f, Vector2.Zero, LogTextScale, SpriteEffects.None, 0f);
            }
            if (_log.Count == 0)
                Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("iso_log_empty"),
                    LogPanel, Color.White * 0.4f, LogTextScale);
            if (_logScroll > 0)
                Ui.DrawTextCentered(batch, _ctx.Font, _ctx.Strings.Get("iso_log_more"),
                    new Rectangle(LogPanel.X, LogPanel.Bottom - 46, LogPanel.Width, 40),
                    Color.Gold * 0.8f, 0.26f);
        }

        Ui.FillRect(batch, _ctx.Pixel, LogToggleRect, new Color(20, 20, 28, 235));
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(LogToggleRect.X, LogToggleRect.Y, LogToggleRect.Width, 3), Color.White * 0.5f);
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(LogToggleRect.X, LogToggleRect.Bottom - 3, LogToggleRect.Width, 3), Color.White * 0.5f);
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(LogToggleRect.X, LogToggleRect.Y, 3, LogToggleRect.Height), Color.White * 0.5f);
        Ui.FillRect(batch, _ctx.Pixel,
            new Rectangle(LogToggleRect.Right - 3, LogToggleRect.Y, 3, LogToggleRect.Height), Color.White * 0.5f);

        // a drawn +/- rather than a glyph, so it stays centred at any font size
        var c = LogToggleRect.Center;
        var ink = _logOpen ? Color.Gold : Color.White;
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(c.X - 26, c.Y - 4, 52, 8), ink);
        if (!_logOpen)
            Ui.FillRect(batch, _ctx.Pixel, new Rectangle(c.X - 4, c.Y - 26, 8, 52), ink);
    }

    /// <summary>
    /// Turn order as a row of faces rather than a row of names. Whoever is
    /// acting sits at the far left, so the strip shuffles along by one every
    /// turn and "next up" is always the face beside the big one.
    /// </summary>
    private void DrawTurnStrip(SpriteBatch batch)
    {
        if (_order.Count == 0) return;

        // rotate the running order so the current actor leads it, skipping
        // anyone who has died and won't be taking a turn
        var upcoming = new List<CharacterInstance>();
        for (int step = 0; step < _order.Count; step++)
        {
            var who = _order[(_turn + step + _order.Count) % _order.Count];
            if (who.Alive) upcoming.Add(who);
        }
        if (upcoming.Count == 0) return;

        int x = TurnStrip.X;
        for (int i = 0; i < upcoming.Count && x < TurnStrip.Right; i++)
        {
            var who = upcoming[i];
            bool active = i == 0;
            int size = active ? TurnFaceActive : TurnFace;
            var slot = new Rectangle(x, TurnStrip.Y + (TurnFaceActive - size) / 2, size, size);

            // the acting character gets a gold frame; the rest sit dimmer and smaller
            var frame = active ? Color.Gold : Color.White * 0.4f;
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(slot.X - 4, slot.Y - 4, slot.Width + 8, slot.Height + 8), frame);
            Ui.FillRect(batch, _ctx.Pixel, slot, new Color(16, 16, 22));

            var face = _ctx.Assets.LoadFirstAvailable(who.ThumbPath, who.SpritePath);
            var fit = Ui.FitCentered(new Vector2(face.Width, face.Height),
                new Rectangle(slot.X + 4, slot.Y + 4, slot.Width - 8, slot.Height - 8));
            batch.Draw(face, fit, active ? Color.White : Color.White * 0.65f);

            // a thin bar under each face says which side it is on
            Ui.FillRect(batch, _ctx.Pixel,
                new Rectangle(slot.X, slot.Bottom - 10, slot.Width, 10),
                who.IsPlayer ? new Color(70, 190, 70) : new Color(200, 60, 60));

            x += size + TurnFaceGap;
        }

        if (Current != null)
            batch.DrawString(_ctx.Font,
                _petControl is CharacterInstance pet && pet.Alive
                    ? _ctx.Strings.Format("iso_pet_turn",
                        ("owner", Current.Name), ("name", pet.Name))
                    : Current.Name,
                new Vector2(TurnStrip.X, TurnStrip.Bottom + 12), Color.Gold,
                0f, Vector2.Zero, 0.38f, SpriteEffects.None, 0f);
    }

    /// <summary>
    /// Action points as a row of pips: filled for what's left, hollow for what
    /// has been spent this turn. A third pip appears only when a point was
    /// carried over, which is the whole tell that rollover happened.
    /// </summary>
    /// <summary>
    /// What the current character has left to spend, written out. Ten hollow
    /// squares in a row were both wider than the screen wanted and slower to
    /// read than the number they added up to.
    /// </summary>
    private void DrawActionPoints(SpriteBatch batch, CharacterInstance who)
    {
        var label = _ctx.Strings.Format("iso_actions_left",
            ("points", who.ActionPoints.ToString()));
        Ui.DrawTextCentered(batch, _ctx.Font, label,
            new Rectangle(0, 232, VirtualViewport.Width, 60),
            who.ActionPoints > 0 ? Color.Orange : Color.Gray, 0.44f);
    }

    private void DrawHand(SpriteBatch batch)
    {
        var rects = HandRects();
        int hovered = -1;
        for (int i = 0; i < _hand.Count; i++)
            if (rects[i].Contains(_pointer)) hovered = i;

        for (int i = 0; i < _hand.Count; i++)
            if (i != hovered)
                DrawCard(batch, _hand[i], rects[i], false);
        if (hovered >= 0)
        {
            int w = (int)(CardW * HoverScale), h = (int)(CardH * HoverScale);
            DrawCard(batch, _hand[hovered], new Rectangle(
                rects[hovered].Center.X - w / 2, VirtualViewport.Height - h - 30, w, h), true);
        }

        // The key that plays each card, over its top edge. Drawn after the
        // cards so a raised hovered card cannot cover its own number.
        for (int i = 0; i < _hand.Count && i < HandKeys.Length; i++)
            Ui.DrawTextCentered(batch, _ctx.Font, HandKeys[i].ToString(),
                new Rectangle(rects[i].X, rects[i].Y - 62, rects[i].Width, 56),
                Acting is CharacterInstance who && who.ActionPoints >= _hand[i].ActionCost
                    ? Color.White : Color.White * 0.35f,
                0.42f);
    }

    /// <summary>
    /// Which key plays which card: 1 to 9, then 0 for the tenth. A hand longer
    /// than ten has no key for the rest, which is what the length check is for.
    /// </summary>
    private static readonly char[] HandKeys = { '1', '2', '3', '4', '5', '6', '7', '8', '9', '0' };

    private void DrawCard(SpriteBatch batch, Card card, Rectangle rect, bool hovered)
    {
        // Text scales with the card it is on, taken from the rect rather than
        // from `hovered`. A hovered card is CardW * HoverScale wide and so gets
        // HoverScale, exactly as before — but the steal picker narrows its
        // cards to fit a big hand on screen, and text that stayed full size on
        // a card half the width was what ran the labels into each other.
        float s = rect.Width / (float)CardW;
        // a card the holder cannot currently afford is greyed out card by card,
        // so with 1 point left the cheap ones stay lit and the dear ones dim
        bool spent = Acting == null || Acting.ActionPoints < card.ActionCost;
        Ui.FillRect(batch, _ctx.Pixel, rect,
            spent ? new Color(20, 20, 26, 250) : new Color(24, 24, 40, 250));
        var border = card == _selectedCard ? Color.Gold
            : spent ? Color.White * 0.25f
            : hovered ? Color.White : Color.White * 0.5f;
        int bw = (int)(6 * s);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Y, rect.Width, bw), border);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Bottom - bw, rect.Width, bw), border);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.X, rect.Y, bw, rect.Height), border);
        Ui.FillRect(batch, _ctx.Pixel, new Rectangle(rect.Right - bw, rect.Y, bw, rect.Height), border);

        var ink = spent ? Color.White * 0.4f : Color.White;
        int pad = (int)(24 * s);
        var inner = new Rectangle(rect.X + pad, rect.Y, rect.Width - pad * 2, rect.Height);

        // The name wraps and reports how tall it came out, so a long one like
        // "Beg, Borrow, but Mostly Steal" takes two lines instead of running
        // off both sides, and pushes the text under it down rather than
        // being drawn through it.
        // The name starts below the cost line rather than beside it. At the old
        // 18px both were drawn across the top of the card and "Pew Pew" ran
        // straight through "Actions 5".
        float nameTop = rect.Y + 74 * s;
        float nameHeight = Ui.DrawWrappedCentered(batch, _ctx.Font, card.Name,
            new Rectangle(inner.X, (int)nameTop, inner.Width, 0), ink, Pt(CardNamePt) * s);

        batch.DrawString(_ctx.Font,
            Ui.Wrap(_ctx.Font, card.CardText, inner.Width, Pt(CardBodyPt) * s),
            new Vector2(inner.X, nameTop + nameHeight + 22 * s), ink * 0.9f,
            0f, Vector2.Zero, Pt(CardBodyPt) * s, SpriteEffects.None, 0f);

        int hitCount = card.TargetsGround && _blastSet.Count > 0
            ? VisibleEnemies.Count(e => e.Footprint.Any(_blastSet.Contains))
            : VisibleEnemies.Count;
        // A card that rolls its damage shows the range it rolls in. Printing
        // only the top of it would read as a promise the card does not make.
        string total = card.VariableDamage
            ? $"{card.DamageMin}-{card.Damage} {card.DamageType}"
            : card.Damage <= 0 && card.Effects.Count > 0
            ? $"+{card.Effects[0].Amount} {card.Effects[0].Name}"
            : $"{card.TotalDamage(hitCount)} {card.DamageType}";
        string range = _ctx.Strings.Format("iso_card_range", ("range", card.Range.ToString()));

        // The bottom row is two labels facing each other, so each is given half
        // of the width and shrunk to fit it. Neither can reach the other however
        // long the words are — "Range 4" and "+3 Stolen" used to meet in the
        // middle of a narrow card and print over one another.
        int half = (inner.Width - (int)(16 * s)) / 2;
        float rangeScale = Ui.FitScale(_ctx.Font, range, half, Pt(CardRangePt) * s);
        float totalScale = Ui.FitScale(_ctx.Font, total, half, Pt(CardTotalPt) * s);
        float baseline = rect.Bottom - 28 * s;

        var totalSize = _ctx.Font.MeasureString(total) * totalScale;
        batch.DrawString(_ctx.Font, total,
            new Vector2(inner.Right - totalSize.X, baseline - totalSize.Y),
            spent ? Color.Gold * 0.4f : Color.Gold, 0f, Vector2.Zero, totalScale,
            SpriteEffects.None, 0f);

        var rangeSize = _ctx.Font.MeasureString(range) * rangeScale;
        batch.DrawString(_ctx.Font, range,
            new Vector2(inner.X, baseline - rangeSize.Y),
            spent ? Color.LightBlue * 0.4f : Color.LightBlue, 0f, Vector2.Zero, rangeScale,
            SpriteEffects.None, 0f);

        // The cost, top-right. It used to be one orange square per point, which
        // at ten points a turn ran off the side of the card and had to be
        // counted anyway. The number says it in less room and at a glance.
        string cost = _ctx.Strings.Format("iso_card_actions",
            ("points", card.ActionCost.ToString()));
        float costScale = Ui.FitScale(_ctx.Font, cost, inner.Width, Pt(CardRangePt) * s);
        var costSize = _ctx.Font.MeasureString(cost) * costScale;
        batch.DrawString(_ctx.Font, cost,
            new Vector2(inner.Right - costSize.X, rect.Y + 20 * s),
            spent ? Color.Orange * 0.4f : Color.Orange, 0f, Vector2.Zero, costScale,
            SpriteEffects.None, 0f);
    }
}
