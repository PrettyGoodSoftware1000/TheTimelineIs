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
}
