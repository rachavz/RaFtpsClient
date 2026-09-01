using System.IO;
using System.Text;

namespace RaFtpsClient;

/// <summary>
/// Provides file path validation utilities.
/// </summary>
public static class PathCheck
{
    private static char replacementChar = '_';

    /// <summary>
    /// Replaces invalid file name characters with underscores.
    /// </summary>
    /// <param name="fileName">The file name to validate.</param>
    /// <returns>A valid local file name.</returns>
    public static string GetValidLocalFileName(string fileName)
    {
        return ReplaceAllChars(fileName, Path.GetInvalidFileNameChars(), replacementChar);
    }

    private static string ReplaceAllChars(string str, char[] oldChars, char newChar)
    {
        StringBuilder stringBuilder = new StringBuilder(str);
        foreach (char oldChar in oldChars)
        {
            stringBuilder.Replace(oldChar, newChar);
        }
        return stringBuilder.ToString();
    }
}
