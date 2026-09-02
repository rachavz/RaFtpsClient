using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RaFtpsClient;

// Data channel transport: passive/active setup, stream access and completion handling.
// Sync and async forms sit next to each other; see the note in FTPSClient.ControlChannel.cs.
public sealed partial class FTPSClient
{
    private FTPStream EndStreamCommand(FTPStream.EAllowedOperation allowedOp)
    {
        return new FTPStream(GetDataStream(), allowedOp, delegate
        {
            CloseDataConnection();
            ReadTransferCompletionReply();
        });
    }

    // Only servers that answered the transfer command with a 1xx preliminary reply still owe a
    // completion reply; reading unconditionally would block against the ones that do not.
    private void ReadTransferCompletionReply()
    {
        if (waitingCompletionReply)
        {
            GetReply();
        }
    }

    private async Task ReadTransferCompletionReplyAsync(CancellationToken cancellationToken)
    {
        if (waitingCompletionReply)
        {
            await GetReplyAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsDataChannelEncryptionRequested =>
        (sslSupportCurrentMode & ESSLSupportMode.DataChannelRequested) == ESSLSupportMode.DataChannelRequested;

    private Stream GetDataStream()
    {
        if (dataConnectionMode == EDataConnectionMode.Active)
        {
            SetupActiveDataConnectionStep2();
        }
        if (IsDataChannelEncryptionRequested)
        {
            if (dataSslStream == null)
            {
                dataSslStream = CreateSslStream(dataClient.GetStream(), leaveInnerStreamOpen: false);
            }
            return dataSslStream;
        }
        return dataClient.GetStream();
    }

    private async Task<Stream> GetDataStreamAsync(CancellationToken cancellationToken)
    {
        if (dataConnectionMode == EDataConnectionMode.Active)
        {
            await SetupActiveDataConnectionStep2Async(cancellationToken).ConfigureAwait(false);
        }
        if (IsDataChannelEncryptionRequested)
        {
            if (dataSslStream == null)
            {
                dataSslStream = await CreateSslStreamAsync(dataClient.GetStream(), leaveInnerStreamOpen: false, cancellationToken).ConfigureAwait(false);
            }
            return dataSslStream;
        }
        return dataClient.GetStream();
    }

    private string GetDataString()
    {
        try
        {
            return ReadStreamAsUtf8(GetDataStream(), listingBufferSize);
        }
        finally
        {
            CloseDataConnection();
        }
    }

    private async Task<string> GetDataStringAsync(CancellationToken cancellationToken)
    {
        try
        {
            Stream dataStream = await GetDataStreamAsync(cancellationToken).ConfigureAwait(false);
            return await ReadStreamAsUtf8Async(dataStream, listingBufferSize, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CloseDataConnection();
        }
    }

    // A shared Decoder carries partial multi-byte sequences across read boundaries. Reads land
    // wherever the network splits them, so decoding each one on its own turns any character
    // unlucky enough to straddle a boundary into U+FFFD.
    internal static string ReadStreamAsUtf8(Stream stream, int bufferSize)
    {
        StringBuilder stringBuilder = new StringBuilder();
        Decoder decoder = Encoding.UTF8.GetDecoder();
        byte[] buffer = new byte[bufferSize];
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(bufferSize)];
        int read;
        do
        {
            read = stream.Read(buffer, 0, buffer.Length);
            int charCount = decoder.GetChars(buffer, 0, read, chars, 0, read == 0);
            stringBuilder.Append(chars, 0, charCount);
        } while (read != 0);
        return stringBuilder.ToString();
    }

    internal static async Task<string> ReadStreamAsUtf8Async(Stream stream, int bufferSize, int perReadTimeout, CancellationToken cancellationToken)
    {
        StringBuilder stringBuilder = new StringBuilder();
        Decoder decoder = Encoding.UTF8.GetDecoder();
        byte[] buffer = new byte[bufferSize];
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(bufferSize)];
        using (CancellationTokenSource scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            int read;
            do
            {
                // Re-arming before each read gives the async path the same per-operation semantics
                // as ReadTimeout on the synchronous one.
                scope.CancelAfter(perReadTimeout);
                try
                {
                    read = await stream.ReadAsync(buffer, 0, buffer.Length, scope.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new FTPException("Timeout reading from the data connection");
                }
                int charCount = decoder.GetChars(buffer, 0, read, chars, 0, read == 0);
                stringBuilder.Append(chars, 0, charCount);
            } while (read != 0);
        }
        return stringBuilder.ToString();
    }

    // ----- active mode ----------------------------------------------------------------------------

    private int SetupActiveDataConnectionStep1()
    {
        CloseDataConnection();
        // Bind to the control connection's own local address so the listener matches the address
        // family we advertise, and so an IPv6 control channel gets an IPv6 listener.
        IPEndPoint localEP = new IPEndPoint(((IPEndPoint)ctrlClient.Client.LocalEndPoint).Address, 0);
        activeDataConnListener = new TcpListener(localEP);
        activeDataConnListener.Start();
        return (activeDataConnListener.LocalEndpoint as IPEndPoint).Port;
    }

    private void SetupActiveDataConnectionStep2()
    {
        try
        {
            int micros = (timeout > int.MaxValue / 1000) ? int.MaxValue : timeout * 1000;
            if (!activeDataConnListener.Server.Poll(micros, SelectMode.SelectRead))
            {
                throw new FTPException("Timeout waiting for the server to open the data connection");
            }
            dataClient = activeDataConnListener.AcceptTcpClient();
            SetDataClientTimeout();
        }
        finally
        {
            StopActiveDataConnListener();
        }
    }

    private async Task SetupActiveDataConnectionStep2Async(CancellationToken cancellationToken)
    {
        Task<TcpClient> accept = activeDataConnListener.AcceptTcpClientAsync();
        try
        {
            using (CancellationTokenSource scope = TimeoutScope(cancellationToken))
            {
                Task finished = await Task.WhenAny(accept, Task.Delay(Timeout.Infinite, scope.Token)).ConfigureAwait(false);
                if (finished != accept)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new FTPException("Timeout waiting for the server to open the data connection");
                }
            }
            dataClient = await accept.ConfigureAwait(false);
            SetDataClientTimeout();
        }
        finally
        {
            // Stopping the listener faults an abandoned accept that nobody awaits; observe it so it
            // stays off TaskScheduler.UnobservedTaskException.
            StopActiveDataConnListener();
            if (!accept.IsCompleted)
            {
                _ = accept.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
        }
    }

    private void StopActiveDataConnListener()
    {
        activeDataConnListener.Stop();
        activeDataConnListener = null;
    }

    // ----- passive mode ---------------------------------------------------------------------------

    private IPAddress DataConnectionAddress(IPEndPoint dataEndPoint)
    {
        return useCtrlEndPointAddressForData ? (ctrlClient.Client.RemoteEndPoint as IPEndPoint).Address : dataEndPoint.Address;
    }

    private void SetupPassiveDataConnection(IPEndPoint dataEndPoint)
    {
        CloseDataConnection();
        dataClient = ConnectWithTimeout(DataConnectionAddress(dataEndPoint).ToString(), dataEndPoint.Port);
        SetDataClientTimeout();
    }

    private async Task SetupPassiveDataConnectionAsync(IPEndPoint dataEndPoint, CancellationToken cancellationToken)
    {
        CloseDataConnection();
        dataClient = await ConnectWithTimeoutAsync(DataConnectionAddress(dataEndPoint).ToString(), dataEndPoint.Port, cancellationToken).ConfigureAwait(false);
        SetDataClientTimeout();
    }

    private void SetDataClientTimeout()
    {
        NetworkStream stream = dataClient.GetStream();
        stream.ReadTimeout = timeout;
        stream.WriteTimeout = timeout;
    }

    internal static IPEndPoint ParsePasvReply(FTPReply reply)
    {
        int num = reply.Message.IndexOf('(');
        if (num < 0) throw new FTPProtocolException(reply);
        int num2 = reply.Message.IndexOf(')', num + 1);
        if (num2 < 0) throw new FTPProtocolException(reply);
        string[] array = reply.Message.Substring(num + 1, num2 - num - 1).Split(new char[1] { ',' });
        if (array.Length != 6) throw new FTPProtocolException(reply);
        byte[] array2 = new byte[4];
        for (int i = 0; i < array2.Length; i++)
        {
            array2[i] = byte.Parse(array[i]);
        }
        int port = byte.Parse(array[4]) * 256 + byte.Parse(array[5]);
        return new IPEndPoint(new IPAddress(array2), port);
    }

    private IPEndPoint ParseEpsvReply(FTPReply reply)
    {
        // EPSV returns only a port: the address is always the server's, i.e. the control channel's
        // remote end, never the client's own local endpoint.
        return new IPEndPoint(((IPEndPoint)ctrlClient.Client.RemoteEndPoint).Address, ParseEpsvPort(reply));
    }

    internal static int ParseEpsvPort(FTPReply reply)
    {
        string[] array = reply.Message.Split(new char[1] { '|' });
        if (array.Length != 5) throw new FTPProtocolException(reply);
        return int.Parse(array[3]);
    }

    private void CloseDataConnection()
    {
        if (dataClient != null)
        {
            if (dataSslStream != null)
            {
                dataSslStream.Close();
                dataSslStream = null;
            }
            dataClient.Close();
            dataClient = null;
        }
        if (activeDataConnListener != null)
        {
            StopActiveDataConnListener();
        }
    }

    // ----- choosing and issuing the data connection command ---------------------------------------

    private string PortCommand()
    {
        int port = SetupActiveDataConnectionStep1();
        byte[] addressBytes = (ctrlClient.Client.LocalEndPoint as IPEndPoint).Address.GetAddressBytes();
        return string.Format("PORT {0},{1},{2},{3},{4},{5}", new object[6]
        {
            addressBytes[0], addressBytes[1], addressBytes[2], addressBytes[3],
            port / 256, port % 256
        });
    }

    // PORT can only carry a 4-byte address; EPRT (RFC 2428) is the IPv6 equivalent.
    private string EprtCommand()
    {
        int port = SetupActiveDataConnectionStep1();
        IPAddress address = (ctrlClient.Client.LocalEndPoint as IPEndPoint).Address;
        int protocol = ((address.AddressFamily == AddressFamily.InterNetworkV6) ? 2 : 1);
        return "EPRT |" + protocol + "|" + address + "|" + port + "|";
    }

    private AddressFamily GetCtrlConnAddressFamily()
    {
        return ((IPEndPoint)ctrlClient.Client.LocalEndPoint).AddressFamily;
    }

    private void SetupDataConnection()
    {
        bool isIPv4 = GetCtrlConnAddressFamily() == AddressFamily.InterNetwork;
        if (dataConnectionMode == EDataConnectionMode.Active)
        {
            HandleCmd(isIPv4 ? PortCommand() : EprtCommand());
        }
        else if (isIPv4)
        {
            SetupPassiveDataConnection(ParsePasvReply(HandleCmd("PASV")));
        }
        else
        {
            SetupPassiveDataConnection(ParseEpsvReply(HandleCmd("EPSV")));
        }
    }

    private async Task SetupDataConnectionAsync(CancellationToken cancellationToken)
    {
        bool isIPv4 = GetCtrlConnAddressFamily() == AddressFamily.InterNetwork;
        if (dataConnectionMode == EDataConnectionMode.Active)
        {
            await HandleCmdAsync(isIPv4 ? PortCommand() : EprtCommand(), cancellationToken).ConfigureAwait(false);
        }
        else if (isIPv4)
        {
            FTPReply reply = await HandleCmdAsync("PASV", cancellationToken).ConfigureAwait(false);
            await SetupPassiveDataConnectionAsync(ParsePasvReply(reply), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            FTPReply reply = await HandleCmdAsync("EPSV", cancellationToken).ConfigureAwait(false);
            await SetupPassiveDataConnectionAsync(ParseEpsvReply(reply), cancellationToken).ConfigureAwait(false);
        }
    }
}
