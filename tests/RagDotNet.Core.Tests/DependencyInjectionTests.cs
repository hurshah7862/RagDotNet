using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using RagDotNet.Access;
using RagDotNet.Configuration;
using RagDotNet.Content;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Pipeline;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class DependencyInjectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class TenantHolder
    {
        public string Tenant { get; set; } = "a";
    }

    private sealed class HolderScope(TenantHolder holder) : IAccessScope
    {
        public ValueTask<AccessScope> GetScopeAsync(CancellationToken cancellationToken) =>
            new(new AccessScope(new Dictionary<string, string> { ["tenant"] = holder.Tenant }));
    }

    private sealed class NullContentStore : IChunkContentStore
    {
        private readonly Dictionary<string, string> _items = [];

        public Task UpsertAsync(IReadOnlyList<ChunkContent> chunks, CancellationToken ct)
        {
            foreach (var c in chunks) _items[c.ChunkKey] = c.Text;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetAsync(IReadOnlyList<string> chunkKeys, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(chunkKeys.Where(_items.ContainsKey).ToDictionary(k => k, k => _items[k]));

        public Task DeleteAsync(IReadOnlyList<string> chunkKeys, CancellationToken ct)
        {
            foreach (var k in chunkKeys) _items.Remove(k);
            return Task.CompletedTask;
        }
    }

    private static (ServiceCollection Services, FakeChatClient Chat) BaseServices()
    {
        var services = new ServiceCollection();
        var chat = new FakeChatClient();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        services.AddSingleton<IChatClient>(chat);
        return (services, chat);
    }

    [Fact]
    public async Task AddRag_resolves_pipeline_and_admin_and_answers()
    {
        var (services, chat) = BaseServices();
        services.AddRag(o => o.EmbeddingDimensions = 64);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var pipeline = scope.ServiceProvider.GetRequiredService<IRagPipeline>();
        Assert.NotNull(provider.GetRequiredService<IRagAdmin>());
        await pipeline.IngestAsync(Md("install-guide", "# Install\n\nUse apt to install the tool."), cancellationToken: Ct);
        chat.Replies.Enqueue("Use apt [1].");
        var answer = await pipeline.AskAsync("how do I install", cancellationToken: Ct);

        Assert.True(answer.IsAnswered);
        Assert.Single(answer.Citations);
    }

    [Fact]
    public void Pipeline_is_scoped_and_admin_is_singleton()
    {
        var (services, _) = BaseServices();
        services.AddRag(o => o.EmbeddingDimensions = 64);
        using var provider = services.BuildServiceProvider();

        using var s1 = provider.CreateScope();
        using var s2 = provider.CreateScope();
        Assert.Same(s1.ServiceProvider.GetRequiredService<IRagPipeline>(), s1.ServiceProvider.GetRequiredService<IRagPipeline>());
        Assert.NotSame(s1.ServiceProvider.GetRequiredService<IRagPipeline>(), s2.ServiceProvider.GetRequiredService<IRagPipeline>());
        Assert.Same(provider.GetRequiredService<IRagAdmin>(), provider.GetRequiredService<IRagAdmin>());
        Assert.Same(provider.GetRequiredService<RagEngine>(), provider.GetRequiredService<RagEngine>());
    }

    [Fact]
    public void Scope_with_undeclared_partition_key_fails_validation()
    {
        var (services, _) = BaseServices();
        services.AddSingleton<TenantHolder>();
        services.AddRag(o => { o.EmbeddingDimensions = 64; o.PartitionKeys.Add("tenant"); }).AddAccessScope<HolderScope>();
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RagOptions>>().Value);
        Assert.Contains("tenant", ex.Message);
    }

    [Fact]
    public void Partition_key_must_be_a_string_field()
    {
        var (services, _) = BaseServices();
        services.AddSingleton<TenantHolder>();
        services.AddRag(o => { o.EmbeddingDimensions = 64; o.Metadata.Add<int>("tenant"); o.PartitionKeys.Add("tenant"); }).AddAccessScope<HolderScope>();
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RagOptions>>().Value);
        Assert.Contains("string", ex.Message);
    }

    [Fact]
    public void Partition_keys_without_a_registered_scope_fail_validation()
    {
        var (services, _) = BaseServices();
        services.AddRag(o => { o.EmbeddingDimensions = 64; o.Metadata.Add<string>("tenant"); o.PartitionKeys.Add("tenant"); });
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<RagEngine>());
        Assert.Contains("access scope", ex.Message);
    }

    [Fact]
    public void Missing_dimensions_fails_validation()
    {
        var (services, _) = BaseServices();
        services.AddRag();
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RagOptions>>().Value);
        Assert.Contains("EmbeddingDimensions", ex.Message);
    }

    [Fact]
    public void Out_of_range_numeric_options_fail_validation()
    {
        var (services, _) = BaseServices();
        services.AddRag(o => { o.EmbeddingDimensions = 64; o.CollectionName = " "; o.EmbeddingBatchSize = 0; o.IngestParallelism = 0; o.MaxContextTokens = 99; });
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RagOptions>>().Value);
        Assert.Equal(4, ex.Failures.Count());
    }

    [Fact]
    public void External_content_without_a_store_fails_validation_and_with_one_resolves()
    {
        var (bad, _) = BaseServices();
        bad.AddRag(o => { o.EmbeddingDimensions = 64; o.ContentStorage = ContentStorageMode.External; });
        using var badProvider = bad.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => badProvider.GetRequiredService<RagEngine>());

        var (good, _) = BaseServices();
        good.AddRag(o => o.EmbeddingDimensions = 64).UseExternalContent<NullContentStore>();
        using var goodProvider = good.BuildServiceProvider();
        Assert.Equal(ContentStorageMode.External, goodProvider.GetRequiredService<IOptions<RagOptions>>().Value.ContentStorage);
        Assert.NotNull(goodProvider.GetRequiredService<RagEngine>().ContentStore);
    }

    [Fact]
    public async Task Request_scoped_access_scope_is_used_per_scope()
    {
        var (services, _) = BaseServices();
        services.AddScoped<TenantHolder>();
        services.AddRag(o => { o.EmbeddingDimensions = 64; o.Metadata.Add<string>("tenant"); o.PartitionKeys.Add("tenant"); })
            .AddAccessScope<HolderScope>();
        using var provider = services.BuildServiceProvider();

        using (var a = provider.CreateScope())
        {
            a.ServiceProvider.GetRequiredService<TenantHolder>().Tenant = "a";
            await a.ServiceProvider.GetRequiredService<IRagPipeline>().IngestAsync(Md("d", "alpha beta gamma"), cancellationToken: Ct);
        }

        using (var b = provider.CreateScope())
        {
            b.ServiceProvider.GetRequiredService<TenantHolder>().Tenant = "b";
            var rag = b.ServiceProvider.GetRequiredService<IRagPipeline>();
            Assert.Empty(await rag.RetrieveAsync("alpha", cancellationToken: Ct));
        }

        using (var a2 = provider.CreateScope())
        {
            a2.ServiceProvider.GetRequiredService<TenantHolder>().Tenant = "a";
            Assert.NotEmpty(await a2.ServiceProvider.GetRequiredService<IRagPipeline>().RetrieveAsync("alpha", cancellationToken: Ct));
        }
    }

    [Fact]
    public void Options_bind_from_configuration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CollectionName"] = "docs",
                ["EmbeddingDimensions"] = "64",
            })
            .Build();
        var (services, _) = BaseServices();
        services.AddRag();
        services.Configure<RagOptions>(config);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<RagOptions>>().Value;
        Assert.Equal("docs", options.CollectionName);
        Assert.Equal(64, options.EmbeddingDimensions);
        Assert.NotNull(provider.GetRequiredService<RagEngine>());
    }

    [Fact]
    public void Builder_admin_is_not_blocked_by_missing_scope()
    {
        var admin = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseVectorStore(new InMemoryVectorStore(), "test", 64)
            .Configure(o => { o.Metadata.Add<string>("tenant"); o.PartitionKeys.Add("tenant"); })
            .BuildAdmin();
        Assert.NotNull(admin);
    }
}
