using System.Text;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using RagDotNet.Configuration;
using RagDotNet.Documents;

namespace RagDotNet.Readers.Tests;

/// <summary>Shared helpers for the reader tests.</summary>
internal static class ReaderTestKit
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string Asset(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "TestAssets", .. parts]);

    public static async Task<ParsedDocument> ReadAsync(IDocumentReader reader, byte[] bytes, string fileName, string? contentType = null)
    {
        using var stream = new MemoryStream(bytes);
        return await reader.ReadAsync(stream, new DocumentReadContext(fileName, contentType), Ct);
    }

    public static Task<ParsedDocument> ReadAsync(IDocumentReader reader, string text, string fileName) =>
        ReadAsync(reader, Encoding.UTF8.GetBytes(text), fileName);

    public static Task<ParsedDocument> ReadFileAsync(IDocumentReader reader, string path) =>
        ReadAsync(reader, File.ReadAllBytes(path), Path.GetFileName(path));

    /// <summary>A builder over an in-memory store and a deterministic bag-of-words embedder (no model needed).</summary>
    public static RagPipelineBuilder Builder() =>
        new RagPipelineBuilder()
            .UseEmbeddingGenerator(new HashEmbedder())
            .UseVectorStore(new InMemoryVectorStore(), "readers", HashEmbedder.Dimensions);

    /// <summary>Deterministic embedder: words hash into buckets; good enough to retrieve by shared words.</summary>
    public sealed class HashEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public const int Dimensions = 64;
        private readonly EmbeddingGeneratorMetadata _metadata = new("hash", null, "hash-embed", Dimensions);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var value in values)
            {
                var v = new float[Dimensions];
                foreach (var word in value.ToLowerInvariant().Split([' ', '\n', '.', ',', ':', '|', '-'], StringSplitOptions.RemoveEmptyEntries))
                    v[(int)((uint)StringComparer.Ordinal.GetHashCode(word) % Dimensions)] += 1;
                var norm = MathF.Sqrt(v.Sum(x => x * x));
                if (norm > 0) for (var i = 0; i < v.Length; i++) v[i] /= norm;
                result.Add(new Embedding<float>(v));
            }

            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(_metadata) ? _metadata : null;

        public void Dispose() { }
    }
}
