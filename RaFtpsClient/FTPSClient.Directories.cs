using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RaFtpsClient;

// Directory listing and working directory operations.
// Sync and async forms sit next to each other; see the note in FTPSClient.ControlChannel.cs.
public sealed partial class FTPSClient
{
    // ----- create / remove / navigate -------------------------------------------------------------

    /// <summary>Creates a remote directory.</summary>
    /// <param name="remoteDirName">The directory name.</param>
    public void MakeDir(string remoteDirName)
    {
        HandleCmd(Cmd.Mkd(remoteDirName));
    }

    /// <summary>Creates a remote directory, asynchronously.</summary>
    /// <param name="remoteDirName">The directory name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task MakeDirAsync(string remoteDirName, CancellationToken cancellationToken = default)
    {
        return HandleCmdAsync(Cmd.Mkd(remoteDirName), cancellationToken);
    }

    /// <summary>Removes a remote directory.</summary>
    /// <param name="remoteDirName">The directory name.</param>
    public void RemoveDir(string remoteDirName)
    {
        HandleCmd(Cmd.Rmd(remoteDirName));
    }

    /// <summary>Removes a remote directory, asynchronously.</summary>
    /// <param name="remoteDirName">The directory name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task RemoveDirAsync(string remoteDirName, CancellationToken cancellationToken = default)
    {
        return HandleCmdAsync(Cmd.Rmd(remoteDirName), cancellationToken);
    }

    /// <summary>Changes to the parent directory.</summary>
    public void ChangeToUpperDir()
    {
        HandleCmd(Cmd.Cdup);
    }

    /// <summary>Changes to the parent directory, asynchronously.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task ChangeToUpperDirAsync(CancellationToken cancellationToken = default)
    {
        return HandleCmdAsync(Cmd.Cdup, cancellationToken);
    }

    /// <summary>Gets the current remote working directory path.</summary>
    /// <returns>The current directory path.</returns>
    public string GetCurrentDirectory()
    {
        return ParsePwdReply(HandleCmd(Cmd.Pwd));
    }

    /// <summary>Gets the current remote working directory path, asynchronously.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The current directory path.</returns>
    public async Task<string> GetCurrentDirectoryAsync(CancellationToken cancellationToken = default)
    {
        return ParsePwdReply(await HandleCmdAsync(Cmd.Pwd, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Changes the current remote working directory.</summary>
    /// <param name="remoteDirName">The directory path to change to.</param>
    public void SetCurrentDirectory(string remoteDirName)
    {
        HandleCmd(Cmd.Cwd(remoteDirName));
    }

    /// <summary>Changes the current remote working directory, asynchronously.</summary>
    /// <param name="remoteDirName">The directory path to change to.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task SetCurrentDirectoryAsync(string remoteDirName, CancellationToken cancellationToken = default)
    {
        return HandleCmdAsync(Cmd.Cwd(remoteDirName), cancellationToken);
    }

    /// <summary>Saves the current directory and pushes it onto the stack.</summary>
    /// <returns>The saved directory path.</returns>
    public string PushCurrentDirectory()
    {
        string currentDirectory = GetCurrentDirectory();
        currDirStack.Push(currentDirectory);
        return currentDirectory;
    }

    /// <summary>Saves the current directory and pushes it onto the stack, asynchronously.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The saved directory path.</returns>
    public async Task<string> PushCurrentDirectoryAsync(CancellationToken cancellationToken = default)
    {
        string currentDirectory = await GetCurrentDirectoryAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Restores the previously saved directory, asynchronously.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The restored directory path.</returns>
    public async Task<string> PopCurrentDirectoryAsync(CancellationToken cancellationToken = default)
    {
        string text = currDirStack.Pop();
        await SetCurrentDirectoryAsync(text, cancellationToken).ConfigureAwait(false);
        return text;
    }

    // ----- listings -------------------------------------------------------------------------------

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
        HandleCmd(Cmd.Nlst(remoteDirName));
        string dataString = GetDataString();
        ReadTransferCompletionReply();
        return SplitShortListing(dataString);
    }

    /// <summary>Gets a short listing of file names, asynchronously.</summary>
    /// <param name="remoteDirName">The remote directory, or null for current.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A list of file names.</returns>
    public async Task<IList<string>> GetShortDirectoryListAsync(string remoteDirName = null, CancellationToken cancellationToken = default)
    {
        await SetupDataConnectionAsync(cancellationToken).ConfigureAwait(false);
        await HandleCmdAsync(Cmd.Nlst(remoteDirName), cancellationToken).ConfigureAwait(false);
        string dataString = await GetDataStringAsync(cancellationToken).ConfigureAwait(false);
        await ReadTransferCompletionReplyAsync(cancellationToken).ConfigureAwait(false);
        return SplitShortListing(dataString);
    }

    private static IList<string> SplitShortListing(string dataString)
    {
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

    /// <summary>Gets a detailed directory listing, asynchronously.</summary>
    /// <param name="remoteDirName">The remote directory, or null for current.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A list of directory items.</returns>
    public async Task<IList<DirectoryListItem>> GetDirectoryListAsync(string remoteDirName = null, CancellationToken cancellationToken = default)
    {
        return DirectoryListParser.GetDirectoryList(await GetDirectoryListUnparsedAsync(remoteDirName, cancellationToken).ConfigureAwait(false));
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
        HandleCmd(Cmd.List(remoteDirName));
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

    /// <summary>Gets the raw directory listing text, asynchronously.</summary>
    /// <param name="remoteDirName">The remote directory, or null for current.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The raw listing string.</returns>
    public async Task<string> GetDirectoryListUnparsedAsync(string remoteDirName = null, CancellationToken cancellationToken = default)
    {
        await SetupDataConnectionAsync(cancellationToken).ConfigureAwait(false);
        await HandleCmdAsync(Cmd.List(remoteDirName), cancellationToken).ConfigureAwait(false);
        string dataString = await GetDataStringAsync(cancellationToken).ConfigureAwait(false);
        await ReadTransferCompletionReplyAsync(cancellationToken).ConfigureAwait(false);
        if (dataString.Length == 0 && !string.IsNullOrEmpty(remoteDirName))
        {
            await PushCurrentDirectoryAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await SetCurrentDirectoryAsync(remoteDirName, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await PopCurrentDirectoryAsync(cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            }
        }
        return dataString;
    }

    internal static string CombineRemotePath(string path1, string path2)
    {
        return (path1.EndsWith("/") ? path1 : (path1 + "/")) + path2;
    }
}
