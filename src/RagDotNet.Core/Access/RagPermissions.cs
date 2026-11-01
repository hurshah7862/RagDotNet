namespace RagDotNet.Access;

/// <summary>The operations an access scope allows.</summary>
[Flags]
public enum RagPermissions
{
    /// <summary>No operations are allowed.</summary>
    None = 0,

    /// <summary>Retrieval and question answering.</summary>
    Read = 1,

    /// <summary>Ingestion.</summary>
    Write = 2,

    /// <summary>Document deletion.</summary>
    Delete = 4,

    /// <summary>Every operation.</summary>
    All = Read | Write | Delete,
}
