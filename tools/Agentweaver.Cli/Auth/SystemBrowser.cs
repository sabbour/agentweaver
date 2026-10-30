using System.ComponentModel;
using System.Diagnostics;

namespace Agentweaver.Cli.Auth;

internal interface ICliSystemBrowser
{
    Task OpenAsync(Uri authorizationUri, CancellationToken ct);
}

internal sealed class SystemBrowser : ICliSystemBrowser
{
    public Task OpenAsync(Uri authorizationUri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (command, arguments) = OperatingSystem.IsWindows()
            ? ("rundll32.exe", new[] { "url.dll,FileProtocolHandler", authorizationUri.AbsoluteUri })
            : OperatingSystem.IsMacOS()
                ? ("open", new[] { authorizationUri.AbsoluteUri })
                : OperatingSystem.IsLinux()
                    ? ("xdg-open", new[] { authorizationUri.AbsoluteUri })
                    : throw new PlatformNotSupportedException("No supported system browser launcher is available.");

        var startInfo = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The system browser did not start.");
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or IOException)
        {
            throw new InvalidOperationException("Could not open the system browser for sign-in.", exception);
        }
    }
}
