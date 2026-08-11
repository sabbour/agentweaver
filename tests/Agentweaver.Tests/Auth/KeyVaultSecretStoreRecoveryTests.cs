using Azure;
using Agentweaver.Api.Auth;
using FluentAssertions;

namespace Agentweaver.Tests.Auth;

public sealed class KeyVaultSecretStoreRecoveryTests
{
    [Fact]
    public async Task SetSecretAsync_ActiveSecret_WritesWithoutRecovery()
    {
        var client = new FakeWriterClient
        {
            Set = (_, _, _) => Task.FromResult("v1"),
        };
        var writer = CreateWriter(client);

        var version = await writer.SetSecretAsync("secret-name", "new-value", CancellationToken.None);

        version.Should().Be("v1");
        client.RecoverCalls.Should().Be(0);
        client.ActivePollCalls.Should().Be(0);
    }

    [Fact]
    public async Task SetSecretAsync_DeletedButRecoverable_RecoversAndRotatesValue()
    {
        var setCalls = 0;
        var persistedValues = new List<string>();
        var client = new FakeWriterClient
        {
            Set = (_, value, _) =>
            {
                setCalls++;
                if (setCalls == 1)
                    throw DeletedButRecoverable();
                persistedValues.Add(value);
                return Task.FromResult("rotated-version");
            },
            Recover = (_, _) => Task.CompletedTask,
            IsActive = (_, _) => Task.FromResult(true),
        };
        var writer = CreateWriter(client);

        var version = await writer.SetSecretAsync(
            "secret-name",
            "fresh-credential",
            CancellationToken.None);

        version.Should().Be("rotated-version");
        client.RecoverCalls.Should().Be(1);
        persistedValues.Should().Equal("fresh-credential");
    }

    [Fact]
    public async Task SetSecretAsync_ConcurrentRecoveryConflict_WaitsForActiveSecret()
    {
        var setCalls = 0;
        var activePolls = 0;
        var client = new FakeWriterClient
        {
            Set = (_, _, _) =>
            {
                setCalls++;
                if (setCalls == 1)
                    throw DeletedButRecoverable();
                return Task.FromResult("v2");
            },
            Recover = (_, _) => throw Conflict(),
            IsActive = (_, _) => Task.FromResult(++activePolls >= 2),
        };
        var writer = CreateWriter(client);

        var version = await writer.SetSecretAsync("secret-name", "new-value", CancellationToken.None);

        version.Should().Be("v2");
        client.RecoverCalls.Should().Be(2);
        client.ActivePollCalls.Should().Be(2);
    }

    [Fact]
    public async Task SetSecretAsync_DeleteTransitionConflict_RecoversAndRetries()
    {
        var setCalls = 0;
        var client = new FakeWriterClient
        {
            Set = (_, _, _) =>
            {
                if (++setCalls == 1)
                    throw Conflict();
                return Task.FromResult("v2");
            },
            Recover = (_, _) => Task.CompletedTask,
            IsActive = (_, _) => Task.FromResult(true),
        };
        var writer = CreateWriter(client);

        var version = await writer.SetSecretAsync("secret-name", "new-value", CancellationToken.None);

        version.Should().Be("v2");
        client.RecoverCalls.Should().Be(1);
    }

    [Fact]
    public async Task SetSecretAsync_DeleteTransitionNotYetRecoverable_RetriesRecovery()
    {
        var setCalls = 0;
        var recoverCalls = 0;
        var activePolls = 0;
        var client = new FakeWriterClient
        {
            Set = (_, _, _) =>
            {
                if (++setCalls == 1)
                    throw Conflict();
                return Task.FromResult("v2");
            },
            Recover = (_, _) =>
            {
                if (++recoverCalls == 1)
                    throw NotFound();
                return Task.CompletedTask;
            },
            IsActive = (_, _) => Task.FromResult(++activePolls >= 2),
        };
        var writer = CreateWriter(client);

        var version = await writer.SetSecretAsync("secret-name", "new-value", CancellationToken.None);

        version.Should().Be("v2");
        client.RecoverCalls.Should().Be(2);
        client.ActivePollCalls.Should().Be(2);
    }

    [Fact]
    public async Task SetSecretAsync_ConcurrentCreators_ConvergeAfterSingleSuccessfulRecovery()
    {
        var client = new ConcurrentRecoveryClient(expectedInitialCreators: 2);
        var writer = CreateWriter(client);

        var writes = new[]
        {
            writer.SetSecretAsync("secret-name", "credential-a", CancellationToken.None),
            writer.SetSecretAsync("secret-name", "credential-b", CancellationToken.None),
        };

        var versions = await Task.WhenAll(writes);

        versions.Should().OnlyHaveUniqueItems();
        client.RecoverCalls.Should().Be(2);
        client.SuccessfulValues.Should().BeEquivalentTo(["credential-a", "credential-b"]);
    }

