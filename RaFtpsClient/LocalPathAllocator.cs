using System;
using System.Collections.Generic;

namespace RaFtpsClient;

/// <summary>
/// Hands out local file paths for a recursive download, appending "_1", "_2"... when a name has
/// already been used. Case-insensitive on purpose: two remote names differing only in case would
/// otherwise map to one local file on Windows.
/// </summary>
internal sealed class LocalPathAllocator
{
    private readonly HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // Where the last probe for a base path ended, so the n-th duplicate does not re-probe the n-1
    // suffixes already handed out.
    private readonly Dictionary<string, int> nextSuffix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public string Reserve(string localFilePath)
    {
        if (used.Add(localFilePath))
        {
            return localFilePath;
        }
        nextSuffix.TryGetValue(localFilePath, out int suffix);
        string candidate;
        do
        {
            suffix++;
            candidate = localFilePath + "_" + suffix;
        } while (!used.Add(candidate));
        nextSuffix[localFilePath] = suffix;
        return candidate;
    }
}
