namespace RagDotNet.Access;

/// <summary>The caller's access scope does not allow the requested operation.</summary>
public sealed class RagAccessDeniedException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public RagAccessDeniedException()
        : base("The access scope does not allow this operation.")
    {
        Operation = "";
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public RagAccessDeniedException(string message)
        : base(message)
    {
        Operation = "";
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public RagAccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Operation = "";
    }

    /// <summary>Creates the exception for a denied operation.</summary>
    /// <param name="operation">The denied operation, for example "ingest".</param>
    /// <param name="missingPermission">The permission the scope lacks, if any.</param>
    /// <param name="message">The message.</param>
    public RagAccessDeniedException(string operation, RagPermissions? missingPermission, string message)
        : base(message)
    {
        Operation = operation;
        MissingPermission = missingPermission;
    }

    /// <summary>Gets the denied operation.</summary>
    public string Operation { get; }

    /// <summary>Gets the permission the scope lacks, if known.</summary>
    public RagPermissions? MissingPermission { get; }
}
