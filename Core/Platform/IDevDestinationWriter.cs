namespace TheTimelineIs.Core.Platform;

/// <summary>
/// Dev-mode only: writes back into the SOURCE content tree, so something set
/// while playing survives the next build.
///
/// Desktop-only by design — the repo does not exist on a tablet install.
/// </summary>
public interface IDevDestinationWriter
{
    /// <summary>
    /// Adds a pin. The name is also the level it opens, so there is nothing
    /// else to supply.
    /// </summary>
    /// <returns>The path written to, for the on-screen confirmation.</returns>
    string Append(string name, int x, int y);

    /// <summary>
    /// Replaces a content file outright, e.g. Config.txt after the scale menu
    /// has been used.
    /// </summary>
    /// <returns>The path written to, or null if it could not be written.</returns>
    string? Write(string contentPath, string text);
}
