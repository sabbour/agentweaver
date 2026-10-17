using System.Text.Json;

namespace Agentweaver.Identity.Broker.Tests;

/// <summary>Loads the checked-in published pricing snapshot for Events integration hosts.</summary>
internal static class PublishedCopilotRateCardFixture
{
    private static readonly (string RateCardJson, string OptionsRevision) Configuration = Load();

    public static string RateCardJson => Configuration.RateCardJson;
    public static string OptionsRevision => Configuration.OptionsRevision;

    private static (string RateCardJson, string OptionsRevision) Load()
    {
        foreach (var startingPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(startingPath); directory is not null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "deploy", "k8s", "runtime-configs",
                    "events-and-sessions-runtime-config.json");
                if (!File.Exists(path))
                    continue;

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var data = document.RootElement.GetProperty("data");
                var rateCardJson = data.GetProperty("EventsAndSessions__Cost__Copilot__RateCard__Json").GetString();
                var optionsRevision = data.GetProperty("EventsAndSessions__Cost__Copilot__OptionsRevision").GetString();
                if (string.IsNullOrWhiteSpace(rateCardJson) || string.IsNullOrWhiteSpace(optionsRevision))
                    throw new InvalidDataException("The published Events rate card configuration is incomplete.");
                return (
                    rateCardJson,
                    optionsRevision);
            }
        }

        throw new InvalidOperationException("Could not find the published Events rate card for integration tests.");
    }
}
