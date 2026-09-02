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

// Control channel transport: connection, TLS wrapping, command/reply exchange and its lock.
public sealed partial class FTPSClient
{
    private void SetupCtrlConnection(string hostname, int port)
    {
        CloseCtrlConnection();
        ctrlClient = ConnectWithTimeout(hostname, port);
        Stream stream = ctrlClient.GetStream();
        stream.ReadTimeout = timeout;
        stream.WriteTimeout = timeout;
        SetupCtrlStreamReaderAndWriter(stream);
    }

    // TcpClient's connecting constructor blocks on the OS default, ignoring the configured timeout.
    private TcpClient ConnectWithTimeout(string host, int port)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress literal)
            ? new IPAddress[1] { literal }
            : Dns.GetHostAddresses(host);
        if (addresses.Length == 0)
        {
            throw new FTPException("Could not resolve " + host);
        }
        Exception lastError = null;
        foreach (IPAddress address in addresses)
        {
            // One socket per address family. A parameterless TcpClient would open a dual mode IPv6
            // socket even for an IPv4 server, and the control channel's family is what decides
            // between PASV/PORT and EPSV/EPRT.
            TcpClient client = new TcpClient(address.AddressFamily);
            try
            {
                IAsyncResult ar = client.BeginConnect(address, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeout))
                {
                    throw new FTPException("Timeout connecting to " + host + ":" + port);
                }
                client.EndConnect(ar);
                return client;
            }
            catch (Exception ex)
            {
                client.Close();
                lastError = ex;
            }
        }
        throw new FTPException("Could not connect to " + host + ":" + port, lastError);
    }

    private void SetupCtrlStreamReaderAndWriter(Stream s)
    {
        if (ctrlSw != null)
        {
            ctrlSw.Flush();
        }
        Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        ctrlSr = new StreamReader(s, encoding);
        ctrlSw = new StreamWriter(s, encoding);
        ctrlSw.NewLine = "\r\n";
    }

    private void SwitchCtrlToSSLMode()
    {
        ctrlSslStream = CreateSSlStream(ctrlClient.GetStream(), leaveInnerStreamOpen: true);
        SetupCtrlStreamReaderAndWriter(ctrlSslStream);
        SetSslInfo(ctrlSslStream);
    }

    private SslStream CreateSSlStream(Stream s, bool leaveInnerStreamOpen)
    {
        SslStream sslStream = new SslStream(s, leaveInnerStreamOpen, ValidateServerCertificate, null);
        sslStream.ReadTimeout = timeout;
        sslStream.WriteTimeout = timeout;
        X509CertificateCollection x509CertificateCollection = new X509CertificateCollection();
        if (sslClientCert != null)
        {
            x509CertificateCollection.Add(sslClientCert);
        }
        sslStream.AuthenticateAsClient(hostname, x509CertificateCollection, sslProtocols, sslCheckCertRevocation);
        CheckSslAlgorithmsStrength(sslStream);
        return sslStream;
    }

    private void CheckSslAlgorithmsStrength(SslStream sslStream)
    {
        if (sslMinKeyExchangeAlgStrength > 0 && sslStream.KeyExchangeStrength < sslMinKeyExchangeAlgStrength)
        {
            throw new FTPSslException("The SSL/TSL key exchange algorithm strength does not fulfill the requirements: " + sslStream.KeyExchangeStrength);
        }
        if (sslMinCipherAlgStrength > 0 && sslStream.CipherStrength < sslMinCipherAlgStrength)
        {
            throw new FTPSslException("The SSL/TSL cipher algorithm strength does not fulfill the requirements: " + sslStream.CipherStrength);
        }
        if (sslMinHashAlgStrength > 0 && sslStream.HashStrength < sslMinHashAlgStrength)
        {
            throw new FTPSslException("The SSL/TSL hash algorithm strength does not fulfill the requirements: " + sslStream.HashStrength);
        }
    }

    private void SwitchCtrlToClearMode()
    {
        ctrlSslStream.Close();
        ctrlSslStream = null;
        SetupCtrlStreamReaderAndWriter(ctrlClient.GetStream());
    }

    private bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
    {
        // The data channel handshake reuses the decision made for the control channel, so the two
        // certificates are compared by their full encoding: X509Certificate.Equals only compares the
        // issuer name and serial number, which a substituted certificate can trivially reproduce.
        byte[] rawData = certificate?.Export(X509ContentType.Cert);
        if (rawData != null && RawDataEquals(sslServerCertRawData, rawData))
        {
            return true;
        }
        bool flag = true;
        if (userValidateServerCertificate != null)
        {
            flag = userValidateServerCertificate(this, certificate, chain, sslPolicyErrors);
        }
        else if (sslPolicyErrors != SslPolicyErrors.None)
        {
            flag = false;
        }
        if (flag)
        {
            sslServerCertRawData = rawData;
        }
        return flag;
    }

    private static bool RawDataEquals(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private FTPReply HandleCmd(string command)
    {
        return HandleCmd(command, waitForAnswer: true);
    }

    private FTPReply HandleCmd(string command, bool waitForAnswer)
    {
        lock (ctrlChannelLock)
        {
            CheckConnection();
            CheckCommandInjection(command);
            ctrlSw.WriteLine(command);
            ctrlSw.Flush();
            this.LogCommand?.Invoke(this, new LogCommandEventArgs(MaskCredentials(command)));
            if (!waitForAnswer) return null;
            return GetReply();
        }
    }

    internal static string MaskCredentials(string command)
    {
        if (command.StartsWith("PASS ", StringComparison.OrdinalIgnoreCase))
        {
            return "PASS ****";
        }
        return command;
    }

    private void CheckConnection()
    {
        if (ctrlClient == null) throw new FTPException("Not connected");
    }

    internal static void CheckCommandInjection(string command)
    {
        // A bare CR or LF is enough: most servers accept either as a command terminator, so a remote
        // name carrying one would smuggle a second command onto the control channel.
        if (command.IndexOf('\r') >= 0 || command.IndexOf('\n') >= 0)
        {
            throw new FTPException("Newlines not allowed in command text");
        }
    }

    private FTPReply GetReply()
    {
        lock (ctrlChannelLock)
        {
            return GetReplyCore();
        }
    }

    private FTPReply GetReplyCore()
    {
        try
        {
            FTPReply fTPReply = new FTPReply();
            bool flag = false;
            do
            {
                string text = ctrlSr.ReadLine();
                if (text == null)
                {
                    throw new FTPException("The server closed the control connection");
                }
                Match match = Regex.Match(text, "^([0-9]{3})([\\s\\-])(.*)$");
                if (match.Success)
                {
                    int num = int.Parse(match.Groups[1].Value);
                    string value = match.Groups[3].Value;
                    flag = match.Groups[2].Value == " ";
                    if (fTPReply.Code == 0)
                    {
                        fTPReply.Code = num;
                        fTPReply.Message = value;
                        continue;
                    }
                    if (fTPReply.Code != num)
                    {
                        throw new FTPReplyParseException(text);
                    }
                    fTPReply.Message = fTPReply.Message + "\r\n" + value;
                }
                else
                {
                    if (fTPReply.Code == 0)
                    {
                        throw new FTPReplyParseException(text);
                    }
                    fTPReply.Message = fTPReply.Message + "\r\n" + text.TrimStart(Array.Empty<char>());
                }
            } while (!flag);
            waitingCompletionReply = fTPReply.Code < 200;
            this.LogServerReply?.Invoke(this, new LogServerReplyEventArgs(fTPReply));
            if (fTPReply.Code >= 400)
            {
                throw new FTPCommandException(fTPReply);
            }
            return fTPReply;
        }
        catch (Exception)
        {
            waitingCompletionReply = false;
            throw;
        }
    }

    private void CloseCtrlConnection()
    {
        if (ctrlClient != null)
        {
            try
            {
                QuitCmd(waitForAnswer: false);
            }
            catch (Exception) { }
            if (ctrlSslStream != null)
            {
                ctrlSslStream.Close();
                ctrlSslStream = null;
            }
            ctrlSr.Close();
            ctrlSr = null;
            ctrlSw.Close();
            ctrlSw = null;
            ctrlClient.Close();
            ctrlClient = null;
            waitingCompletionReply = false;
        }
    }
}
