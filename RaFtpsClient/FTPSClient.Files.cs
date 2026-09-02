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

// File transfer operations: single files, recursive directory transfers and progress callbacks.
public sealed partial class FTPSClient
{
    /// <summary>Opens a stream to download a remote file.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>A readable FTP stream.</returns>
    public FTPStream GetFile(string remoteFileName)
    {
        SetupDataConnection();
        RetrCmd(remoteFileName);
        return EndStreamCommand(FTPStream.EAllowedOperation.Read);
    }

    /// <summary>Downloads a remote file to a local path.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <param name="localFileName">The local file path to save to.</param>
    /// <returns>The number of bytes downloaded.</returns>
    public ulong GetFile(string remoteFileName, string localFileName)
    {
        return GetFile(remoteFileName, localFileName, null);
    }

    /// <summary>Downloads a remote file to a local path with progress callback.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <param name="localFileName">The local file path to save to.</param>
    /// <param name="transferCallback">Progress callback, or null.</param>
    /// <returns>The number of bytes downloaded.</returns>
    public ulong GetFile(string remoteFileName, string localFileName, FileTransferCallback transferCallback)
    {
        ulong num = 0uL;
        ulong? fileTransferSize = null;
        if (transferCallback != null)
        {
            try
            {
                fileTransferSize = GetFileTransferSize(remoteFileName);
            }
            catch (FTPCommandException ex)
            {
                if (ex.ErrorCode == 550)
                {
                    throw new FTPException("Could not get the requested remote file", ex);
                }
                throw;
            }
        }
        using (Stream stream = GetFile(remoteFileName))
        {
            using (FileStream fileStream = new FileStream(localFileName, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] array = new byte[transferBufferSize];
                int num2 = 0;
                do
                {
                    CallTransferCallback(transferCallback, ETransferActions.FileDownloadingStatus, localFileName, remoteFileName, num, fileTransferSize);
                    num2 = stream.Read(array, 0, array.Length);
                    if (num2 > 0)
                    {
                        fileStream.Write(array, 0, num2);
                        num += (ulong)num2;
                    }
                } while (num2 > 0);
                fileStream.Close();
            }
            stream.Close();
        }
        CallTransferCallback(transferCallback, ETransferActions.FileDownloaded, localFileName, remoteFileName, num, fileTransferSize);
        return num;
    }

    /// <summary>Downloads multiple files from a remote directory.</summary>
    /// <param name="remoteDirectoryName">The remote directory, or null for current.</param>
    /// <param name="localDirectoryName">The local directory to save files to.</param>
    /// <param name="filePattern">File name pattern, or null for all files.</param>
    /// <param name="patternStyle">The pattern matching style.</param>
    /// <param name="recursive">Whether to download subdirectories recursively.</param>
    /// <param name="transferCallback">Progress callback, or null.</param>
    public void GetFiles(string remoteDirectoryName, string localDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive, FileTransferCallback transferCallback)
    {
        GetFiles(remoteDirectoryName, localDirectoryName, filePattern, patternStyle, recursive, transferCallback, new List<string>(), new HashSet<string>());
    }

