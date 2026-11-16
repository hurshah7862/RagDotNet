using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Content;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Keyword;
using RagDotNet.Retrieval;

namespace RagDotNet.Core.Tests.Hybrid;

public class HybridOptionsTests
{
    [Fact]
    public void Defaults_are_Auto_and_20_candidates()
    {
        var o = new RagOptions();

        Assert.Equal(HybridMode.Auto, o.Hybrid);
        Assert.Equal(20, o.RerankCandidates);
        Assert.Equal(20, o.HybridCandidates);
        Assert.Null(new RetrievalOptions().Hybrid);
        Assert.Null(new RetrievalOptions().UseReranker);
    }

    [Theory]
    [InlineData("RerankCandidates", 0)]
    [InlineData("RerankCandidates", 101)]
    [InlineData("HybridCandidates", 0)]
    [InlineData("HybridCandidates", 101)]
    public void Candidates_outside_1_to_100_fail_validation(string option, int value)
    {
        void Set(RagOptions o)
        {
            if (option == "RerankCandidates") o.RerankCandidates = value; else o.HybridCandidates = value;
        }

        var builderEx = Assert.Throws<OptionsValidationException>(() => TestRag.Create(Set));
        Assert.Contains($"{option} must be between 1 and 100.", builderEx.Message);

        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        services.AddRag(o => { o.EmbeddingDimensions = 64; Set(o); });
        using var provider = services.BuildServiceProvider();
        var diEx = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RagOptions>>().Value);
        Assert.Contains($"{option} must be between 1 and 100.", diEx.Message);
    }

    [Fact(Skip = "Turned on in Task 4, when the default keyword index is registered.")]
    public void Hybrid_On_with_external_content_and_no_keyword_index_fails_at_startup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        services.AddRag(o => { o.EmbeddingDimensions = 64; o.Hybrid = HybridMode.On; })
            .UseExternalContent<InMemoryChunkContentStore>();
        services.RemoveAll<IKeywordIndex>();
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<RagOptions>>().Value);
        Assert.Contains("keyword index", ex.Message);
    }
}