    [Fact]
    public async Task SetSecretAsync_RecoveryForbidden_PropagatesWithoutSecretValue()
    {
        var client = new FakeWriterClient
        {
            Set = (_, _, _) => throw DeletedButRecoverable(),
            Recover = (_, _) => throw Forbidden(),
        };
        var writer = CreateWriter(client);

        var action = () => writer.SetSecretAsync(
            "secret-name",
            "must-not-appear",
            CancellationToken.None);

        var exception = await action.Should().ThrowAsync<RequestFailedException>();
        exception.Which.Status.Should().Be(403);
        exception.Which.Message.Should().NotContain("must-not-appear");
    }

    [Fact]
    public async Task SetSecretAsync_RecoveryNeverCompletes_TimesOutWithoutSecretValue()
    {
        var client = new FakeWriterClient
        {
            Set = (_, _, _) => throw DeletedButRecoverable(),
            Recover = (_, _) => Task.CompletedTask,
            IsActive = (_, _) => Task.FromResult(false),
        };
        var writer = CreateWriter(client, maxPollAttempts: 3);

        var action = () => writer.SetSecretAsync(
            "secret-name",
            "must-not-appear",
            CancellationToken.None);

        var exception = await action.Should().ThrowAsync<TimeoutException>();
        exception.Which.Message.Should().Contain("secret-name").And.NotContain("must-not-appear");
        client.ActivePollCalls.Should().Be(3);
    }

    private static KeyVaultRecoverableSecretWriter CreateWriter(
        IKeyVaultSecretWriterClient client,
        int maxPollAttempts = 5) =>
        new(
            client,
            maxPollAttempts,
            TimeSpan.FromMilliseconds(1),
            (_, _) => Task.CompletedTask);

    private static RequestFailedException DeletedButRecoverable() =>
        new(409, "Secret is deleted but recoverable.", "ObjectIsDeletedButRecoverable", new Exception());

    private static RequestFailedException Conflict() =>
        new(409, "Recovery already in progress.", "Conflict", new Exception());

    private static RequestFailedException NotFound() =>
        new(404, "Deleted secret is not visible yet.", "SecretNotFound", new Exception());

    private static RequestFailedException Forbidden() =>
        new(403, "Recovery permission is required.", "Forbidden", new Exception());

    private sealed class FakeWriterClient : IKeyVaultSecretWriterClient
    {
        public Func<string, string, CancellationToken, Task<string>> Set { get; init; } =
            (_, _, _) => throw new InvalidOperationException("Set behavior was not configured.");

        public Func<string, CancellationToken, Task> Recover { get; init; } =
            (_, _) => throw new InvalidOperationException("Recover behavior was not configured.");

        public Func<string, CancellationToken, Task<bool>> IsActive { get; init; } =
            (_, _) => throw new InvalidOperationException("Active behavior was not configured.");

        public int RecoverCalls { get; private set; }
        public int ActivePollCalls { get; private set; }

        public Task<string> SetSecretAsync(string key, string value, CancellationToken ct) => Set(key, value, ct);

        public Task RecoverDeletedSecretAsync(string key, CancellationToken ct)
        {
            RecoverCalls++;
            return Recover(key, ct);
        }

        public Task<bool> IsSecretActiveAsync(string key, CancellationToken ct)
        {
            ActivePollCalls++;
            return IsActive(key, ct);
        }
    }

    private sealed class ConcurrentRecoveryClient(int expectedInitialCreators) : IKeyVaultSecretWriterClient
    {
        private readonly object _lock = new();
        private readonly TaskCompletionSource _initialSetBarrier =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _initialSetCalls;
        private int _version;
        private bool _active;

        public int RecoverCalls { get; private set; }
        public List<string> SuccessfulValues { get; } = [];

        public async Task<string> SetSecretAsync(string key, string value, CancellationToken ct)
        {
            var initialCall = false;
            lock (_lock)
            {
                if (_initialSetCalls < expectedInitialCreators)
                {
                    initialCall = true;
                    _initialSetCalls++;
                    if (_initialSetCalls == expectedInitialCreators)
                        _initialSetBarrier.TrySetResult();
                }
            }

            if (initialCall)
            {
                await _initialSetBarrier.Task.WaitAsync(ct);
                throw DeletedButRecoverable();
            }

            lock (_lock)
            {
                if (!_active)
                    throw DeletedButRecoverable();
                SuccessfulValues.Add(value);
                return $"v{++_version}";
            }
        }

        public Task RecoverDeletedSecretAsync(string key, CancellationToken ct)
        {
            lock (_lock)
            {
                RecoverCalls++;
                if (_active)
                    throw Conflict();
                _active = true;
                return Task.CompletedTask;
            }
        }

        public Task<bool> IsSecretActiveAsync(string key, CancellationToken ct)
        {
            lock (_lock)
                return Task.FromResult(_active);
        }
    }
}
