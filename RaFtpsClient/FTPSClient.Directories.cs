using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RaFtpsClient;

// Directory listing and working directory operations.
public sealed partial class FTPSClient
{
    /// <summary>Creates a remote directory.</summary>
    /// <param name="remoteDirName">The directory name.</param>
    public void MakeDir(string remoteDirName)
    {
        MkdCmd(remoteDirName);
    }

    /// <summary>Removes a remote directory.</summary>
    /// <param name="remoteDirName">The directory name.</param>
    public void RemoveDir(string remoteDirName)
    {
        RmdCmd(remoteDirName);
    }

    /// <summary>Changes to the parent directory.</summary>
    public void ChangeToUpperDir()
    {
        CdupCmd();
    }

    /// <summary>Gets a short listing of file names in the current directory.</summary>
    /// <returns>A list of file names.</returns>
    public IList<string> GetShortDirectoryList()
    {
        return GetShortDirectoryList(null);
    }

    /// <summary>Gets a short listing of file names in the specified directory.</summary>
    /// <param name="remoteDirName">The remote directory, or null for current.</param>
    /// <returns>A list of file names.</returns>
    public IList<string> GetShortDirectoryList(string remoteDirName)
    {
        SetupDataConnection();
        NlstCmd(remoteDirName);
        string dataString = GetDataString();
        ReadTransferCompletionReply();
        return new List<string>(dataString.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Gets a detailed directory listing for the current directory.</summary>
    /// <returns>A list of directory items.</returns>
    public IList<DirectoryListItem> GetDirectoryList()
    {
        return GetDirectoryList(null);
    }

    /// <summary>Gets a detailed directory listing for the specified directory.</summary>
    /// <param name="remoteDirName">The remote directory, or null for current.</param>
    /// <returns>A list of directory items.</returns>
    public IList<DirectoryListItem> GetDirectoryList(string remoteDirName)
    {
        return DirectoryListParser.GetDirectoryList(GetDirectoryListUnparsed(remoteDirName));
    }

    /// <summary>Gets the raw directory listing text for the current directory.</summary>
    /// <returns>The raw listing string.</returns>
    public string GetDirectoryListUnparsed()
    {
        return GetDirectoryListUnparsed(null);
    }

    /// <summary>Gets the raw directory listing text for the specified directory.</summary>
    /// <param name="remoteDirName">The remote directory, or null for current.</param>
    /// <returns>The raw listing string.</returns>
    public string GetDirectoryListUnparsed(string remoteDirName)
    {
        SetupDataConnection();
        ListCmd(remoteDirName);
        string dataString = GetDataString();
        ReadTransferCompletionReply();
        // An empty listing is ambiguous: probe with a CWD so a missing directory raises 550 instead
        // of silently looking like an empty one.
        if (dataString.Length == 0 && !string.IsNullOrEmpty(remoteDirName))
        {
            PushCurrentDirectory();
            try
            {
                SetCurrentDirectory(remoteDirName);
            }
            finally
            {
                PopCurrentDirectory();
            }
        }
        return dataString;
    }

    /// <summary>Gets the current remote working directory path.</summary>
    /// <returns>The current directory path.</returns>
    public string GetCurrentDirectory()
    {
        return PwdCmd();
    }

    /// <summary>Saves the current directory and pushes it onto the stack.</summary>
    /// <returns>The saved directory path.</returns>
    public string PushCurrentDirectory()
    {
        string currentDirectory = GetCurrentDirectory();
        currDirStack.Push(currentDirectory);
        return currentDirectory;
    }

    /// <summary>Restores the previously saved directory.</summary>
    /// <returns>The restored directory path.</returns>
    public string PopCurrentDirectory()
    {
        string text = currDirStack.Pop();
        SetCurrentDirectory(text);
        return text;
    }

    /// <summary>Changes the current remote working directory.</summary>
    /// <param name="remoteDirName">The directory path to change to.</param>
    public void SetCurrentDirectory(string remoteDirName)
    {
        CwdCmd(remoteDirName);
    }

    internal static string CombineRemotePath(string path1, string path2)
    {
        return (path1.EndsWith("/") ? path1 : (path1 + "/")) + path2;
    }
}
