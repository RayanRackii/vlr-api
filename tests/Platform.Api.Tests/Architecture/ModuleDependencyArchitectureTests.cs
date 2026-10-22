using System.Text.RegularExpressions;

namespace Platform.Api.Tests.Architecture;

public sealed class ModuleDependencyArchitectureTests
{
    private static readonly Regex ModuleReference = new(
        @"\bPlatform\s*\.\s*Api\s*\.\s*Modules\s*\.\s*(?<module>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void Module_references_match_the_versioned_baseline()
    {
        var repositoryRoot = FindRepositoryRoot();
        var modulesRoot = Path.Combine(repositoryRoot, "Platform.Api", "Modules");
        var baselinePath = Path.Combine(
            repositoryRoot,
            "tests",
            "Platform.Api.Tests",
            "Architecture",
            "module-dependency-baseline.txt");
        var knownModules = Directory.GetDirectories(modulesRoot)
            .Select(directory => Path.GetFileName(directory)!)
            .ToHashSet(StringComparer.Ordinal);

        var actual = ScanModuleReferences(modulesRoot, knownModules);
        var baseline = File.ReadAllLines(baselinePath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        var added = actual.Except(baseline, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var resolved = baseline.Except(actual, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.True(
            added.Length == 0 && resolved.Length == 0,
            BuildFailureMessage(added, resolved, baselinePath));
    }

    private static HashSet<string> ScanModuleReferences(string modulesRoot, HashSet<string> knownModules)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sourcePath in Directory.EnumerateFiles(modulesRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(modulesRoot, sourcePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var sourceModule = relativePath.Split('/')[0];
            var source = RemoveCommentsAndLiterals(File.ReadAllText(sourcePath));

            foreach (Match match in ModuleReference.Matches(source))
            {
                var targetModule = match.Groups["module"].Value;
                if (!knownModules.Contains(targetModule) || targetModule == sourceModule)
                {
                    continue;
                }

                var suffix = source[(match.Index + match.Length)..];
                if (Regex.IsMatch(suffix, @"^\s*\.\s*Contracts\b", RegexOptions.CultureInvariant))
                {
                    continue;
                }

                references.Add($"{sourceModule} -> {targetModule} : Platform.Api/Modules/{relativePath}");
            }
        }

        return references;
    }

    private static string RemoveCommentsAndLiterals(string source)
    {
        // Keep offsets stable so the module name can be checked against its following namespace segment.
        return Regex.Replace(
            source,
            @"//[^\r\n]*|/\*[\s\S]*?\*/|\$?@?""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'",
            match => new string(' ', match.Length),
            RegexOptions.CultureInvariant);
    }

    private static string BuildFailureMessage(string[] added, string[] resolved, string baselinePath)
    {
        var lines = new List<string>
        {
            "Module dependency architecture baseline changed.",
            "Only Platform.Api.Modules.<Target>.Contracts references are allowed across modules.",
        };

        if (added.Length > 0)
        {
            lines.Add("New violations (remove the dependency or deliberately add these lines to the baseline):");
            lines.AddRange(added.Select(item => $"  + {item}"));
        }

        if (resolved.Length > 0)
        {
            lines.Add("Resolved baseline entries (delete these stale lines to shrink the baseline):");
            lines.AddRange(resolved.Select(item => $"  - {item}"));
        }

        lines.Add($"Baseline: {Path.GetRelativePath(FindRepositoryRoot(), baselinePath)}");
        return string.Join(Environment.NewLine, lines);
    }

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
