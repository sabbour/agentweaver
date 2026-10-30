# Quickstart: build the Agentweaver 1.x source

In this quickstart, you restore the v1 dependencies, build the solution, and
run local source checks. These steps do not deploy Azure resources, publish
packages or images, or start a product platform.

## Before you begin

Install:

- .NET 10 SDK, as selected by `global.json`.
- Node.js 24 and npm.
- Git with the v1 source checkout.

PostgreSQL integration tests use Testcontainers and require a container engine
and permission to pull the test image. This quickstart runs source checks that
do not require a cloud subscription.

## Build the source

1. Open PowerShell at the repository root.
2. Install the locked Node dependencies:

   ```powershell
   npm ci
   ```

3. Restore the .NET solution in locked mode:

   ```powershell
   dotnet restore Agentweaver.slnx --locked-mode
   ```

4. Build the solution:

   ```powershell
   dotnet build Agentweaver.slnx --no-restore --configuration Release
   ```

5. Run the workflow and Azure source checks:

   ```powershell
   npm run test:release
   npm run test:azure
   ```

## Verify the result

The .NET build completes, and each Node command exits successfully. These
results validate source contracts and local tooling. They do not prove that
Azure resources exist, a Kubernetes workload is running, a provider can reach
its resource, or a user workflow succeeds.

To continue, read [What Agentweaver 1.x can do](../experience/00-overview.md),
then follow [Configure providers](./configure-providers.md) or
[Deploy to Azure](./deploy-to-azure.md).

To build and run the standalone command-line client, including its
self-contained local publish command and distribution status, see the
[Agentweaver CLI guide](./cli.md).
