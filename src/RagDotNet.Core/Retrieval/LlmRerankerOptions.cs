namespace RagDotNet.Retrieval;

/// <summary>Options for <see cref="LlmReranker"/>.</summary>
public sealed class LlmRerankerOptions
{
    /// <summary>
    /// How many candidates go in one chat call. Default 5, so that a call of up to 5 × <see cref="MaxCandidateTokens"/>
    /// tokens fits a 4,096-token context window. Raise it only when the model's window holds
    /// <c>BatchSize × MaxCandidateTokens</c> tokens plus the question.
    /// </summary>
    public int BatchSize { get; set; } = 5;

    /// <summary>The most chat calls running at once for one retrieval. Default 4.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Each candidate is cut to this many tokens before it is sent. Default 512.</summary>
    public int MaxCandidateTokens { get; set; } = 512;

    /// <summary>
    /// Replaces the scoring instruction (how to judge a passage against the question), for domain wording. The reply
    /// format (a JSON array of 0 to 10 scores) is always requested. Null uses the built-in instruction.
    /// </summary>
    public string? Instructions { get; set; }
}
