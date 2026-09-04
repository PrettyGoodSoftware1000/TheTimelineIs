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
    /// Files directly inside a content folder, by name only. The path is
    /// content-relative, e.g. "Content/Cast/PlayerCharacters/Florida Man/BathSalts".
    ///
    /// - A missing folder comes back empty; that is not an error.
    /// - Extensions include the dot and are matched case-insensitively.
    /// </summary>
    IReadOnlyList<string> Files(string folder, params string[] extensions);

    /// <summary>Every picture in a folder, whatever image format it is in.</summary>
    IReadOnlyList<string> Images(string folder) => Files(folder, ImageTypes);

    /// <summary>
    /// The names of the folders directly inside one, so art laid out as a
    /// folder per animation can be found by looking rather than by being
    /// listed somewhere. A missing folder comes back empty.
    /// </summary>
    IReadOnlyList<string> Folders(string folder);

    /// <summary>Image formats the texture loader can read.</summary>
    public static readonly string[] ImageTypes = { ".png", ".jpg", ".jpeg", ".bmp" };
}
