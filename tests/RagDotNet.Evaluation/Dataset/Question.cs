using System.Text.Json.Serialization;

namespace RagDotNet.Evaluation.Datasets;

/// <summary>A labelled question.</summary>
/// <param name="Id">Unique question id.</param>
/// <param name="Text">The question text.</param>
/// <param name="DocumentId">The document that answers it (its file name without extension).</param>
/// <param name="Evidence">A short verbatim fragment of the answering passage; occurs exactly once in the corpus.</param>
public sealed record Question(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("question")] string Text,
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("evidence")] string Evidence);

/// <summary>A corpus document.</summary>
/// <param name="DocumentId">The file name without extension.</param>
/// <param name="FilePath">The full path of the file.</param>
/// <param name="NormalizedText">The <c>MarkdownReader</c> blocks joined with a space, normalized.</param>
public sealed record CorpusDocument(string DocumentId, string FilePath, string NormalizedText);

/// <summary>A validated corpus and question set.</summary>
/// <param name="Documents">Documents in ordinal path order.</param>
/// <param name="Questions">Questions in file order.</param>
/// <param name="Hash">First 12 hex characters of SHA-256 over the corpus files and questions.json.</param>
public sealed record Dataset(IReadOnlyList<CorpusDocument> Documents, IReadOnlyList<Question> Questions, string Hash);

/// <summary>A named dataset; each set is ingested and scored on its own.</summary>
/// <param name="Name">The set's directory name.</param>
/// <param name="Dataset">Its documents and questions.</param>
public sealed record DatasetSet(string Name, Dataset Dataset);

/// <summary>The dataset failed validation.</summary>
public sealed class DatasetValidationException : Exception
{
    /// <summary>Creates the exception with every problem found.</summary>
    public DatasetValidationException(IReadOnlyList<string> errors)
        : base("Dataset validation failed:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  " + e)))
    {
        Errors = errors;
    }

    /// <summary>Every problem found, one per line.</summary>
    public IReadOnlyList<string> Errors { get; }
}