    private void GetFiles(string remoteDirectoryName, string localDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive, FileTransferCallback transferCallback, IList<string> paths, ISet<string> visitedRemoteDirs)
    {
        Regex regex = null;
        if (filePattern != null)
        {
            regex = new Regex(GetRegexPattern(filePattern, patternStyle));
        }
        string text = localDirectoryName;
        if (text == null || text.Length == 0)
        {
            text = Directory.GetCurrentDirectory();
        }
        else if (!Directory.Exists(text))
        {
            Directory.CreateDirectory(text);
            CallTransferCallback(transferCallback, ETransferActions.LocalDirectoryCreated, text, null, 0uL, null);
        }
        string text2 = remoteDirectoryName;
        if (text2 == null || text2.Length == 0)
        {
            text2 = GetCurrentDirectory();
        }
        if (!visitedRemoteDirs.Add(text2)) return;
        IList<DirectoryListItem> directoryList = GetDirectoryList(text2);
        CheckSymLinks(text2, directoryList);
        foreach (DirectoryListItem item in directoryList)
        {
            if (!item.IsDirectory && (regex == null || regex.IsMatch(item.Name)))
            {
                string uniquePath = GetUniquePath(paths, Path.Combine(text, PathCheck.GetValidLocalFileName(item.Name)));
                string remoteFileName = CombineRemotePath(text2, item.Name);
                GetFile(remoteFileName, uniquePath, transferCallback);
            }
        }
        if (!recursive) return;
        foreach (DirectoryListItem item2 in directoryList)
        {
            if (!item2.IsDirectory) continue;
            // A symlinked directory is recursed under the path CheckSymLinks resolved it to, so a
            // link pointing back at an ancestor is recognised as already visited instead of looping.
            string remoteDirectoryName2 = (item2.IsSymLink && item2.SymLinkTargetPath != null)
                ? item2.SymLinkTargetPath
                : CombineRemotePath(text2, item2.Name);
            if (visitedRemoteDirs.Contains(remoteDirectoryName2)) continue;
            string uniquePath2 = GetUniquePath(paths, Path.Combine(text, PathCheck.GetValidLocalFileName(item2.Name)));
            GetFiles(remoteDirectoryName2, uniquePath2, filePattern, patternStyle, recursive, transferCallback, paths, visitedRemoteDirs);
        }
    }

    internal static string GetUniquePath(IList<string> paths, string localFilePath)
    {
        string text = localFilePath;
        int num = 1;
        while (paths.Contains(text.ToLowerInvariant()))
        {
            text = localFilePath + "_" + num++;
        }
        paths.Add(text.ToLowerInvariant());
        return text;
    }

    /// <summary>Downloads files from the current remote directory.</summary>
    /// <param name="localDirectoryName">The local directory.</param>
    /// <param name="filePattern">File pattern.</param>
    /// <param name="patternStyle">Pattern style.</param>
    /// <param name="recursive">Recursive download.</param>
    public void GetFiles(string localDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive)
    {
        GetFiles(null, localDirectoryName, filePattern, patternStyle, recursive, null);
    }

    /// <summary>Downloads all files from the current remote directory.</summary>
    /// <param name="localDirectoryName">The local directory.</param>
    /// <param name="recursive">Recursive download.</param>
    public void GetFiles(string localDirectoryName, bool recursive)
    {
        GetFiles(null, localDirectoryName, null, EPatternStyle.Verbatim, recursive, null);
    }

    /// <summary>Downloads all files (non-recursive) from the current remote directory.</summary>
    /// <param name="localDirectoryName">The local directory.</param>
    public void GetFiles(string localDirectoryName)
    {
        GetFiles(null, localDirectoryName, null, EPatternStyle.Verbatim, recursive: false, null);
    }

    /// <summary>Opens a stream to upload a file to the server.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>A writable FTP stream.</returns>
    public FTPStream PutFile(string remoteFileName)
    {
        SetupDataConnection();
        StorCmd(remoteFileName);
        return EndStreamCommand(FTPStream.EAllowedOperation.Write);
    }

    /// <summary>Uploads a local file to the server.</summary>
    /// <param name="localFileName">The local file path.</param>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>The number of bytes uploaded.</returns>
    public ulong PutFile(string localFileName, string remoteFileName)
    {
        return PutFile(localFileName, remoteFileName, null);
    }

    /// <summary>Uploads a local file with progress callback.</summary>
    /// <param name="localFileName">The local file path.</param>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <param name="transferCallback">Progress callback, or null.</param>
    /// <returns>The number of bytes uploaded.</returns>
    public ulong PutFile(string localFileName, string remoteFileName, FileTransferCallback transferCallback)
    {
        using Stream s = PutFile(remoteFileName);
        return SendFile(localFileName, remoteFileName, s, transferCallback);
    }

