using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: TargetFramework(".NETStandard,Version=v2.0", FrameworkDisplayName = "")]
[assembly: AssemblyCompany("Raúl Chávez Vázquez")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyCopyright("Copyright (c)  2022 - 2099")]
[assembly: AssemblyDescription("is a free FTP/FTPS client and class library available on any platform supporting the .NetStandard 2.0. It's written to overcome the limits of .Net's System.Net.FTPWebRequest in terms of FTPS support")]
[assembly: AssemblyFileVersion("22.05.31.2")]
[assembly: AssemblyInformationalVersion("22.05.31.2")]
[assembly: AssemblyProduct("RaFtpsClient")]
[assembly: AssemblyTitle("RaFtpsClient")]
[assembly: AssemblyVersion("22.5.31.2")]
namespace RaFtpsClient;

public enum ETransferMode
{
	ASCII,
	Binary
}
public enum ETextEncoding
{
	ASCII,
	UTF8
}
public class FTPReply
{
	private int code;

	private string message;

	public int Code
	{
		get
		{
			return code;
		}
		set
		{
			code = value;
		}
	}

	public string Message
	{
		get
		{
			return message;
		}
		set
		{
			message = value;
		}
	}

	public override string ToString()
	{
		return $"{Code} {Message}";
	}
}
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

	public ulong Size
	{
		get
		{
			return size;
		}
		set
		{
			size = value;
		}
	}

	public string SymLinkTargetPath
	{
		get
		{
			return symLinkTargetPath;
		}
		set
		{
			symLinkTargetPath = value;
		}
	}

	public string Flags
	{
		get
		{
			return flags;
		}
		set
		{
			flags = value;
		}
	}

	public string Owner
	{
		get
		{
			return owner;
		}
		set
		{
			owner = value;
		}
	}

	public string Group
	{
		get
		{
			return group;
		}
		set
		{
			group = value;
		}
	}

	public bool IsDirectory
	{
		get
		{
			return isDirectory;
		}
		set
		{
			isDirectory = value;
		}
	}

	public bool IsSymLink
	{
		get
		{
			return isSymLink;
		}
		set
		{
			isSymLink = value;
		}
	}

	public string Name
	{
		get
		{
			return name;
		}
		set
		{
			name = value;
		}
	}

	public DateTime CreationTime
	{
		get
		{
			return creationTime;
		}
		set
		{
			creationTime = value;
		}
	}
}
public class SslInfo
{
	private SslProtocols sslProtocol;

	private CipherAlgorithmType cipherAlgorithm;

	private int cipherStrength;

	private HashAlgorithmType hashAlgorithm;

	private int hashStrength;

	private ExchangeAlgorithmType keyExchangeAlgorithm;

	private int keyExchangeStrength;

	public SslProtocols SslProtocol
	{
		get
		{
			return sslProtocol;
		}
		set
		{
			sslProtocol = value;
		}
	}

	public CipherAlgorithmType CipherAlgorithm
	{
		get
		{
			return cipherAlgorithm;
		}
		set
		{
			cipherAlgorithm = value;
		}
	}

	public int CipherStrength
	{
		get
		{
			return cipherStrength;
		}
		set
		{
			cipherStrength = value;
		}
	}

	public HashAlgorithmType HashAlgorithm
	{
		get
		{
			return hashAlgorithm;
		}
		set
		{
			hashAlgorithm = value;
		}
	}

	public int HashStrength
	{
		get
		{
			return hashStrength;
		}
		set
		{
			hashStrength = value;
		}
	}

	public ExchangeAlgorithmType KeyExchangeAlgorithm
	{
		get
		{
			return keyExchangeAlgorithm;
		}
		set
		{
			keyExchangeAlgorithm = value;
		}
	}

	public int KeyExchangeStrength
	{
		get
		{
			return keyExchangeStrength;
		}
		set
		{
			keyExchangeStrength = value;
		}
	}

	public override string ToString()
	{
		return SslProtocol.ToString() + ", " + CipherAlgorithm.ToString() + " (" + cipherStrength + " bit), " + KeyExchangeAlgorithm.ToString() + " (" + keyExchangeStrength + " bit), " + HashAlgorithm.ToString() + " (" + hashStrength + " bit)";
	}
}
public class LogCommandEventArgs : EventArgs
{
	public string CommandText { get; private set; }

	public LogCommandEventArgs(string commandText)
	{
		CommandText = commandText;
	}
}
public class LogServerReplyEventArgs : EventArgs
{
	public FTPReply ServerReply { get; private set; }

	public LogServerReplyEventArgs(FTPReply serverReply)
	{
		ServerReply = serverReply;
	}
}
internal class DirectoryListParser
{
	private enum EDirectoryListingStyle
	{
		UnixStyle,
		WindowsStyle,
		Unknown
	}

	private const string unixSymLinkPathSeparator = " -> ";

	public static IList<DirectoryListItem> GetDirectoryList(string datastring)
	{
		try
		{
			List<DirectoryListItem> list = new List<DirectoryListItem>();
			string[] array = datastring.Split(new char[1] { '\n' });
			EDirectoryListingStyle eDirectoryListingStyle = GuessDirectoryListingStyle(array);
			string[] array2 = array;
			foreach (string text in array2)
			{
				if (eDirectoryListingStyle != EDirectoryListingStyle.Unknown && text != "")
				{
					DirectoryListItem directoryListItem = new DirectoryListItem();
					directoryListItem.Name = "..";
					switch (eDirectoryListingStyle)
					{
					case EDirectoryListingStyle.UnixStyle:
						directoryListItem = ParseDirectoryListItemFromUnixStyleRecord(text);
						break;
					case EDirectoryListingStyle.WindowsStyle:
						directoryListItem = ParseDirectoryListItemFromWindowsStyleRecord(text);
						break;
					}
					if (directoryListItem != null && !(directoryListItem.Name == ".") && !(directoryListItem.Name == ".."))
					{
						list.Add(directoryListItem);
					}
				}
			}
			return list;
		}
		catch (Exception innerException)
		{
			throw new FTPException("Unable to parse the directory list", innerException);
		}
	}

