using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// One borrowed card: who lost it, and how many of the thief's turns are left
/// before it goes home. The same object sits in the thief's Stolen list and the
/// victim's Lost list, so returning it is one removal from each.
/// </summary>
public class StolenCard
{
    public string CardName = "";
    public CharacterInstance? From;
    /// <summary>True when it came out of EnemyCards.txt rather than PlayerCards.txt.</summary>
    public bool FromEnemyDeck;
    public int TurnsLeft;
}

/// <summary>
/// One concrete character in a level: a party member or a spawned enemy. It
/// holds everything that changes while the level is played — health, grid
/// square, points, status effects, borrowed cards — none of which outlives it.
/// </summary>
public class CharacterInstance
{
    public string Name = "";
    public int OccurrenceIndex;      // 0 = first Goblin in a cast line, 1 = second...
    public string SpriteFile = "";   // e.g. "Goblin2.png"
    public bool IsPlayer;
    public bool Alive = true;

    public int MaxHp = 20;
    public int Hp = 20;

    // --- transient render state: never saved ---

    /// <summary>Counts down while the sprite is recoiling from a hit.</summary>
    public float ShakeTimer;

    // --- isometric grid state ---

    /// <summary>
    /// The square this character stands on. For anything bigger than one tile
    /// this is the corner of its footprint with the smallest X and Y, and the
    /// body spreads out from there — see <see cref="Footprint"/>.
    /// </summary>
    public int GX, GY;

    /// <summary>
    /// How many squares the body covers, across and down. 1x1 for everyone
    /// normal; a Living Stone is 2x2 and a Gator 2x1 — long rather than square,
    /// which is what forced these apart. The rectangle never rotates, so no
    /// rule has to know which way anything is facing.
    /// </summary>
    public int SizeX = 1, SizeY = 1;

    /// <summary>The longer side, for anything that wants one number.</summary>
    public int Size => Math.Max(SizeX, SizeY);

    /// <summary>Every square the body currently covers, anchor first.</summary>
    public IEnumerable<Point> Footprint
    {
        get
        {
            for (int y = 0; y < SizeY; y++)
                for (int x = 0; x < SizeX; x++)
                    yield return new Point(GX + x, GY + y);
        }
    }

    /// <summary>Whether the body covers a given square.</summary>
    public bool Covers(Point p) =>
        p.X >= GX && p.X < GX + SizeX && p.Y >= GY && p.Y < GY + SizeY;

    /// <summary>
    /// Grid distance from a square to the nearest part of this body. A big
    /// enemy is therefore in melee reach of anything touching any of its
    /// tiles, not just of things near its anchor corner.
    /// </summary>
    public int DistanceTo(Point p) =>
        Math.Max(0, p.X < GX ? GX - p.X : p.X - (GX + SizeX - 1)) +
        Math.Max(0, p.Y < GY ? GY - p.Y : p.Y - (GY + SizeY - 1));

    /// <summary>Grid distance between the nearest parts of two bodies.</summary>
    public int DistanceTo(CharacterInstance other)
    {
        int best = int.MaxValue;
        foreach (var tile in other.Footprint) best = Math.Min(best, DistanceTo(tile));
        return best;
    }

    /// <summary>Movement points per turn (from Classes.txt / Enemies.txt).</summary>
    public int MoveMax = 5;
    /// <summary>Points left this turn.</summary>
    public int MovePoints;

    /// <summary>
    /// Action points left. Cards cost these; walking is free. Everyone is
    /// topped up to ActionsPerTurn at the start of their turn, plus at most
    /// ActionRollover carried over from what they didn't spend last time.
    /// </summary>
    public int ActionPoints;

    /// <summary>
    /// Fresh points granted at the start of every turn, from the "Actions:"
    /// line in Classes.txt or Enemies.txt. Everyone gets this many; nobody has
    /// a ceiling.
    /// </summary>
    public int ActionsPerTurn = DefaultActionsPerTurn;

    /// <summary>What a class or enemy gets without an "Actions:" line of its own.</summary>
    public const int DefaultActionsPerTurn = 5;

    /// <summary>
    /// Turn start: this turn's points are ADDED to whatever is left over, with
    /// no cap. Saving up is a real option — three quiet turns buy a card that
    /// no single turn could pay for — and the pile is only cleared when the
    /// fight ends, so nothing carries between battles.
    /// </summary>
    public void RefreshActionPoints() => ActionPoints += ActionsPerTurn;

    /// <summary>Back to nothing saved up. Called when a fight begins and when it ends.</summary>
    public void ResetActionPoints() => ActionPoints = 0;

    // --- status effects ---
    /// <summary>
    /// One entry per stack of Burning, holding that stack's own remaining
    /// turns. Stacks are independent: a stack applied later expires later,
    /// and adding one never extends the ones already burning.
    /// </summary>
    public List<int> Burns = new();