    /// <summary>Uploads multiple files from a local directory.</summary>
    /// <param name="localDirectoryName">The local directory.</param>
    /// <param name="remoteDirectoryName">The remote directory, or null for current.</param>
    /// <param name="filePattern">File pattern filter, or null for all.</param>
    /// <param name="patternStyle">Pattern matching style.</param>
    /// <param name="recursive">Whether to upload subdirectories.</param>
    /// <param name="transferCallback">Progress callback, or null.</param>
    public void PutFiles(string localDirectoryName, string remoteDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive, FileTransferCallback transferCallback)
    {
        Regex regex = null;
        if (filePattern != null)
        {
            regex = new Regex(GetRegexPattern(filePattern, patternStyle));
        }
        string text = null;
        string text2 = remoteDirectoryName;
        if (text2 != null)
        {
            text = GetCurrentDirectory();
            EnsureDir(text2, transferCallback);
        }
        else
        {
            text2 = GetCurrentDirectory();
        }
        string text3 = localDirectoryName;
        if (text3 == null || text3.Length == 0)
        {
            text3 = Directory.GetCurrentDirectory();
        }
        try
        {
            string[] files = Directory.GetFiles(text3);
            foreach (string text4 in files)
            {
                string fileName = Path.GetFileName(text4);
                if (regex == null || regex.IsMatch(fileName))
                {
                    string remoteFileName = CombineRemotePath(text2, fileName);
                    PutFile(text4, remoteFileName, transferCallback);
                }
            }
            if (recursive)
            {
                files = Directory.GetDirectories(text3);
                foreach (string text5 in files)
                {
                    string remoteDirectoryName2 = CombineRemotePath(text2, Path.GetFileName(text5));
                    PutFiles(text5, remoteDirectoryName2, filePattern, patternStyle, recursive, transferCallback);
                }
            }
        }
        finally
        {
            if (text != null)
            {
                SetCurrentDirectory(text);
            }
        }
    }

    /// <summary>Uploads files to the current remote directory.</summary>
    public void PutFiles(string localDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive)
    {
        PutFiles(localDirectoryName, null, filePattern, patternStyle, recursive, null);
    }

    /// <summary>Uploads all files from a local directory.</summary>
    public void PutFiles(string localDirectoryName, bool recursive)
    {
        PutFiles(localDirectoryName, null, null, EPatternStyle.Verbatim, recursive, null);
    }

    /// <summary>Uploads all files (non-recursive) from a local directory.</summary>
    public void PutFiles(string localDirectoryName)
    {
        PutFiles(localDirectoryName, null, null, EPatternStyle.Verbatim, recursive: false, null);
    }

    /// <summary>Opens a stream to append data to a remote file.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>A writable FTP stream.</returns>
    public FTPStream AppendFile(string remoteFileName)
    {
        SetupDataConnection();
        AppeCmd(remoteFileName);
        return EndStreamCommand(FTPStream.EAllowedOperation.Write);
    }

    /// <summary>Appends a local file to a remote file.</summary>
    /// <param name="localFileName">The local file path.</param>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>The number of bytes uploaded.</returns>
    public ulong AppendFile(string localFileName, string remoteFileName)
    {
        return AppendFile(localFileName, remoteFileName, null);
    }

    /// <summary>Appends a local file to a remote file with progress callback.</summary>
    public ulong AppendFile(string localFileName, string remoteFileName, FileTransferCallback transferCallback)
    {
        using Stream s = AppendFile(remoteFileName);
        return SendFile(localFileName, remoteFileName, s, transferCallback);
    }

    /// <summary>Opens a stream to upload a uniquely named file on the server.</summary>
    /// <param name="remoteFileName">Outputs the generated remote file name.</param>
    /// <returns>A writable FTP stream.</returns>
    public FTPStream PutUniqueFile(out string remoteFileName)
    {
        SetupDataConnection();
        StouCmd(out remoteFileName);
        return EndStreamCommand(FTPStream.EAllowedOperation.Write);
    }

    /// <summary>Uploads a local file with a unique remote name.</summary>
    public ulong PutUniqueFile(string localFileName, out string remoteFileName)
    {
        return PutUniqueFile(localFileName, out remoteFileName, null);
    }

    /// <summary>Uploads a local file with a unique remote name and progress callback.</summary>
    public ulong PutUniqueFile(string localFileName, out string remoteFileName, FileTransferCallback transferCallback)
    {
        using Stream s = PutUniqueFile(out remoteFileName);
        return SendFile(localFileName, remoteFileName, s, transferCallback);
    }

