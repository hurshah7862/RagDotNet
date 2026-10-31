using Microsoft.Extensions.AI;
using RagDotNet.Retrieval;

namespace RagDotNet.Generation;

/// <summary>An answer with its sources.</summary>
/// <param name="Text">The model's answer, unmodified.</param>
/// <param name="Citations">Valid citations found in the answer, in first-seen order.</param>
/// <param name="RetrievedChunks">Every chunk retrieved for the question, including those that did not fit the context budget.</param>
/// <param name="Usage">Token usage reported by the model, if any.</param>
/// <param name="IsAnswered">False when the documents did not contain the answer.</param>
public sealed record RagAnswer(
    string Text,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<RetrievedChunk> RetrievedChunks,
    UsageDetails? Usage,
    bool IsAnswered);

/// <summary>One update of a streamed answer: either a text delta or the final answer.</summary>
/// <param name="Text">A piece of answer text; null on the final update.</param>
/// <param name="Final">The complete answer; set only on the last update.</param>
public sealed record RagAnswerUpdate(string? Text, RagAnswer? Final);
