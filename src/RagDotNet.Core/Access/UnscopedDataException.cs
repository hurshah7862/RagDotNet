namespace RagDotNet.Access;

/// <summary>The collection holds documents ingested without a partition, so an access scope cannot be enforced over it.</summary>
public sealed class UnscopedDataException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public UnscopedDataException()
        : base("The collection contains documents ingested without a partition. Re-ingest into a new collection before enabling an access scope.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public UnscopedDataException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public UnscopedDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
