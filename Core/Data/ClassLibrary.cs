using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace TheTimelineIs.Core.Data;

/// <summary>
/// A shape a class can wear: its own name, the folder its art is in, and
/// optionally the animation it casts with. A blank animation falls back to
/// the class's own line.
/// </summary>
public record ClassForm(string Name, string Art, string Animation = "");

/// <summary>One playable class: stats, art and forms from a Classes.txt block.</summary>
public class PlayerClass
{
    public string Name = "";
    public int Hp = 20;
    public int Movement = 5;

    /// <summary>
    /// Action points granted each turn once a fight starts. At most one
    /// unspent point carries into the next turn, so this is close to a hard
    /// budget rather than an allowance to save up.
    /// </summary>
    public int Actions = CharacterInstance.DefaultActionsPerTurn;

    /// <summary>
    /// Which state folder under the class's folder to draw from. Empty means
    /// the first folder that has rotations in it — the common case, since
    /// most characters have one state.
    /// </summary>
    public string Art = "";

    /// <summary>The colour of this class's placeholder cube while it has no art.</summary>
    public Color Colour = CastPlaceholder.DefaultColour;

    public List<string> CardTags = new();  // defaults to the class's own name

    /// <summary>Declared with "Form: Name, Folder". The first one is where the class starts.</summary>
    public List<ClassForm> Forms = new();

    /// <summary>
    /// The animation folder played while this class casts a card, under its
    /// state's animations/. A form carrying its own overrides it — the wolf
    /// never plays the witch's spell.
    /// </summary>
    public string CastAnimation = "";
    public int Line;

    /// <summary>
    /// The class that calls this creature onto the board, for a block declared
    /// with "Summon:" instead of "Class:". Empty for an ordinary class.
    ///
    /// A summon is a player character in every way that matters — it is on your
    /// side, it holds player cards, it moves when you tell it to — so it lives
    /// here rather than in Enemies.txt. What it is NOT is pickable: it never
    /// appears at the party screen, because you get it by playing the card.
    /// </summary>
    public string SummonedBy = "";

    public bool IsSummon => SummonedBy.Length > 0;

    /// <summary>How many squares this body covers, set by "Size: 2 x 1".</summary>
    public int SizeX = 1, SizeY = 1;

    /// <summary>
    /// Where this class's art lives. A summon's sits inside its summoner's
    /// folder, since it is that character's creature — a Gator's pictures are
    /// at "Florida Man/Gator/".
    /// </summary>
    public string Folder => IsSummon
        ? $"Content/Cast/PlayerCharacters/{SummonedBy}/{Name}"
        : $"Content/Cast/PlayerCharacters/{Name}";

    public string StartingForm => Forms.Count > 0 ? Forms[0].Name : "";

    public ClassForm? FindForm(string name) =>
        Forms.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The art folder for a form; the class's own when it has no forms.</summary>
    public string ArtFor(string form) => FindForm(form)?.Art ?? Art;

    /// <summary>
    /// The animation to cast with in a given shape, or empty when neither the
    /// form nor the class declares one. Most specific wins.
    /// </summary>
    public string CastAnimationFor(string form) =>
        FindForm(form)?.Animation is { Length: > 0 } own ? own : CastAnimation;
}

/// <summary>
/// Parses Content/Cast/PlayerCharacters/Classes.txt, the single authoritative
/// class file. The format is documented at the top of that file.
/// </summary>
public class ClassLibrary
{
    private readonly Dictionary<string, PlayerClass> _classes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();
    private readonly List<PlayerClass> _needsSummoner = new();

    public const string Path = "Content/Cast/PlayerCharacters/Classes.txt";

    /// <summary>Set by Load; lets a character look its own class up.</summary>
    public static ClassLibrary Current { get; private set; } = new();

    public IReadOnlyList<string> ClassNames => _order;
    public PlayerClass? Get(string name) => _classes.TryGetValue(name, out var c) ? c : null;
    public int LineOf(string name) => Get(name)?.Line ?? 0;

