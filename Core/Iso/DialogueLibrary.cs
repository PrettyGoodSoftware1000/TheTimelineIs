using System;
using System.Collections.Generic;
using TheTimelineIs.Core.Data;

namespace TheTimelineIs.Core.Iso;

public record DialogueLine(string Speaker, string Text);

/// <summary>
/// Level dialogue, in Content/Levels/{Level}Dialogue.txt. Blocks are named so
/// a trigger area painted in the editor can call one up:
///
///   Dialogue: Intro
///   Dirtbag: Goddamn if my balls ain't itching.
///   Goblin: Give gold or I EAT BALLS!
///
/// Same "Speaker: text" shape as the old mission scripts — only the trigger
/// changed, from the start of a room to stepping on a marked square.
/// </summary>
public class DialogueLibrary
{
    private readonly Dictionary<string, List<DialogueLine>> _blocks =
        new(StringComparer.OrdinalIgnoreCase);

    public static string PathFor(string levelName) => $"Content/Levels/{levelName}Dialogue.txt";

    public IEnumerable<string> Ids => _blocks.Keys;
    public List<DialogueLine>? Get(string id) => _blocks.TryGetValue(id, out var v) ? v : null;
    public bool Has(string id) => _blocks.ContainsKey(id);

    /// <summary>A level with no dialogue file is normal, not an error.</summary>
    public static DialogueLibrary Load(string levelName)
    {
        var lib = new DialogueLibrary();
        string path = PathFor(levelName);
        if (!AssetLoader.Exists(path)) return lib;

        List<DialogueLine>? current = null;
        foreach (var (lineNo, raw) in AssetLoader.ReadNumbered(path, path))
        {
            string line = TextUtil.Clean(raw);
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                Diagnostics.Current.Error(path, lineNo,
                    $"unrecognized line '{line}' — expected 'Dialogue: Name' or 'Speaker: text'");
                continue;
            }
            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();

            if (key.Equals("Dialogue", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length == 0)
                {
                    Diagnostics.Current.Error(path, lineNo, "'Dialogue:' has no name");
                    continue;
                }
                if (lib._blocks.ContainsKey(value))
                    Diagnostics.Current.Warn(path, lineNo, $"dialogue '{value}' is declared twice");
                current = lib._blocks[value] = new List<DialogueLine>();
                continue;
            }
            if (current == null)
            {
                Diagnostics.Current.Error(path, lineNo,
                    $"'{line}' appears before any 'Dialogue:' block");
                continue;
            }
            current.Add(new DialogueLine(key, value));
        }
        return lib;
    }
}
