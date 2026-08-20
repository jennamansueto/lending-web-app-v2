namespace Contoso.Lending.Workflow;

public static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var command = args.FirstOrDefault() ?? "worker";
        var rest = args.Skip(1).ToArray();
        switch (command)
        {
            case "worker":
                await WorkerHost.RunAsync(rest).ConfigureAwait(false);
                return 0;
            case "demo":
                return await DemoRunner.RunAsync(rest).ConfigureAwait(false);
            case "payoff":
                return await DemoRunner.RunPayoffAsync(rest).ConfigureAwait(false);
            default:
                Console.Error.WriteLine(
                    """
                    usage:
                      worker                          host the Temporal worker (default)
                      demo [--scenarios <path>] [--artifact <path>]
                                                        run scenarios and write structured results
                      payoff --loan-id <id> [--as-of <yyyy-MM-dd>]
                    """);
                return 2;
        }
    }

    internal static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