	private static DirectoryListItem ParseDirectoryListItemFromWindowsStyleRecord(string record)
	{
		DirectoryListItem directoryListItem = new DirectoryListItem();
		string text = record.Trim();
		string text2 = text.Substring(0, 8);
		text = text.Substring(8, text.Length - 8).Trim();
		string text3 = text.Substring(0, 7);
		text = text.Substring(7, text.Length - 7).Trim();
		directoryListItem.CreationTime = DateTime.Parse(text2 + " " + text3, CultureInfo.GetCultureInfo("en-US"));
		if (text.Substring(0, 5) == "<DIR>")
		{
			directoryListItem.IsDirectory = true;
			text = text.Substring(5, text.Length - 5).Trim();
		}
		else
		{
			directoryListItem.IsDirectory = false;
			int num = text.IndexOf(' ');
			directoryListItem.Size = ulong.Parse(text.Substring(0, num));
			text = text.Substring(num + 1);
		}
		directoryListItem.Name = text;
		return directoryListItem;
	}

	private static EDirectoryListingStyle GuessDirectoryListingStyle(string[] recordList)
	{
		foreach (string text in recordList)
		{
			if (text.Length > 10 && Regex.IsMatch(text.Substring(0, 10), "(-|d)(-|r)(-|w)(-|x)(-|r)(-|w)(-|x)(-|r)(-|w)(-|x)"))
			{
				return EDirectoryListingStyle.UnixStyle;
			}
			if (text.Length > 8 && Regex.IsMatch(text.Substring(0, 8), "[0-9][0-9]-[0-9][0-9]-[0-9][0-9]"))
			{
				return EDirectoryListingStyle.WindowsStyle;
			}
		}
		return EDirectoryListingStyle.Unknown;
	}

	private static DirectoryListItem ParseDirectoryListItemFromUnixStyleRecord(string record)
	{
		if (record.ToLower().StartsWith("total "))
		{
			return null;
		}
		DirectoryListItem directoryListItem = new DirectoryListItem();
		string text = record.Trim();
		directoryListItem.Flags = text.Substring(0, 9);
		directoryListItem.IsDirectory = directoryListItem.Flags[0] == 'd';
		directoryListItem.IsSymLink = directoryListItem.Flags[0] == 'l';
		text = text.Substring(11).Trim();
		CutSubstringFromStringWithTrim(ref text, " ", 0);
		directoryListItem.Owner = CutSubstringFromStringWithTrim(ref text, " ", 0);
		directoryListItem.Group = CutSubstringFromStringWithTrim(ref text, " ", 0);
		directoryListItem.Size = ulong.Parse(CutSubstringFromStringWithTrim(ref text, " ", 0));
		string text2 = CutSubstringFromStringWithTrim(ref text, " ", 8);
		string format = ((text2.IndexOf(':') >= 0) ? "MMM dd H:mm" : "MMM dd yyyy");
		if (text2[4] == ' ')
		{
			text2 = text2.Substring(0, 4) + "0" + text2.Substring(5);
		}
		directoryListItem.CreationTime = DateTime.ParseExact(text2, format, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces);
		if (directoryListItem.IsSymLink && text.IndexOf(" -> ") > 0)
		{
			directoryListItem.Name = CutSubstringFromStringWithTrim(ref text, " -> ", 0);
			directoryListItem.SymLinkTargetPath = text;
		}
		else
		{
			directoryListItem.Name = text;
		}
		return directoryListItem;
	}

	private static string CutSubstringFromStringWithTrim(ref string s, string str, int startIndex)
	{
		int num = s.IndexOf(str, startIndex);
		string result = s.Substring(0, num);
		s = s.Substring(num + str.Length).Trim();
		return result;
	}
}
public class FTPException : Exception
{
	protected FTPException()
	{
	}

	public FTPException(string Message)
		: base(Message)
	{
	}

	public FTPException(string Message, Exception innerException)
		: base(Message, innerException)
	{
	}
}
public class FTPReplyParseException : FTPException
{
	private string replyText;

	public string ReplyText => replyText;

	public FTPReplyParseException(string replyText)
		: base("Invalid server reply: " + replyText)
	{
		this.replyText = replyText;
	}
}
public class FTPProtocolException : FTPException
{
	private FTPReply reply;

	public FTPReply Reply => reply;

	public FTPProtocolException(FTPReply reply)
		: base("Invalid FTP protocol reply: " + reply.ToString())
	{
		this.reply = reply;
	}
}
public class FTPOperationCancelledException : FTPException
{
	public FTPOperationCancelledException(string Message)
		: base(Message)
	{
	}
}
public class FTPCommandException : FTPException
{
	private int errorCode;

	public int ErrorCode => errorCode;

	public FTPCommandException(string Message)
		: base(Message)
	{
	}

	public FTPCommandException(string Message, Exception innerException)
		: base(Message, innerException)
	{
	}

	public FTPCommandException(FTPReply reply)
		: base(reply.Message)
	{
		errorCode = reply.Code;
	}
}
public class FTPSslException : FTPException
{
	public FTPSslException(string Message)
		: base(Message)
	{
	}