    /// <summary>Deletes a remote file.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    public void DeleteFile(string remoteFileName)
    {
        DeleCmd(remoteFileName);
    }

    /// <summary>Renames a remote file.</summary>
    /// <param name="remoteFileNameFrom">The current file name.</param>
    /// <param name="remoteFileNameTo">The new file name.</param>
    public void RenameFile(string remoteFileNameFrom, string remoteFileNameTo)
    {
        RnfrCmd(remoteFileNameFrom);
        RntoCmd(remoteFileNameTo);
    }

    private void CheckSymLinks(string remoteDirectoryName, IList<DirectoryListItem> dirList)
    {
        string text = null;
        try
        {
            foreach (DirectoryListItem dir in dirList)
            {
                if (!dir.IsSymLink) continue;
                try
                {
                    if (text == null)
                    {
                        text = GetCurrentDirectory();
                    }
                    string currentDirectory = CombineRemotePath(remoteDirectoryName, dir.Name);
                    SetCurrentDirectory(currentDirectory);
                    dir.IsDirectory = true;
                    // Resolving the link to its absolute path is what lets a recursive download
                    // detect a link that points back into a directory it has already walked.
                    dir.SymLinkTargetPath = GetCurrentDirectory();
                }
                catch (FTPCommandException ex)
                {
                    if (ex.ErrorCode == 550)
                    {
                        dir.IsDirectory = false;
                        continue;
                    }
                    throw;
                }
            }
        }
        finally
        {
            if (text != null)
            {
                SetCurrentDirectory(text);
            }
        }
    }

    internal static string GetRegexPattern(string filePattern, EPatternStyle patternStyle)
    {
        string text = filePattern;
        if ((uint)patternStyle <= 1u)
        {
            text = "^" + Regex.Escape(filePattern) + "$";
            if (patternStyle == EPatternStyle.Wildcard)
            {
                text = text.Replace("\\*", ".*").Replace("\\?", ".{1}");
            }
        }
        return text;
    }

    private void CallTransferCallback(FileTransferCallback transferCallback, ETransferActions transferAction, string localObjectName, string remoteObjectName, ulong fileTransmittedBytes, ulong? fileTransferSize)
    {
        if (transferCallback != null)
        {
            bool cancel = false;
            transferCallback(this, transferAction, localObjectName, remoteObjectName, fileTransmittedBytes, fileTransferSize, ref cancel);
            if (cancel)
            {
                throw new FTPOperationCancelledException("Operation cancelled by the user");
            }
        }
    }

    private ulong SendFile(string localFileName, string remoteFileName, Stream s, FileTransferCallback transferCallback)
    {
        ulong num = 0uL;
        ulong? fileTransferSize = null;
        if (transferCallback != null)
        {
            fileTransferSize = (ulong)new FileInfo(localFileName).Length;
        }
        using (FileStream fileStream = File.OpenRead(localFileName))
        {
            byte[] array = new byte[transferBufferSize];
            int num2 = 0;
            do
            {
                CallTransferCallback(transferCallback, ETransferActions.FileUploadingStatus, localFileName, remoteFileName, num, fileTransferSize);
                num2 = fileStream.Read(array, 0, array.Length);
                if (num2 > 0)
                {
                    s.Write(array, 0, num2);
                    num += (ulong)num2;
                }
            } while (num2 > 0);
            fileStream.Close();
        }
        s.Close();
        CallTransferCallback(transferCallback, ETransferActions.FileUploaded, localFileName, remoteFileName, num, fileTransferSize);
        return num;
    }

    private void EnsureDir(string remoteDirectoryName, FileTransferCallback transferCallback)
    {
        try
        {
            string currentDirectory = GetCurrentDirectory();
            SetCurrentDirectory(remoteDirectoryName);
            SetCurrentDirectory(currentDirectory);
        }
        catch (FTPCommandException ex)
        {
            if (ex.ErrorCode == 550)
            {
                MakeDir(remoteDirectoryName);
                CallTransferCallback(transferCallback, ETransferActions.RemoteDirectoryCreated, null, remoteDirectoryName, 0uL, null);
                return;
            }
            throw;
        }
    }
}
