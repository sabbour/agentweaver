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
}
finally
{
    Environment.SetEnvironmentVariable("ACS_OPA_PATH", originalOpaPath);
}

Console.WriteLine("ACS image probe passed: native load, OPA allow, OPA deny, and OPA error deny.");

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