	public FTPSslException(string Message, Exception innerException)
		: base(Message, innerException)
	{
	}
}
[Flags]
public enum ESSLSupportMode
{
	ClearText = 0,
	CredentialsRequested = 1,
	CredentialsRequired = 3,
	ControlChannelRequested = 5,
	ControlChannelRequired = 7,
	DataChannelRequested = 9,
	DataChannelRequired = 0x1B,
	ControlAndDataChannelsRequested = 0xD,
	ControlAndDataChannelsRequired = 0x1F,
	All = 0x1F,
	Implicit = 0x3F
}
public enum ETransferActions
{
	LocalDirectoryCreated,
	RemoteDirectoryCreated,
	FileUploaded,
	FileUploadingStatus,
	FileDownloaded,
	FileDownloadingStatus
}
public enum EPatternStyle
{
	Verbatim,
	Wildcard,
	Regex
}
public enum EDataConnectionMode
{
	Active,
	Passive
}
public delegate void FileTransferCallback(FTPSClient sender, ETransferActions action, string localObjectName, string remoteObjectName, ulong fileTransmittedBytes, ulong? fileTransferSize, ref bool cancel);
public delegate void LogCommandEventHandler(object sender, LogCommandEventArgs args);
public delegate void LogServerReplyEventHandler(object sender, LogServerReplyEventArgs args);
public sealed class FTPSClient : IDisposable
{
	private enum EProtCode
	{
		C,
		S,
		E,
		P
	}

	private enum EAuthMechanism
	{
		TLS
	}

	private enum ERepType
	{
		A,
		E,
		I,
		L
	}

	private TcpClient ctrlClient;

	private StreamReader ctrlSr;

	private StreamWriter ctrlSw;

	private SslStream ctrlSslStream;

	private TcpClient dataClient;

	private SslStream dataSslStream;

	private EDataConnectionMode dataConnectionMode = EDataConnectionMode.Passive;

	private bool useCtrlEndPointAddressForData = true;

	private bool waitingCompletionReply;

	private string hostname;

	private const string anonUsername = "anonymous";

	private const string anonPassword = "anonymous@FTPSClient.org";

	private const string clntName = "AlexFTPS";

	private const ESSLSupportMode defaultSSLSupportMode = (ESSLSupportMode)11;

	private ESSLSupportMode sslSupportRequestedMode;

	private ESSLSupportMode sslSupportCurrentMode;

	private X509Certificate sslServerCert;

	private X509Certificate sslClientCert;

	private SslInfo sslInfo;

	private int sslMinKeyExchangeAlgStrength;

	private int sslMinCipherAlgStrength;

	private int sslMinHashAlgStrength;

	private bool sslCheckCertRevocation = true;

	private RemoteCertificateValidationCallback userValidateServerCertificate;

	private int timeout = 120000;

	private IList<string> features;

	private ETransferMode transferMode;

	private ETextEncoding textEncoding;

	private string welcomeMessage;

	private string bannerMessage;

	private Stack<string> currDirStack = new Stack<string>();

	private TcpListener activeDataConnListener;

	private Thread keepAliveThread;

	private volatile bool keepAlive = true;

	private int keepAliveTimeout = 20000;

	public ESSLSupportMode SslSupportRequestedMode => sslSupportRequestedMode;

	public ESSLSupportMode SslSupportCurrentMode => sslSupportCurrentMode;

	public ETextEncoding TextEncoding => textEncoding;

	public ETransferMode TransferMode => transferMode;

	public string WelcomeMessage => welcomeMessage;

	public string BannerMessage => bannerMessage;

	public X509Certificate RemoteCertificate
	{
		get
		{
			if (ctrlSslStream == null)
			{
				return null;
			}
			return ctrlSslStream.RemoteCertificate;
		}
	}

	public SslInfo SslInfo => sslInfo;

	public X509Certificate LocalCertificate
	{
		get
		{
			if (ctrlSslStream == null)
			{
				return null;
			}
			return ctrlSslStream.LocalCertificate;
		}
	}

	public bool KeepAliveStarted => keepAliveThread != null;

	private bool IsControlChannelEncrypted => ctrlSslStream != null;

	private bool IsDataChannelOpen => dataClient != null;

	public event LogCommandEventHandler LogCommand;

	public event LogServerReplyEventHandler LogServerReply;

	public string Connect(string hostname)
	{
		return Connect(hostname, (ESSLSupportMode)11);
	}

	public string Connect(string hostname, ESSLSupportMode sslSupportMode)
	{
		return Connect(hostname, null, sslSupportMode);
	}

	public string Connect(string hostname, NetworkCredential credential)
	{
		return Connect(hostname, credential, (ESSLSupportMode)11);
	}

	public string Connect(string hostname, NetworkCredential credential, ESSLSupportMode sslSupportMode)
	{
		return Connect(hostname, credential, sslSupportMode, null);
	}

	public string Connect(string hostname, NetworkCredential credential, ESSLSupportMode sslSupportMode, RemoteCertificateValidationCallback userValidateServerCertificate)
	{
		int port = (((sslSupportMode & ESSLSupportMode.Implicit) == ESSLSupportMode.Implicit) ? 990 : 21);
		return Connect(hostname, port, credential, sslSupportMode, userValidateServerCertificate, null, 0, 0, 0, null);
	}

	public string Connect(string hostname, int port, NetworkCredential credential, ESSLSupportMode sslSupportMode, RemoteCertificateValidationCallback userValidateServerCertificate)
	{
		return Connect(hostname, port, credential, sslSupportMode, userValidateServerCertificate, null, 0, 0, 0, null);
	}

	public string Connect(string hostname, int port, NetworkCredential credential, ESSLSupportMode sslSupportMode, RemoteCertificateValidationCallback userValidateServerCertificate, X509Certificate x509ClientCert, int sslMinKeyExchangeAlgStrength, int sslMinCipherAlgStrength, int sslMinHashAlgStrength, int? timeout)
	{
		return Connect(hostname, port, credential, sslSupportMode, userValidateServerCertificate, x509ClientCert, sslMinKeyExchangeAlgStrength, sslMinCipherAlgStrength, sslMinHashAlgStrength, timeout, useCtrlEndPointAddressForData: true);
	}

