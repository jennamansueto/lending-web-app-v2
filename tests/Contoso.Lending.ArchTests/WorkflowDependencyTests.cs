using System.Text.Json;
using System.Text.RegularExpressions;
using Contoso.Lending.Workflow;

namespace Contoso.Lending.ArchTests;

/// <summary>
/// Layer 3 may depend on Temporal and the BCL, and on nothing that knows a business rule or how
/// to reach a database. Enforced against the compiled assembly, not the source, so a dependency
/// added anywhere in the graph is caught.
/// </summary>
public class WorkflowDependencyTests
{
    private static readonly Regex Forbidden = new(
        @"^(Contoso\.Lending\.Domain|Contoso\.Lending\.Api|Npgsql.*|Oracle\..*|System\.Data\.(SqlClient|Odbc|OleDb|Common)|Microsoft\.Data\..*|Microsoft\.EntityFrameworkCore.*|EntityFramework.*|Dapper.*|MySql.*|MongoDB\..*|StackExchange\.Redis)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void WorkflowAssembly_ReferencesNoDomainOrDataAccessAssembly()
    {
        var referenced = typeof(LoanOriginationWorkflow).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var offenders = referenced.Where(name => Forbidden.IsMatch(name)).ToList();

        Assert.True(
            offenders.Count == 0,
            $"Contoso.Lending.Workflow references forbidden assemblies: {string.Join(", ", offenders)}." +
            $" Layer 3 may only reach behavior through the service HTTP API." +
            $"{Environment.NewLine}referenced: {string.Join(", ", referenced)}");
    }

    [Fact]
    public void WorkflowDependencyGraph_ContainsNoDomainOrDatabasePackage()
    {
        string depsPath = Path.Combine(RepoPaths.WorkflowOutputDir, "Contoso.Lending.Workflow.deps.json");
        Assert.True(File.Exists(depsPath), $"{depsPath} is missing; build src/Contoso.Lending.Workflow first");

        using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
        var libraries = document.RootElement.GetProperty("libraries")
            .EnumerateObject()
            .Select(property => property.Name.Split('/')[0])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var offenders = libraries.Where(name => Forbidden.IsMatch(name)).ToList();

        Assert.True(
            offenders.Count == 0,
            $"the workflow dependency graph contains forbidden packages: {string.Join(", ", offenders)}." +
            $"{Environment.NewLine}graph: {string.Join(", ", libraries)}");
    }

    [Fact]
    public void WorkflowProject_HasNoProjectReferences()
    {
        string csproj = File.ReadAllText(
            Path.Combine(RepoPaths.WorkflowProjectDir, "Contoso.Lending.Workflow.csproj"));

        Assert.DoesNotContain("ProjectReference", csproj, StringComparison.Ordinal);
    }
}
