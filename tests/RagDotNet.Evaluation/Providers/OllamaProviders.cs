using Microsoft.Extensions.AI;
using OllamaSharp;

namespace RagDotNet.Evaluation.Providers;

/// <summary>The Ollama embedding and chat clients for real mode.</summary>
public static class OllamaProviders
{
    /// <summary>How long the reachability check waits.</summary>
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Creates the embedding client.</summary>
    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbedder(Uri url, string model) => new OllamaApiClient(url, model);

    /// <summary>Creates the chat client.</summary>
    public static IChatClient CreateChat(Uri url, string model) => new OllamaApiClient(url, model);

    /// <summary>
    /// Lists the models Ollama has pulled, within <see cref="PingTimeout"/>, and checks each of <paramref name="models"/>
    /// is among them. Returns null when all are present, or a one-line message for the user.
    /// A name without a tag matches <c>:latest</c>, as in Ollama itself.
    /// </summary>
    public static async Task<string?> CheckModelsAsync(Uri url, IEnumerable<string> models, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(models);
        using var client = new OllamaApiClient(url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PingTimeout);
        HashSet<string> pulled;
        try
        {
            pulled = [.. (await client.ListLocalModelsAsync(timeout.Token).ConfigureAwait(false)).Select(m => m.Name)];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"Ollama is not reachable at {url.OriginalString}: no response within {PingTimeout.TotalSeconds:0} seconds";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Ollama is not reachable at {url.OriginalString}: {Describe(ex)}";
        }

        foreach (var model in models)
        {
            if (!pulled.Contains(model) && (model.Contains(':', StringComparison.Ordinal) || !pulled.Contains(model + ":latest")))
            {
                return $"Ollama at {url.OriginalString} does not have model '{model}'; run `ollama pull {model}`";
            }
        }

        return null;
    }

    /// <summary>One line for an exception, with its inner message when that adds anything.</summary>
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.InnerException is { } inner && !ex.Message.Contains(inner.Message, StringComparison.Ordinal)
            ? $"{ex.Message} ({inner.Message})"
            : ex.Message;
    }

    /// <summary>
    /// Embeds <c>"ping"</c> within <see cref="PingTimeout"/>. Returns null on success, or a one-line reason on failure.
    /// A vector of the wrong length is a failure too, since every later write would be rejected.
    /// </summary>
    public static async Task<string?> PingAsync(IEmbeddingGenerator<string, Embedding<float>> embedder, int dimensions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PingTimeout);
        try
        {
            var result = await embedder.GenerateAsync(["ping"], cancellationToken: timeout.Token).ConfigureAwait(false);
            var length = result.Count == 0 ? 0 : result[0].Vector.Length;
            return length == dimensions ? null : $"the model returned {length} dimensions, expected {dimensions} (use --dimensions)";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"no response within {PingTimeout.TotalSeconds:0} seconds";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Describe(ex);
        }
    }
}
