using System.Reflection;

namespace Contoso.Lending.ArchTests;

/// <summary>Locations the purity test reads, injected by MSBuild so the test is run-directory independent.</summary>
internal static class RepoPaths
{
    internal static string Root { get; } = Metadata("RepoRoot");

    internal static string BuildConfiguration { get; } = Metadata("BuildConfiguration");

    internal static string WorkflowProjectDir => Path.Combine(Root, "src", "Contoso.Lending.Workflow");

    internal static string WorkflowOutputDir =>
        Path.Combine(WorkflowProjectDir, "bin", BuildConfiguration, "net8.0");

    private static string Metadata(string key) =>
        typeof(RepoPaths).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value
        ?? throw new InvalidOperationException($"assembly metadata '{key}' was not set by the build");
}
