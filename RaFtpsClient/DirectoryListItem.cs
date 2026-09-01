using System;

namespace RaFtpsClient;

/// <summary>
/// Represents an item in an FTP directory listing.
/// </summary>
public class DirectoryListItem
{
    private string flags;
    private string owner;
    private string group;
    private bool isDirectory;
    private bool isSymLink;
    private string name;
    private ulong size;
    private DateTime creationTime;
    private string symLinkTargetPath;

    /// <summary>Gets or sets the file size in bytes.</summary>
    public ulong Size
    {
        get { return size; }
        set { size = value; }
    }

    /// <summary>
    /// Gets or sets the target path if this item is a symbolic link. Directory listings report the
    /// target exactly as the server wrote it, which may be relative; a recursive
    /// <see cref="FTPSClient.GetFiles(string, string, string, EPatternStyle, bool, FileTransferCallback)"/>
    /// replaces it with the absolute path the link resolves to.
    /// </summary>
    public string SymLinkTargetPath
    {
        get { return symLinkTargetPath; }
        set { symLinkTargetPath = value; }
    }

    /// <summary>Gets or sets the Unix-style permission flags.</summary>
    public string Flags
    {
        get { return flags; }
        set { flags = value; }
    }

    /// <summary>Gets or sets the file owner name.</summary>
    public string Owner
    {
        get { return owner; }
        set { owner = value; }
    }

    /// <summary>Gets or sets the file group name.</summary>
    public string Group
    {
        get { return group; }
        set { group = value; }
    }

    /// <summary>Gets or sets whether this item is a directory.</summary>
    public bool IsDirectory
    {
        get { return isDirectory; }
        set { isDirectory = value; }
    }

    /// <summary>Gets or sets whether this item is a symbolic link.</summary>
    public bool IsSymLink
    {
        get { return isSymLink; }
        set { isSymLink = value; }
    }

    /// <summary>Gets or sets the file or directory name.</summary>
    public string Name
    {
        get { return name; }
        set { name = value; }
    }

    /// <summary>Gets or sets the creation/modification time.</summary>
    public DateTime CreationTime
    {
        get { return creationTime; }
        set { creationTime = value; }
    }
}
