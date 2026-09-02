using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RaFtpsClient;

internal static class DirectoryListParser
{
    private enum EDirectoryListingStyle
    {
        UnixStyle,
        WindowsStyle,
        Unknown
    }

    private const string unixSymLinkPathSeparator = " -> ";
    private static readonly CultureInfo listingCulture = CultureInfo.GetCultureInfo("en-US");

    // The type character covers links, sockets and device nodes as well as files and directories,
    // and the execute positions carry setuid/setgid/sticky bits, so a listing of nothing but
    // symlinks or setgid directories must still be recognised as Unix style. Anchored at the start
    // only, so the whole line can be tested without cutting a prefix off it first.
    private static readonly Regex unixStylePattern = new Regex("^[bcdlpsD-][r-][w-][xsS-][r-][w-][xsS-][r-][w-][xtT-]", RegexOptions.Compiled);
    private static readonly Regex windowsStylePattern = new Regex("^[0-9][0-9]-[0-9][0-9]-[0-9][0-9]", RegexOptions.Compiled);

    public static IList<DirectoryListItem> GetDirectoryList(string datastring)
    {
        try
        {
            List<DirectoryListItem> list = new List<DirectoryListItem>();
            string[] records = datastring.Split('\n');
            EDirectoryListingStyle style = GuessDirectoryListingStyle(records);
            if (style == EDirectoryListingStyle.Unknown)
            {
                return list;
            }
            // ls omits the year on recent entries and the parser assumes the current one, so a
            // December file read in January would land eleven months in the future. Evaluated once
            // per listing rather than per record.
            DateTime futureLimit = DateTime.Now.AddDays(1.0);
            foreach (string record in records)
            {
                if (string.IsNullOrWhiteSpace(record))
                {
                    continue;
                }
                DirectoryListItem item;
                try
                {
                    item = (style == EDirectoryListingStyle.UnixStyle)
                        ? ParseUnixStyleRecord(record, futureLimit)
                        : ParseWindowsStyleRecord(record);
                }
                catch (Exception)
                {
                    // Servers mix status lines and vendor extensions into LIST output; one record we
                    // cannot read is no reason to discard the whole listing.
                    continue;
                }
                if (item != null && item.Name != "." && item.Name != "..")
                {
                    list.Add(item);
                }
            }
            return list;
        }
        catch (Exception innerException)
        {
            throw new FTPException("Unable to parse the directory list", innerException);
        }
    }

    private static EDirectoryListingStyle GuessDirectoryListingStyle(string[] records)
    {
        foreach (string record in records)
        {
            if (record.Length > 10 && unixStylePattern.IsMatch(record))
            {
                return EDirectoryListingStyle.UnixStyle;
            }
            if (record.Length > 8 && windowsStylePattern.IsMatch(record))
            {
                return EDirectoryListingStyle.WindowsStyle;
            }
        }
        return EDirectoryListingStyle.Unknown;
    }

    // "MM-dd-yy  hh:mmAM  <DIR>|size  name"
    private static DirectoryListItem ParseWindowsStyleRecord(string record)
    {
        string text = record.Trim();
        int pos = 0;
        string date = NextToken(text, ref pos);
        string time = NextToken(text, ref pos);
        string sizeOrDir = NextToken(text, ref pos);
        DirectoryListItem item = new DirectoryListItem
        {
            CreationTime = DateTime.Parse(date + " " + time, listingCulture),
            IsDirectory = sizeOrDir == "<DIR>",
            Name = Rest(text, pos)
        };
        if (!item.IsDirectory)
        {
            item.Size = ulong.Parse(sizeOrDir, NumberStyles.None, CultureInfo.InvariantCulture);
        }
        return item;
    }

