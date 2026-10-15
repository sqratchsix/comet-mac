namespace Comet.Core;

/// <summary>
/// Lists the command files offered under Load Commands in the history pane's
/// menu, the counterpart of the Windows build's Load submenu, which listed every
/// .txt file in and below the folder COMET.exe ran from.
///
/// On macOS that folder can be /Applications, so the walk is bounded: it skips
/// app bundles and hidden folders, stops a few levels down, and caps the count.
/// </summary>
public static class CommandFileFinder
{
    public const int DefaultMaxDepth = 3;
    public const int DefaultMaxFiles = 200;

    public static IReadOnlyList<string> Find(string folder, int maxDepth = DefaultMaxDepth,
        int maxFiles = DefaultMaxFiles)
    {
        var found = new List<string>();
        if (Directory.Exists(folder)) Walk(folder, 0, maxDepth, maxFiles, found);

        IComparer<string> comparer = new NaturalComparer();
        found.Sort(comparer);
        return found;
    }

    private static void Walk(string folder, int depth, int maxDepth, int maxFiles, List<string> found)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.txt"))
            {
                if (found.Count >= maxFiles) return;
                if (!Path.GetFileName(file).StartsWith('.')) found.Add(file);
            }

            if (depth >= maxDepth) return;
            foreach (var sub in Directory.EnumerateDirectories(folder))
            {
                if (found.Count >= maxFiles) return;
                string name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
                Walk(sub, depth + 1, maxDepth, maxFiles, found);
            }
        }
        catch (Exception)
        {
            // Unreadable folder: offer what was found so far.
        }
    }
}
