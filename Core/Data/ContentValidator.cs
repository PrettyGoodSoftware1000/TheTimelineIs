using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using TheTimelineIs.Core.Iso;
using TheTimelineIs.Core.Pixel;
using TheTimelineIs.Core.Screens;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// Runs once at startup and cross-checks the content files against each other
/// and against the files on disk: card tags against Classes.txt, levels
/// against the block palette, enemy list, and decoration files, every
/// referenced sound and image against whether it actually exists. Everything
/// it finds lands in Diagnostics, which becomes the startup popup and the
/// log file.
/// </summary>
public static class ContentValidator
{
    public static void Run(CardLibrary cards, CardLibrary enemyCards, ClassLibrary classes,
        EnemyLibrary enemies, Strings strings, Platform.IContentIndex? index)
    {
        var diag = Diagnostics.Current;
        ValidateCards(cards, classes.AllPlayableTags(), diag);
        ValidateCards(enemyCards, enemies.AllTags(), diag);
        ValidateCardsAgainstClasses(cards, classes, diag);
        ValidateRoster(classes, cards, diag);
        ValidateEnemies(enemies, enemyCards, diag);
        ValidateLevels(enemies, diag);
        ValidateStrings(strings, diag);
        ValidateGround(diag);
        ValidateArt(classes, enemies, index, diag);
    }

    /// <summary>
    /// Every art folder and animation somebody NAMED must exist. Not having
    /// art at all is fine — that is a cube, and the cube is the plan while
    /// the art is drawn — but naming a folder that is not there is a typo,
    /// and a typo that quietly drew a cube would never get found.
    /// </summary>
    private static void ValidateArt(ClassLibrary classes, EnemyLibrary enemies,
        Platform.IContentIndex? index, Diagnostics diag)
    {
        void CheckState(string file, int line, string who, string folder, string state, string animation)
        {
            // A named folder that is not there is a typo. A folder that IS
            // there with no rotations in it yet is work in progress — the
            // cube is what it is meant to draw — and does not need a note
            // every time the game starts.
            if (state.Length > 0 && index != null &&
                !index.Folders(folder).Contains(state, StringComparer.OrdinalIgnoreCase))
                diag.Warn(file, line, $"'{who}': there is no folder '{state}' under {folder} — " +
                    "drawing a cube. Make the folder, or fix the name.");
            if (animation.Length > 0 && index != null)
            {
                string root = state.Length > 0 ? state : FirstStateWithArt(index, folder) ?? "";
                bool any = false;
                foreach (var f in Pixel.Facings.All)
                    if (index.Images($"{folder}/{root}/animations/{animation}/{f.FileName()}").Count > 0)
                        any = true;
                if (!any)
                    diag.Warn(file, line, $"'{who}': cast animation '{animation}' has no frames under " +
                        $"{folder}/{root}/animations/{animation}/<direction>/ — casting will not animate");
            }
        }

        foreach (var name in classes.ClassNames)
        {
            var cls = classes.Get(name)!;
            if (cls.Forms.Count == 0)
                CheckState(ClassLibrary.Path, cls.Line, name, cls.Folder, cls.Art, cls.CastAnimation);
            foreach (var form in cls.Forms)
                CheckState(ClassLibrary.Path, cls.Line, $"{name} ({form.Name})", cls.Folder, form.Art,
                    cls.CastAnimationFor(form.Name));
        }
        foreach (var name in enemies.EnemyNames)
        {
            var def = enemies.Get(name)!;
            CheckState(EnemyLibrary.Path, def.Line, name, def.Folder, def.Art, def.CastAnimation);
        }
    }

    private static string? FirstStateWithArt(Platform.IContentIndex index, string folder)
    {
        foreach (string state in index.Folders(folder))
            if (AssetLoader.Exists($"{folder}/{state}/rotations/{Pixel.Facings.Default.FileName()}.png"))
                return state;
        return null;
    }

