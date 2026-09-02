using System;

namespace RaFtpsClient;

/// <summary>
/// Represents an item in an FTP directory listing.
/// </summary>
public class DirectoryListItem
{
    /// <summary>Gets or sets the file size in bytes.</summary>
    public ulong Size { get; set; }

    /// <summary>
    /// Gets or sets the target path if this item is a symbolic link. Directory listings report the
    /// target exactly as the server wrote it, which may be relative; a recursive
    /// <see cref="FTPSClient.GetFiles(string, string, string, EPatternStyle, bool, FileTransferCallback)"/>
    /// replaces it with the absolute path the link resolves to.
    /// </summary>
    public string SymLinkTargetPath { get; set; }

    /// <summary>Gets or sets the Unix-style permission flags.</summary>
    public string Flags { get; set; }

    /// <summary>Gets or sets the file owner name.</summary>
    public string Owner { get; set; }

    /// <summary>Gets or sets the file group name.</summary>
    public string Group { get; set; }

    /// <summary>Gets or sets whether this item is a directory.</summary>
    public bool IsDirectory { get; set; }

    /// <summary>Gets or sets whether this item is a symbolic link.</summary>
    public bool IsSymLink { get; set; }

    /// <summary>Gets or sets the file or directory name.</summary>
    public string Name { get; set; }

    /// <summary>Gets or sets the timestamp shown in the listing. Despite the name this is the
    /// modification time on every server that reports one; the property keeps its original name
    /// for compatibility.</summary>
    public DateTime CreationTime { get; set; }
}
