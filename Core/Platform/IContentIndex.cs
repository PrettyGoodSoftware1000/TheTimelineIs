using System.Collections.Generic;

namespace TheTimelineIs.Core.Platform;

/// <summary>
/// Answers "what files are in this content folder?".
///
/// TitleContainer can open a file you already know the name of, but it cannot
/// tell you what is there — and some content is meant to be added by dropping
/// pictures into a folder rather than by writing another manifest. Listing a
/// directory needs a door TitleContainer does not open, the same as saves and
/// replays, so Core asks for one instead of reaching for the file system.
///
/// A platform that genuinely cannot enumerate its own bundle (a packed mobile
/// build) implements this by reading a manifest generated at build time. The
/// game only ever sees the list.
/// </summary>
public interface IContentIndex
{
    /// <summary>
    /// The files directly inside a content folder, by name only, in whatever
    /// order the platform gives them. The path is content-relative and uses
    /// forward slashes, e.g. "Content/Cast/PlayerCharacters/Florida Man/BathSalts".
    /// A folder that does not exist is not an error: it comes back empty.
    /// </summary>
    IReadOnlyList<string> Files(string folder, string extension);
}
