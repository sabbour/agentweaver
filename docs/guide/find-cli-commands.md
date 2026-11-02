# Find CLI commands without signing in

Use local help to find the Agentweaver CLI command groups and options before
you configure a target. You do not need to sign in or connect to a Gateway
to read help or version information.

This procedure uses the Windows executable built from the v1 source.

## Before you begin

Publish the Windows CLI by following
[Build and run from source](./cli.md#build-and-run-from-source). That procedure
requires the .NET 10 SDK and writes the executable to
`artifacts\cli\win-x64\publish\agentweaver.exe`.

Open PowerShell at the repository root. You do not need to put the executable
on `PATH` when you invoke it by the path below.

## Read local help

1. Display the command list:

   ```powershell
   .\artifacts\cli\win-x64\publish\agentweaver.exe --help
   ```

   The output starts with `Agentweaver 1.x CLI` and includes `Usage:`.
   It lists the `auth`, `api`, `rest`, and `test` command groups with their
   options.

2. Display version and source metadata:

   ```powershell
   .\artifacts\cli\win-x64\publish\agentweaver.exe --version
   ```

   The version line includes the CLI version, source and tree metadata.
   `unbound` means that source metadata was not embedded in that build.

## Verify the result

Both commands print information and exit successfully. They do not open a
sign-in browser, call a Gateway, or run an API test. Local help does not tell
you which services or API operations are available on a target.

If PowerShell cannot find the executable, check that you are at the repository
root and that the publish step completed at the documented output path.
If the file is absent, return to the build procedure rather than attempting
sign-in or changing target settings.

## Next step

Follow [Configure a target](./cli.md#configure-a-target), then
[discover API operations](./cli.md#discover-and-invoke-api-operations) from that
Gateway. Unlike local help, API discovery contacts the configured Gateway.
