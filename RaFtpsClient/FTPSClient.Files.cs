using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RaFtpsClient;

// File transfer operations: single files, recursive directory transfers and progress callbacks.
// Sync and async forms sit next to each other; see the note in FTPSClient.ControlChannel.cs.
public sealed partial class FTPSClient
{
    // ----- download -------------------------------------------------------------------------------

    /// <summary>Opens a stream to download a remote file.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>A readable FTP stream.</returns>
    public FTPStream GetFile(string remoteFileName)
    {
        SetupDataConnection();
        HandleCmd(Cmd.Retr(remoteFileName));
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
        ulong? fileTransferSize = (transferCallback != null) ? QuerySizeForProgress(remoteFileName) : null;
        FTPStream stream = GetFile(remoteFileName);
        ulong total = RunTransfer(stream, () =>
        {
            using (FileStream fileStream = OpenForWrite(localFileName, useAsync: false))
            {
                return CopyWithProgress(stream, fileStream, ETransferActions.FileDownloadingStatus, localFileName, remoteFileName, fileTransferSize, transferCallback);
            }
        });
        CallTransferCallback(transferCallback, ETransferActions.FileDownloaded, localFileName, remoteFileName, total, fileTransferSize);
        return total;
    }

    /// <summary>Downloads a remote file to a local path, asynchronously.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <param name="localFileName">The local file path to save to.</param>
    /// <param name="transferCallback">Progress callback, or null.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>The number of bytes downloaded.</returns>
    public async Task<ulong> GetFileAsync(string remoteFileName, string localFileName, FileTransferCallback transferCallback = null, CancellationToken cancellationToken = default)
    {
        ulong? fileTransferSize = (transferCallback != null) ? await QuerySizeForProgressAsync(remoteFileName, cancellationToken).ConfigureAwait(false) : null;
        await SetupDataConnectionAsync(cancellationToken).ConfigureAwait(false);
        await HandleCmdAsync(Cmd.Retr(remoteFileName), cancellationToken).ConfigureAwait(false);
        ulong total = await RunTransferAsync(async () =>
        {
            Stream stream = await GetDataStreamAsync(cancellationToken).ConfigureAwait(false);
            using (FileStream fileStream = OpenForWrite(localFileName, useAsync: true))
            {
                return await CopyWithProgressAsync(stream, fileStream, ETransferActions.FileDownloadingStatus, localFileName, remoteFileName, fileTransferSize, transferCallback, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
        CallTransferCallback(transferCallback, ETransferActions.FileDownloaded, localFileName, remoteFileName, total, fileTransferSize);
        return total;
    }

    // A 550 here means the file itself is missing; say so instead of reporting a failed SIZE.
    private ulong? QuerySizeForProgress(string remoteFileName)
    {
        try
        {
            return GetFileTransferSize(remoteFileName);
        }
        catch (FTPCommandException ex) when (ex.ErrorCode == 550)
        {
            throw new FTPException("Could not get the requested remote file", ex);
        }
    }

    private async Task<ulong?> QuerySizeForProgressAsync(string remoteFileName, CancellationToken cancellationToken)
    {
        try
        {
            return await GetFileTransferSizeAsync(remoteFileName, cancellationToken).ConfigureAwait(false);
        }
        catch (FTPCommandException ex) when (ex.ErrorCode == 550)
        {
            throw new FTPException("Could not get the requested remote file", ex);
        }
    }

    private static FileStream OpenForWrite(string localFileName, bool useAsync)
    {
        return new FileStream(localFileName, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync);
    }

    private static FileStream OpenForRead(string localFileName, bool useAsync)
    {
        return new FileStream(localFileName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync);
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
        GetFiles(remoteDirectoryName, localDirectoryName, filePattern, patternStyle, recursive, transferCallback, new LocalPathAllocator(), new HashSet<string>());
    }

    /// <summary>Downloads multiple files from a remote directory, asynchronously.</summary>
    /// <param name="remoteDirectoryName">The remote directory, or null for current.</param>
    /// <param name="localDirectoryName">The local directory to save files to.</param>
    /// <param name="filePattern">File name pattern, or null for all files.</param>
    /// <param name="patternStyle">The pattern matching style.</param>
    /// <param name="recursive">Whether to download subdirectories recursively.</param>
    /// <param name="transferCallback">Progress callback, or null.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task GetFilesAsync(string remoteDirectoryName, string localDirectoryName, string filePattern = null, EPatternStyle patternStyle = EPatternStyle.Verbatim, bool recursive = false, FileTransferCallback transferCallback = null, CancellationToken cancellationToken = default)
    {
        return GetFilesAsync(remoteDirectoryName, localDirectoryName, filePattern, patternStyle, recursive, transferCallback, new LocalPathAllocator(), new HashSet<string>(), cancellationToken);
    }

    private void GetFiles(string remoteDirectoryName, string localDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive, FileTransferCallback transferCallback, LocalPathAllocator paths, ISet<string> visitedRemoteDirs)
    {
        Regex regex = PatternFor(filePattern, patternStyle);
        string localDir = ResolveLocalDirectory(localDirectoryName, transferCallback);
        string remoteDir = string.IsNullOrEmpty(remoteDirectoryName) ? GetCurrentDirectory() : remoteDirectoryName;
        if (!visitedRemoteDirs.Add(remoteDir)) return;
        IList<DirectoryListItem> directoryList = GetDirectoryList(remoteDir);
        CheckSymLinks(remoteDir, directoryList);
        foreach (DirectoryListItem item in directoryList)
        {
            if (!item.IsDirectory && (regex == null || regex.IsMatch(item.Name)))
            {
                string uniquePath = paths.Reserve(Path.Combine(localDir, PathCheck.GetValidLocalFileName(item.Name)));
                GetFile(CombineRemotePath(remoteDir, item.Name), uniquePath, transferCallback);
            }
        }
        if (!recursive) return;
        foreach (DirectoryListItem item in directoryList)
        {
            if (!item.IsDirectory) continue;
            string childRemoteDir = ChildDirectoryPath(remoteDir, item);
            if (visitedRemoteDirs.Contains(childRemoteDir)) continue;
            string uniquePath = paths.Reserve(Path.Combine(localDir, PathCheck.GetValidLocalFileName(item.Name)));
            GetFiles(childRemoteDir, uniquePath, filePattern, patternStyle, recursive, transferCallback, paths, visitedRemoteDirs);
        }
    }

    private async Task GetFilesAsync(string remoteDirectoryName, string localDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive, FileTransferCallback transferCallback, LocalPathAllocator paths, ISet<string> visitedRemoteDirs, CancellationToken cancellationToken)
    {
        Regex regex = PatternFor(filePattern, patternStyle);
        string localDir = ResolveLocalDirectory(localDirectoryName, transferCallback);
        string remoteDir = string.IsNullOrEmpty(remoteDirectoryName) ? await GetCurrentDirectoryAsync(cancellationToken).ConfigureAwait(false) : remoteDirectoryName;
        if (!visitedRemoteDirs.Add(remoteDir)) return;
        IList<DirectoryListItem> directoryList = await GetDirectoryListAsync(remoteDir, cancellationToken).ConfigureAwait(false);
        await CheckSymLinksAsync(remoteDir, directoryList, cancellationToken).ConfigureAwait(false);
        foreach (DirectoryListItem item in directoryList)
        {
            if (!item.IsDirectory && (regex == null || regex.IsMatch(item.Name)))
            {
                string uniquePath = paths.Reserve(Path.Combine(localDir, PathCheck.GetValidLocalFileName(item.Name)));
                await GetFileAsync(CombineRemotePath(remoteDir, item.Name), uniquePath, transferCallback, cancellationToken).ConfigureAwait(false);
            }
        }
        if (!recursive) return;
        foreach (DirectoryListItem item in directoryList)
        {
            if (!item.IsDirectory) continue;
            string childRemoteDir = ChildDirectoryPath(remoteDir, item);
            if (visitedRemoteDirs.Contains(childRemoteDir)) continue;
            string uniquePath = paths.Reserve(Path.Combine(localDir, PathCheck.GetValidLocalFileName(item.Name)));
            await GetFilesAsync(childRemoteDir, uniquePath, filePattern, patternStyle, recursive, transferCallback, paths, visitedRemoteDirs, cancellationToken).ConfigureAwait(false);
        }
    }

    // A symlinked directory is recursed under the path CheckSymLinks resolved it to, so a link
    // pointing back at an ancestor is recognised as already visited instead of looping.
    private static string ChildDirectoryPath(string remoteDir, DirectoryListItem item)
    {
        return (item.IsSymLink && item.SymLinkTargetPath != null) ? item.SymLinkTargetPath : CombineRemotePath(remoteDir, item.Name);
    }

    private string ResolveLocalDirectory(string localDirectoryName, FileTransferCallback transferCallback)
    {
        if (string.IsNullOrEmpty(localDirectoryName))
        {
            return Directory.GetCurrentDirectory();
        }
        if (!Directory.Exists(localDirectoryName))
        {
            Directory.CreateDirectory(localDirectoryName);
            CallTransferCallback(transferCallback, ETransferActions.LocalDirectoryCreated, localDirectoryName, null, 0uL, null);
        }
        return localDirectoryName;
    }

    private static Regex PatternFor(string filePattern, EPatternStyle patternStyle)
    {
        return (filePattern != null) ? new Regex(GetRegexPattern(filePattern, patternStyle)) : null;
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

    // ----- upload ---------------------------------------------------------------------------------

    /// <summary>Opens a stream to upload a file to the server.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>A writable FTP stream.</returns>
    public FTPStream PutFile(string remoteFileName)
    {
        SetupDataConnection();
        HandleCmd(Cmd.Stor(remoteFileName));
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
        return SendFile(localFileName, remoteFileName, PutFile(remoteFileName), transferCallback);
    }

    /// <summary>Uploads a local file to the server, asynchronously.</summary>
    /// <param name="localFileName">The local file path.</param>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <param name="transferCallback">Progress callback, or null.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>The number of bytes uploaded.</returns>
    public Task<ulong> PutFileAsync(string localFileName, string remoteFileName, FileTransferCallback transferCallback = null, CancellationToken cancellationToken = default)
    {
        return UploadAsync(Cmd.Stor(remoteFileName), localFileName, remoteFileName, transferCallback, cancellationToken);
    }

    /// <summary>Opens a stream to append data to a remote file.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <returns>A writable FTP stream.</returns>
    public FTPStream AppendFile(string remoteFileName)
    {
        SetupDataConnection();
        HandleCmd(Cmd.Appe(remoteFileName));
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
        return SendFile(localFileName, remoteFileName, AppendFile(remoteFileName), transferCallback);
    }

    /// <summary>Appends a local file to a remote file, asynchronously.</summary>
    /// <param name="localFileName">The local file path.</param>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <param name="transferCallback">Progress callback, or null.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>The number of bytes uploaded.</returns>
    public Task<ulong> AppendFileAsync(string localFileName, string remoteFileName, FileTransferCallback transferCallback = null, CancellationToken cancellationToken = default)
    {
        return UploadAsync(Cmd.Appe(remoteFileName), localFileName, remoteFileName, transferCallback, cancellationToken);
    }

    /// <summary>Opens a stream to upload a uniquely named file on the server.</summary>
    /// <param name="remoteFileName">Outputs the generated remote file name.</param>
    /// <returns>A writable FTP stream.</returns>
    public FTPStream PutUniqueFile(out string remoteFileName)
    {
        SetupDataConnection();
        remoteFileName = ParseStouReply(HandleCmd(Cmd.Stou));
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
        FTPStream dataStream = PutUniqueFile(out remoteFileName);
        return SendFile(localFileName, remoteFileName, dataStream, transferCallback);
    }

    /// <summary>Uploads a local file under a server-generated unique name, asynchronously.</summary>
    /// <param name="localFileName">The local file path.</param>
    /// <param name="transferCallback">Progress callback, or null.</param>
    /// <param name="cancellationToken">Cancels the transfer.</param>
    /// <returns>The number of bytes uploaded and the name the server chose.</returns>
    public async Task<(ulong bytes, string remoteFileName)> PutUniqueFileAsync(string localFileName, FileTransferCallback transferCallback = null, CancellationToken cancellationToken = default)
    {
        await SetupDataConnectionAsync(cancellationToken).ConfigureAwait(false);
        string remoteFileName = ParseStouReply(await HandleCmdAsync(Cmd.Stou, cancellationToken).ConfigureAwait(false));
        ulong bytes = await SendFileAsync(localFileName, remoteFileName, transferCallback, cancellationToken).ConfigureAwait(false);
        return (bytes, remoteFileName);
    }

    private async Task<ulong> UploadAsync(string command, string localFileName, string remoteFileName, FileTransferCallback transferCallback, CancellationToken cancellationToken)
    {
        await SetupDataConnectionAsync(cancellationToken).ConfigureAwait(false);
        await HandleCmdAsync(command, cancellationToken).ConfigureAwait(false);
        return await SendFileAsync(localFileName, remoteFileName, transferCallback, cancellationToken).ConfigureAwait(false);
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
        Regex regex = PatternFor(filePattern, patternStyle);
        string restoreDir = null;
        string remoteDir = remoteDirectoryName;
        if (remoteDir != null)
        {
            restoreDir = GetCurrentDirectory();
            EnsureDir(remoteDir, transferCallback);
        }
        else
        {
            remoteDir = GetCurrentDirectory();
        }
        string localDir = string.IsNullOrEmpty(localDirectoryName) ? Directory.GetCurrentDirectory() : localDirectoryName;
        try
        {
            foreach (string localFile in Directory.GetFiles(localDir))
            {
                string fileName = Path.GetFileName(localFile);
                if (regex == null || regex.IsMatch(fileName))
                {
                    PutFile(localFile, CombineRemotePath(remoteDir, fileName), transferCallback);
                }
            }
            if (recursive)
            {
                foreach (string localSubDir in Directory.GetDirectories(localDir))
                {
                    PutFiles(localSubDir, CombineRemotePath(remoteDir, Path.GetFileName(localSubDir)), filePattern, patternStyle, recursive, transferCallback);
                }
            }
        }
        finally
        {
            if (restoreDir != null)
            {
                SetCurrentDirectory(restoreDir);
            }
        }
    }

    /// <summary>Uploads multiple files from a local directory, asynchronously.</summary>
    /// <param name="localDirectoryName">The local directory.</param>
    /// <param name="remoteDirectoryName">The remote directory, or null for current.</param>
    /// <param name="filePattern">File pattern filter, or null for all.</param>
    /// <param name="patternStyle">Pattern matching style.</param>
    /// <param name="recursive">Whether to upload subdirectories.</param>
    /// <param name="transferCallback">Progress callback, or null.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task PutFilesAsync(string localDirectoryName, string remoteDirectoryName, string filePattern = null, EPatternStyle patternStyle = EPatternStyle.Verbatim, bool recursive = false, FileTransferCallback transferCallback = null, CancellationToken cancellationToken = default)
    {
        Regex regex = PatternFor(filePattern, patternStyle);
        string restoreDir = null;
        string remoteDir = remoteDirectoryName;
        if (remoteDir != null)
        {
            restoreDir = await GetCurrentDirectoryAsync(cancellationToken).ConfigureAwait(false);
            await EnsureDirAsync(remoteDir, transferCallback, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            remoteDir = await GetCurrentDirectoryAsync(cancellationToken).ConfigureAwait(false);
        }
        string localDir = string.IsNullOrEmpty(localDirectoryName) ? Directory.GetCurrentDirectory() : localDirectoryName;
        try
        {
            foreach (string localFile in Directory.GetFiles(localDir))
            {
                string fileName = Path.GetFileName(localFile);
                if (regex == null || regex.IsMatch(fileName))
                {
                    await PutFileAsync(localFile, CombineRemotePath(remoteDir, fileName), transferCallback, cancellationToken).ConfigureAwait(false);
                }
            }
            if (recursive)
            {
                foreach (string localSubDir in Directory.GetDirectories(localDir))
                {
                    await PutFilesAsync(localSubDir, CombineRemotePath(remoteDir, Path.GetFileName(localSubDir)), filePattern, patternStyle, recursive, transferCallback, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (restoreDir != null)
            {
                // Restoring the working directory is best effort once the operation itself has been
                // cancelled; the caller's token would refuse the command outright.
                await SetCurrentDirectoryAsync(restoreDir, cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
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

    // ----- delete / rename ------------------------------------------------------------------------

    /// <summary>Deletes a remote file.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    public void DeleteFile(string remoteFileName)
    {
        HandleCmd(Cmd.Dele(remoteFileName));
    }

    /// <summary>Deletes a remote file, asynchronously.</summary>
    /// <param name="remoteFileName">The remote file path.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task DeleteFileAsync(string remoteFileName, CancellationToken cancellationToken = default)
    {
        return HandleCmdAsync(Cmd.Dele(remoteFileName), cancellationToken);
    }

    /// <summary>Renames a remote file.</summary>
    /// <param name="remoteFileNameFrom">The current file name.</param>
    /// <param name="remoteFileNameTo">The new file name.</param>
    public void RenameFile(string remoteFileNameFrom, string remoteFileNameTo)
    {
        HandleCmd(Cmd.Rnfr(remoteFileNameFrom));
        HandleCmd(Cmd.Rnto(remoteFileNameTo));
    }

    /// <summary>Renames a remote file, asynchronously.</summary>
    /// <param name="remoteFileNameFrom">The current file name.</param>
    /// <param name="remoteFileNameTo">The new file name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task RenameFileAsync(string remoteFileNameFrom, string remoteFileNameTo, CancellationToken cancellationToken = default)
    {
        await HandleCmdAsync(Cmd.Rnfr(remoteFileNameFrom), cancellationToken).ConfigureAwait(false);
        await HandleCmdAsync(Cmd.Rnto(remoteFileNameTo), cancellationToken).ConfigureAwait(false);
    }

    // ----- shared transfer machinery --------------------------------------------------------------

    internal static string GetRegexPattern(string filePattern, EPatternStyle patternStyle)
    {
        if (patternStyle == EPatternStyle.Regex)
        {
            return filePattern;
        }
        string text = "^" + Regex.Escape(filePattern) + "$";
        if (patternStyle == EPatternStyle.Wildcard)
        {
            text = text.Replace("\\*", ".*").Replace("\\?", ".{1}");
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

    // The callback runs before every read so a caller can cancel before the next block arrives.
    private ulong CopyWithProgress(Stream source, Stream destination, ETransferActions status, string localName, string remoteName, ulong? fileTransferSize, FileTransferCallback transferCallback)
    {
        ulong total = 0uL;
        byte[] buffer = new byte[transferBufferSize];
        int read;
        do
        {
            CallTransferCallback(transferCallback, status, localName, remoteName, total, fileTransferSize);
            read = source.Read(buffer, 0, buffer.Length);
            if (read > 0)
            {
                destination.Write(buffer, 0, read);
                total += (ulong)read;
            }
        } while (read > 0);
        return total;
    }

    private async Task<ulong> CopyWithProgressAsync(Stream source, Stream destination, ETransferActions status, string localName, string remoteName, ulong? fileTransferSize, FileTransferCallback transferCallback, CancellationToken cancellationToken)
    {
        ulong total = 0uL;
        byte[] buffer = new byte[transferBufferSize];
        using (CancellationTokenSource scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            int read;
            do
            {
                CallTransferCallback(transferCallback, status, localName, remoteName, total, fileTransferSize);
                // Re-armed per operation to mirror ReadTimeout/WriteTimeout on the synchronous path.
                scope.CancelAfter(timeout);
                try
                {
                    read = await source.ReadAsync(buffer, 0, buffer.Length, scope.Token).ConfigureAwait(false);
                    if (read > 0)
                    {
                        await destination.WriteAsync(buffer, 0, read, scope.Token).ConfigureAwait(false);
                        total += (ulong)read;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new FTPException("Timeout during the data transfer");
                }
            } while (read > 0);
            scope.CancelAfter(timeout);
            await destination.FlushAsync(scope.Token).ConfigureAwait(false);
        }
        return total;
    }

    private ulong SendFile(string localFileName, string remoteFileName, FTPStream dataStream, FileTransferCallback transferCallback)
    {
        ulong? fileTransferSize = (transferCallback != null) ? (ulong)new FileInfo(localFileName).Length : (ulong?)null;
        ulong total = RunTransfer(dataStream, () =>
        {
            using (FileStream fileStream = OpenForRead(localFileName, useAsync: false))
            {
                return CopyWithProgress(fileStream, dataStream, ETransferActions.FileUploadingStatus, localFileName, remoteFileName, fileTransferSize, transferCallback);
            }
        });
        CallTransferCallback(transferCallback, ETransferActions.FileUploaded, localFileName, remoteFileName, total, fileTransferSize);
        return total;
    }

    // Runs the data phase, then closes the stream, which settles the control channel. If the data
    // phase failed the server still owes a 426/451 for the aborted transfer: the close consumes it
    // but its error must not replace the failure the caller actually needs to see.
    private static ulong RunTransfer(FTPStream dataStream, Func<ulong> dataPhase)
    {
        ulong total;
        try
        {
            total = dataPhase();
        }
        catch (Exception failure)
        {
            try
            {
                dataStream.Close();
            }
            catch (Exception) { }
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        dataStream.Close();
        return total;
    }

    // Expects the transfer command to have been sent already; opens the data stream, streams the
    // local file into it and settles the completion reply.
    private async Task<ulong> SendFileAsync(string localFileName, string remoteFileName, FileTransferCallback transferCallback, CancellationToken cancellationToken)
    {
        ulong? fileTransferSize = (transferCallback != null) ? (ulong)new FileInfo(localFileName).Length : (ulong?)null;
        ulong total = await RunTransferAsync(async () =>
        {
            Stream dataStream = await GetDataStreamAsync(cancellationToken).ConfigureAwait(false);
            using (FileStream fileStream = OpenForRead(localFileName, useAsync: true))
            {
                return await CopyWithProgressAsync(fileStream, dataStream, ETransferActions.FileUploadingStatus, localFileName, remoteFileName, fileTransferSize, transferCallback, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
        CallTransferCallback(transferCallback, ETransferActions.FileUploaded, localFileName, remoteFileName, total, fileTransferSize);
        return total;
    }

    // Runs the data phase of a transfer, then settles the control channel. On failure the server
    // still owes a 426/451 for the aborted transfer; consuming it (errors ignored) keeps the next
    // command from reading a stale reply, which is what FTPStream's close callback does for the
    // synchronous path.
    private async Task<ulong> RunTransferAsync(Func<Task<ulong>> dataPhase, CancellationToken cancellationToken)
    {
        ulong total;
        try
        {
            total = await dataPhase().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            CloseDataConnection();
            try
            {
                await ReadTransferCompletionReplyAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) { }
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        CloseDataConnection();
        await ReadTransferCompletionReplyAsync(cancellationToken).ConfigureAwait(false);
        return total;
    }

    private void EnsureDir(string remoteDirectoryName, FileTransferCallback transferCallback)
    {
        try
        {
            string currentDirectory = GetCurrentDirectory();
            SetCurrentDirectory(remoteDirectoryName);
            SetCurrentDirectory(currentDirectory);
        }
        catch (FTPCommandException ex) when (ex.ErrorCode == 550)
        {
            MakeDir(remoteDirectoryName);
            CallTransferCallback(transferCallback, ETransferActions.RemoteDirectoryCreated, null, remoteDirectoryName, 0uL, null);
        }
    }

    private async Task EnsureDirAsync(string remoteDirectoryName, FileTransferCallback transferCallback, CancellationToken cancellationToken)
    {
        try
        {
            string currentDirectory = await GetCurrentDirectoryAsync(cancellationToken).ConfigureAwait(false);
            await SetCurrentDirectoryAsync(remoteDirectoryName, cancellationToken).ConfigureAwait(false);
            await SetCurrentDirectoryAsync(currentDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (FTPCommandException ex) when (ex.ErrorCode == 550)
        {
            await MakeDirAsync(remoteDirectoryName, cancellationToken).ConfigureAwait(false);
            CallTransferCallback(transferCallback, ETransferActions.RemoteDirectoryCreated, null, remoteDirectoryName, 0uL, null);
        }
    }

    // Resolving each link to its absolute path is what lets a recursive download detect a link that
    // points back into a directory it has already walked.
    private void CheckSymLinks(string remoteDirectoryName, IList<DirectoryListItem> dirList)
    {
        string restoreDir = null;
        try
        {
            foreach (DirectoryListItem dir in dirList)
            {
                if (!dir.IsSymLink) continue;
                try
                {
                    if (restoreDir == null)
                    {
                        restoreDir = GetCurrentDirectory();
                    }
                    SetCurrentDirectory(CombineRemotePath(remoteDirectoryName, dir.Name));
                    dir.IsDirectory = true;
                    dir.SymLinkTargetPath = GetCurrentDirectory();
                }
                catch (FTPCommandException ex) when (ex.ErrorCode == 550)
                {
                    dir.IsDirectory = false;
                }
            }
        }
        finally
        {
            if (restoreDir != null)
            {
                SetCurrentDirectory(restoreDir);
            }
        }
    }

    private async Task CheckSymLinksAsync(string remoteDirectoryName, IList<DirectoryListItem> dirList, CancellationToken cancellationToken)
    {
        string restoreDir = null;
        try
        {
            foreach (DirectoryListItem dir in dirList)
            {
                if (!dir.IsSymLink) continue;
                try
                {
                    if (restoreDir == null)
                    {
                        restoreDir = await GetCurrentDirectoryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    await SetCurrentDirectoryAsync(CombineRemotePath(remoteDirectoryName, dir.Name), cancellationToken).ConfigureAwait(false);
                    dir.IsDirectory = true;
                    dir.SymLinkTargetPath = await GetCurrentDirectoryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (FTPCommandException ex) when (ex.ErrorCode == 550)
                {
                    dir.IsDirectory = false;
                }
            }
        }
        finally
        {
            if (restoreDir != null)
            {
                await SetCurrentDirectoryAsync(restoreDir, cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
