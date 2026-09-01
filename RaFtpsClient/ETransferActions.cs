namespace RaFtpsClient;

/// <summary>
/// Indicates the action being performed during a file transfer operation.
/// </summary>
public enum ETransferActions
{
    /// <summary>A local directory was created.</summary>
    LocalDirectoryCreated,
    /// <summary>A remote directory was created.</summary>
    RemoteDirectoryCreated,
    /// <summary>A file upload completed.</summary>
    FileUploaded,
    /// <summary>A file upload is in progress.</summary>
    FileUploadingStatus,
    /// <summary>A file download completed.</summary>
    FileDownloaded,
    /// <summary>A file download is in progress.</summary>
    FileDownloadingStatus
}
