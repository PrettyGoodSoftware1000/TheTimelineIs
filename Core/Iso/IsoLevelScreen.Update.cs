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
    // ---------------- update ----------------

    private Mode _lastTraced;
    public void Update(InputState input, float dt)
    {
        if (_mode != _lastTraced && Environment.GetEnvironmentVariable("TIMELINE_TRACE") == "1")
        {
            Console.WriteLine($"[trace] mode {_lastTraced} -> {_mode}  current={Current?.Name}  walker={_walker?.Name} path={_walkPath.Count} act={_act}");
            _lastTraced = _mode;
        }
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
        if (_mode is Mode.Explore && (input.SelectAll || input.MiddleTap.HasValue))
        {
            _picked.Clear();
            _picked.AddRange(LivingParty);
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
            if (c.CastFrames == null) continue;
            c.CastAnimTime += dt;
            if (c.CastAnimTime >= c.CastFrames.Count / DirectionalSprite.Fps)
            {
                c.CastFrames = null;
                c.CastAnimTime = 0f;
            }
        }
    }

    /// <summary>
    /// Plays the caster's casting animation over its sprite, from the first
    /// frame, facing the way it is facing. A shapeshifter picks up the one for
    /// the shape it is wearing right now, so a card that changes form still
    /// casts in the form it started in. Anyone with no animation declared —
    /// or none drawn yet — simply stands there.
    /// </summary>
    private void StartCastAnimation(CharacterInstance actor)
    {
        actor.CastFrames = _ctx.Sprites.Frames(actor, actor.CastAnimation);
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
}
