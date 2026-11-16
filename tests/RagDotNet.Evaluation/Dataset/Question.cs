using System.Text.Json.Serialization;

namespace RagDotNet.Evaluation.Datasets;

/// <summary>A labelled question.</summary>
/// <param name="Id">Unique question id.</param>
/// <param name="Text">The question text.</param>
/// <param name="DocumentId">The document that answers it (its file name without extension).</param>
/// <param name="Evidence">A short verbatim fragment of the answering passage; occurs exactly once in the corpus.</param>
/// <param name="Tags">Optional labels; questions tagged <see cref="ExactTerm"/> are also scored on their own.</param>
public sealed record Question(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("question")] string Text,
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags = null)
{
    /// <summary>The tag of questions whose answer hinges on an exact term: a version, an error code, a setting name.</summary>
    public const string ExactTerm = "exact-term";

    /// <summary>Value equality, comparing tags element by element.</summary>
    public bool Equals(Question? other) =>
        other is not null && Id == other.Id && Text == other.Text && DocumentId == other.DocumentId && Evidence == other.Evidence
        && (Tags ?? []).SequenceEqual(other.Tags ?? []);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, Text, DocumentId, Evidence);
}

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

/// <summary>A named dataset in one file format; each is ingested and scored on its own.</summary>
/// <param name="Name">The set's directory name.</param>
/// <param name="Dataset">Its documents and questions.</param>
/// <param name="Format"><c>md</c> for the set's own corpus, or <c>pdf</c>, <c>docx</c>, <c>html</c> or <c>scanned</c> for a rendered variant.</param>
/// <param name="UnscorableIds">
/// Ids of questions whose evidence the variant's reader did not keep intact in one body block; they always score as a miss.
/// </param>
public sealed record DatasetSet(string Name, Dataset Dataset, string Format = DatasetSet.Markdown, IReadOnlyList<string>? UnscorableIds = null)
{
    /// <summary>The number of unscorable questions.</summary>
    public int Unscorable => UnscorableIds?.Count ?? 0;

    /// <summary>The format of a set's own corpus.</summary>
    public const string Markdown = "md";

    /// <summary>The scanned check's format: image-only PDFs, read with OCR.</summary>
    public const string Scanned = "scanned";

    /// <summary>The formats a set can be read in.</summary>
    public static IReadOnlyList<string> Formats { get; } = [Markdown, "pdf", "docx", "html", Scanned];

    /// <summary>The file extension of a format's files, without the dot; scanned copies are PDFs.</summary>
    public static string Extension(string format) => format == Scanned ? "pdf" : format;

    /// <summary>The report section title: the set name, plus the format for a rendered variant.</summary>
    public string Label => Format == Markdown ? Name : $"{Name} · {Format}";
}

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
