namespace PacketReady.Tests.Infrastructure.Extraction;

/// <summary>
/// Shared filesystem helpers for the extraction tests. The eval dataset lives
/// outside the test bin dir; tests anchor on a source-controlled directory
/// marker instead of brittle relative paths.
/// </summary>
internal static class TestPaths
{
    /// <summary>
    /// Walks up from the test assembly's directory until <c>evals/dataset/</c>
    /// is found. Test runners change CWD; <c>AppContext.BaseDirectory</c> is
    /// reliable.
    /// </summary>
    public static string LocateDatasetRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "evals", "dataset");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate evals/dataset/ — extraction tests assume they run inside the PacketReady repo.");
    }
}
