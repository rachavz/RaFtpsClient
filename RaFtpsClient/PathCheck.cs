using System.IO;

namespace RaFtpsClient;

/// <summary>
/// Provides file path validation utilities.
/// </summary>
public static class PathCheck
{
    private const char replacementChar = '_';
    // Path.GetInvalidFileNameChars allocates a fresh array on every call.
    private static readonly char[] invalidFileNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Replaces invalid file name characters with underscores.
    /// </summary>
    /// <param name="fileName">The file name to validate.</param>
    /// <returns>A valid local file name.</returns>
    public static string GetValidLocalFileName(string fileName)
    {
        int first = fileName.IndexOfAny(invalidFileNameChars);
        if (first < 0)
        {
            return fileName;
        }
        char[] chars = fileName.ToCharArray();
        for (int i = first; i < chars.Length; i++)
        {
            if (System.Array.IndexOf(invalidFileNameChars, chars[i]) >= 0)
            {
                chars[i] = replacementChar;
            }
        }
        return new string(chars);
    }
}
