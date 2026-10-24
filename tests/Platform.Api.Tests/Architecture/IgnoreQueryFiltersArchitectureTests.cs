using System.Text.RegularExpressions;

namespace Platform.Api.Tests.Architecture;

public sealed partial class IgnoreQueryFiltersArchitectureTests
{
    private static readonly string[] SourceRoots = ["Platform.Api", "Core"];

    [Fact]
    public void IgnoreQueryFilters_is_only_used_in_baseline_files()
    {
        var repositoryRoot = FindRepositoryRoot();
        var baselinePath = Path.Combine(
            repositoryRoot,
            "tests",
            "Platform.Api.Tests",
            "Architecture",
            "ignore-query-filters-baseline.txt");

        var actual = SourceRoots
            .SelectMany(root => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, root), "*.cs", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(repositoryRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !path.Contains("/bin/", StringComparison.Ordinal)
                && !path.Contains("/obj/", StringComparison.Ordinal))
            .Where(path => IgnoreQueryFiltersCall().IsMatch(File.ReadAllText(Path.Combine(repositoryRoot, path))))
            .ToHashSet(StringComparer.Ordinal);
        var baseline = File.ReadAllLines(baselinePath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        var added = actual.Except(baseline, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var resolved = baseline.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            added.Length == 0 && resolved.Length == 0,
            string.Join(
                Environment.NewLine,
                [
                    "IgnoreQueryFilters baseline changed (ratchet: the list may only shrink).",
                    .. added.Select(item => $"  + {item} (new IgnoreQueryFilters: use the tenant filter or a reviewed PlatformScope path)"),
                    .. resolved.Select(item => $"  - {item} (no longer uses IgnoreQueryFilters: delete this line)"),
                    $"Baseline: {Path.GetRelativePath(repositoryRoot, baselinePath)}",
                ]));
    }

    [GeneratedRegex(@"\.IgnoreQueryFilters\s*\(")]
    private static partial Regex IgnoreQueryFiltersCall();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Platform.Api", "Modules")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests", "Platform.Api.Tests")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root containing Platform.Api/Modules and tests/Platform.Api.Tests.");
    }
}