	public string Connect(string hostname, int port, NetworkCredential credential, ESSLSupportMode sslSupportMode, RemoteCertificateValidationCallback userValidateServerCertificate, X509Certificate x509ClientCert, int sslMinKeyExchangeAlgStrength, int sslMinCipherAlgStrength, int sslMinHashAlgStrength, int? timeout, bool useCtrlEndPointAddressForData)
	{
		return Connect(hostname, port, credential, sslSupportMode, userValidateServerCertificate, x509ClientCert, sslMinKeyExchangeAlgStrength, sslMinCipherAlgStrength, sslMinHashAlgStrength, timeout, useCtrlEndPointAddressForData: true, EDataConnectionMode.Passive);
	}

	public string Connect(string hostname, int port, NetworkCredential credential, ESSLSupportMode sslSupportMode, RemoteCertificateValidationCallback userValidateServerCertificate, X509Certificate x509ClientCert, int sslMinKeyExchangeAlgStrength, int sslMinCipherAlgStrength, int sslMinHashAlgStrength, int? timeout, bool useCtrlEndPointAddressForData, EDataConnectionMode dataConnectionMode)
	{
		Close();
		if (credential == null)
		{
			credential = new NetworkCredential("anonymous", "anonymous@FTPSClient.org");
		}
		if (timeout.HasValue)
		{
			this.timeout = timeout.Value;
		}
		sslClientCert = x509ClientCert;
		this.userValidateServerCertificate = userValidateServerCertificate;
		this.sslMinKeyExchangeAlgStrength = sslMinKeyExchangeAlgStrength;
		this.sslMinCipherAlgStrength = sslMinCipherAlgStrength;
		this.sslMinHashAlgStrength = sslMinHashAlgStrength;
		sslSupportRequestedMode = sslSupportMode;
		sslSupportCurrentMode = sslSupportMode;
		this.useCtrlEndPointAddressForData = useCtrlEndPointAddressForData;
		this.dataConnectionMode = dataConnectionMode;
		sslInfo = null;
		features = null;
		transferMode = ETransferMode.ASCII;
		textEncoding = ETextEncoding.ASCII;
		bannerMessage = null;
		welcomeMessage = null;
		currDirStack.Clear();
		SetupCtrlConnection(hostname, port, Encoding.ASCII);
		this.hostname = hostname;
		bool flag = (sslSupportMode & ESSLSupportMode.Implicit) == ESSLSupportMode.Implicit;
		if (flag)
		{
			SwitchCtrlToSSLMode();
		}
		bannerMessage = GetReply().Message;
		if (!flag)
		{
			SslControlChannelCheckExplicitEncryptionRequest(sslSupportMode);
		}
		if (UserCmd(credential.UserName))
		{
			welcomeMessage = PassCmd(credential.Password);
		}
		GetFeaturesFromServer();
		if (IsControlChannelEncrypted)
		{
			if (!flag)
			{
				SslDataChannelCheckExplicitEncryptionRequest();
				if ((sslSupportMode & ESSLSupportMode.ControlChannelRequested) != ESSLSupportMode.ControlChannelRequested)
				{
					SSlCtrlChannelCheckRevertToClearText();
				}
			}
			else
			{
				SslDataChannelImplicitEncryptionRequest();
			}
		}
		try
		{
			if (CheckFeature("CLNT"))
			{
				ClntCmd("AlexFTPS");
			}
			if (CheckFeature("UTF8"))
			{
				SetTextEncoding(ETextEncoding.UTF8);
			}
		}
		catch (Exception)
		{
		}
		SetTransferMode(ETransferMode.Binary);
		return welcomeMessage;
	}

	private void KeepAliveThreadFunc()
	{
		while (keepAlive)
		{
			try
			{
				NoopCmd();
				Thread.Sleep(keepAliveTimeout);
			}
			catch
			{
			}
		}
	}

	public void StartKeepAlive()
	{
		CheckConnection();
		if (keepAliveThread != null)
		{
			throw new FTPException("KeepAlive already started");
		}
		keepAliveThread = new Thread(KeepAliveThreadFunc);
		keepAliveThread.Start();
	}

	public void StopKeepAlive()
	{
		if (keepAliveThread != null)
		{
			keepAlive = false;
			keepAliveThread.Interrupt();
			keepAliveThread.Join();
			keepAliveThread = null;
		}
	}

	private void SslDataChannelImplicitEncryptionRequest()
	{
		try
		{
			if (CheckFeature("AUTH SSL") || CheckFeature("AUTH TLS") || (CheckFeature("PBSZ") && CheckFeature("PROT")))
			{
				PbszCmd(0u);
				ProtCmd(EProtCode.P);
			}
		}
		catch (Exception)
		{
		}
	}

	public void SetTransferMode(ETransferMode transferMode)
	{
		TypeCmd((transferMode != ETransferMode.ASCII) ? ERepType.I : ERepType.A, null);
		this.transferMode = transferMode;
	}

	public IList<string> GetFeatures()
	{
		if (features == null)
		{
			return null;
		}
		return new List<string>(features);
	}

	public ulong? GetFileTransferSize(string remoteFileName)
	{
		if (!CheckFeature("SIZE"))
		{
			return null;
		}
		return SizeCmd(remoteFileName);
	}

	public FTPStream GetFile(string remoteFileName)
	{
		SetupDataConnection();
		RetrCmd(remoteFileName);
		return EndStreamCommand(FTPStream.EAllowedOperation.Read);
	}

