using System;
using System.Collections.Generic;
using System.Linq;
using TheTimelineIs.Core.Iso;

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
    public static void Run(CardLibrary cards, ClassLibrary classes, EnemyLibrary enemies, Strings strings)
    {
        var diag = Diagnostics.Current;
        ValidateCards(cards, classes, diag);
        ValidateRoster(classes, diag);
        ValidateEnemies(enemies, diag);
        ValidateLevels(enemies, diag);
        ValidateStrings(strings, diag);
    }

    private static void ValidateCards(CardLibrary cards, ClassLibrary classes, Diagnostics diag)
    {
        if (cards.All.Count == 0)
            diag.Error(CardLibrary.Path, 0, "no cards were loaded at all");

        var playableTags = classes.AllPlayableTags();

        foreach (var card in cards.All)
        {
            foreach (var tag in card.Tags)
                if (!playableTags.Contains(tag))
                    diag.Error(CardLibrary.Path, card.Line,
                        $"'{card.Name}': no class in {ClassLibrary.Path} plays the tag '{tag}', " +
                        "so nobody can ever hold this card");

            if (card.Delivery == Delivery.Ranged)
            {
                string art = $"Content/Images/Effects/{card.ProjectileArt}";
                if (!AssetLoader.Exists(art))
                    diag.Error(CardLibrary.Path, card.Line,
                        $"'{card.Name}': Projectile Art '{card.ProjectileArt}' not found at {art}");
            }

            CheckSound(card.Name, card.Line, card.CastingSound, "Casting Sound", diag);
            foreach (var hit in card.HitEvents)
                CheckSound(card.Name, card.Line, hit.Sound, "Hit Sound", diag);

            if (card.Kind == CardKind.MultiTarget && card.Targets < 1)
                diag.Error(CardLibrary.Path, card.Line, $"'{card.Name}': needs at least one target");
        }
    }

    private static void CheckSound(string owner, int line, string? file, string field, Diagnostics diag)
    {
        if (string.IsNullOrWhiteSpace(file)) return;
        string path = $"{Audio.SoundBank.Folder}/{file}";
        if (!AssetLoader.Exists(path))
            diag.Error(CardLibrary.Path, line, $"'{owner}': {field} '{file}' not found at {path}");
        else if (!file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            diag.Warn(CardLibrary.Path, line,
                $"'{owner}': {field} '{file}' is not a .wav — only PCM WAV can be played");
    }

    private static void ValidateRoster(ClassLibrary classes, Diagnostics diag)
    {
        if (classes.ClassNames.Count == 0)
            diag.Error(ClassLibrary.Path, 0, "no classes declared, so the party picker is empty");

        foreach (var name in classes.ClassNames)
        {
            var cls = classes.Get(name)!;
            foreach (var sprite in cls.SpriteFiles)
                if (!AssetLoader.Exists($"{cls.Folder}/{sprite}"))
                    diag.Error(ClassLibrary.Path, cls.Line,
                        $"class '{name}': sprite '{sprite}' not found at {cls.Folder}/{sprite}");
        }

        var playable = classes.PlayableClasses();
        if (playable.Count is > 0 and < 3)
            diag.Warn(ClassLibrary.Path, 0,
                $"only {playable.Count} class(es) can be picked ({string.Join(", ", playable)}); " +
                "a party is 3, so slots will be filled with duplicates");
    }

    private static void ValidateEnemies(EnemyLibrary enemies, Diagnostics diag)
    {
        foreach (var name in enemies.EnemyNames)
        {
            var def = enemies.Get(name)!;
            foreach (var sprite in def.SpriteFiles)
                if (!AssetLoader.Exists($"{def.Folder}/{sprite}"))
                    diag.Error(EnemyLibrary.Path, def.Line,
                        $"enemy '{name}': sprite '{sprite}' not found at {def.Folder}/{sprite}");
            if (def.AttackSound != null &&
                !AssetLoader.Exists($"{Audio.SoundBank.Folder}/{def.AttackSound}"))
                diag.Error(EnemyLibrary.Path, def.Line,
                    $"enemy '{name}': sound '{def.AttackSound}' not found in {Audio.SoundBank.Folder}/");
        }
    }

    private static void ValidateLevels(EnemyLibrary enemies, Diagnostics diag)
    {
        // block palette art must exist before any level can draw
        foreach (var type in BlockCatalog.BlockTypes)
        {
            if (!AssetLoader.Exists(BlockCatalog.TopPath(type)))
                diag.Error(BlockCatalog.BlocksIndex, 0, $"block '{type}': missing {BlockCatalog.TopPath(type)}");
            if (!AssetLoader.Exists(BlockCatalog.SidePath(type)))
                diag.Error(BlockCatalog.BlocksIndex, 0, $"block '{type}': missing {BlockCatalog.SidePath(type)}");
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
            string path = LevelData.PathFor(dest.Mission);
            if (!AssetLoader.Exists(path))
            {
                diag.Error(DestinationTable.Path, dest.Line,
                    $"'{dest.Name}' points at level '{dest.Mission}' but {path} does not exist");
                continue;
            }
            var level = LevelData.Load(dest.Mission);
            var rooms = new HashSet<string>(level.RoomNames, StringComparer.OrdinalIgnoreCase);

            if (level.Blocks.Count == 0)
                diag.Error(path, 0, "level has no blocks at all");
            if (level.PlayerStarts.Count == 0)
                diag.Error(path, 0, "no PlayerStart — the party has nowhere to appear");
            else if (level.PlayerStarts.Count < 3)
                diag.Warn(path, 0,
                    $"only {level.PlayerStarts.Count} PlayerStart(s); a party of 3 will stack the rest nearby");

            foreach (var block in level.Blocks.Values)
                if (!BlockCatalog.IsBlockType(block.Type))
                    diag.Error(path, 0, $"block at {block.X},{block.Y} uses unknown type '{block.Type}'");
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
            }
            foreach (var door in level.Doors)
            {
                if (!rooms.Contains(door.RoomA) || !rooms.Contains(door.RoomB))
                    diag.Error(path, 0,
                        $"door at {door.X},{door.Y} joins '{door.RoomA}' and '{door.RoomB}' " +
                        "but at least one of those rooms has no blocks");
                if (level.BlockAt(new Microsoft.Xna.Framework.Point(door.X, door.Y)) == null)
                    diag.Error(path, 0, $"door at {door.X},{door.Y} has no block under it");
            }
            foreach (var start in level.PlayerStarts)
                if (level.BlockAt(start) == null)
                    diag.Error(path, 0, $"PlayerStart at {start.X},{start.Y} has no block under it");

            // trigger squares must name a dialogue block that exists and has lines
            var dialogue = DialogueLibrary.Load(dest.Mission);
            foreach (var trigger in level.Triggers)
            {
                if (level.BlockAt(new Microsoft.Xna.Framework.Point(trigger.X, trigger.Y)) == null)
                    diag.Error(path, 0, $"trigger at {trigger.X},{trigger.Y} has no block under it");
                if (!dialogue.Has(trigger.Dialogue))
                    diag.Error(path, 0,
                        $"trigger at {trigger.X},{trigger.Y} calls dialogue '{trigger.Dialogue}', " +
                        $"which is not in {DialogueLibrary.PathFor(dest.Mission)}");
                else if (dialogue.Get(trigger.Dialogue)!.Count == 0)
                    diag.Error(DialogueLibrary.PathFor(dest.Mission), 0,
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
            "title", "title_scramble_word", "menu_new_game", "menu_continue",
            "map_save", "room_save", "saved",
            "party_title", "party_start", "party_slot_empty",
            "battle_placeholder", "battle_win", "battle_turn", "battle_victory",
            "battle_pick_target", "battle_pick_targets",
            "battle_hit", "battle_enemy_hit", "battle_down",
            "death_title", "death_reload", "death_no_save",
            "devmap_hint", "devmap_name_prompt", "devmap_mission_prompt", "devmap_saved",
            "error_title", "error_continue", "error_more", "error_log", "error_counts",
            "iso_enter", "iso_explore_hint", "iso_spotted", "iso_done", "iso_clear",
            "iso_end_turn", "iso_move_left", "iso_out_of_range", "iso_card_spent",
            "iso_door_open", "iso_victory", "iso_card_range",
            "iso_move_spent", "iso_pick_target", "iso_confirm_strike", "iso_dialogue_next",
            "iso_pick_more",
        };
        foreach (var key in required)
            if (strings.Get(key) == $"[{key}]")
                diag.Error("Content/Text/Strings.txt", 0,
                    $"missing key '{key}' — the game will show [{key}] on screen");
    }
}
