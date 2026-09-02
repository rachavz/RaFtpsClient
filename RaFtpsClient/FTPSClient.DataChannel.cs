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

// Data channel transport: passive/active setup, stream access and completion handling.
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

    private Stream GetDataStream()
    {
        if (dataConnectionMode == EDataConnectionMode.Active)
        {
            SetupActiveDataConnectionStep2();
        }
        if ((sslSupportCurrentMode & ESSLSupportMode.DataChannelRequested) == ESSLSupportMode.DataChannelRequested)
        {
            if (dataSslStream == null)
            {
                dataSslStream = CreateSSlStream(dataClient.GetStream(), leaveInnerStreamOpen: false);
            }
            return dataSslStream;
        }
        return dataClient.GetStream();
    }

    private string GetDataString()
    {
        try
        {
            return ReadStreamAsUtf8(GetDataStream(), transferBufferSize);
        }
        finally
        {
            CloseDataConnection();
        }
    }

    internal static string ReadStreamAsUtf8(Stream stream, int bufferSize)
    {
        StringBuilder stringBuilder = new StringBuilder();
        // A shared Decoder carries partial multi-byte sequences across read boundaries. Reads land
        // wherever the network splits them, so decoding each one on its own turns any character
        // unlucky enough to straddle a boundary into U+FFFD.
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

    private void StopActiveDataConnListener()
    {
        activeDataConnListener.Stop();
        activeDataConnListener = null;
    }

    private void SetupPassiveDataConnection(IPEndPoint dataEndPoint)
    {
        CloseDataConnection();
        IPAddress iPAddress = ((!useCtrlEndPointAddressForData) ? dataEndPoint.Address : (ctrlClient.Client.RemoteEndPoint as IPEndPoint).Address);
        dataClient = new TcpClient(iPAddress.ToString(), dataEndPoint.Port);
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
        for (num = 0; num < array2.Length; num++)
        {
            array2[num] = byte.Parse(array[num]);
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

    private void PortCmd()
    {
        int num = SetupActiveDataConnectionStep1();
        byte[] addressBytes = (ctrlClient.Client.LocalEndPoint as IPEndPoint).Address.GetAddressBytes();
        string text = string.Format("{0},{1},{2},{3},{4},{5}", new object[6]
        {
            addressBytes[0], addressBytes[1], addressBytes[2], addressBytes[3],
            num / 256, num % 256
        });
        HandleCmd("PORT " + text);
    }

    // PORT can only carry a 4-byte address; EPRT (RFC 2428) is the IPv6 equivalent.
    private void EprtCmd()
    {
        int num = SetupActiveDataConnectionStep1();
        IPAddress address = (ctrlClient.Client.LocalEndPoint as IPEndPoint).Address;
        int protocol = ((address.AddressFamily == AddressFamily.InterNetworkV6) ? 2 : 1);
        HandleCmd("EPRT |" + protocol + "|" + address + "|" + num + "|");
    }

    private void PasvCmd()
    {
        IPEndPoint dataEndPoint = ParsePasvReply(HandleCmd("PASV"));
        SetupPassiveDataConnection(dataEndPoint);
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
            if (isIPv4)
            {
                PortCmd();
            }
            else
            {
                EprtCmd();
            }
        }
        else if (isIPv4)
        {
            PasvCmd();
        }
        else
        {
            EpsvCmd();
        }
    }

    private void EpsvCmd()
    {
        FTPReply reply = HandleCmd("EPSV");
        IPEndPoint dataEndPoint = ParseEpsvReply(reply);
        SetupPassiveDataConnection(dataEndPoint);
    }
}