    // "drwxr-xr-x  2 owner group  4096 May 31 12:00 name [-> target]"
    private static DirectoryListItem ParseUnixStyleRecord(string record, DateTime futureLimit)
    {
        if (record.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string text = record.Trim();
        // Type character plus nine permission bits. Position 10 is skipped unconditionally: it is
        // either the separating space or an ACL marker ("+", ".") glued to the permissions.
        string flags = text.Substring(0, 10);
        int pos = 11;
        NextToken(text, ref pos);                    // link count
        string owner = NextToken(text, ref pos);
        string group = NextToken(text, ref pos);
        string size = NextToken(text, ref pos);
        string month = NextToken(text, ref pos);
        string day = NextToken(text, ref pos);
        string yearOrTime = NextToken(text, ref pos);
        string name = Rest(text, pos);

        DirectoryListItem item = new DirectoryListItem
        {
            Flags = flags,
            IsDirectory = flags[0] == 'd',
            IsSymLink = flags[0] == 'l',
            Owner = owner,
            Group = group,
            Size = ulong.Parse(size, NumberStyles.None, CultureInfo.InvariantCulture),
            CreationTime = ParseUnixTimestamp(month, day, yearOrTime, futureLimit)
        };
        if (item.IsSymLink)
        {
            int arrow = name.IndexOf(unixSymLinkPathSeparator, StringComparison.Ordinal);
            if (arrow > 0)
            {
                item.SymLinkTargetPath = name.Substring(arrow + unixSymLinkPathSeparator.Length);
                name = name.Substring(0, arrow);
            }
        }
        item.Name = name;
        return item;
    }

    private static readonly string[] monthNames = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

    // "May 31 12:00" or "May 31 2019". The fields are assembled directly because DateTime.ParseExact
    // and the string it needs building were most of the cost of parsing a large listing; anything
    // that does not fit the two shapes falls back to it.
    private static DateTime ParseUnixTimestamp(string month, string day, string yearOrTime, DateTime futureLimit)
    {
        bool hasTime = yearOrTime.IndexOf(':') >= 0;
        int monthNumber = Array.IndexOf(monthNames, month.ToLowerInvariant()) + 1;
        if (monthNumber > 0 && TryParseSmallInt(day, out int dayNumber))
        {
            if (hasTime)
            {
                int colon = yearOrTime.IndexOf(':');
                if (TryParseSmallInt(yearOrTime.Substring(0, colon), out int hour)
                    && TryParseSmallInt(yearOrTime.Substring(colon + 1), out int minute)
                    && TryMakeDate(futureLimit.Year, monthNumber, dayNumber, hour, minute, out DateTime withTime))
                {
                    return withTime > futureLimit ? withTime.AddYears(-1) : withTime;
                }
            }
            else if (TryParseSmallInt(yearOrTime, out int year) && TryMakeDate(year, monthNumber, dayNumber, 0, 0, out DateTime withYear))
            {
                return withYear;
            }
        }
        string text = month + " " + day.PadLeft(2, '0') + " " + yearOrTime;
        DateTime timestamp = DateTime.ParseExact(text, hasTime ? "MMM dd H:mm" : "MMM dd yyyy", listingCulture, DateTimeStyles.None);
        return (hasTime && timestamp > futureLimit) ? timestamp.AddYears(-1) : timestamp;
    }

    private static bool TryMakeDate(int year, int month, int day, int hour, int minute, out DateTime result)
    {
        result = default;
        if (year < 1 || year > 9999 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59) return false;
        result = new DateTime(year, month, day, hour, minute, 0);
        return true;
    }

    private static bool TryParseSmallInt(string s, out int value)
    {
        value = 0;
        if (s.Length == 0 || s.Length > 4) return false;
        foreach (char c in s)
        {
            if (c < '0' || c > '9') return false;
            value = value * 10 + (c - '0');
        }
        return true;
    }

    /// <summary>Returns the next space-delimited token starting at <paramref name="pos"/>, leaving
    /// <paramref name="pos"/> on the character after it.</summary>
    private static string NextToken(string s, ref int pos)
    {
        while (pos < s.Length && s[pos] == ' ') pos++;
        int start = pos;
        while (pos < s.Length && s[pos] != ' ') pos++;
        if (start == pos) throw new FormatException("Unexpected end of listing record");
        return s.Substring(start, pos - start);
    }

    /// <summary>Everything after the current position, minus the delimiting spaces. Names keep their
    /// own inner spaces.</summary>
    private static string Rest(string s, int pos)
    {
        while (pos < s.Length && s[pos] == ' ') pos++;
        return s.Substring(pos);
    }
}
