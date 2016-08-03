using System;
using System.Configuration;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

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
        private static string currentUrl = "";


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
                Console.WriteLine(ex.Message);
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
                //read the first line HTTP command
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
                String remoteUri = splitBuffer[1];
                if (!remoteUri.Contains("url="))
                {
                    remoteUri = currentUrl + remoteUri;
                    uriHasChanged = true;
                }
                else
                {
                    currentUrl = remoteUri.Substring(6);

                }

                Version version = new Version(1, 0);

                HttpWebRequest webReq;
                HttpWebResponse response = null;
                if (!uriHasChanged)
                {
                    remoteUri = remoteUri.Substring(6);
                }
                else
                {
                    
                }
         

                //construct the web request that we are going to issue on behalf of the client.
                
                if (remoteUri.Contains("twitter"))
                {
                    
                    webReq = (HttpWebRequest)HttpWebRequest.Create("https://"+remoteUri);
                }
                else if (!remoteUri.Contains("http"))
                {
                    webReq = (HttpWebRequest)HttpWebRequest.Create("http://"+remoteUri);  
                }
                else webReq = (HttpWebRequest)HttpWebRequest.Create(remoteUri);
                webReq.Method = method;
                webReq.ProtocolVersion = version;

                //read the request headers from the client and copy them to our request
                int contentLen = ReadRequestHeaders(clientStreamReader, webReq);
                
                webReq.Proxy = null;
                webReq.KeepAlive = false;
                webReq.AllowAutoRedirect = false;
                webReq.AutomaticDecompression = DecompressionMethods.None;




                 if (method.ToUpper() == "POST")
                {
                    char[] postBuffer = new char[contentLen];
                    int bytesRead;
                    int totalBytesRead = 0;
                    StreamWriter sw = new StreamWriter(webReq.GetRequestStream());
                    while (totalBytesRead < contentLen && (bytesRead = clientStreamReader.ReadBlock(postBuffer, 0, contentLen)) > 0)
                    {
                        totalBytesRead += bytesRead;
                        sw.Write(postBuffer, 0, bytesRead);

                    }


                    sw.Close();
                }

                    //Console.WriteLine(String.Format("ThreadID: {2} Requesting {0} on behalf of client {1}", webReq.RequestUri, client.Client.RemoteEndPoint.ToString(), Thread.CurrentThread.ManagedThreadId));
                    webReq.Timeout = 15000;

                    try
                    {
                        response = (HttpWebResponse)webReq.GetResponse();
                    }
                    catch (WebException webEx)
                    {
                        response = webEx.Response as HttpWebResponse;
                    }
                    if (response != null)
                    {
                        List<Tuple<String,String>> responseHeaders = ProcessResponse(response);
                        StreamWriter myResponseWriter = new StreamWriter(outStream);
                        Stream responseStream = response.GetResponseStream();
                        try
                        {
                            //send the response status and response headers
                            WriteResponseStatus(response.StatusCode,response.StatusDescription, myResponseWriter);
                            WriteResponseHeaders(myResponseWriter, responseHeaders);

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

                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.Message);
                        }
                        finally
                        {
                            responseStream.Close();
                            response.Close();
                            myResponseWriter.Close();
                        }
                    }
               
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
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

        private static void DumpHeaderCollectionToConsole(WebHeaderCollection headers)
        {
            foreach (String s in headers.AllKeys)
                Console.WriteLine(String.Format("{0}: {1}", s,headers[s]));
            Console.WriteLine();
        }

        private static void DumpHeaderCollectionToConsole(List<Tuple<String,String>> headers)
        {
            foreach (Tuple<String,String> header in headers)
                Console.WriteLine(String.Format("{0}: {1}", header.Item1,header.Item2));
            Console.WriteLine();
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
                            Console.WriteLine(String.Format("Could not add header {0}.  Exception message:{1}", header[0], ex.Message));
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
                        "sslpass"); //password to encrypt key file
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
    }
}
