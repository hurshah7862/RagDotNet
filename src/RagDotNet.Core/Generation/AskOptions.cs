using Microsoft.Extensions.AI;
using RagDotNet.Retrieval;

namespace RagDotNet.Generation;

/// <summary>Per-call options for asking a question.</summary>
public sealed record AskOptions
{
    /// <summary>Retrieval options; defaults are used when null.</summary>
    public RetrievalOptions? Retrieval { get; init; }

    /// <summary>Overrides the configured context token budget for this call.</summary>
    public int? MaxContextTokens { get; init; }

    /// <summary>Options passed to the chat client.</summary>
    public ChatOptions? ChatOptions { get; init; }
}
