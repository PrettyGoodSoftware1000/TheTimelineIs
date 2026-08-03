using System;
using System.Collections.Generic;
using System.Linq;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// Runs once at startup and cross-checks the content files against each other
/// and against the files on disk: card tags against Classes.txt, missions against
/// backgrounds and cast folders, every referenced sound and image against
/// whether it actually exists. Everything it finds lands in Diagnostics, which
/// becomes the startup popup and the log file.
///
/// The point is that a typo shows up here, by file and line, instead of as a
/// card that quietly does nothing three hours into playtesting.
/// </summary>
public static class ContentValidator
{
    public static void Run(CardLibrary cards, ClassLibrary classes, Strings strings)
    {
        var diag = Diagnostics.Current;
        ValidateCards(cards, classes, diag);
        ValidateRoster(classes, diag);
        ValidateMissions(diag);
        ValidateStrings(strings, diag);
    }

    private static void ValidateCards(CardLibrary cards, ClassLibrary classes, Diagnostics diag)
    {
        if (cards.All.Count == 0)
            diag.Error(CardLibrary.Path, 0, "no cards were loaded at all");

        // a tag is a label some class opts into, not a class name itself
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

            CheckSound(card, card.CastingSound, "Casting Sound", diag);
            foreach (var hit in card.HitEvents)
                CheckSound(card, hit.Sound, "Hit Sound", diag);

            // a slow card on a big stage is a real problem, not a style note
            if (card.Delivery != Delivery.Instant && card.Speed > 0 && card.Speed < 1.5f)
                diag.Warn(CardLibrary.Path, card.Line,
                    $"'{card.Name}': Speed {card.Speed} ft/s means up to " +
                    $"{17.3f / card.Speed:0} seconds to cross the stage");

            if (card.Kind == CardKind.MultiTarget && card.Targets < 1)
                diag.Error(CardLibrary.Path, card.Line, $"'{card.Name}': needs at least one target");
        }
    }

    private static void CheckSound(Card card, string? file, string field, Diagnostics diag)
    {
        if (string.IsNullOrWhiteSpace(file)) return;
        string path = $"{Audio.SoundBank.Folder}/{file}";
        if (!AssetLoader.Exists(path))
            diag.Error(CardLibrary.Path, card.Line,
                $"'{card.Name}': {field} '{file}' not found at {path}");
        else if (!file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            diag.Warn(CardLibrary.Path, card.Line,
                $"'{card.Name}': {field} '{file}' is not a .wav — only PCM WAV can be played");
    }

    /// <summary>
    /// Classes.txt is the roster. Every class it declares needs character art
    /// to be pickable, and the picker needs enough of them to fill three slots.
    /// </summary>
    private static void ValidateRoster(ClassLibrary classes, Diagnostics diag)
    {
        if (classes.ClassNames.Count == 0)
            diag.Error(ClassLibrary.Path, 0, "no classes declared, so the party picker is empty");

        foreach (var name in classes.ClassNames)
            if (!AssetLoader.Exists($"Content/Cast/PlayerCharacters/{name}/{name}.txt"))
                diag.Warn(ClassLibrary.Path, classes.LineOf(name),
                    $"class '{name}' has no folder at Content/Cast/PlayerCharacters/{name}/, " +
                    "so it can't be picked at New Game");

        var playable = classes.PlayableClasses();
        if (playable.Count is > 0 and < 3)
            diag.Warn(ClassLibrary.Path, 0,
                $"only {playable.Count} class(es) can be picked ({string.Join(", ", playable)}); " +
                "a party is 3, so slots will be filled with duplicates");

        foreach (var name in playable)
            CheckCastFolder(name, isPlayer: true, diag);
    }

    private static void CheckCastFolder(string name, bool isPlayer, Diagnostics diag)
    {
        string folder = isPlayer
            ? $"Content/Cast/PlayerCharacters/{name}"
            : $"Content/Cast/EnemyCharacters/{name}";
        var manifest = CastManifest.Get(name);
        string source = $"{folder}/{name}.txt";

        if (manifest.Variants.Count == 0)
            diag.Error(source, 0, $"'{name}' lists no sprite files");

        foreach (var variant in manifest.Variants)
            if (!AssetLoader.Exists($"{folder}/{variant}"))
                diag.Error(source, 0, $"'{name}' lists '{variant}' but {folder}/{variant} does not exist");
    }

    private static void ValidateMissions(Diagnostics diag)
    {
        if (!AssetLoader.Exists(Render.Backdrop.GroundPath))
            diag.Error(Render.Backdrop.GroundPath, 0,
                "ground art is missing, so every room shows a blank strip under the background");

        var destinations = DestinationTable.Load();
        if (destinations.All.Count == 0)
            diag.Warn(DestinationTable.Path, 0, "no destinations, so the map has nothing to click");

        foreach (var dest in destinations.All)
        {
            string path = $"Content/Missions/{dest.Mission}/{dest.Mission}.txt";
            if (!AssetLoader.Exists(path))
            {
                diag.Error(DestinationTable.Path, dest.Line,
                    $"'{dest.Name}' points at mission '{dest.Mission}' but {path} does not exist");
                continue;
            }

            var script = MissionScript.Load(dest.Mission);
            if (script.Rooms.Count == 0)
                diag.Error(path, 0, "no 'Room N:' sections found");

            for (int i = 0; i < script.Rooms.Count; i++)
            {
                var room = script.Rooms[i];
                int number = i + 1;

                if (room.Background.Length == 0)
                    diag.Error(path, 0, $"Room {number} has no background (and no earlier room to inherit from)");
                else if (!AssetLoader.Exists($"Content/Images/Backgrounds/{room.Background}"))
                    diag.Error(path, room.BackgroundLine,
                        $"Room {number}: background '{room.Background}' not found in Content/Images/Backgrounds/");

                if (room.Cast.Count == 0)
                    diag.Error(path, 0, $"Room {number} has no Cast line");

                var names = new List<string>();
                foreach (var member in room.Cast)
                {
                    if (member.Equals("Player characters", StringComparison.OrdinalIgnoreCase)) continue;
                    names.Add(member);
                    bool isPlayer = AssetLoader.Exists($"Content/Cast/PlayerCharacters/{member}/{member}.txt");
                    bool isEnemy = AssetLoader.Exists($"Content/Cast/EnemyCharacters/{member}/{member}.txt");
                    if (!isPlayer && !isEnemy)
                        diag.Error(path, room.CastLine,
                            $"Room {number}: cast member '{member}' has no folder under " +
                            "Content/Cast/PlayerCharacters or EnemyCharacters");
                    else
                        CheckCastFolder(member, isPlayer, diag);
                }

                bool hasParty = room.Cast.Any(c =>
                    c.Equals("Player characters", StringComparison.OrdinalIgnoreCase));
                foreach (var line in room.Entries.OfType<DialogueEntry>())
                    if (!hasParty && !names.Contains(line.Speaker, StringComparer.OrdinalIgnoreCase))
                        diag.Warn(path, line.Line,
                            $"Room {number}: '{line.Speaker}' speaks but is not in the Cast line, " +
                            "so the line shows with no portrait");
            }
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
            "error_title", "error_continue", "error_more", "error_log",
        };
        foreach (var key in required)
            if (strings.Get(key) == $"[{key}]")
                diag.Error("Content/Text/Strings.txt", 0,
                    $"missing key '{key}' — the game will show [{key}] on screen");
    }
}