    /// <summary>
    /// A checkerboard family needs both halves. With only one, every square
    /// would want the shade that isn't there and the family would paint
    /// nothing at all — silently, which is the worst way to fail.
    /// </summary>
    private static void ValidateGround(Diagnostics diag)
    {
        foreach (var family in BlockCatalog.Families)
        {
            if (!BlockCatalog.IsCheckerboard(family)) continue;
            var pieces = BlockCatalog.PiecesIn(family);
            int line = pieces.Count > 0 ? pieces[0].Line : 0;

            foreach (var shade in new[] { Checker.Dark, Checker.Light })
                if (BlockCatalog.PiecesIn(family, shade).Count == 0)
                    diag.Error(BlockCatalog.BlocksIndex, line,
                        $"family '{family}' is a checkerboard but declares no " +
                        $"'Checkerboard {shade}:' pieces, so half its squares would draw nothing");

            if (pieces.Any(p => p.Shade == Checker.None))
                diag.Warn(BlockCatalog.BlocksIndex, line,
                    $"family '{family}' mixes checkerboard pieces with plain ones; the plain " +
                    "pieces belong to no shade and will never be painted");
        }
    }

    /// <summary>Checks that hold for any deck: art, sounds, shapes, effects.</summary>
    private static void ValidateCards(CardLibrary cards, HashSet<string> holderTags, Diagnostics diag)
    {
        if (cards.All.Count == 0)
            diag.Error(cards.Source, 0, "no cards were loaded at all");

        foreach (var card in cards.All)
        {
            foreach (var tag in card.Tags)
                if (!holderTags.Contains(tag) &&
                    !tag.Equals(CardLibrary.DefaultTag, StringComparison.OrdinalIgnoreCase))
                    diag.Error(card.Source, card.Line,
                        $"'{card.Name}': nothing declares the tag '{tag}', " +
                        "so nobody can ever hold this card");

            if (card.Delivery == Delivery.Ranged && card.ProjectileArt.Length > 0)
            {
                string art = $"Content/Images/Pixel/Effects/{card.ProjectileArt}";
                if (!AssetLoader.Exists(art))
                    diag.Warn(card.Source, card.Line,
                        $"'{card.Name}': Projectile Art '{card.ProjectileArt}' not found at {art} — " +
                        "the ball will be thrown instead");
            }

            CheckSound(card, card.CastingSound, "Casting Sound", diag);
            foreach (var hit in card.HitEvents)
                CheckSound(card, hit.Sound, "Hit Sound", diag);

            if (card.Kind == CardKind.MultiTarget && card.Targets < 1)
                diag.Error(card.Source, card.Line, $"'{card.Name}': needs at least one target");

            // A swap naming a card nobody wrote would take one card out of the
            // hand and put nothing back, leaving a hole and no explanation.
            if (card.IsSwap)
            {
                if (card.Replaces.Length == 0 || card.With.Length == 0)
                    diag.Error(card.Source, card.Line,
                        $"'{card.Name}': swaps cards but needs both a 'Replaces:' and a 'With:' line");
                foreach (var (field, name) in new[] { ("Replaces", card.Replaces), ("With", card.With) })
                    if (name.Length > 0 &&
                        !cards.All.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        diag.Error(card.Source, card.Line,
                            $"'{card.Name}': {field} names '{name}', which is not a card in {cards.Source}");
            }
            else if (card.Replaces.Length > 0 || card.With.Length > 0)
            {
                diag.Warn(card.Source, card.Line,
                    $"'{card.Name}': has a Replaces/With line but no 'Effects: Swap 1', " +
                    "so it never swaps anything");
            }

            if (!card.FriendlyFireDeclared)
                diag.Warn(card.Source, card.Line,
                    $"'{card.Name}': has no 'Friendly Fire:' line, so it is treated as No and " +
                    "will never touch its caster's own side");

            foreach (var effect in card.Effects)
            {
                if (effect.Amount <= 0)
                    diag.Error(card.Source, card.Line,
                        $"'{card.Name}': effect '{effect.Name}' needs an amount above 0");
                if (effect.Is(Effects.Armor) && card.Damage > 0)
                    diag.Warn(card.Source, card.Line,
                        $"'{card.Name}': carries Armor and damage, so it aims at enemies " +
                        "and will armour whatever it hits");
            }
            // Only one point carries forward, so the most anybody can ever
            // bring to a turn is one turn's worth plus that. A card dearer than
            // this is unplayable by a normal character, not merely expensive.
            int reachable = CharacterInstance.DefaultActionsPerTurn + CharacterInstance.MaxCarriedActions;
            if (card.ActionCost > reachable)
                diag.Warn(card.Source, card.Line,
                    $"'{card.Name}': costs {card.ActionCost} points, but the most anyone can hold " +
                    $"is {reachable} ({CharacterInstance.DefaultActionsPerTurn} a turn plus " +
                    $"{CharacterInstance.MaxCarriedActions} carried), so nobody can play it");
            if (card.FireTileTurns > 0 && !card.TargetsGround)
                diag.Warn(card.Source, card.Line,
                    $"'{card.Name}': FireTiles needs ground to burn, so the card should be " +
                    "AoE damage or a [cone]");

            if (card.Delivery == Delivery.Cone && card.Kind != CardKind.AoEDamage)
                diag.Warn(card.Source, card.Line,
                    $"'{card.Name}': a [cone] card should be 'AoE damage' — the cone is its area");
        }
    }

    /// <summary>Which room a transition pad sits in, or null if it is over nothing.</summary>
    private static string? RoomOf(LevelData level, TransitionPad pad) =>
        pad.Tiles.Select(t => level.BlockAt(t)?.Room).FirstOrDefault(r => r != null);

    /// <summary>Form gating only means anything for the player deck.</summary>
    private static void ValidateCardsAgainstClasses(CardLibrary cards, ClassLibrary classes, Diagnostics diag)
    {
        // A summon card naming a creature nobody declared used to reach the
        // board as a magenta checkerboard with no explanation. Caught here now,
        // at startup, with the name that was actually asked for.
        foreach (var card in cards.All.Where(c => c.IsSummon))
        {
            if (card.Summons.Length == 0)
                diag.Error(card.Source, card.Line,
                    $"'{card.Name}': has a Summon effect but no 'Summons:' line saying what it calls");
            else if (classes.Get(card.Summons) is not { IsSummon: true })
                diag.Error(card.Source, card.Line,
                    $"'{card.Name}': summons '{card.Summons}', which is not a 'Summon:' block in " +
                    $"{ClassLibrary.Path}" +
                    (classes.SummonNames().Count > 0
                        ? $" (there is: {string.Join(", ", classes.SummonNames())})"
                        : " (that file declares no summons at all)"));
        }
        foreach (var card in cards.All.Where(c => c.Summons.Length > 0 && !c.IsSummon))
            diag.Warn(card.Source, card.Line,
                $"'{card.Name}': names a 'Summons:' creature but has no 'Effects: Summon N' line, " +
                "so nothing is ever called up");

        foreach (var card in cards.All.Where(c => c.Form.Length > 0))
        {
            var owners = classes.ClassNames
                .Where(n => classes.CardTagsFor(n).Intersect(card.Tags, StringComparer.OrdinalIgnoreCase).Any())
                .Select(n => classes.Get(n)!).ToList();
            if (owners.Count > 0 && owners.All(o => o.FindForm(card.Form) == null))
                diag.Error(card.Source, card.Line,
                    $"'{card.Name}': needs form '{card.Form}', which no class holding its tags declares");
        }
    }

    private static void CheckSound(Card card, string? file, string field, Diagnostics diag)
    {
        if (string.IsNullOrWhiteSpace(file)) return;
        string path = $"{Audio.SoundBank.Folder}/{file}";
        if (!AssetLoader.Exists(path))
            diag.Error(card.Source, card.Line, $"'{card.Name}': {field} '{file}' not found at {path}");
        else if (!file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            diag.Warn(card.Source, card.Line,
                $"'{card.Name}': {field} '{file}' is not a .wav — only PCM WAV can be played");
    }

    private static void ValidateRoster(ClassLibrary classes, CardLibrary cards, Diagnostics diag)
    {
        if (classes.ClassNames.Count == 0)
            diag.Error(ClassLibrary.Path, 0, "no classes declared, so the party picker is empty");

        foreach (var name in classes.ClassNames)
        {
            var cls = classes.Get(name)!;
            // every form needs a card that can leave it, or the shape is a trap
            foreach (var form in cls.Forms)
                if (!cards.All.Any(c => c.BecomesForm != null &&
                        c.Tags.Intersect(classes.CardTagsFor(name), StringComparer.OrdinalIgnoreCase).Any() &&
                        (c.Form.Length == 0 || c.Form.Equals(form.Name, StringComparison.OrdinalIgnoreCase))))
                    diag.Warn(ClassLibrary.Path, cls.Line,
                        $"class '{name}': form '{form.Name}' has no card that changes out of it");
            // a class wears EITHER a form's art or its own Art line, so
            // declaring both silently throws one of them away
            if (cls.Forms.Count > 0 && cls.Art.Length > 0)
                diag.Warn(ClassLibrary.Path, cls.Line,
                    $"class '{name}': has both Form: and Art: lines. Forms win — " +
                    $"'{cls.Art}' is ignored. Give each form its own folder instead.");

            // a card that shifts into a shape the class never declared is a
            // dead card: the shift silently fails at the moment it is played
            foreach (var card in cards.All.Where(c => c.BecomesForm != null &&
                         c.Tags.Intersect(classes.CardTagsFor(name), StringComparer.OrdinalIgnoreCase).Any()))
                if (cls.FindForm(card.BecomesForm!) == null)
                    diag.Error(card.Source, card.Line,
                        $"'{card.Name}' changes '{name}' into form '{card.BecomesForm}', " +
                        $"which that class does not declare" +
                        (cls.Forms.Count > 0
                            ? $" (it has: {string.Join(", ", cls.Forms.Select(f => f.Name))})"
                            : " (it has no Form: lines at all)"));
        }

        var playable = classes.PlayableClasses();
        if (playable.Count is > 0 && playable.Count < PartySelectScreen.PartySize)
            diag.Warn(ClassLibrary.Path, 0,
                $"only {playable.Count} class(es) can be picked ({string.Join(", ", playable)}); " +
                $"a party is {PartySelectScreen.PartySize}, so slots will be filled with duplicates");
    }

    private static void ValidateEnemies(EnemyLibrary enemies, CardLibrary enemyCards, Diagnostics diag)
    {
        foreach (var name in enemies.EnemyNames)
        {
            var def = enemies.Get(name)!;
            // an isometric enemy acts entirely through its cards; one with none
            // of its own is dealt the Default-tagged card instead
            var hand = enemyCards.HandFor(enemies.CardTagsFor(name));
            if (hand.Count == 0) hand = enemyCards.DefaultHand();
            if (hand.Count == 0)
                diag.Error(CardLibrary.EnemyPath, 0,
                    $"enemy '{name}' has no cards of its own and there is no card tagged " +
                    $"'{CardLibrary.DefaultTag}' to fall back on, so it can never act");
            else if (hand.All(c => c.TargetsAllies))
                diag.Warn(EnemyLibrary.Path, def.Line,
                    $"enemy '{name}': every one of its cards targets allies, so it never attacks");
        }
    }

    private static void ValidateLevels(EnemyLibrary enemies, Diagnostics diag)
    {
        // ground art must exist before any level can draw
        foreach (var piece in BlockCatalog.Pieces)
        {
            if (!AssetLoader.Exists(piece.Path))
                diag.Error(BlockCatalog.BlocksIndex, piece.Line,
                    $"piece '{piece.File}' not found at {piece.Path}");
            // an unset anchor puts the art's top-left corner on the square's
            // centre, which reads as "everything is shoved down and right"
            if (piece.Anchor == Point.Zero)
                diag.Warn(BlockCatalog.BlocksIndex, piece.Line,
                    $"piece '{piece.File}' has no Anchor, so it will draw off the grid — " +
                    "set one with the editor's Anchor button");
        }
        foreach (var deco in BlockCatalog.Decorations)
            if (!AssetLoader.Exists(BlockCatalog.DecorationPath(deco)))
                diag.Error(BlockCatalog.DecorationsIndex, 0,
                    $"decoration '{deco}' not found at {BlockCatalog.DecorationPath(deco)}");

        var destinations = DestinationTable.Load();
        if (destinations.All.Count == 0)
            diag.Warn(DestinationTable.Path, 0, "no destinations, so the map has nothing to click");

        foreach (var dest in destinations.All)
        {
            string path = LevelData.PathFor(dest.Level);
            if (!AssetLoader.Exists(path))
            {
                diag.Error(DestinationTable.Path, dest.Line,
                    $"'{dest.Name}' points at level '{dest.Level}' but {path} does not exist");
                continue;
            }
            var level = LevelData.Load(dest.Level);
            var rooms = new HashSet<string>(level.RoomNames, StringComparer.OrdinalIgnoreCase);

            if (level.Blocks.Count == 0)
                diag.Error(path, 0, "level has no blocks at all");
            if (level.PlayerStarts.Count == 0)
                diag.Error(path, 0, "no PlayerStart — the party has nowhere to appear");
            else if (level.PlayerStarts.Count < PartySelectScreen.PartySize)
                diag.Warn(path, 0,
                    $"only {level.PlayerStarts.Count} PlayerStart(s); a party of " +
                    $"{PartySelectScreen.PartySize} will stack the rest nearby");

            foreach (var block in level.Blocks.Values)
                if (!BlockCatalog.IsPiece(block.Type))
                    diag.Error(path, 0,
                        $"block at {block.X},{block.Y} uses '{block.Type}', which is not a piece in " +
                        $"{BlockCatalog.BlocksIndex} — that square will draw nothing");
            foreach (var deco in level.Decorations)
                if (!BlockCatalog.Decorations.Contains(deco.File, StringComparer.OrdinalIgnoreCase))
                    diag.Error(path, 0, $"decoration at {deco.X},{deco.Y} uses unknown file '{deco.File}'");
            foreach (var enemy in level.Enemies)
            {
                if (enemies.Get(enemy.Name) == null)
                    diag.Error(path, 0,
                        $"enemy at {enemy.X},{enemy.Y} is '{enemy.Name}', which is not in {EnemyLibrary.Path}");
                if (level.BlockAt(new Microsoft.Xna.Framework.Point(enemy.X, enemy.Y)) == null)
                    diag.Error(path, 0, $"enemy at {enemy.X},{enemy.Y} is floating in the void (no block)");
                // a big body needs its whole footprint under it, flat and clear,
                // or it spawns half off the ground and can never move
                var body = enemies.Get(enemy.Name);
                int sizeX = body?.SizeX ?? 1, sizeY = body?.SizeY ?? 1;
                if (sizeX > 1 || sizeY > 1)
                {
                    var anchor = new Microsoft.Xna.Framework.Point(enemy.X, enemy.Y);
                    int? floor = null;
                    foreach (var t in Iso.Pathfinder.Footprint(anchor, sizeX, sizeY))
                    {
                        var under = level.BlockAt(t);
                        if (under == null)
                        {
                            diag.Error(path, 0,
                                $"'{enemy.Name}' at {enemy.X},{enemy.Y} covers {sizeX}x{sizeY} squares, " +
                                $"but {t.X},{t.Y} has no block under it");
                            continue;
                        }
                        if (floor is int known && known != under.Height)
                            diag.Error(path, 0,
                                $"'{enemy.Name}' at {enemy.X},{enemy.Y} straddles a step: {t.X},{t.Y} " +
                                $"is {under.Height} feet where the rest of its footprint is {known}");
                        floor ??= under.Height;
                    }
                }
            }
            foreach (var door in level.Doors)
            {
                var at = door.Tile;
                var under = level.BlockAt(at);
                if (under == null)
                {
                    diag.Error(path, 0, $"door at {at.X},{at.Y} has no block under it, " +
                        "so nobody can walk through it");
                    continue;
                }
                // A doorway belongs to no room. One left painted is part of a
                // room instead of a gap between two, and would be revealed with
                // that room whether or not it had ever been opened.
                if (under.Room.Length > 0)
                {
                    diag.Error(path, 0,
                        $"door at {at.X},{at.Y} sits on a square painted '{under.Room}' — " +
                        "a doorway square must belong to no room");
                    continue;
                }
                var joins = level.RoomsBeside(at);
                if (joins.Count < 2)
                    diag.Error(path, 0,
                        $"door at {at.X},{at.Y} touches " +
                        (joins.Count == 0 ? "no rooms" : $"only '{joins[0]}'") +
                        " — a door needs a different room on each side");
                if (level.Doors.Count(d => d.Covers(at)) > 1)
                    diag.Error(path, 0,
                        $"more than one door on {at.X},{at.Y}; only one of them can ever be opened");
            }
            foreach (var start in level.PlayerStarts)
                if (level.BlockAt(start) == null)
                    diag.Error(path, 0, $"PlayerStart at {start.X},{start.Y} has no block under it");

            // area transitions: a pad has to be walkable, has to lead somewhere,
            // and has to lead somewhere unambiguous
            foreach (var t in level.Transitions)
                if (level.BlockAt(new Microsoft.Xna.Framework.Point(t.X, t.Y)) == null)
                    diag.Error(path, 0,
                        $"the area transition at {t.X},{t.Y} has no block under it, so nobody can step on it");

            var pads = level.TransitionPads();
            foreach (var pad in pads)
            {
                var at = pad.Key;
                if (pad.Pair == 0)
                {
                    diag.Warn(path, 0,
                        $"the area transition at {at.X},{at.Y} is not linked to anything, so " +
                        "stepping on it does nothing — right-click it and then its far end");
                    continue;
                }
                if (pad.Mixed)
                    diag.Error(path, 0,
                        $"the area transition at {at.X},{at.Y} is made of squares with different " +
                        "pair numbers — two linked pads have been painted into one, and only " +
                        $"pair {pad.Pair} survives. Rub out the squares joining them, or link it again");
                int ends = pads.Count(p => p.Pair == pad.Pair);
                if (ends == 1)
                    diag.Error(path, 0,
                        $"the area transition at {at.X},{at.Y} is pair {pad.Pair}, but it is the " +
                        "only pad with that number, so it leads nowhere");
                else if (ends > 2)
                    diag.Error(path, 0,
                        $"pair {pad.Pair} is shared by {ends} area transitions; a pair joins " +
                        "exactly two, so there is no telling which one this leads to");

                // a pad in the same room as its far end moves the party without
                // changing anything, which is almost never what was wanted
                var other = pads.FirstOrDefault(p => p != pad && p.Pair == pad.Pair);
                if (other != null && RoomOf(level, pad) is string a && RoomOf(level, other) is string b &&
                    a.Equals(b, StringComparison.OrdinalIgnoreCase))
                    diag.Warn(path, 0,
                        $"the area transition at {at.X},{at.Y} leads to another pad in the same " +
                        $"room ('{a}'), so the party is moved but nothing is revealed or hidden");
            }

            // trigger squares must name a dialogue block that exists and has lines
            var dialogue = DialogueLibrary.Load(dest.Level);
            foreach (var trigger in level.Triggers)
            {
                if (level.BlockAt(new Microsoft.Xna.Framework.Point(trigger.X, trigger.Y)) == null)
                    diag.Error(path, 0, $"trigger at {trigger.X},{trigger.Y} has no block under it");
                if (!dialogue.Has(trigger.Dialogue))
                    diag.Error(path, 0,
                        $"trigger at {trigger.X},{trigger.Y} calls dialogue '{trigger.Dialogue}', " +
                        $"which is not in {DialogueLibrary.PathFor(dest.Level)}");
                else if (dialogue.Get(trigger.Dialogue)!.Count == 0)
                    diag.Error(DialogueLibrary.PathFor(dest.Level), 0,
                        $"dialogue '{trigger.Dialogue}' has no lines");
            }

            if (!AssetLoader.Exists("Content/Images/Decorations/Door.png") && level.Doors.Count > 0)
                diag.Error(path, 0, "level has doors but Content/Images/Decorations/Door.png is missing");
        }
    }

    /// <summary>Every key the code asks for must exist, or the player sees "[key]".</summary>
    private static void ValidateStrings(Strings strings, Diagnostics diag)
    {
        string[] required =
        {
            "title", "title_scramble_word", "version", "menu_new_game", "menu_continue",
            "map_save", "saved",
            "party_title", "party_start", "party_slot_empty",
            "battle_win", "battle_turn", "battle_hit", "battle_down",
            "death_title", "death_reload", "death_no_save",
            "devmap_hint", "devmap_name_prompt", "devmap_saved",
            "devmap_on", "devmap_off",
            "replay_start", "replay_stop", "replay_started", "replay_done", "replay_saved", "replay_failed", "replay_watching",
            "replay_title", "replay_turn", "replay_end", "replay_next", "replay_card",
            "replay_over", "replay_none", "menu_replays",
            "error_title", "error_continue", "error_more", "error_log", "error_counts",
            "iso_enter", "iso_explore_hint", "iso_clear",
            "iso_end_turn", "iso_move_left", "iso_out_of_range",
            "iso_door_open", "iso_victory", "iso_card_range", "iso_transition",
            "iso_move_spent", "iso_pick_target", "iso_dialogue_next",
            "iso_pick_more", "iso_needs_enemy", "iso_needs_ally", "iso_hit_armor",
            "iso_burning", "iso_burn_out", "iso_armored", "iso_nimble",
            "iso_cursed", "iso_form", "iso_pet_turn", "iso_summoned", "iso_summon_no_room", "iso_summon_already",
            "iso_guarding", "iso_guard_fires",
            "iso_stole", "iso_steal_over", "iso_nothing_to_steal", "iso_no_cards", "iso_needs_other",
            "iso_log_empty", "iso_log_more", "iso_actions_left", "iso_card_actions", "iso_no_actions",
            "iso_steal_pick", "iso_steal_pick_form", "iso_empty_square",
            "iso_channel_start", "iso_channelling", "iso_channel_rooted", "iso_channel_waiting", "iso_fire_lit",
            "iso_vulnerable", "iso_vulnerable_hit",
            "iso_stunned", "iso_stun_skip", "iso_swapped",
            "iso_trip_start", "iso_trip_empty", "iso_trip_survivor", "iso_trip_woke",
            "dev_title", "dev_win", "dev_die", "dev_fps", "dev_close",
        };
        foreach (var key in required)
            if (strings.Get(key) == $"[{key}]")
                diag.Error("Content/Text/Strings.txt", 0,
                    $"missing key '{key}' — the game will show [{key}] on screen");
    }
}
