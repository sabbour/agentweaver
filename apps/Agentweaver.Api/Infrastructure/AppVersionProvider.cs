namespace Agentweaver.Api.Infrastructure;

public interface IAppVersionProvider
{
    string Version { get; }
}

public class AppVersionProvider : IAppVersionProvider
{
    public string Version { get; }

    public AppVersionProvider(IWebHostEnvironment env)
    {
        // Try reading VERSION file from content root or repo root
        var versionFile = Path.Combine(env.ContentRootPath, "VERSION");
        if (!File.Exists(versionFile))
            versionFile = Path.Combine(env.ContentRootPath, "..", "VERSION");
        if (!File.Exists(versionFile))
            versionFile = Path.Combine(env.ContentRootPath, "..", "..", "VERSION");

        Version = File.Exists(versionFile)
            ? File.ReadAllText(versionFile).Trim()
            : "0.0.0";
    }
}
