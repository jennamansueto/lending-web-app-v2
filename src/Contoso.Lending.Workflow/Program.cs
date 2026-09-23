using System.Text.Json;
using Contoso.Lending.Workflow;
using Temporalio.Client;
using Temporalio.Worker;

// Two verbs, one host process:
//   worker   (default) run the origination worker against the Temporal dev server
//   start    start one workflow from a JSON input file and write its result as JSON
//
// Environment: TEMPORAL_ADDRESS (default localhost:7233), TEMPORAL_NAMESPACE (default default),
// SERVICE_API_URL (default http://localhost:5080).

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
string address = Environment.GetEnvironmentVariable("TEMPORAL_ADDRESS") ?? "localhost:7233";
string ns = Environment.GetEnvironmentVariable("TEMPORAL_NAMESPACE") ?? "default";
string verb = args.Length == 0 ? "worker" : args[0];

switch (verb)
{
    case "worker":
        await RunWorkerAsync();
        break;
    case "start":
        await StartWorkflowAsync();
        break;
    default:
        Console.Error.WriteLine($"usage: dotnet run --project src/Contoso.Lending.Workflow -- [worker|start --input <file> --out <file>]");
        return 2;
}

return 0;

async Task RunWorkerAsync()
{
    var client = await TemporalClient.ConnectAsync(new(address) { Namespace = ns });
    using var api = new ServiceApiClient(ServiceApiClient.BaseAddressFromEnvironment());
    using var worker = new TemporalWorker(
        client,
        new TemporalWorkerOptions(LoanOriginationWorkflow.TaskQueue)
            .AddAllActivities(new OriginationActivities(api))
            .AddWorkflow<LoanOriginationWorkflow>());

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        stop.Cancel();
    };
    Console.WriteLine($"worker: task queue '{LoanOriginationWorkflow.TaskQueue}' on {address}/{ns}, service {ServiceApiClient.BaseAddressFromEnvironment()}");
    try
    {
        await worker.ExecuteAsync(stop.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("worker: stopped");
    }
}

async Task StartWorkflowAsync()
{
    string? inputPath = ArgValue("--input");
    string? outPath = ArgValue("--out");
    string workflowId = ArgValue("--id") ?? $"origination-{Guid.NewGuid():N}";
    string? reviewer = ArgValue("--reviewer");
    if (inputPath is null)
    {
        throw new ArgumentException("start requires --input <file>");
    }

    var input = JsonSerializer.Deserialize<LoanOriginationInput>(await File.ReadAllTextAsync(inputPath), json)
        ?? throw new ArgumentException($"{inputPath} did not contain a workflow input");

    var client = await TemporalClient.ConnectAsync(new(address) { Namespace = ns });
    var handle = await client.StartWorkflowAsync(
        (LoanOriginationWorkflow wf) => wf.RunAsync(input),
        new(id: workflowId, taskQueue: LoanOriginationWorkflow.TaskQueue));

    bool reviewerRecorded = false;
    if (reviewer is not null)
    {
        try
        {
            await handle.SignalAsync(wf => wf.RecordReviewerAsync(reviewer));
            reviewerRecorded = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"signal not delivered ({ex.GetType().Name}); the workflow had already closed");
        }
    }

    var result = await handle.GetResultAsync();
    var status = await handle.QueryAsync(wf => wf.GetStatus());
    var run = new
    {
        workflowId,
        runId = handle.ResultRunId,
        reviewerRecorded,
        status,
        result,
    };

    string rendered = JsonSerializer.Serialize(run, json);
    if (outPath is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, rendered + Environment.NewLine);
    }
    Console.WriteLine(rendered);
}

string? ArgValue(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