    public static ClassLibrary Load()
    {
        var diag = Diagnostics.Current;
        var lib = new ClassLibrary();
        PlayerClass? current = null;

        foreach (var (lineNo, raw) in AssetLoader.ReadNumbered(Path, Path))
        {
            string line = TextUtil.Clean(raw);
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                diag.Error(Path, lineNo, $"unrecognized line '{line}' — expected 'Key: value'");
                continue;
            }
            string key = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();

            // "Class:" and "Summon:" both open a block; the only difference is
            // that a summon is not offered at the party screen
            if (key is "class" or "summon")
            {
                if (value.Length == 0)
                {
                    diag.Error(Path, lineNo, $"'{line[..colon]}:' has no name");
                    continue;
                }
                if (lib._classes.ContainsKey(value))
                {
                    diag.Warn(Path, lineNo, $"class '{value}' is declared twice");
                    current = lib._classes[value];
                    continue;
                }
                current = new PlayerClass { Name = value, Line = lineNo };
                // marked provisionally so a "Summon:" block missing its
                // "Summoned By:" line can be caught below rather than quietly
                // passing itself off as a playable class
                if (key == "summon") lib._needsSummoner.Add(current);
                lib._classes[value] = current;
                lib._order.Add(value);
                continue;
            }
            if (current == null)
            {
                diag.Error(Path, lineNo, $"'{key}' appears before any 'Class:' line");
                continue;
            }
            switch (key)
            {
                case "hp":
                    if (int.TryParse(value, out int hp) && hp > 0) current.Hp = hp;
                    else diag.Error(Path, lineNo, $"'{current.Name}': HP must be a positive number, got '{value}'");
                    break;
                case "movement":
                    if (int.TryParse(value, out int mv) && mv > 0) current.Movement = mv;
                    else diag.Error(Path, lineNo, $"'{current.Name}': Movement must be a positive number, got '{value}'");
                    break;
                case "actions":
                    if (int.TryParse(value, out int ap) && ap > 0) current.Actions = ap;
                    else diag.Error(Path, lineNo,
                        $"'{current.Name}': Actions must be a positive number of points a turn, got '{value}'");
                    break;
                case "art":
                    if (CastPlaceholder.LooksLikeAPicture(value))
                        diag.Error(Path, lineNo, $"'{current.Name}': Art is a FOLDER now, not a picture — " +
                            $"got '{value}'. Put rotations/ inside a folder and name the folder here.");
                    else current.Art = value;
                    break;
                case "colour":
                case "color":
                    if (CastPlaceholder.TryParseColour(value, out var colour)) current.Colour = colour;
                    else diag.Error(Path, lineNo,
                        $"'{current.Name}': Colour must be three numbers 0-255 like '120, 80, 40', got '{value}'");
                    break;
                case "sprites":
                    diag.Error(Path, lineNo, $"'{current.Name}': 'Sprites:' is gone — art is a folder of " +
                        "rotations now. Delete the line, or use 'Art: FolderName' to pick a folder.");
                    break;
                case "form":
                    // "Form: Witch, WitchForm" — and optionally a third field,
                    // the animation this shape casts with
                    var bits = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (bits.Length < 2)
                        diag.Error(Path, lineNo,
                            $"'{current.Name}': Form needs a name and an art folder, e.g. 'Form: Witch, WitchForm'");
                    else if (CastPlaceholder.LooksLikeAPicture(bits[1]) ||
                             (bits.Length > 2 && CastPlaceholder.LooksLikeAPicture(bits[2])))
                        diag.Error(Path, lineNo, $"'{current.Name}': a Form names an art FOLDER and an " +
                            $"animation folder, not pictures — got '{value}'");
                    else if (current.Forms.Exists(f => f.Name.Equals(bits[0], StringComparison.OrdinalIgnoreCase)))
                        diag.Warn(Path, lineNo, $"'{current.Name}': form '{bits[0]}' is declared twice");
                    else
                        current.Forms.Add(new ClassForm(bits[0], bits[1], bits.Length > 2 ? bits[2] : ""));
                    break;
                case "cast animation":
                    if (CastPlaceholder.LooksLikeAPicture(value))
                        diag.Error(Path, lineNo, $"'{current.Name}': Cast Animation names a folder under " +
                            $"animations/ now, like 'GunShot' — got '{value}'");
                    else current.CastAnimation = value;
                    break;
                case "summoned by":
                    current.SummonedBy = value;
                    break;
                case "size":
                    // "2 x 1" is two squares side by side; a bare "2" is square
                    var span = value.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (span.Length == 1 && int.TryParse(span[0], out int both) && both > 0)
                        current.SizeX = current.SizeY = both;
                    else if (span.Length == 2 && int.TryParse(span[0], out int sx) && sx > 0
                             && int.TryParse(span[1], out int sy) && sy > 0)
                    { current.SizeX = sx; current.SizeY = sy; }
                    else
                        diag.Error(Path, lineNo, $"'{current.Name}': Size must be a number of squares " +
                            $"like '2' or '2 x 1', got '{value}'");
                    break;
                case "card tags":
                    current.CardTags = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    break;
                default:
                    diag.Warn(Path, lineNo, $"'{current.Name}': unknown line '{line}' ignored");
                    break;
            }
        }

        // A summon whose summoner is missing or misspelled would look for its
        // art in a folder that isn't there, with nothing said about why. Say
        // it here instead.
        foreach (var s in lib._needsSummoner)
        {
            if (s.SummonedBy.Length == 0)
                diag.Error(Path, s.Line, $"'{s.Name}' is a Summon but has no 'Summoned By:' line " +
                    "naming the class that calls it — that is also where its art lives");
            else if (lib.Get(s.SummonedBy) is not { IsSummon: false })
                diag.Error(Path, s.Line,
                    $"'{s.Name}' is summoned by '{s.SummonedBy}', which is not a class in this file");
        }
        lib._needsSummoner.Clear();

        Current = lib;
        return lib;
    }

    /// <summary>The tags this class can play; its own name when none are declared.</summary>
    public IReadOnlyList<string> CardTagsFor(string className)
    {
        var cls = Get(className);
        return cls != null && cls.CardTags.Count > 0
            ? cls.CardTags : new List<string> { className };
    }

    /// <summary>Every tag any class can play — what a card may legally be tagged with.</summary>
    public HashSet<string> AllPlayableTags() =>
        new(_order.SelectMany(CardTagsFor), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The party picker's roster: every declared class that is not a summon.
    /// Having art is not a condition — a class with none is a cube, and a cube
    /// can still be picked and played.
    /// </summary>
    public List<string> PlayableClasses() =>
        _order.Where(n => Get(n) is { IsSummon: false }).ToList();

    /// <summary>Creatures declared with "Summon:" — everything a card can call up.</summary>
    public List<string> SummonNames() =>
        _order.Where(n => Get(n) is { IsSummon: true }).ToList();
}
