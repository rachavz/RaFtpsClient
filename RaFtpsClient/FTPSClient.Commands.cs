using System;
using System.Collections.Generic;
using System.Globalization;

namespace RaFtpsClient;

// The FTP vocabulary: how each verb is spelled and how its reply is read. Everything here is pure,
// which is what lets the synchronous and asynchronous paths share it instead of each carrying a
// set of one-line wrappers.
public sealed partial class FTPSClient
{
    private static class Cmd
    {
        public const string Cdup = "CDUP";
        public const string Syst = "SYST";
        public const string Pwd = "PWD";
        public const string Quit = "QUIT";
        public const string Noop = "NOOP";
        public const string Stou = "STOU";
        public const string Ccc = "CCC";
        public const string Feat = "FEAT";
        public const string Pasv = "PASV";
        public const string Epsv = "EPSV";

        public static string Stor(string fileName) => "STOR " + fileName;
        public static string Appe(string fileName) => "APPE " + fileName;
        public static string Retr(string fileName) => "RETR " + fileName;
        public static string Dele(string fileName) => "DELE " + fileName;
        public static string Mkd(string dirName) => "MKD " + dirName;
        public static string Rmd(string dirName) => "RMD " + dirName;
        public static string Cwd(string dirName) => "CWD " + dirName;
        public static string Rnfr(string fileName) => "RNFR " + fileName;
        public static string Rnto(string fileName) => "RNTO " + fileName;
        public static string User(string userName) => "USER " + userName;
        public static string Pass(string password) => "PASS " + password;
        public static string Type(ERepType repType) => "TYPE " + repType;
        public static string Auth(EAuthMechanism mechanism) => "AUTH " + mechanism;
        public static string Prot(EProtCode protCode) => "PROT " + protCode;
        public static string Pbsz(uint maxSize) => "PBSZ " + maxSize;
        public static string Opts(string option) => "OPTS " + option;
        public static string Mdtm(string fileName) => "MDTM " + fileName;
        public static string Size(string fileName) => "SIZE " + fileName;
        public static string Clnt(string name) => "CLNT " + name;
        public static string List(string dirName) => WithOptionalArgument("LIST", dirName);
        public static string Nlst(string dirName) => WithOptionalArgument("NLST", dirName);
        public static string Lang(string ietfLanguageTag) => WithOptionalArgument("LANG", ietfLanguageTag);

        private static string WithOptionalArgument(string verb, string argument)
        {
            return (argument != null) ? verb + " " + argument : verb;
        }
    }

    internal static string ParsePwdReply(FTPReply reply)
    {
        int num = reply.Message.IndexOf('"');
        if (num < 0) throw new FTPProtocolException(reply);
        int num2 = reply.Message.IndexOf('"', num + 1);
        if (num2 < 0) throw new FTPProtocolException(reply);
        return reply.Message.Substring(num + 1, num2 - num - 1);
    }

    internal static string ParseStouReply(FTPReply reply)
    {
        int num = reply.Message.LastIndexOf(' ');
        if (num < 0) throw new FTPProtocolException(reply);
        return reply.Message.Substring(num + 1);
    }

    // 230 (already logged in) and 232 (authorised by security data exchange) both mean no PASS is due.
    private static bool PasswordRequired(FTPReply userReply)
    {
        return userReply.Code != 230 && userReply.Code != 232;
    }

    private static IList<string> ParseFeatReply(FTPReply reply)
    {
        List<string> list = new List<string>(reply.Message.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        // The reply is bracketed by an introductory line and a closing "End" line, neither of which
        // is a feature; a server that answers with anything shorter advertises nothing.
        if (list.Count < 3) return new List<string>();
        list.RemoveAt(list.Count - 1);
        list.RemoveAt(0);
        for (int i = 0; i < list.Count; i++)
        {
            list[i] = list[i].Trim();
        }
        return list;
    }

    private static ulong ParseSizeReply(FTPReply reply)
    {
        return ulong.Parse(reply.Message.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    internal static DateTime ParseFTPDateTime(string message)
    {
        return DateTime.ParseExact(message, "yyyyMMddHHmmss.FFF", CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AssumeUniversal);
    }
}
