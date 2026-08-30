using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TheTimelineIs.Core.Iso;

/// <summary>
/// One thing the trip shows: either a single picture, or a run of numbered
/// frames that belong together.
///
/// A lone picture hangs there for <see cref="BathSaltsTrip.StillSeconds"/>,
/// fading in and out. A series — Gator1.png, Gator2.png, Gator3.png — is one
/// animation and runs at thirty frames a second in order, because those files
/// are frames of a thing moving, not three separate pictures.
/// </summary>
public record TripShot(string Name, IReadOnlyList<string> Frames)
{
    public bool IsSeries => Frames.Count > 1;

    /// <summary>How long this shot is on screen.</summary>
    public float Duration => IsSeries
        ? Frames.Count / BathSaltsTrip.SeriesFps
        : BathSaltsTrip.StillSeconds;

    /// <summary>Which frame is showing this far into the shot.</summary>
    public string FrameAt(float t) => IsSeries
        ? Frames[Math.Clamp((int)(t * BathSaltsTrip.SeriesFps), 0, Frames.Count - 1)]
        : Frames[0];
}

/// <summary>
/// Works out what a Bath Salts trip shows and in what order, from whatever
/// pictures happen to be in the folder.
///
/// Kept away from the screen so the grouping rule — which names are frames of
/// one animation and which are pictures in their own right — can be checked
/// without a graphics device. The screen only plays back the list.
/// </summary>
public class BathSaltsTrip
{
    /// <summary>Frames a second for a numbered series.</summary>
    public const float SeriesFps = 30f;

    /// <summary>How long a single picture hangs there, fading in and out.</summary>
    public const float StillSeconds = 2f;

    /// <summary>Seconds the screen takes to go black at the start and come back at the end.</summary>
    public const float FadeSeconds = 0.9f;

    /// <summary>"Gator12.png" -> stem "Gator", number 12. A name with no trailing number is its own thing.</summary>
    private static readonly Regex Numbered = new(@"^(?<stem>.*?)(?<n>\d+)$");

    public IReadOnlyList<TripShot> Shots { get; }

    /// <summary>How long the whole thing takes, fades included.</summary>
    public float Duration => FadeSeconds * 2 + Shots.Sum(s => s.Duration);

    private BathSaltsTrip(IReadOnlyList<TripShot> shots) => Shots = shots;

    /// <summary>
    /// Groups a folder's file names into shots and puts them in a random order.
    ///
    /// Files sharing a stem and ending in a number are one series, ordered by
    /// that number — so Gator1, Gator2, Gator10 play in the right order rather
    /// than the order the file system lists them in, which would put 10 second.
    /// Everything else is a picture on its own. The SHOTS are shuffled; the
    /// frames inside a series never are.
    /// </summary>
    public static BathSaltsTrip From(IEnumerable<string> fileNames, Random rng)
    {
        var series = new Dictionary<string, List<(int N, string File)>>(StringComparer.OrdinalIgnoreCase);
        var singles = new List<string>();

        foreach (string file in fileNames)
        {
            string bare = StripExtension(file);
            var m = Numbered.Match(bare);
            if (m.Success && m.Groups["stem"].Value.Length > 0 &&
                int.TryParse(m.Groups["n"].Value, out int n))
            {
                string stem = m.Groups["stem"].Value;
                if (!series.TryGetValue(stem, out var frames))
                    series[stem] = frames = new List<(int, string)>();
                frames.Add((n, file));
            }
            else
            {
                singles.Add(file);
            }
        }

        var shots = new List<TripShot>();
        foreach (var (stem, frames) in series)
        {
            var ordered = frames.OrderBy(f => f.N).Select(f => f.File).ToList();
            // a lone "Gator1.png" is a picture, not a one-frame animation
            if (ordered.Count == 1) singles.Add(ordered[0]);
            else shots.Add(new TripShot(stem, ordered));
        }
        shots.AddRange(singles.Select(f => new TripShot(StripExtension(f), new[] { f })));

        return new BathSaltsTrip(shots.OrderBy(_ => rng.Next()).ToList());
    }

    private static string StripExtension(string file)
    {
        int dot = file.LastIndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }

    /// <summary>
    /// How much of a still picture's own time has passed, as a 0..1 fade that
    /// rises and falls — in at the start, out at the end, full in the middle.
    /// A series does not fade: it is one moving thing and holds full strength.
    /// </summary>
    public static float Opacity(TripShot shot, float t)
    {
        if (shot.IsSeries) return 1f;
        float half = shot.Duration / 2f;
        return half <= 0f ? 1f : 1f - Math.Abs(t - half) / half;
    }
}