    /// <summary>Live stacks; each deals Effects.BurnDamagePerStack at this character's turn start.</summary>
    public int BurningStacks => Burns.Count;

    /// <summary>Soaks damage before health does, and shows grey on the bar.</summary>
    public int Armor;

    /// <summary>One entry per curse: extra melee damage taken, and its own remaining turns.</summary>
    public List<(int Amount, int Turns)> Curses = new();

    /// <summary>Extra damage this character takes from melee cards right now.</summary>
    public int CurseBonus => Curses.Sum(c => c.Amount);

    /// <summary>
    /// The card being channelled, if any. While this is set the character is
    /// rooted: they cannot move, and the only card they can play is this one,
    /// which releases it.
    /// </summary>
    public string ChannellingCard = "";

    /// <summary>Turns still to wait before the channel can be released.</summary>
    public int ChannelTurnsLeft;

    /// <summary>
    /// Where the channelled card was aimed when it was STARTED. A channelled
    /// card is aimed once, on the turn it is begun; releasing it on a later
    /// turn fires it at that spot rather than asking again. What it catches is
    /// worked out when it lands, so anyone who has wandered into the area since
    /// is hit and anyone who has left is not.
    /// </summary>
    public Point ChannelAim;

    public bool IsChannelling => ChannellingCard.Length > 0;

    /// <summary>Which shape a shapeshifter currently wears; blank for everyone else.</summary>
    public string Form = "";

    /// <summary>
    /// Who summoned this, if anybody. A pet has no turn of its own: it acts
    /// during its owner's, and the player picks whichever of the two they want
    /// to move by clicking on it.
    /// </summary>
    public CharacterInstance? Owner;

    /// <summary>True for something summoned onto the board rather than placed in the level.</summary>
    public bool IsPet => Owner != null;

    /// <summary>
    /// Whether a party still has somebody who can take a turn. A pet acts only
    /// inside its summoner's turn, so a gator left standing over a dead party
    /// is not a survivor — nothing would ever move again. Counting it would
    /// hang the mission instead of ending it.
    /// </summary>
    public static bool AnyoneStanding(IEnumerable<CharacterInstance> party) =>
        party.Any(p => p.Alive && !p.IsPet);

    /// <summary>
    /// How far this character shoots anything hostile that comes near, or 0
    /// when they are not standing their ground. Set by a Guard card, and it
    /// costs them their movement for as long as it lasts.
    /// </summary>
    public int GuardRange;

    /// <summary>Shots fired at whoever walks into that range, and the damage each does.</summary>
    public int GuardShots, GuardDamage;

    /// <summary>Who has already been shot for coming close, so one approach draws one volley.</summary>
    public List<CharacterInstance> GuardedAgainst = new();

    /// <summary>Cards lifted off somebody else and playable until the clock runs out.</summary>
    public List<StolenCard> Stolen = new();

    /// <summary>The same records, seen from the victim's side: cards they can't play.</summary>
    public List<StolenCard> Lost = new();

    // --- transient render state: never saved, and cleared when the cast ends ---

    /// <summary>
    /// The casting animation now playing over this character's sprite, or null
    /// when it is standing still. Set when a card is played and cleared when
    /// the last frame has been shown.
    /// </summary>
    public SpriteAnimation? CastAnim;

    /// <summary>Seconds into <see cref="CastAnim"/>.</summary>
    public float CastAnimTime;

    /// <summary>
    /// The sheet this character plays while casting — a shapeshifter's current
    /// form picks its own. Null when nobody declared one, which is the normal
    /// case: the static sprite simply stays put.
    /// </summary>
    public string? CastAnimationPath => IsPlayer
        ? ClassLibrary.Current.Get(Name)?.CastAnimationPath(Form)
        : EnemyLibrary.Current.Get(Name)?.CastAnimationPath;

    public string Folder => IsPlayer
        ? $"Content/Cast/PlayerCharacters/{Name}"
        : $"Content/Cast/EnemyCharacters/{Name}";
    public string SpritePath => $"{Folder}/{SpriteFile}";
    /// <summary>Optional: Goblin1.png -> Goblin1Thumb.png. Falls back to the full sprite.</summary>
    public string ThumbPath => $"{Folder}/{System.IO.Path.GetFileNameWithoutExtension(SpriteFile)}Thumb.png";

    public CharacterInstance Clone()
    {
        var copy = (CharacterInstance)MemberwiseClone();
        copy.CastAnim = null;               // a snapshot is of somebody standing still
        copy.CastAnimTime = 0f;
        copy.Burns = new List<int>(Burns);   // don't share the stack lists with the original
        copy.Curses = new List<(int, int)>(Curses);
        copy.Stolen = new List<StolenCard>(Stolen);
        copy.Lost = new List<StolenCard>(Lost);
        copy.GuardedAgainst = new List<CharacterInstance>(GuardedAgainst);
        return copy;
    }
}
