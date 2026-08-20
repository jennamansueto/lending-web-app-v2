using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

// Parity dashboard: runs parity/run-all.sh on demand, streams progress over SSE and
// serves the structured results written to parity/parity-dashboard.json.
// It contains no business logic and no parity assertions of its own: every verdict it
// shows comes from the parity harness.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ParityRunner>();
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/run", (ParityRunner runner, string? levels) =>
    runner.TryStart(levels) ? Results.Accepted() : Results.Conflict(new { error = "a run is already in progress" }));

app.MapGet("/api/results", (ParityRunner runner) =>
    runner.LastResults is null ? Results.NoContent() : Results.Content(runner.LastResults, "application/json"));

app.MapGet("/api/status", (ParityRunner runner) => Results.Json(new { running = runner.IsRunning }));

app.MapGet("/api/events", async (ParityRunner runner, HttpContext http, CancellationToken ct) =>
{
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    var reader = runner.Subscribe(out var replay);
    foreach (var evt in replay)
    {
        await Write(http, evt, ct);
    }

    try
    {
        await foreach (var evt in reader.ReadAllAsync(ct))
        {
            await Write(http, evt, ct);
        }
    }
    catch (OperationCanceledException)
    {
        // client went away
    }

    static async Task Write(HttpContext http, string payload, CancellationToken ct)
    {
        await http.Response.WriteAsync($"data: {payload}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
});

app.Run();

internal sealed class ParityRunner
{
    private static readonly string[] AllLevels = ["L2", "L3", "L4"];
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly List<Channel<string>> _subscribers = [];
    private readonly List<string> _log = [];
    private readonly object _gate = new();
    private readonly ILogger<ParityRunner> _logger;
    private readonly string _repoRoot;

    private int _running;

    public ParityRunner(ILogger<ParityRunner> logger)
    {
        _logger = logger;
        _repoRoot = FindRepoRoot();
        var artifact = Path.Combine(_repoRoot, "parity", "parity-dashboard.json");
        if (File.Exists(artifact))
        {
            LastResults = File.ReadAllText(artifact);
        }
    }

    public string? LastResults { get; private set; }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public ChannelReader<string> Subscribe(out IReadOnlyList<string> replay)
    {
        var channel = Channel.CreateUnbounded<string>();
        lock (_gate)
        {
            _subscribers.Add(channel);
            replay = _log.ToArray();
        }
        return channel.Reader;
    }

    public bool TryStart(string? levels)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return false;
        }

        var requested = (levels ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => AllLevels.Contains(l, StringComparer.OrdinalIgnoreCase))
            .Select(l => l.ToUpperInvariant())
            .ToArray();
        if (requested.Length == 0)
        {
            requested = AllLevels;
        }

        _ = Task.Run(() => RunAsync(requested));
        return true;
    }

    private async Task RunAsync(string[] levels)
    {
        lock (_gate)
        {
            _log.Clear();
        }

        Emit(new { type = "run-started", levels, startedAt = DateTime.UtcNow });
        foreach (var level in levels)
        {
            Emit(new { type = "level", id = level, status = "pending" });
        }

        var script = Path.Combine(_repoRoot, "parity", "run-all.sh");
        var stopwatch = Stopwatch.StartNew();
        var exitCode = -1;

        try
        {
            var psi = new ProcessStartInfo("/bin/bash", $"\"{script}\"")
            {
                WorkingDirectory = _repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.Environment["PARITY_LEVELS"] = string.Join(',', levels);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"could not start {script}");

            var stderr = process.StandardError.ReadToEndAsync();
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                // run-all.sh emits "##parity <level> start|exit-ok|exit-fail" progress
                // markers; they move the UI from pending to running to a provisional
                // verdict. The authoritative statuses arrive with the results artifact.
                if (line.StartsWith("##parity ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 3 && levels.Contains(parts[1]))
                    {
                        var status = parts[2] switch
                        {
                            "start" => "running",
                            "exit-ok" => "pass",
                            "exit-fail" => "fail",
                            _ => null,
                        };
                        if (status is not null)
                        {
                            Emit(new { type = "level", id = parts[1], status });
                        }
                    }
                    continue;
                }

                Emit(new { type = "log", line });
            }

            var errors = await stderr;
            if (!string.IsNullOrWhiteSpace(errors))
            {
                foreach (var line in errors.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    Emit(new { type = "log", line = line.TrimEnd() });
                }
            }

            await process.WaitForExitAsync();
            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "parity run failed to execute");
            Emit(new { type = "log", line = $"dashboard: {ex.Message}" });
        }

        var artifact = Path.Combine(_repoRoot, "parity", "parity-dashboard.json");
        JsonNode? results = null;
        if (File.Exists(artifact))
        {
            LastResults = await File.ReadAllTextAsync(artifact);
            results = JsonNode.Parse(LastResults);
        }

        Emit(new
        {
            type = "run-finished",
            exitCode,
            durationMs = stopwatch.ElapsedMilliseconds,
            results,
        });

        Volatile.Write(ref _running, 0);
    }

    private void Emit(object payload)
    {
        var json = JsonSerializer.Serialize(payload, Json);
        lock (_gate)
        {
            _log.Add(json);
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(json);
            }
        }
    }

    private static string FindRepoRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("PARITY_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot) && IsRepoRoot(explicitRoot))
        {
            return Path.GetFullPath(explicitRoot);
        }

        var argRoot = ReadOption(Environment.GetCommandLineArgs(), "--repo-root");
        if (!string.IsNullOrWhiteSpace(argRoot) && IsRepoRoot(argRoot))
        {
            return Path.GetFullPath(argRoot);
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }
        if (dir is not null)
        {
            return dir.FullName;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root. Set PARITY_REPO_ROOT or pass --repo-root <path>.");
    }

    private static bool IsRepoRoot(string path) =>
        Directory.Exists(Path.Combine(Path.GetFullPath(path), ".git"));

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{name} requires a path");
                }

                return args[i + 1];
            }
        }

        return null;
    }
}
