using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Platform.Api.Tests.Architecture;

public sealed class ModuleDependencyArchitectureTests
{
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
        var files = Directory.EnumerateFiles(modulesRoot, "*.cs", SearchOption.AllDirectories).ToArray();
        var trees = files.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)).ToArray();
        var references = GetMetadataReferences();
        var compilation = CSharpCompilation.Create(
            "ModuleDependencyArchitectureScan",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var dependencies = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tree in trees)
        {
            var relativePath = Path.GetRelativePath(modulesRoot, tree.FilePath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var sourceModule = relativePath.Split('/')[0];
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var name in root.DescendantNodesAndSelf().OfType<NameSyntax>())
            {
                var symbol = semanticModel.GetSymbolInfo(name).Symbol;
                while (symbol is IAliasSymbol alias)
                {
                    symbol = alias.Target;
                }

                var targetNamespace = symbol switch
                {
                    INamespaceSymbol ns => ns,
                    ITypeSymbol type => type.ContainingNamespace,
                    _ => symbol?.ContainingNamespace
                };

                if (targetNamespace is null)
                {
                    continue;
                }

                var namespaceParts = targetNamespace.ToDisplayString().Split('.');
                if (namespaceParts.Length < 4 ||
                    namespaceParts[0] != "Platform" ||
                    namespaceParts[1] != "Api" ||
                    namespaceParts[2] != "Modules")
                {
                    continue;
                }

                var targetModule = namespaceParts[3];
                if (!knownModules.Contains(targetModule) || targetModule == sourceModule)
                {
                    continue;
                }

                if (namespaceParts.Length >= 5 && namespaceParts[4] == "Contracts")
                {
                    continue;
                }

                dependencies.Add($"{sourceModule} -> {targetModule} : Platform.Api/Modules/{relativePath}");
            }
        }

        return dependencies;
    }

    private static IEnumerable<MetadataReference> GetMetadataReferences()
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator)
            .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return paths.Select(path => MetadataReference.CreateFromFile(path));
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