	public ulong GetFile(string remoteFileName, string localFileName)
	{
		return GetFile(remoteFileName, localFileName, null);
	}

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
				throw ex;
			}
		}
		using (Stream stream = GetFile(remoteFileName))
		{
			using (FileStream fileStream = new FileStream(localFileName, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				byte[] array = new byte[1024];
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
				}
				while (num2 > 0);
				fileStream.Close();
			}
			stream.Close();
		}
		CallTransferCallback(transferCallback, ETransferActions.FileDownloaded, localFileName, remoteFileName, num, fileTransferSize);
		return num;
	}

	public void GetFiles(string remoteDirectoryName, string localDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive, FileTransferCallback transferCallback)
	{
		GetFiles(remoteDirectoryName, localDirectoryName, filePattern, patternStyle, recursive, transferCallback, new List<string>());
	}

	private void GetFiles(string remoteDirectoryName, string localDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive, FileTransferCallback transferCallback, IList<string> paths)
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
		if (!recursive)
		{
			return;
		}
		foreach (DirectoryListItem item2 in directoryList)
		{
			if (item2.IsDirectory)
			{
				string uniquePath2 = GetUniquePath(paths, Path.Combine(text, PathCheck.GetValidLocalFileName(item2.Name)));
				string remoteDirectoryName2 = CombineRemotePath(text2, item2.Name);
				GetFiles(remoteDirectoryName2, uniquePath2, filePattern, patternStyle, recursive, transferCallback, paths);
			}
		}
	}

	private static string GetUniquePath(IList<string> paths, string localFilePath)
	{
		string text = localFilePath;
		int num = 1;
		while (paths.Contains(text.ToLower()))
		{
			text = localFilePath + "_" + num++;
		}
		paths.Add(text.ToLower());
		return text;
	}

	public void GetFiles(string localDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive)
	{
		GetFiles(null, localDirectoryName, filePattern, patternStyle, recursive, null);
	}

	public void GetFiles(string localDirectoryName, bool recursive)
	{
		GetFiles(null, localDirectoryName, null, EPatternStyle.Verbatim, recursive, null);
	}

	public void GetFiles(string localDirectoryName)
	{
		GetFiles(null, localDirectoryName, null, EPatternStyle.Verbatim, recursive: false, null);
	}

	public FTPStream PutFile(string remoteFileName)
	{
		SetupDataConnection();
		StorCmd(remoteFileName);
		return EndStreamCommand(FTPStream.EAllowedOperation.Write);
	}

	public ulong PutFile(string localFileName, string remoteFileName)
	{
		return PutFile(localFileName, remoteFileName, null);
	}

	public ulong PutFile(string localFileName, string remoteFileName, FileTransferCallback transferCallback)
	{
		using Stream s = PutFile(remoteFileName);
		return SendFile(localFileName, remoteFileName, s, transferCallback);
	}

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
		if (text != null)
		{
			SetCurrentDirectory(text);
		}
	}

	public void PutFiles(string localDirectoryName, string filePattern, EPatternStyle patternStyle, bool recursive)
	{
		PutFiles(localDirectoryName, null, filePattern, patternStyle, recursive, null);
	}

	public void PutFiles(string localDirectoryName, bool recursive)
	{
		PutFiles(localDirectoryName, null, null, EPatternStyle.Verbatim, recursive, null);
	}

	public void PutFiles(string localDirectoryName)
	{
		PutFiles(localDirectoryName, null, null, EPatternStyle.Verbatim, recursive: false, null);
	}

	public FTPStream AppendFile(string remoteFileName)
	{
		SetupDataConnection();
		AppeCmd(remoteFileName);
		return EndStreamCommand(FTPStream.EAllowedOperation.Write);
	}

	public ulong AppendFile(string localFileName, string remoteFileName)
	{
		return AppendFile(localFileName, remoteFileName, null);
	}

	public ulong AppendFile(string localFileName, string remoteFileName, FileTransferCallback transferCallback)
	{
		using Stream s = AppendFile(remoteFileName);
		return SendFile(localFileName, remoteFileName, s, transferCallback);
	}

	public FTPStream PutUniqueFile(out string remoteFileName)
	{
		SetupDataConnection();
		StouCmd(out remoteFileName);
		return EndStreamCommand(FTPStream.EAllowedOperation.Write);
	}

	public ulong PutUniqueFile(string localFileName, out string remoteFileName)
	{
		return PutUniqueFile(localFileName, out remoteFileName, null);
	}

	public ulong PutUniqueFile(string localFileName, out string remoteFileName, FileTransferCallback transferCallback)
	{
		using Stream s = PutUniqueFile(out remoteFileName);
		return SendFile(localFileName, remoteFileName, s, transferCallback);
	}

	public void DeleteFile(string remoteFileName)
	{
		DeleCmd(remoteFileName);
	}

	public void RenameFile(string remoteFileNameFrom, string remoteFileNameTo)
	{
		RnfrCmd(remoteFileNameFrom);
		RntoCmd(remoteFileNameTo);
	}

	public void MakeDir(string remoteDirName)
	{
		MkdCmd(remoteDirName);
	}

	public void RemoveDir(string remoteDirName)
	{
		RmdCmd(remoteDirName);
	}

	public void ChangeToUpperDir()
	{
		CdupCmd();
	}

	public IList<string> GetShortDirectoryList()
	{
		return GetShortDirectoryList(null);
	}

	public IList<string> GetShortDirectoryList(string remoteDirName)
	{
		SetupDataConnection();
		NlstCmd(remoteDirName);
		string dataString = GetDataString();
		GetReply();
		return new List<string>(dataString.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
	}

	public IList<DirectoryListItem> GetDirectoryList()
	{
		return GetDirectoryList(null);
	}

	public IList<DirectoryListItem> GetDirectoryList(string remoteDirName)
	{
		return DirectoryListParser.GetDirectoryList(GetDirectoryListUnparsed(remoteDirName));
	}

	public string GetDirectoryListUnparsed()
	{
		return GetDirectoryListUnparsed(null);
	}

	public string GetDirectoryListUnparsed(string remoteDirName)
	{
		SetupDataConnection();
		ListCmd(remoteDirName);
		string dataString = GetDataString();
		GetReply();
		if (dataString.Length == 0)
		{
			PushCurrentDirectory();
			SetCurrentDirectory(remoteDirName);
			PopCurrentDirectory();
		}
		return dataString;
	}

	public string GetCurrentDirectory()
	{
		return PwdCmd();
	}

	public string PushCurrentDirectory()
	{
		string currentDirectory = GetCurrentDirectory();
		currDirStack.Push(currentDirectory);
		return currentDirectory;
	}

	public string PopCurrentDirectory()
	{
		string text = currDirStack.Pop();
		SetCurrentDirectory(text);
		return text;
	}

	public void SetCurrentDirectory(string remoteDirName)
	{
		CwdCmd(remoteDirName);
	}

	public string GetSystem()
	{
		return SystCmd();
	}

	public DateTime? GetFileModificationTime(string remoteFileName)
	{
		if (!CheckFeature("MDTM"))
		{
			return null;
		}
		return MdtmCmd(remoteFileName);
	}

	public void SetLanguage(string ietfLanguageTag)
	{
		LangCmd(ietfLanguageTag);
	}

	public void SetTextEncoding(ETextEncoding textEncoding)
	{
		OptsCmd("UTF8 " + ((textEncoding == ETextEncoding.UTF8) ? "ON" : "OFF"));
		this.textEncoding = textEncoding;
	}

	public FTPReply SendCustomCommand(string command)
	{
		return HandleCmd(command);
	}

	public void Close()
	{
		StopKeepAlive();
		CloseDataConnection();
		CloseCtrlConnection();
		sslServerCert = null;
		sslClientCert = null;
	}

	public void Dispose()
	{
		Close();
	}

	private void SetSslInfo(SslStream sslStream)
	{
		sslInfo = new SslInfo
		{
			SslProtocol = sslStream.SslProtocol,
			CipherAlgorithm = sslStream.CipherAlgorithm,
			CipherStrength = sslStream.CipherStrength,
			HashAlgorithm = sslStream.HashAlgorithm,
			HashStrength = sslStream.HashStrength,
			KeyExchangeAlgorithm = sslStream.KeyExchangeAlgorithm,
			KeyExchangeStrength = sslStream.KeyExchangeStrength
		};
	}

	private void CheckSymLinks(string remoteDirectoryName, IList<DirectoryListItem> dirList)
	{
		string text = null;
		foreach (DirectoryListItem dir in dirList)
		{
			if (!dir.IsSymLink)
			{
				continue;
			}
			try
			{
				if (text == null)
				{
					text = GetCurrentDirectory();
				}
				string currentDirectory = CombineRemotePath(remoteDirectoryName, dir.Name);
				SetCurrentDirectory(currentDirectory);
				dir.IsDirectory = true;
				if (dir.SymLinkTargetPath == null)
				{
					dir.SymLinkTargetPath = GetCurrentDirectory();
				}
			}
			catch (FTPCommandException ex)
			{
				if (ex.ErrorCode == 550)
				{
					dir.IsDirectory = false;
					continue;
				}
				throw ex;
			}
		}
		if (text != null)
		{
			SetCurrentDirectory(text);
		}
	}

	private void SSlCtrlChannelCheckRevertToClearText()
	{
		if (CheckFeature("CCC"))
		{
			CccCmd();
		}
		else
		{
			sslSupportCurrentMode |= ESSLSupportMode.ControlChannelRequested;
		}
	}

	private bool CheckFeature(string feature)
	{
		if (features != null)
		{
			return features.Contains(feature);
		}
		return false;
	}

	private void GetFeaturesFromServer()
	{
		try
		{
			features = FeatCmd();
		}
		catch (FTPCommandException)
		{
			features = null;
		}
	}

	private void SslDataChannelCheckExplicitEncryptionRequest()
	{
		if ((sslSupportCurrentMode & ESSLSupportMode.DataChannelRequested) != ESSLSupportMode.DataChannelRequested)
		{
			return;
		}
		PbszCmd(0u);
		try
		{
			ProtCmd(EProtCode.P);
		}
		catch (FTPCommandException ex)
		{
			if ((sslSupportCurrentMode & ESSLSupportMode.DataChannelRequired) == ESSLSupportMode.DataChannelRequired)
			{
				if (ex.ErrorCode == 534 || ex.ErrorCode == 536)
				{
					throw new FTPSslException("The server policy denies SSL/TLS", ex);
				}
				throw ex;
			}
			sslSupportCurrentMode ^= ESSLSupportMode.DataChannelRequired;
			ProtCmd(EProtCode.C);
		}
	}

	private void SslControlChannelCheckExplicitEncryptionRequest(ESSLSupportMode sslSupportMode)
	{
		if ((sslSupportMode & ESSLSupportMode.CredentialsRequested) != ESSLSupportMode.CredentialsRequested)
		{
			return;
		}
		try
		{
			AuthCmd(EAuthMechanism.TLS);
		}
		catch (FTPCommandException ex)
		{
			if ((sslSupportMode & ESSLSupportMode.CredentialsRequired) == ESSLSupportMode.CredentialsRequired)
			{
				if (ex.ErrorCode == 530 || ex.ErrorCode == 534)
				{
					throw new FTPSslException("SSL/TLS connection not supported on server", ex);
				}
				throw ex;
			}
			sslSupportCurrentMode = ESSLSupportMode.ClearText;
		}
	}

	private static string GetRegexPattern(string filePattern, EPatternStyle patternStyle)
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
			byte[] array = new byte[1024];
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
			}
			while (num2 > 0);
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
			throw ex;
		}
	}

	private FTPStream EndStreamCommand(FTPStream.EAllowedOperation allowedOp)
	{
		return new FTPStream(GetDataStream(), allowedOp, delegate
		{
			CloseDataConnection();
			if (waitingCompletionReply)
			{
				GetReply();
			}
		});
	}

	private Stream GetDataStream()
	{
		Stream stream = null;
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
			Stream dataStream = GetDataStream();
			StringBuilder stringBuilder = new StringBuilder();
			byte[] array = new byte[1024];
			int num = 0;
			do
			{
				num = dataStream.Read(array, 0, array.Length);
				stringBuilder.Append(Encoding.UTF8.GetString(array, 0, num));
			}
			while (num != 0);
			return stringBuilder.ToString();
		}
		finally
		{
			CloseDataConnection();
		}
	}

	private void SetupCtrlConnection(string hostname, int port, Encoding textEncoding)
	{
		CloseCtrlConnection();
		ctrlClient = new TcpClient(hostname, port);
		Stream stream = ctrlClient.GetStream();
		stream.ReadTimeout = timeout;
		stream.WriteTimeout = timeout;
		SetupCtrlStreamReaderAndWriter(stream);
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

	private int SetupActiveDataConnectionStep1()
	{
		CloseDataConnection();
		IPEndPoint localEP = new IPEndPoint(IPAddress.Any, 0);
		activeDataConnListener = new TcpListener(localEP);
		activeDataConnListener.Start();
		return (activeDataConnListener.LocalEndpoint as IPEndPoint).Port;
	}

	private void SetupActiveDataConnectionStep2()
	{
		try
		{
			dataClient = activeDataConnListener.AcceptTcpClient();
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
		sslStream.AuthenticateAsClient(hostname, x509CertificateCollection, SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12, checkCertificateRevocation: false);
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
		bool flag = true;
		if (sslServerCert == null || !sslServerCert.Equals(certificate))
		{
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
				sslServerCert = new X509Certificate(certificate.Export(X509ContentType.Cert));
			}
		}
		return flag;
	}

	private static string ParsePwdReply(FTPReply reply)
	{
		int num = reply.Message.IndexOf('"');
		if (num < 0)
		{
			throw new FTPProtocolException(reply);
		}
		int num2 = reply.Message.IndexOf('"', num + 1);
		if (num2 < 0)
		{
			throw new FTPProtocolException(reply);
		}
		return reply.Message.Substring(num + 1, num2 - num - 1);
	}

	private static IPEndPoint ParsePasvReply(FTPReply reply)
	{
		int num = reply.Message.IndexOf('(');
		if (num < 0)
		{
			throw new FTPProtocolException(reply);
		}
		int num2 = reply.Message.IndexOf(')', num + 1);
		if (num2 < 0)
		{
			throw new FTPProtocolException(reply);
		}
		string[] array = reply.Message.Substring(num + 1, num2 - num - 1).Split(new char[1] { ',' });
		if (array.Length != 6)
		{
			throw new FTPProtocolException(reply);
		}
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
		string[] array = reply.Message.Split(new char[1] { '|' });
		if (array.Length != 5)
		{
			throw new FTPProtocolException(reply);
		}
		int port = int.Parse(array[3]);
		return new IPEndPoint(((IPEndPoint)ctrlClient.Client.LocalEndPoint).Address, port);
	}

	private FTPReply HandleCmd(string command)
	{
		return HandleCmd(command, waitForAnswer: true);
	}

	[MethodImpl(MethodImplOptions.Synchronized)]
	private FTPReply HandleCmd(string command, bool waitForAnswer)
	{
		CheckConnection();
		CheckCommandInjection(command);
		ctrlSw.WriteLine(command);
		ctrlSw.Flush();
		if (this.LogCommand != null)
		{
			this.LogCommand(this, new LogCommandEventArgs(command));
		}
		if (!waitForAnswer)
		{
			return null;
		}
		return GetReply();
	}

	private void CheckConnection()
	{
		if (ctrlClient == null)
		{
			throw new FTPException("Not connected");
		}
	}

	private static void CheckCommandInjection(string command)
	{
		if (command.Contains("\r\n"))
		{
			throw new FTPException("Newlines not allowed in command text");
		}
	}

	private static string CombineRemotePath(string path1, string path2)
	{
		return (path1.EndsWith("/") ? path1 : (path1 + "/")) + path2;
	}

	[MethodImpl(MethodImplOptions.Synchronized)]
	private FTPReply GetReply()
	{
		try
		{
			FTPReply fTPReply = new FTPReply();
			bool flag = false;
			do
			{
				string text = ctrlSr.ReadLine();
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
			}
			while (!flag);
			waitingCompletionReply = fTPReply.Code < 200;
			if (this.LogServerReply != null)
			{
				this.LogServerReply(this, new LogServerReplyEventArgs(fTPReply));
			}
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
			catch (Exception)
			{
			}
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

	private void StorCmd(string fileName)
	{
		HandleCmd("STOR " + fileName);
	}

	private void StouCmd(out string fileName)
	{
		FTPReply reply = HandleCmd("STOU");
		fileName = ParseStouReply(reply);
	}

	private static string ParseStouReply(FTPReply reply)
	{
		int num = reply.Message.LastIndexOf(' ');
		if (num < 0)
		{
			throw new FTPProtocolException(reply);
		}
		return reply.Message.Substring(num + 1, reply.Message.Length - num - 2);
	}

	private void AppeCmd(string fileName)
	{
		HandleCmd("APPE " + fileName);
	}

	private void RetrCmd(string fileName)
	{
		HandleCmd("RETR " + fileName);
	}

	private void DeleCmd(string fileName)
	{
		HandleCmd("DELE " + fileName);
	}

	private void MkdCmd(string dirName)
	{
		HandleCmd("MKD " + dirName);
	}

	private void RmdCmd(string dirName)
	{
		HandleCmd("RMD " + dirName);
	}

	private void CdupCmd()
	{
		HandleCmd("CDUP");
	}

	private string SystCmd()
	{
		return HandleCmd("SYST").Message;
	}

	private void TypeCmd(ERepType repType, string param2)
	{
		HandleCmd("TYPE " + repType.ToString() + ((param2 != null) ? (" " + param2) : ""));
	}

	private string PwdCmd()
	{
		return ParsePwdReply(HandleCmd("PWD"));
	}

	private void CwdCmd(string dirName)
	{
		HandleCmd("CWD " + dirName);
	}

	private bool UserCmd(string userName)
	{
		return HandleCmd("USER " + userName).Code != 232;
	}

	private string PassCmd(string password)
	{
		return HandleCmd("PASS " + password).Message;
	}

	private void PortCmd()
	{
		int num = SetupActiveDataConnectionStep1();
		byte[] addressBytes = (ctrlClient.Client.LocalEndPoint as IPEndPoint).Address.GetAddressBytes();
		string text = string.Format("{0},{1},{2},{3},{4},{5}", new object[6]
		{
			addressBytes[0],
			addressBytes[1],
			addressBytes[2],
			addressBytes[3],
			num / 256,
			num % 256
		});
		HandleCmd("PORT " + text);
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
		if (dataConnectionMode == EDataConnectionMode.Active)
		{
			PortCmd();
		}
		else if (GetCtrlConnAddressFamily() == AddressFamily.InterNetwork)
		{
			PasvCmd();
		}
		else
		{
			EpsvCmd();
		}
	}

	private void ListCmd(string dirName)
	{
		HandleCmd("LIST" + ((dirName != null) ? (" " + dirName) : ""));
	}

	private void NlstCmd(string dirName)
	{
		HandleCmd("NLST" + ((dirName != null) ? (" " + dirName) : ""));
	}

	private void RnfrCmd(string fileOldName)
	{
		HandleCmd("RNFR " + fileOldName);
	}

	private void RntoCmd(string fileNewName)
	{
		HandleCmd("RNTO " + fileNewName);
	}

	private void QuitCmd(bool waitForAnswer)
	{
		HandleCmd("QUIT", waitForAnswer);
	}

	private void NoopCmd()
	{
		HandleCmd("NOOP");
	}

	private void AuthCmd(EAuthMechanism authMech)
	{
		HandleCmd("AUTH " + authMech);
		SwitchCtrlToSSLMode();
	}

	private void CccCmd()
	{
		HandleCmd("CCC");
		SwitchCtrlToClearMode();
	}

	private void ProtCmd(EProtCode protCode)
	{
		HandleCmd("PROT " + protCode);
	}

	private void PbszCmd(uint maxSize)
	{
		HandleCmd("PBSZ " + maxSize);
	}

	private IList<string> FeatCmd()
	{
		List<string> list = new List<string>(HandleCmd("FEAT").Message.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
		((IList<string>)list).RemoveAt(0);
		((IList<string>)list).RemoveAt(((ICollection<string>)list).Count - 1);
		return list;
	}

	private void OptsCmd(string command)
	{
		HandleCmd("OPTS " + command);
	}

	private void EpsvCmd()
	{
		FTPReply reply = HandleCmd("EPSV");
		IPEndPoint dataEndPoint = ParseEpsvReply(reply);
		SetupPassiveDataConnection(dataEndPoint);
	}

	private void LangCmd(string ietfLanguageTag)
	{
		HandleCmd("LANG" + ((ietfLanguageTag != null) ? (" " + ietfLanguageTag) : ""));
	}

	private DateTime MdtmCmd(string fileName)
	{
		return ParseFTPDateTime(HandleCmd("MDTM " + fileName).Message);
	}

	private ulong SizeCmd(string fileName)
	{
		return ulong.Parse(HandleCmd("SIZE " + fileName).Message);
	}

	private static DateTime ParseFTPDateTime(string message)
	{
		return DateTime.ParseExact(message, "yyyyMMddHHmmss.FFF", CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AssumeUniversal);
	}

	private void ClntCmd(string name)
	{
		HandleCmd("CLNT " + name);
	}
}
internal delegate void FTPStreamCallback();
public class FTPStream : Stream
{
	public enum EAllowedOperation
	{
		Read = 1,
		Write
	}

	private Stream innerStream;

	private FTPStreamCallback streamClosedCallback;

	private EAllowedOperation allowedOp;

	public override bool CanRead
	{
		get
		{
			if (innerStream.CanRead)
			{
				return (allowedOp & EAllowedOperation.Read) == EAllowedOperation.Read;
			}
			return false;
		}
	}

	public override bool CanSeek => innerStream.CanSeek;

	public override bool CanWrite
	{
		get
		{
			if (innerStream.CanWrite)
			{
				return (allowedOp & EAllowedOperation.Write) == EAllowedOperation.Write;
			}
			return false;
		}
	}

	public override long Length => innerStream.Length;

	public override long Position
	{
		get
		{
			return innerStream.Position;
		}
		set
		{
			innerStream.Position = value;
		}
	}

	internal FTPStream(Stream innerStream, EAllowedOperation allowedOp, FTPStreamCallback streamClosedCallback)
	{
		this.innerStream = innerStream;
		this.streamClosedCallback = streamClosedCallback;
		this.allowedOp = allowedOp;
	}

	public override void Flush()
	{
		innerStream.Flush();
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		if (!CanRead)
		{
			throw new FTPException("Operation not allowed");
		}
		return innerStream.Read(buffer, offset, count);
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		return innerStream.Seek(offset, origin);
	}

	public override void SetLength(long value)
	{
		innerStream.SetLength(value);
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		if (!CanWrite)
		{
			throw new FTPException("Operation not allowed");
		}
		innerStream.Write(buffer, offset, count);
	}

	public override void Close()
	{
		base.Close();
		streamClosedCallback();
	}
}
public static class PathCheck
{
	private static char replacementChar = '_';

	public static string GetValidLocalFileName(string fileName)
	{
		return ReplaceAllChars(fileName, Path.GetInvalidFileNameChars(), replacementChar);
	}

	private static string ReplaceAllChars(string str, char[] oldChars, char newChar)
	{
		StringBuilder stringBuilder = new StringBuilder(str);
		foreach (char oldChar in oldChars)
		{
			stringBuilder.Replace(oldChar, newChar);
		}
		return stringBuilder.ToString();
	}
}
