using System.Reflection;
using System.Xml.Linq;
using Contoso.Lending.Workflow.Workflows;
using Xunit;

namespace Contoso.Lending.WorkflowTests;

/// <summary>
/// Executable enforcement of the layer-3 boundary from docs/architecture.md: the workflow
/// assembly may not reach business logic or a database, only the service API over HTTP.
///
/// Three independent checks, so a violation cannot slip through any one of them:
///   1. the project's declared PackageReference / ProjectReference set,
///   2. the compiled assembly's referenced assemblies (catches actual code use),
///   3. the assemblies that land in the build output (catches transitive package pulls).
/// </summary>
public class WorkflowPurityTests
{
    /// <summary>Forbidden reference name fragments, matched case-insensitively.</summary>
    private static readonly string[] Forbidden =
    [
        "Npgsql",
        "Oracle",
        "Dapper",
        "EntityFramework",
        "EntityFrameworkCore",
        "Contoso.Lending.Domain",
        "Contoso.Lending.Data",
        "Contoso.Lending.Migrator",
        "System.Data.SqlClient",
        "Microsoft.Data.SqlClient",
    ];

    private static readonly Assembly WorkflowAssembly = typeof(LoanApplicationWorkflow).Assembly;

    [Fact]
    public void ProjectFileDeclaresNoDomainDataOrDatabaseReference()
    {
        var project = XDocument.Load(WorkflowProjectPath());
        var references = project.Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .ToList();

        Assert.NotEmpty(references);
        var violations = references.Where(IsForbidden).ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void CompiledAssemblyReferencesNoDomainDataOrDatabaseAssembly()
    {
        var referenced = WorkflowAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.NotEmpty(referenced);
        var violations = referenced.Where(IsForbidden).ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void BuildOutputContainsNoDomainDataOrDatabaseAssembly()
    {
        var directory = Path.GetDirectoryName(WorkflowAssembly.Location)!;
        var violations = Directory.EnumerateFiles(directory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => IsForbidden(name!))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void WorkflowsReachTheServiceOnlyThroughActivities()
    {
        // Types the workflow assembly uses to talk out of process: HTTP only, no DB drivers.
        var namespaces = WorkflowAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.Contains("Temporalio", namespaces);
        Assert.DoesNotContain(namespaces, n => n.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsForbidden(string name) =>
        Forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));

    private static string WorkflowProjectPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "Contoso.Lending.Workflow",
                "Contoso.Lending.Workflow.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate Contoso.Lending.Workflow.csproj above " + AppContext.BaseDirectory);
    }
}
