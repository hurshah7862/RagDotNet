namespace RagDotNet.Storage;

/// <summary>The CLR type of record keys in the vector store.</summary>
public enum RagKeyType
{
    /// <summary>String keys.</summary>
    String,

    /// <summary>Guid keys, derived deterministically from the string key.</summary>
    Guid,
}
