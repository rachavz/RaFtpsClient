using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RaFtpsClient;

internal class DirectoryListParser
{
    private enum EDirectoryListingStyle
    {
        UnixStyle,
        WindowsStyle,
        Unknown
    }

    private const string unixSymLinkPathSeparator = " -> ";

    public static IList<DirectoryListItem> GetDirectoryList(string datastring)
    {
        try
        {
            List<DirectoryListItem> list = new List<DirectoryListItem>();
            string[] array = datastring.Split(new char[1] { '\n' });
            EDirectoryListingStyle eDirectoryListingStyle = GuessDirectoryListingStyle(array);
            foreach (string text in array)
            {
                if (eDirectoryListingStyle == EDirectoryListingStyle.Unknown || text.Trim().Length == 0)
                {
                    continue;
                }
                DirectoryListItem directoryListItem;
                try
                {
                    directoryListItem = ((eDirectoryListingStyle == EDirectoryListingStyle.UnixStyle)
                        ? ParseDirectoryListItemFromUnixStyleRecord(text)
                        : ParseDirectoryListItemFromWindowsStyleRecord(text));
                }
                catch (Exception)
                {
                    // Servers mix status lines and vendor extensions into LIST output; one record we
                    // cannot read is no reason to discard the whole listing.
                    continue;
                }
                if (directoryListItem != null && directoryListItem.Name != "." && directoryListItem.Name != "..")
                {
                    list.Add(directoryListItem);
                }
            }
            return list;
        }
        catch (Exception innerException)
        {
            throw new FTPException("Unable to parse the directory list", innerException);
        }
    }

    private static DirectoryListItem ParseDirectoryListItemFromWindowsStyleRecord(string record)
    {
        DirectoryListItem directoryListItem = new DirectoryListItem();
        string text = record.Trim();
        string text2 = text.Substring(0, 8);
        text = text.Substring(8, text.Length - 8).Trim();
        string text3 = text.Substring(0, 7);
        text = text.Substring(7, text.Length - 7).Trim();
        directoryListItem.CreationTime = DateTime.Parse(text2 + " " + text3, CultureInfo.GetCultureInfo("en-US"));
        if (text.Substring(0, 5) == "<DIR>")
        {
            directoryListItem.IsDirectory = true;
            text = text.Substring(5, text.Length - 5).Trim();
        }
        else
        {
            directoryListItem.IsDirectory = false;
            int num = text.IndexOf(' ');
            directoryListItem.Size = ulong.Parse(text.Substring(0, num));
            text = text.Substring(num + 1);
        }
        directoryListItem.Name = text;
        return directoryListItem;
    }

    private static EDirectoryListingStyle GuessDirectoryListingStyle(string[] recordList)
    {
        foreach (string text in recordList)
        {
            // The type character covers links, sockets and device nodes as well as files and
            // directories, and the execute positions carry setuid/setgid/sticky bits, so a listing of
            // nothing but symlinks or setgid directories must still be recognised as Unix style.
            if (text.Length > 10 && Regex.IsMatch(text.Substring(0, 10), "^[bcdlpsD-][r-][w-][xsS-][r-][w-][xsS-][r-][w-][xtT-]$"))
            {
                return EDirectoryListingStyle.UnixStyle;
            }
            if (text.Length > 8 && Regex.IsMatch(text.Substring(0, 8), "[0-9][0-9]-[0-9][0-9]-[0-9][0-9]"))
            {
                return EDirectoryListingStyle.WindowsStyle;
            }
        }
        return EDirectoryListingStyle.Unknown;
    }

    private static DirectoryListItem ParseDirectoryListItemFromUnixStyleRecord(string record)
    {
        if (record.ToLower().StartsWith("total "))
        {
            return null;
        }
        DirectoryListItem directoryListItem = new DirectoryListItem();
        string text = record.Trim();
        directoryListItem.Flags = text.Substring(0, 9);
        directoryListItem.IsDirectory = directoryListItem.Flags[0] == 'd';
        directoryListItem.IsSymLink = directoryListItem.Flags[0] == 'l';
        text = text.Substring(11).Trim();
        CutSubstringFromStringWithTrim(ref text, " ", 0);
        directoryListItem.Owner = CutSubstringFromStringWithTrim(ref text, " ", 0);
        directoryListItem.Group = CutSubstringFromStringWithTrim(ref text, " ", 0);
        directoryListItem.Size = ulong.Parse(CutSubstringFromStringWithTrim(ref text, " ", 0));
        string text2 = CutSubstringFromStringWithTrim(ref text, " ", 8);
        string format = ((text2.IndexOf(':') >= 0) ? "MMM dd H:mm" : "MMM dd yyyy");
        if (text2.Length > 4 && text2[4] == ' ')
        {
            text2 = text2.Substring(0, 4) + "0" + text2.Substring(5);
        }
        DateTime timestamp = DateTime.ParseExact(text2, format, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces);
        // ls omits the year on recent entries, so ParseExact assumes the current one: a December
        // file read in January would otherwise be dated eleven months into the future.
        if (format == "MMM dd H:mm" && timestamp > DateTime.Now.AddDays(1.0))
        {
            timestamp = timestamp.AddYears(-1);
        }
        directoryListItem.CreationTime = timestamp;
        if (directoryListItem.IsSymLink && text.IndexOf(" -> ") > 0)
        {
            directoryListItem.Name = CutSubstringFromStringWithTrim(ref text, " -> ", 0);
            directoryListItem.SymLinkTargetPath = text;
        }
        else
        {
            directoryListItem.Name = text;
        }
        return directoryListItem;
    }

    private static string CutSubstringFromStringWithTrim(ref string s, string str, int startIndex)
    {
        int num = s.IndexOf(str, startIndex);
        string result = s.Substring(0, num);
        s = s.Substring(num + str.Length).Trim();
        return result;
    }
}
