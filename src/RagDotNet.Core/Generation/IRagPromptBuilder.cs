using Microsoft.Extensions.AI;

namespace RagDotNet.Generation;

/// <summary>Builds the chat prompt for answer generation.</summary>
public interface IRagPromptBuilder
{
    /// <summary>Builds the messages sent to the model.</summary>
    /// <param name="context">The question and the numbered context chunks.</param>
    /// <returns>The chat messages.</returns>
    IList<ChatMessage> Build(RagPromptContext context);

    /// <summary>Tells whether a model reply means the documents did not contain the answer.</summary>
    /// <param name="answerText">The model reply.</param>
    /// <returns>True when the reply is a "not answered" reply.</returns>
    bool IsUnanswered(string answerText);
}
