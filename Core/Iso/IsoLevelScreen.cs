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
        Explore, PlayerTurn, PlayerTarget, StealPick, EnemyTurn, Acting, Victory,
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
                MaxHp = def.Hp,
                Hp = def.Hp,
                MoveMax = def.Movement,
                ActionsPerTurn = def.Actions,
                SizeX = def.SizeX, SizeY = def.SizeY,
                GX = spawn.X, GY = spawn.Y,
            });
        }
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
        // which in this projection is straight down-screen for a square body
        // and offset sideways for a long one
        at.X += (c.SizeX - c.SizeY) * (IsoMath.TileW / 2f) / 2f;
        at.Y += (c.SizeX + c.SizeY - 2) * (IsoMath.TileH / 2f) / 2f;
        // this IS the middle of the square: the picture's lowest solid pixel
        // lands here, so a character stands exactly where the highlight says
        return at + Recoil.Offset(c);
    }
}
