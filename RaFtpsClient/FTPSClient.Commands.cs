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

// One method per FTP verb, plus the parsers for their replies.
public sealed partial class FTPSClient
{
    internal static string ParsePwdReply(FTPReply reply)
    {
        int num = reply.Message.IndexOf('"');
        if (num < 0) throw new FTPProtocolException(reply);
        int num2 = reply.Message.IndexOf('"', num + 1);
        if (num2 < 0) throw new FTPProtocolException(reply);
        return reply.Message.Substring(num + 1, num2 - num - 1);
    }

    private void StorCmd(string fileName) { HandleCmd("STOR " + fileName); }

    private void StouCmd(out string fileName)
    {
        FTPReply reply = HandleCmd("STOU");
        fileName = ParseStouReply(reply);
    }

    internal static string ParseStouReply(FTPReply reply)
    {
        int num = reply.Message.LastIndexOf(' ');
        if (num < 0) throw new FTPProtocolException(reply);
        return reply.Message.Substring(num + 1);
    }

    private void AppeCmd(string fileName) { HandleCmd("APPE " + fileName); }

    private void RetrCmd(string fileName) { HandleCmd("RETR " + fileName); }

    private void DeleCmd(string fileName) { HandleCmd("DELE " + fileName); }

    private void MkdCmd(string dirName) { HandleCmd("MKD " + dirName); }

    private void RmdCmd(string dirName) { HandleCmd("RMD " + dirName); }

    private void CdupCmd() { HandleCmd("CDUP"); }

    private string SystCmd() { return HandleCmd("SYST").Message; }

    private void TypeCmd(ERepType repType, string param2)
    {
        HandleCmd("TYPE " + repType.ToString() + ((param2 != null) ? (" " + param2) : ""));
    }

    private string PwdCmd() { return ParsePwdReply(HandleCmd("PWD")); }

    private void CwdCmd(string dirName) { HandleCmd("CWD " + dirName); }

    // 230 (already logged in) and 232 (authorised by security data exchange) both mean no PASS is due.
    private bool UserCmd(string userName, out string message)
    {
        FTPReply reply = HandleCmd("USER " + userName);
        message = reply.Message;
        return reply.Code != 230 && reply.Code != 232;
    }

    private string PassCmd(string password) { return HandleCmd("PASS " + password).Message; }

    private void ListCmd(string dirName) { HandleCmd("LIST" + ((dirName != null) ? (" " + dirName) : "")); }

    private void NlstCmd(string dirName) { HandleCmd("NLST" + ((dirName != null) ? (" " + dirName) : "")); }

    private void RnfrCmd(string fileOldName) { HandleCmd("RNFR " + fileOldName); }

    private void RntoCmd(string fileNewName) { HandleCmd("RNTO " + fileNewName); }

    private void QuitCmd(bool waitForAnswer) { HandleCmd("QUIT", waitForAnswer); }

    private void NoopCmd() { HandleCmd("NOOP"); }

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

    private void ProtCmd(EProtCode protCode) { HandleCmd("PROT " + protCode); }

    private void PbszCmd(uint maxSize) { HandleCmd("PBSZ " + maxSize); }

    private IList<string> FeatCmd()
    {
        List<string> list = new List<string>(HandleCmd("FEAT").Message.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
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

    private void OptsCmd(string command) { HandleCmd("OPTS " + command); }

    private void LangCmd(string ietfLanguageTag) { HandleCmd("LANG" + ((ietfLanguageTag != null) ? (" " + ietfLanguageTag) : "")); }

    private DateTime MdtmCmd(string fileName) { return ParseFTPDateTime(HandleCmd("MDTM " + fileName).Message); }

    private ulong SizeCmd(string fileName) { return ulong.Parse(HandleCmd("SIZE " + fileName).Message); }

    internal static DateTime ParseFTPDateTime(string message)
    {
        return DateTime.ParseExact(message, "yyyyMMddHHmmss.FFF", CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AssumeUniversal);
    }

    private void ClntCmd(string name) { HandleCmd("CLNT " + name); }
}
