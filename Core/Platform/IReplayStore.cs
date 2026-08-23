using System.Collections.Generic;

namespace TheTimelineIs.Core.Platform;

/// <summary>
/// Where mission replays are kept. Writing needs a door TitleContainer does not
/// open, the same as saves and the content log, so Core asks for one rather
/// than reaching for the file system itself.
///
/// A replay is two files under the same name: the record of what happened, and
/// a copy of the level it happened in. The level is copied rather than
/// referenced because levels get edited — a replay that pointed at
/// Content/Levels/TestLevel.txt would start showing people walking through
/// walls the first time that level was changed.
/// </summary>
public interface IReplayStore
{
    /// <summary>Human-readable location, for telling the player where it went.</summary>
    string DisplayPath { get; }

    /// <summary>The names of every replay on disk, newest first.</summary>
    IReadOnlyList<string> List();

    /// <summary>
    /// Writes both halves under one name. Returns where it landed, or null if
    /// it could not be written.
    /// </summary>
    string? Save(string name, string replayText, string levelText);

    /// <summary>The record and the level it was played in, or null if missing.</summary>
    (string Replay, string Level)? Load(string name);
}
