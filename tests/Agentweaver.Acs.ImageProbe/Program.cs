using System.Diagnostics;
using AgentControlSpecification;

var manifestPath = Path.Combine(AppContext.BaseDirectory, "manifest.yaml");
var probeNativeLibrary = Path.Combine(AppContext.BaseDirectory, "libagent_control_specification.so");
if (!File.Exists(manifestPath))
{
    throw new FileNotFoundException("The image probe manifest is missing.", manifestPath);
}

if (File.Exists(probeNativeLibrary))
{
    throw new InvalidOperationException("The probe must load the native library from the image, not its own output.");
}

var originalOpaPath = Environment.GetEnvironmentVariable("ACS_OPA_PATH");
try
{
    var control = AgentControl.FromPath(manifestPath);
    var allowed = await control.EvaluatePreModelCallAsync(
        new { block = false, messages = new[] { new { role = "user", content = "hello" } } });
    Require(allowed.Verdict.Decision == Decision.Allow, "OPA allow evaluation did not allow.");

    var denied = await control.EvaluatePreModelCallAsync(
        new { block = true, messages = new[] { new { role = "user", content = "blocked" } } });
    Require(denied.Verdict.Decision == Decision.Deny, "OPA deny evaluation did not deny.");
    Require(denied.Verdict.Reason == "acs_image_probe_denied", "OPA deny reason did not round-trip.");

    Environment.SetEnvironmentVariable("ACS_OPA_PATH", "/app/acs-opa-missing");
    var failingControl = AgentControl.FromPath(manifestPath);
    var failed = await failingControl.EvaluatePreModelCallAsync(
        new { block = false, messages = new[] { new { role = "user", content = "error" } } });
    Require(failed.Verdict.Decision == Decision.Deny, "OPA execution failure did not deny.");
    Require(
        failed.Verdict.Reason == "runtime_error:policy_invocation_failed",
        "OPA execution failure used an unexpected reason.");

    if (!OperatingSystem.IsLinux())
    {
        throw new PlatformNotSupportedException("The ACS image probe requires Linux.");
    }

    Require(File.Exists("/bin/sh") && File.Exists("/bin/sleep"), "The timeout probe needs /bin/sh and /bin/sleep.");

    var probeDirectory = Environment.GetEnvironmentVariable("ACS_PROBE_TEMP_DIRECTORY")
        ?? throw new InvalidOperationException("The image probe needs a writable probe directory.");
    var slowOpaPath = Path.Combine(probeDirectory, $"acs-opa-timeout-{Guid.NewGuid():N}.sh");
    var startedMarkerPath = $"{slowOpaPath}.started";
    try
    {
        File.WriteAllText(slowOpaPath, "#!/bin/sh\n: > \"$0.started\"\nexec /bin/sleep 35\n");
        File.SetUnixFileMode(
            slowOpaPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Environment.SetEnvironmentVariable("ACS_OPA_PATH", slowOpaPath);
        var timeoutControl = AgentControl.FromPath(manifestPath);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stopwatch = Stopwatch.StartNew();
        var timedOut = await timeoutControl.EvaluatePreModelCallAsync(
            new { block = false, messages = new[] { new { role = "user", content = "timeout" } } },
            cancellationToken: watchdog.Token);
        stopwatch.Stop();
        Require(File.Exists(startedMarkerPath), "OPA timeout script did not start.");
        Require(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "OPA timeout exceeded the probe watchdog.");
        Require(timedOut.Verdict.Decision == Decision.Deny, "OPA timeout did not deny.");
        Require(
            timedOut.Verdict.Reason == "runtime_error:policy_invocation_failed",
            "OPA timeout used an unexpected reason.");
    }
    catch (OperationCanceledException error)
    {
        throw new InvalidOperationException("OPA did not return a timeout denial within 30 seconds.", error);
    }
    finally
    {
        File.Delete(slowOpaPath);
        File.Delete(startedMarkerPath);
        Environment.SetEnvironmentVariable("ACS_OPA_PATH", originalOpaPath);
    }
}
finally
{
    Environment.SetEnvironmentVariable("ACS_OPA_PATH", originalOpaPath);
}

Console.WriteLine("ACS image probe passed: native load, OPA allow, deny, error deny, and timeout deny.");

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
