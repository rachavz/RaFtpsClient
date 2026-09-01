namespace RaFtpsClient;

/// <summary>
/// Specifies the pattern matching style for file name filters.
/// </summary>
public enum EPatternStyle
{
    /// <summary>Exact file name match.</summary>
    Verbatim,
    /// <summary>Wildcard pattern (* and ?).</summary>
    Wildcard,
    /// <summary>Regular expression pattern.</summary>
    Regex
}
