using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// Ground somebody is standing guard over, and who is currently on it.
///
/// The rule is about ENTERING, not about being inside: walking in draws one
/// volley, crossing the rest of it draws nothing, and walking out and back in
/// draws another. That "who is already in" bookkeeping is the whole reason this
/// is its own class — it was spread across a walk loop and a turn change
/// before, and the two did not always agree about who had been shot.
///
/// It holds no reference to a character, only a key per body, so the rules can
/// be tested without a board.
/// </summary>
public class GuardWatch
{
    /// <summary>Every square being watched.</summary>
    public HashSet<Point> Ground { get; private set; } = new();

    /// <summary>Shots fired at whoever walks in, and what each one does.</summary>
    public int Shots, Damage;

    /// <summary>Bodies standing on the ground right now, by key.</summary>
    private readonly HashSet<string> _inside = new();

    public bool Watching => Ground.Count > 0;

    public void Cover(IEnumerable<Point> ground, int shots, int damage)
    {
        Ground = new HashSet<Point>(ground);
        Shots = shots;
        Damage = damage;
        _inside.Clear();
    }

    /// <summary>Lifts the watch: the ground stops being covered and the marks come off.</summary>
    public void Stand_Down()
    {
        Ground.Clear();
        Shots = Damage = 0;
        _inside.Clear();
    }

    public bool Covers(IEnumerable<Point> footprint) => footprint.Any(Ground.Contains);

    /// <summary>
    /// Marks a body as already standing here without firing at it. Used when
    /// the watch is first set up: whoever is standing there when the card is
    /// played does not get shot for having been there first.
    /// </summary>
    public void AlreadyHere(string key) => _inside.Add(key);

    /// <summary>
    /// A body is about to start walking, so it stops counting as "already
    /// standing here".
    ///
    /// Being marked as inside is only meant to stop the card shooting people
    /// where they stand the moment it is played. Once they take their turn and
    /// move, they are fair game — somebody caught inside the zone who shuffles
    /// one square gets shot exactly like somebody who walked in from outside.
    /// Without this they could cross the whole zone untouched, which read as
    /// the card simply not working when a target started close.
    /// </summary>
    public void AboutToWalk(string key) => _inside.Remove(key);

    /// <summary>
    /// A body has finished a step. Answers whether that step should draw a
    /// volley — true on the first step that lands on watched ground.
    /// </summary>
    public bool Entered(string key, IEnumerable<Point> footprint)
    {
        if (!Covers(footprint))
        {
            // out again: the next step in is a fresh approach
            _inside.Remove(key);
            return false;
        }
        // Add answers false when the key was already there, which is exactly
        // "they were already inside, so this is not an entry"
        return _inside.Add(key);
    }

    /// <summary>Forgets anybody no longer standing here, so they can be shot again.</summary>
    public void Forget(string key) => _inside.Remove(key);

    /// <summary>For tests and the log: how many bodies are being tracked as inside.</summary>
    public int Tracking => _inside.Count;
}
