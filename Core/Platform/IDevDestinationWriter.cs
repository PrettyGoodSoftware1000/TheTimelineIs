namespace TheTimelineIs.Core.Platform;

/// <summary>
/// Dev-mode only: appends a row to the source-tree destinations.txt.
/// Desktop-only by design — it writes into the repo, which doesn't exist
/// on a tablet install.
/// </summary>
public interface IDevDestinationWriter
{
    /// <returns>The path written to, for the on-screen confirmation.</returns>
    string Append(string name, int x, int y, string mission);
}
