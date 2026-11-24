using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using RagDotNet.Access;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Pipeline;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Hosting.Tests;

public sealed class IngestionQueueTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ragdotnet-hosting-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<IHost> _hosts = [];

    public ValueTask InitializeAsync() => default;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>The tenant of the current "request"; a test changes it between requests.</summary>
    private sealed class Tenant
    {
        public string Value { get; set; } = "a";

        public RagPermissions Permissions { get; set; } = RagPermissions.All;

        public string Key { get; set; } = "tenant";
    }

    private sealed class TenantScope(Tenant tenant) : IAccessScope
    {
        public ValueTask<AccessScope?> GetScopeAsync(CancellationToken cancellationToken) =>
            new(new AccessScope(new Dictionary<string, string> { [tenant.Key] = tenant.Value }, null, tenant.Permissions));
    }

    private sealed class RecordingQueue : IIngestionJobQueue
    {
        public List<IngestionJob> Jobs { get; } = [];

        public ValueTask EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default)
        {
            Jobs.Add(job);
            return default;
        }

        public ValueTask<IngestionJobStatus?> GetStatusAsync(string jobId, CancellationToken cancellationToken = default) => new((IngestionJobStatus?)null);

        public ValueTask<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default) => new(false);

        public ValueTask PauseAsync(CancellationToken cancellationToken = default) => default;

        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => default;
    }

    private async Task<IHost> StartHostAsync(bool scoped, Action<IServiceCollection>? services = null, Action<IngestionQueueOptions>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        builder.Services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        builder.Services.AddSingleton<Tenant>();
        services?.Invoke(builder.Services);
        var rag = builder.Services.AddRag(o =>
        {
            o.EmbeddingDimensions = 64;
            if (scoped)
            {
                o.Metadata.Add<string>("tenant");
                o.PartitionKeys.Add("tenant");
            }
        });
        if (scoped) rag.AddAccessScope<TenantScope>();
        rag.AddIngestionQueue(o =>
        {
            o.RetryDelay = TimeSpan.Zero;
            o.TempDirectory = _dir;
            configure?.Invoke(o);
        });
        var host = builder.Build();
        _hosts.Add(host);
        await host.StartAsync(Ct);
        return host;
    }

    private static async Task<string> EnqueueInRequestAsync(IHost host, string documentId, string text = "alpha beta gamma")
    {
        await using var request = host.Services.CreateAsyncScope();
        return await request.ServiceProvider.GetRequiredService<IIngestionQueue>().EnqueueAsync(Md(documentId, text), Ct);
    }

    private static async Task<IngestionJobStatus> WaitForStateAsync(IHost host, string jobId, IngestionJobState state)
    {
        var queue = host.Services.GetRequiredService<IIngestionJobQueue>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var status = await queue.GetStatusAsync(jobId, Ct);
            if (status?.State == state) return status;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Job {jobId} is {status?.State.ToString() ?? "unknown"} ({status?.Error}).");
            await Task.Delay(10, Ct);
        }
    }

    private string[] QueueDirectories() => Directory.Exists(_dir) ? Directory.GetDirectories(_dir, "ragdotnet-queue-*") : [];

    [Fact]
    public async Task Enqueue_through_DI_runs_on_the_hosted_worker()
    {
        var host = await StartHostAsync(scoped: false);

        var id = await EnqueueInRequestAsync(host, "d");

        await WaitForStateAsync(host, id, IngestionJobState.Succeeded);
        var admin = host.Services.GetRequiredService<IRagAdmin>();
        Assert.Equal("d", Assert.Single(await admin.ForScope(null).RetrieveAsync("alpha", cancellationToken: Ct)).DocumentId);
        await using var request = host.Services.CreateAsyncScope();
        Assert.Equal(IngestionJobState.Succeeded, (await request.ServiceProvider.GetRequiredService<IIngestionQueue>().GetStatusAsync(id, Ct))!.State);
    }

    [Fact]
    public async Task Scope_is_captured_at_enqueue()
    {
        var host = await StartHostAsync(scoped: true);
        var tenant = host.Services.GetRequiredService<Tenant>();
        await host.Services.GetRequiredService<IIngestionJobQueue>().PauseAsync(Ct);

        var id = await EnqueueInRequestAsync(host, "d");
        tenant.Value = "b";
        await host.Services.GetRequiredService<IIngestionJobQueue>().ResumeAsync(Ct);

        await WaitForStateAsync(host, id, IngestionJobState.Succeeded);
        var admin = host.Services.GetRequiredService<IRagAdmin>();
        Assert.Single(await admin.ForScope(TestScopes.Tenant("a")).RetrieveAsync("alpha", cancellationToken: Ct));
        Assert.Empty(await admin.ForScope(TestScopes.Tenant("b")).RetrieveAsync("alpha", cancellationToken: Ct));
    }

    [Fact]
    public async Task Scope_without_write_is_refused_at_enqueue()
    {
        var host = await StartHostAsync(scoped: true);
        host.Services.GetRequiredService<Tenant>().Permissions = RagPermissions.Read;

        var e = await Assert.ThrowsAsync<RagAccessDeniedException>(() => EnqueueInRequestAsync(host, "d"));

        Assert.Equal(RagPermissions.Write, e.MissingPermission);
        Assert.Empty(QueueDirectories().SelectMany(Directory.GetFiles));
    }

    [Fact]
    public async Task Malformed_scope_is_refused_at_enqueue()
    {
        var host = await StartHostAsync(scoped: true);
        host.Services.GetRequiredService<Tenant>().Key = "other";

        await Assert.ThrowsAsync<ArgumentException>(() => EnqueueInRequestAsync(host, "d"));

        Assert.Empty(QueueDirectories().SelectMany(Directory.GetFiles));
    }

    [Fact]
    public async Task App_queue_registered_first_replaces_the_default()
    {
        var recording = new RecordingQueue();
        var host = await StartHostAsync(scoped: true, services => services.AddSingleton<IIngestionJobQueue>(recording));
        var source = Md("d", "alpha");

        await using (var request = host.Services.CreateAsyncScope())
            await request.ServiceProvider.GetRequiredService<IIngestionQueue>().EnqueueAsync(source, Ct);

        var job = Assert.Single(recording.Jobs);
        Assert.Same(source, job.Source);
        Assert.Equal("a", job.Scope!.Partition["tenant"]);
        Assert.Empty(QueueDirectories());
    }

    [Fact]
    public void Invalid_options_fail_at_startup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        builder.Services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        builder.Services.AddRag(o => o.EmbeddingDimensions = 64).AddIngestionQueue(o => o.Concurrency = 0);
        using var host = builder.Build();

        var e = Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IIngestionJobQueue>());
        Assert.Contains("Concurrency", e.Message);
    }
}
