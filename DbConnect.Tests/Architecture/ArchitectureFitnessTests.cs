using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DbConnect.Services;
using Xunit;
using Xunit.Sdk;

namespace DbConnect.Tests.Architecture;

/// <summary>
/// Ratchet tests against god classes. Every file and type gets a default cap; the ones already past it
/// are listed in <see cref="ArchitectureBudgets"/> at their current size and may not grow. When one
/// shrinks, its budget has to come down with it, so a refactor's gains can't be quietly spent again.
/// A failure means: extract the new behaviour into its own class rather than raising the budget.
/// </summary>
public class ArchitectureFitnessTests
{
    private static readonly string[] ProviderNames = ["PostgreSQL", "SQL Server", "MySQL", "SQLite", "DuckDB"];

    [Fact]
    public void SourceFiles_StayWithinTheirLineBudgets()
    {
        var actual = SourceFiles("DbConnect").ToDictionary(RelativePath, path => File.ReadLines(path).Count(), StringComparer.Ordinal);

        AssertWithinBudgets("lines", actual, ArchitectureBudgets.FileLines, ArchitectureBudgets.DefaultFileLines,
            ArchitectureBudgets.FileLinesSlack);
    }

    [Fact]
    public void Types_StayWithinTheirMethodBudgets()
    {
        var actual = typeof(IDatabaseProvider).Assembly.GetTypes()
            .Where(type => !type.IsInterface && !IsCompilerGenerated(type))
            .ToDictionary(type => type.FullName!, CountHandWrittenMethods, StringComparer.Ordinal);

        AssertWithinBudgets("methods", actual, ArchitectureBudgets.TypeMethods, ArchitectureBudgets.DefaultTypeMethods,
            ArchitectureBudgets.TypeMethodsSlack);
    }

    [Fact]
    public void ProviderInterface_DoesNotGrow()
    {
        // New provider behaviour belongs on a smaller, focused interface, not on IDatabaseProvider.
        var members = typeof(IDatabaseProvider).GetMembers().Length;

        AssertWithinBudgets("interface members",
            new Dictionary<string, int> { [nameof(IDatabaseProvider)] = members },
            new Dictionary<string, int> { [nameof(IDatabaseProvider)] = ArchitectureBudgets.ProviderInterfaceMembers },
            defaultBudget: 0, slack: 0);
    }

    [Fact]
    public void ProviderRoles_StayFocused()
    {
        // Count inherited members too, so another aggregate cannot hide a growing role.
        Type[] roles = [typeof(IDatabaseQueryProvider), typeof(IDatabaseSchemaProvider),
            typeof(IDatabaseRoutineProvider), typeof(IDatabaseAdministrationProvider)];

        foreach (var role in roles)
        {
            var members = role.GetInterfaces().Append(role).SelectMany(type => type.GetMembers()).Distinct();
            Assert.True(members.Count() <= 20, $"{role.Name} exceeds 20 members; extract a focused role.");
        }
    }

    [Fact]
    public void ViewModelsAndViews_DoNotNameProviders()
    {
        // Provider-specific behaviour lives in providers or capability flags, never in view models.
        var pattern = new Regex("\"(" + string.Join("|", ProviderNames.Select(Regex.Escape)) + ")\"");
        var actual = SourceFiles(Path.Combine("DbConnect", "ViewModels")).Concat(SourceFiles(Path.Combine("DbConnect", "Views")))
            .ToDictionary(RelativePath, path => pattern.Matches(File.ReadAllText(path)).Count, StringComparer.Ordinal);

        AssertWithinBudgets("provider-name literals", actual, ArchitectureBudgets.ProviderNameLiterals, defaultBudget: 0, slack: 0);
    }

    /// <summary>
    /// Fails when anything is over its budget (or the default for unlisted entries), or when a listed budget
    /// is looser than needed by more than <paramref name="slack"/>, listing every entry to fix.
    /// </summary>
    private static void AssertWithinBudgets(
        string metric,
        IReadOnlyDictionary<string, int> actual,
        IReadOnlyDictionary<string, int> budgets,
        int defaultBudget,
        int slack)
    {
        var problems = new List<string>();

        foreach (var (name, value) in actual.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var hasBudget = budgets.TryGetValue(name, out var budget);
            var limit = hasBudget ? budget : defaultBudget;
            if (value > limit)
            {
                problems.Add(hasBudget
                    ? $"{name}: {value} {metric}, budget {budget}. It may not grow; move the new behaviour into its own class."
                    : $"{name}: {value} {metric}, default cap {defaultBudget}. Split it rather than adding a budget.");
            }
            else if (hasBudget && value <= defaultBudget)
            {
                problems.Add($"{name}: {value} {metric} is within the default cap {defaultBudget}; remove its budget.");
            }
            else if (hasBudget && budget - value > slack)
            {
                problems.Add($"{name}: shrank to {value} {metric}; lower its budget from {budget} to {value}.");
            }
        }

        foreach (var name in budgets.Keys.Where(name => !actual.ContainsKey(name)).Order(StringComparer.Ordinal))
            problems.Add($"{name}: no longer exists; remove its {metric} budget.");

        if (problems.Count > 0)
            throw new XunitException($"Architecture budget ({metric}) failed:{Environment.NewLine}" + string.Join(Environment.NewLine, problems));
    }

    /// <summary>Methods the type declares itself, excluding accessors, lambdas/local functions and source-generated code.</summary>
    private static int CountHandWrittenMethods(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Concat<MethodBase>(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .Count(method => !method.IsSpecialName &&
                             !method.Name.StartsWith('<') &&
                             !method.IsDefined(typeof(CompilerGeneratedAttribute)) &&
                             !method.IsDefined(typeof(GeneratedCodeAttribute)));

    private static bool IsCompilerGenerated(Type type) =>
        type.Name.Contains('<') ||
        type.IsDefined(typeof(CompilerGeneratedAttribute)) ||
        type.IsDefined(typeof(GeneratedCodeAttribute)) ||
        type.FullName is null;

    private static IEnumerable<string> SourceFiles(string relativeDirectory)
    {
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(RepoRoot, relativeDirectory), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}obj{separator}") && !path.Contains($"{separator}bin{separator}"));
    }

    private static string RelativePath(string path) => Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');

    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DbConnect.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("DbConnect.slnx was not found above the test output directory.");
    }
}
