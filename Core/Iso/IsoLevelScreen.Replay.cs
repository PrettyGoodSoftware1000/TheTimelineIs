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
        foreach (var c in Everyone) { c.CastFrames = null; c.CastAnimTime = 0f; }
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
}
