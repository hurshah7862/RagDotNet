namespace RagDotNet.Retrieval;

/// <summary>Options for <see cref="LlmReranker"/>.</summary>
public sealed class LlmRerankerOptions
{
    /// <summary>How many candidates go in one chat call. Default 10.</summary>
    public int BatchSize { get; set; } = 10;

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
