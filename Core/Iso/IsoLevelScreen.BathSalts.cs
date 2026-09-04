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
}
