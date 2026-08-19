using Contoso.Lending.Workflow.Activities;
using Contoso.Lending.Workflow.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Temporalio.Extensions.Hosting;

namespace Contoso.Lending.Workflow;

/// <summary>Task queue and endpoint configuration shared by the worker and the demo runner.</summary>
public static class WorkerConfig
{
    public const string TaskQueue = "lending-origination";

    public static string TemporalAddress =>
        Environment.GetEnvironmentVariable("TEMPORAL_ADDRESS") ?? "localhost:7233";

    public static string TemporalNamespace =>
        Environment.GetEnvironmentVariable("TEMPORAL_NAMESPACE") ?? "default";

    public static string ServiceBaseUrl =>
        Environment.GetEnvironmentVariable("LENDING_API_BASE") ?? "http://localhost:5080";
}

public static class WorkerHost
{
    public static Task RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services
            .AddHttpClient<LendingServiceActivities>(client =>
            {
                client.BaseAddress = new Uri(WorkerConfig.ServiceBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(20);
            });

        builder.Services
            .AddHostedTemporalWorker(
                WorkerConfig.TemporalAddress,
                WorkerConfig.TemporalNamespace,
                WorkerConfig.TaskQueue)
            .AddScopedActivities<LendingServiceActivities>()
            .AddWorkflow<LoanApplicationWorkflow>()
            .AddWorkflow<PayoffQuoteWorkflow>();

        return builder.Build().RunAsync();
    }
}
