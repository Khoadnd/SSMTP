﻿using System.Collections;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ssmtp
{
    public class SsmtpSession
    {
        private readonly Socket m_ClientSocket; // ref to Client socket
        private readonly SSMTP_Server m_SsmtpServer; // ref to SSMTP server
        private readonly string m_SessionId = "";
        private string m_Username = "";
        private string m_ConnectedIP = "";
        private string m_ConnectedHostName = "";
        private string m_ReversePath = "";
        private Hashtable m_ForwardPath;
        private bool m_Authenticated = false;
        private bool m_MailFrom_ok = false;
        private bool m_Helo_ok = false;
        private bool m_RcptTo_ok = false;
        private MemoryStream m_MsgStream = null;

        internal SsmtpSession(Socket clientSocket, SSMTP_Server server, string sesisonId)
        {
            m_ClientSocket = clientSocket;
            m_SsmtpServer = server;
            m_SessionId = sesisonId;
            m_ForwardPath = new Hashtable();
        }

        public void StartProcessing()
        {
            try
            {
                m_ConnectedIP = GetIpFromEndPoint(RemoteEndpoint.ToString());
                m_ConnectedHostName = Dns.GetHostEntry(m_ConnectedIP).HostName;

                while (true)
                {
                    if (m_ClientSocket.Available > 0)
                    {
                        var lastCmd = ReadLine();
                        if (SwitchCommand(lastCmd))
                            break;
                    }
                }
            }
            catch (Exception _)
            {
                if (m_ClientSocket.Connected)
                    SendData("->Service not available ^^\r\n");
                else
                    Console.WriteLine("Connection is aborted!", m_SessionId, m_ConnectedIP, "x");
                Console.WriteLine("Exception caught: " + _.ToString());
            }
            finally
            {
                m_SsmtpServer.RemoveSession(m_SessionId);

                if (m_ClientSocket.Connected)
                    m_ClientSocket.Close();

                Console.WriteLine("Session " + m_SessionId + " disconnected! " + DateTime.Now);
            }
        }

        private bool SwitchCommand(string ssmtpCommandtxt)
        {
            // ----Parse command---------------------------------------------------- //
            var cmdParts = ssmtpCommandtxt.TrimStart().Split(new char[] { ' ' });
            var ssmtpCommand = cmdParts[0].ToUpper().Trim();
            var argsText = GetArgsText(ssmtpCommandtxt, ssmtpCommand);
            // --------------------------------------------------------------------- //

            switch (ssmtpCommand)
            {
                case "HELO":
                    HELO(argsText);
                    break;

                case "EHLO":
                    EHLO(argsText);
                    break;

                case "AUTH":
                    AUTH(argsText);
                    break;

                case "MAIL":
                    MAIL(argsText);
                    break;

                case "RCPT":
                    RCPT(argsText);
                    break;

                case "DATA":
                    DATA(argsText);
                    break;

                //case "BDAT":
                //    BDAT(argsText);
                //    break;

                //case "RSET":
                //    RSET(argsText);
                //    break;

                //case "VRFY":
                //    VRFY();
                //    break;

                //case "EXPN":
                //    EXPN();
                //    break;

                //case "HELP":
                //    HELP();
                //    break;

                //case "NOOP":
                //    NOOP();
                //    break;

                case "QUIT":
                    QUIT(argsText);
                    return true;

                default:
                    SendData("->command unrecognized\r\n");
                    Console.WriteLine("-> Unrecognized command");
                    break;
            }

            return false;
        }

        private void HELO(string argsText)
        {
            SendData("->250 " + Dns.GetHostName() + " Hello [" + m_ConnectedIP + "]\r\n");
            m_Helo_ok = true;
        }

        private void EHLO(string argsText)
        {
            var reply = "" +
                        "250-" + Dns.GetHostName() + " Hello [" + m_ConnectedIP + "]\r\n" +
                        "250-PIPELINING\r\n" +
                        "250-SIZE " + m_SsmtpServer.MaxMessageSize + "\r\n" +
                        "250-8BITMIME\r\n" +
                        "250-BINARYMIME\r\n" +
                        "250-CHUNKING\r\n" +
                        "250-AUTH LOGIN CRAM-MD5\r\n" + //CRAM-MD5 DIGEST-MD5
                        "250 Ok\r\n";

            SendData(reply);
            m_Helo_ok = true;
        }

        private void QUIT(string argsText)
        {
            if (argsText.Length > 0)
            {
                SendData("->500 Syntax error. Syntax:{DATA}\r\n");
                return;
            }

            SendData("->221 Service closing transmission channel\r\n");
        }

        private void AUTH(string argsText)
        {
            if (m_Authenticated)
            {
                SendData("->Already authenticated!\r\n");
                Console.WriteLine("->Already auth");
                return;
            }

            var username = "";
            var password = "";

            var param = argsText.Split(new char[] { ' ' });
            switch (param[0].ToUpper())
            {
                case "PLAIN":
                    Console.WriteLine("->Not implemented");
                    SendData("->Not implemented! ^^\r\n");
                    break;
                case "LOGIN":
                    SendData("->VXNlcm5hbWU6\r\n");
                    var usernameLine = ReadLine();
                    if (usernameLine.Length > 0)
                        username = Encoding.Default.GetString(Convert.FromBase64String(usernameLine));

                    SendData("->UGFzc3dvcmQ6\r\n");
                    var passwordLine = ReadLine();
                    if (passwordLine.Length > 0)
                        password = Encoding.Default.GetString(Convert.FromBase64String(passwordLine));

                    m_Authenticated = m_SsmtpServer.AuthUser(username, password);

                    if (!m_Authenticated)
                    {
                        Console.WriteLine("->Auth failed");
                        SendData("->420 Authentication failed\r\n");
                    }
                    else
                    {
                        m_Authenticated = true;
                        SendData("->Authenticated!\r\n");
                        m_Username = username;
                    }
                    break;
                case "CRAM-MD5":
                    SendData("->Not implemented! ^^");
                    break;
                default:
                    SendData("->?\r\n");
                    break;
            }
        }

        private void MAIL(string argsText)
        {
            if (!m_Authenticated)
            {
                SendData("-> You must AUTH first!\r\n");
                Console.WriteLine("->No Auth");
                return;
            }
            if (!CanMAIL)
            {
                if (m_MailFrom_ok)
                {
                    SendData("-> Sender already specified\r\n");
                    Console.WriteLine("->Sender already specified\r\n");
                }
                else
                {
                    Console.WriteLine("->Users? are you ok?");
                    SendData("-> Bad bad bad boy\r\n");
                }
                return;
            }

            // parsed param
            var reversePath = ""; // FROM :v don't know why they named it like that
            var senderEmail = "";

            // regex to parse param

            const string exp = @"(?<param>FROM)[\s]{0,}:\s{0,}<?\s{0,}(?<value>[\w\@\.\-\*\+\=\#\/]*)\s{0,}>?(\s|$)";
            var r = new Regex(exp, RegexOptions.IgnoreCase);
            var tmp = r.Match(argsText.Trim());

            if (!tmp.Success)
            {
                SendData("-> Error in params, syntax is:{MAIL FROM:<address>}\r\n");
                Console.WriteLine("->Param error");
                return;
            }
            if (tmp.Result("${value}").Length == 0)
            {
                SendData("-> Address not specified\r\n");
                Console.WriteLine("->No address specified");
                return;
            }

            if (tmp.Result("${param}").ToUpper().Equals("FROM"))
                reversePath = tmp.Result("${value}");

            senderEmail = reversePath;

            // need to check senderEmail
            SendData("-> OK <" + senderEmail + "> sender ok\r\n");
            ResetState();

            m_ReversePath = reversePath;
            m_MailFrom_ok = true;
        }

        private void RCPT(string argsText)
        {
            if (!m_Authenticated)
            {
                SendData("->You must AUTH first\r\n");
                Console.WriteLine("Not AUTH");
                return;
            }

            if (!CanRCPT)
            {
                SendData("-> BAD BAD BAD BOY!\r\n");
                Console.WriteLine("->Bad sequence of commands");
                return;
            }

            if (m_ForwardPath.Count > m_SsmtpServer.MaxRecipients)
            {
                SendData("-> Too many recipients\r\n");
                Console.WriteLine("->Too many recipients");
                return;
            }


            // ---- params ---- //
            var forwardPath = "";
            var recipientEmail = "";
            var messageSize = 0;

            var exp = @"(?<param>TO)[\s]{0,}:\s{0,}<?\s{0,}(?<value>[\w\@\.\-\*\+\=\#\/]*)\s{0,}>?(\s|$)";
            var r = new Regex(exp, RegexOptions.IgnoreCase);
            var tmp = r.Match(argsText.Trim());

            if (!tmp.Success)
            {
                SendData("->Error in params, Syntax:{RCPT TO:<address>}\r\n");
                Console.WriteLine("->Param error");
                return;
            }

            if (!tmp.Result("${param}").ToUpper().Equals("TO"))
            {
                SendData("->Error in params, Syntax:{RCPT TO:<address>}\r\n");
                Console.WriteLine("->Param error");
                return;
            }

            if (tmp.Result("${value}").Length == 0)
            {
                SendData("->Recipients is not specified\r\n");
                Console.WriteLine("->No recipients");
                return;
            }

            forwardPath = tmp.Result("${value}");
            recipientEmail = forwardPath;

            // need to check mail size
            // need to validate mail
            // need to validate many things :v

            if (!m_ForwardPath.Contains(recipientEmail))
                m_ForwardPath.Add(recipientEmail, forwardPath);

            SendData("->OK <" + recipientEmail + "> Recipient ok\r\n");
            m_RcptTo_ok = true;
        }

        private void DATA(string argsText)
        {
            if (argsText.Length > 0)
            {
                SendData("->Syntax error, Syntax: {DATA}\r\n");
                Console.WriteLine("->Syntax Error");
                return;
            }

            if (!CanDATA)
            {
                SendData("->Bad bad bad boy\r\n");
                Console.WriteLine("->Bad sequence of commands");
                return;
            }

            if (m_ForwardPath.Count == 0)
            {
                SendData("->No valid recipients given\r\n");
                Console.WriteLine("->No valid recipients");
                return;
            }

            SendData("->Start mail input; end with <CRLF>.<CRLF>\r\n");

            byte[] headers = null;
            var header = "Received: from " + m_ConnectedHostName + " (" + m_ConnectedIP + ")\r\n";
            header += "\tby " + System.Net.Dns.GetHostName() + " with SMTP; " + DateTime.Now.ToUniversalTime().ToString("r", System.Globalization.DateTimeFormatInfo.InvariantInfo) + "\r\n";

            headers = System.Text.Encoding.ASCII.GetBytes(header.ToCharArray());
            MemoryStream reply = null;
            ReadReplyCode replyCode = ReadData(m_ClientSocket, out reply, headers, m_SsmtpServer.MaxMessageSize,
                "\r\n.\r\n", ".\r\n");
            if (replyCode == ReadReplyCode.Ok)
            {
                var receivedCount = reply.Length;
                using (MemoryStream msgStream = DoPeriodHandling(reply, false))
                {
                    reply.Close();
                    
                    var message = Encoding.ASCII.GetString(msgStream.ToArray());
                    Console.WriteLine("Message received:\n" + message);

                    // store message
                    using (FileStream file = new FileStream("msg.bin", FileMode.Create, FileAccess.Write))
                    {
                        var bytes = new byte[msgStream.Length];
                        msgStream.Read(bytes, 0, (int)msgStream.Length);
                        file.Write(bytes, 0, bytes.Length);
                        msgStream.Close();
                    }

                    SendData("->Ok message received\r\n");
                }

                ResetState();
            }
            else
            {
                if (replyCode == ReadReplyCode.LengthExceeded)
                    SendData("->Exceeded storage allocation\r\n");
                else 
                    SendData("->Mail not end with <CRLF>.<CRLF>");
            }
        }

        public static MemoryStream DoPeriodHandling(Stream stream, bool addRemove)
        {
            return DoPeriodHandling(stream, addRemove, true);
        }

        public static MemoryStream DoPeriodHandling(Stream stream, bool addRemove, bool setStreamPosTo0)
        {
            MemoryStream replyData = new MemoryStream();

            var crlf = new byte[] { (byte)'\r', (byte)'\n' };
            if (setStreamPosTo0)
                stream.Position = 0;

            StreamLineReader r = new StreamLineReader(stream);
            byte[] line = r.ReadLine();

            while (line != null)
            {
                if (line.Length > 0)
                {
                    if (line[0] == (byte)'.')
                    {
                        if (addRemove)
                        {
                            replyData.WriteByte((byte)'.');
                            replyData.Write(line, 0, line.Length);
                        }
                        else
                            replyData.Write(line, 1, line.Length - 1);
                    }
                    else
                    {
                       replyData.Write(line, 0, line.Length);
                    }
                }
                replyData.Write(crlf, 0, crlf.Length);
                line = r.ReadLine();
            }
            replyData.Position = 0;
            return replyData;
        }

        public static ReadReplyCode ReadData(Socket socket, out MemoryStream replyData, byte[] addData, int maxLength,
            string terminator, string removeFromEnd)
        {
            ReadReplyCode replyCode = ReadReplyCode.Ok;
            replyData = null;

            try
            {
                replyData = new MemoryStream();
                // write header
                replyData.Write(addData, 0, addData.Length);
                FixedStack stack = new FixedStack(terminator);
                var nextReadWritelen = 1;

                var lastDataTime = DateTime.Now.Ticks;
                while (nextReadWritelen > 0)
                {
                    if (socket.Available >= nextReadWritelen)
                    {
                        var b = new byte[nextReadWritelen];
                        var countReceived = socket.Receive(b);

                        if (replyCode != ReadReplyCode.LengthExceeded)
                            replyData.Write(b, 0, countReceived);

                        nextReadWritelen = stack.Push(b, countReceived);

                        if (replyCode != ReadReplyCode.LengthExceeded && replyData.Length > maxLength)
                            replyCode = ReadReplyCode.LengthExceeded;

                        lastDataTime = DateTime.Now.Ticks;
                    }
                    else
                    {
                        // timeout stuff if you want to implement ^^
                    }
                }
                if (replyCode == ReadReplyCode.Ok && removeFromEnd.Length > 0)
                    replyData.SetLength(replyData.Length - removeFromEnd.Length);
            }
            catch
            {
                replyCode = ReadReplyCode.UnknownError;
            }
            return replyCode;
        }

        private void ResetState()
        {
            m_ForwardPath.Clear();
            m_ReversePath = "";
        }

        private string GetArgsText(string input, string cmdTxtToRemove)
        {
            var buff = input.Trim();
            if (buff.Length >= cmdTxtToRemove.Length)
            {
                buff = buff.Substring(cmdTxtToRemove.Length);
            }
            buff = buff.Trim();

            return buff;
        }

        private EndPoint RemoteEndpoint
        {
            get { return m_ClientSocket.RemoteEndPoint; }
        }

        private string GetIpFromEndPoint(string x)
        {
            return x.Remove(x.IndexOf(":"));
        }

        // Read data from socket
        private string ReadLine()
        {
            var line = ReadLine(m_ClientSocket, 500);

            Console.WriteLine(m_SessionId + ": " + line + "<CRLF>");

            return line;
        }

        private static string ReadLine(Socket socket, int maxLen)
        {
            var lineBuf = new ArrayList();
            byte prevByte = 0;

            while (true)
            {
                if (socket.Available <= 0) continue;
                var currByte = new byte[1];
                var countReceived = socket.Receive(currByte, 1, SocketFlags.None);
                if (countReceived != 1) continue;
                lineBuf.Add(currByte[0]);

                if (prevByte == (byte)'\r' && currByte[0] == '\n')
                {
                    var ret = new byte[lineBuf.Count - 2]; // remove <CRLF>
                    lineBuf.CopyTo(0, ret, 0, lineBuf.Count - 2);

                    return Encoding.Default.GetString(ret).Trim();
                }

                prevByte = currByte[0];

                if (lineBuf.Count > maxLen)
                {
                    throw new Exception("Maximum line length exceeded");
                }
            }
        }


        // Send data to socket
        private void SendData(string data)
        {
            var byteData = System.Text.Encoding.ASCII.GetBytes(data.ToCharArray());

            var nCount = m_ClientSocket.Send(byteData, byteData.Length, 0);
            if (nCount != byteData.Length)
                throw new Exception("SendData not send enough data as requested!");

            Console.WriteLine("-> OK");
        }

        private bool CanMAIL
        {
            get { return m_Helo_ok && !m_MailFrom_ok; }
        }

        private bool CanRCPT
        {
            get { return m_MailFrom_ok; }
        }

        private bool CanDATA
        {
            get { return m_RcptTo_ok; }
        }
    }
}
