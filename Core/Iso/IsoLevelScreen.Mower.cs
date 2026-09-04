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
}
