namespace Agentweaver.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        return await CliApplication.RunAsync(
            args,
            new CliRuntimeOptions(HttpClient: client),
            cancellation.Token).ConfigureAwait(false);
    }
}
