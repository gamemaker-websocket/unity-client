
//#define LOG_WEBSOCKET
namespace WSNet
{
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading.Tasks;
    using System.Threading;
    using UnityEngine;

    public class Websocket
    {
        public TcpClient client;

        public System.Exception lastException;

        public delegate void CallbackConnect (ConnectionResult result);
        public CallbackConnect onConnectionOpen;

        public delegate void CallbackReceive (byte[] buffer, int count);
        public CallbackReceive onReceive;

        public delegate void CallbackClose ();
        public CallbackClose onConnectionClose;

        const int readChunkSize = 65536;
        byte[] bufferRead = new byte[readChunkSize];
        MemoryStream streamSend = new MemoryStream(1024);
        MemoryStream streamReceive = new MemoryStream(1024);
        MemoryStream streamMerge = new MemoryStream(1024);
        MemoryStream streamMessage = new MemoryStream(1024);

        bool buffering = false;
        bool receivedHeader = false;
        readonly string headerEnd = "\r\n\r\n";
        bool keepAlive = true;
        int pingIntervalSeconds = 5; //seconds
        int connectTimeoutSeconds = 10;
        CancellationTokenSource cts = new CancellationTokenSource();
        CancellationTokenSource ctsPing = new CancellationTokenSource();

        public async Task Connect (string host, int port)
        {
            client = new TcpClient();

#if LOG_WEBSOCKET
            Debug.Log("Trying to connect");
#endif
            var timeoutTask = Task.Delay(connectTimeoutSeconds * 1000);

            try
            {
                Task connectTask = client.ConnectAsync(host, port);

                //if cancelTask throws exception
                await Task.WhenAny(connectTask, timeoutTask);
            }
            catch (System.Exception e)
            {
                lastException = e;
                onConnectionOpen?.Invoke(ConnectionResult.Exception);
                return;
            }

            if (!client.Client.Connected)
            {
#if LOG_WEBSOCKET
                Debug.LogError("Could not reach host");
#endif
                onConnectionOpen?.Invoke(ConnectionResult.CantReach);
                return;
            }


            if (timeoutTask.IsCompleted)
            {
#if LOG_WEBSOCKET
                Debug.LogError("Connection timeout");
#endif
                client.Dispose();
                onConnectionOpen?.Invoke(ConnectionResult.Timeout);
                return;
            }


            StringBuilder sb = new StringBuilder(512);
            var rn = "\r\n";
            sb.Append("GET / HTTP/1.1");
            sb.Append(rn);
            sb.Append("Host: " + host);
            sb.Append(rn);
            sb.Append("Connection: Upgrade");
            sb.Append(rn);
            sb.Append("Pragma: no-cache");
            sb.Append(rn);
            sb.Append("Upgrade: websocket");
            sb.Append(rn);
            sb.Append("Sec-WebSocket-Version: 13");
            sb.Append(rn);
            sb.Append("Sec-WebSocket-Key: Q2a/gzoUTK69Ts89FPFaLA==");
            sb.Append(headerEnd);

#if LOG_WEBSOCKET
            Debug.Log("Sending upgrade request");
#endif
            var clientStream = client.GetStream();
            var writer = new StreamWriter(clientStream);
            try
            {
                writer.AutoFlush = true;
                await writer.WriteAsync(sb.ToString());
            }
            catch (System.Exception e)
            {
                lastException = e;
                client.Dispose();
                onConnectionOpen?.Invoke(ConnectionResult.ExceptionHeader);
#if LOG_WEBSOCKET
                Debug.LogError("Can't send header");
#endif
                return;
            }

#if LOG_WEBSOCKET
            Debug.Log("Start receiving");
#endif

            while (true)
            {
                if (cts.IsCancellationRequested)
                {
#if LOG_WEBSOCKET
                    Debug.LogWarning("Cancellation requested");
#endif
                    break;
                }

                try
                {
                    int count = await clientStream.ReadAsync(bufferRead, 0, readChunkSize, cts.Token);
                    if (count > 0)
                    {
                        HandleBuffer(count);
                    }
                    else if (count < 0)
                    {
                        Debug.LogError("Negative read count");
                    }
                }
                catch (System.Exception e)
                {
                    _ = CloseAsync();
                    //client.Dispose();
                    Debug.LogException(e);
                    break;
                }
            }
        }

        void HandleBuffer (int receivedCount)
        {
#if LOG_WEBSOCKET
            LogBuffer(receivedCount);
#endif
            streamMerge.Seek(0, SeekOrigin.Begin);
            int count = receivedCount;

            //append received data         
            if (buffering)
            {
                buffering = false;
#if LOG_WEBSOCKET
                Debug.LogWarning("buffering, received another part");
#endif

                //append new received data
                streamMerge.Write(streamReceive.GetBuffer(), 0, (int)streamReceive.Position);
                streamMerge.Write(bufferRead, 0, receivedCount);
                count = (int)streamMerge.Position;
            }
            else
            {
                streamMerge.Write(bufferRead, 0, receivedCount);
            }

            //start reading from the beginning
            streamMerge.Seek(0, SeekOrigin.Begin);

            if (!receivedHeader)
            {
                int endIndex = 0;
                bool found = false;
                StringBuilder sb = new StringBuilder(512);
                for (var i = 0; i < count; i++)
                {
                    char c = (char)streamMerge.ReadByte();
                    sb.Append(c);
                    if (c == headerEnd[endIndex])
                    {
                        endIndex++;
                        if (endIndex >= headerEnd.Length)
                        {
                            found = true;
                            break;
                        }
                    }
                    else
                        endIndex = 0;
                }

                //found header
                if (found)
                {
                    string header = sb.ToString();
#if LOG_WEBSOCKET
                    Debug.Log(header);
#endif
                    receivedHeader = true;
                    onConnectionOpen?.Invoke(ConnectionResult.Open);

                    //start async task for the ping
                    _ = Task.Run(async () =>
                    {
                        while (true)
                        {
                            await Task.Delay(pingIntervalSeconds * 1000);
                            if (!keepAlive)
                            {
#if LOG_WEBSOCKET
                                Debug.LogError("Connection closed, didn't receive keepalive");
#endif
                                _ = CloseAsync();
                                break;
                            }
                            else
                                SendPing();
                        }
                    }, ctsPing.Token);

                }
                else
                {
#if LOG_WEBSOCKET
                    Debug.LogWarning("Header not fully readed");
#endif

                    if (receivedCount > 4096)
                    {
#if LOG_WEBSOCKET
                        Debug.LogError("Can't find header after 4096 bytes");
#endif
                        //todo callback
                    }

                    //write to streamreceive
                    streamReceive.Seek(0, SeekOrigin.Begin);
                    streamReceive.Write(streamMerge.GetBuffer(), 0, count);

                    buffering = true;

                    return;
                }
            }


            //read the target_buffer 
            if ((int)streamMerge.Position >= count)
            {
#if LOG_WEBSOCKET
                Debug.Log("Packet fully read");
#endif
                return;
            }

            //read the stream until it reaches the end
            while (true)
            {
                int payloadSize;
                var frameOffset = (int)streamMerge.Position;
                var remaining = count - frameOffset;
                if (remaining >= 2)
                {
                    //Header
                    //first byte: packet info
                    int bitmask = streamMerge.ReadByte();
                    var fin = (bitmask >> 7) & 1;
                    var rsv1 = (bitmask >> 6) & 1;
                    var rsv2 = (bitmask >> 5) & 1;
                    var rsv3 = (bitmask >> 4) & 1;
                    var opcode = bitmask & 15;

                    int DEBUG = bitmask;
                    //second byte: packet length
                    bitmask = streamMerge.ReadByte();
                    var mask = (bitmask >> 7) & 1;
                    payloadSize = bitmask & 127;

#if LOG_WEBSOCKET
                    if(opcode != 9 && opcode != 10)
                        Debug.LogWarning(
                            "bitmask: " + DEBUG + " " + 
                            "opcode: " + opcode +
                            "\nfin: " + fin +
                            "\nrsv1: " + rsv1 +
                            "\nrsv2: " + rsv2 +
                            "\nrsv3: " + rsv3 +
                            "\nmask: " + mask);
#endif

                    if (payloadSize == 126)
                    {
                        if (remaining < 4)
                        {
                            //packet received partially, stop and wait
#if LOG_WEBSOCKET
                            Debug.LogWarning("payload header partially received, waiting...");
#endif
                            //write to streamreceive
                            streamReceive.Seek(0, SeekOrigin.Begin);
                            streamReceive.Write(streamMerge.GetBuffer(), frameOffset, count - frameOffset);

                            buffering = true;
                            return;
                        }

                        payloadSize = (((byte)streamMerge.ReadByte()) << 8) | ((byte)streamMerge.ReadByte());
#if LOG_WEBSOCKET
                        Debug.LogWarning("payload length medium: " + payloadSize);
#endif
                    }
                    else
                    {
#if LOG_WEBSOCKET
                        if (payloadSize > 0)
                            Debug.LogWarning("payload length small: " + payloadSize);
                        else
                            Debug.LogWarning("payload size: 0");
#endif
                    }

                    //Body
                    remaining = count - (int)streamMerge.Position;
                    if (remaining < payloadSize)
                    {
#if LOG_WEBSOCKET
                        Debug.LogWarning("Payload body received partially, waiting for more: " + remaining + "/" + payloadSize);
#endif
                        //packet received partially, stop and wait

                        //write to streamreceive
                        streamReceive.Seek(0, SeekOrigin.Begin);
                        streamReceive.Write(streamMerge.GetBuffer(), frameOffset, count - frameOffset);
                        buffering = true;
                        return;
                    }

                    //handle message
                    streamMessage.Seek(0, SeekOrigin.Begin);
                    streamMessage.Write(streamMerge.GetBuffer(), (int)streamMerge.Position, payloadSize);
                    HandlePayload(opcode);

                    //check if there are extra bytes
                    remaining = count - ((int)streamMerge.Position + payloadSize);
                    if (remaining > 0)
                    {
                        streamMerge.Seek(payloadSize, SeekOrigin.Current);
#if LOG_WEBSOCKET
                        Debug.LogWarning("Extra bytes remaining: " + remaining);
#endif
                    }
                    else
                    {
                        //Debug.Log("No bytes remaining. Waiting for a new message.");
                        break;
                    }
                }
                else
                {
#if LOG_WEBSOCKET
                    Debug.LogWarning("Received less than 2 bytes, waiting for more");
#endif
                    //write to streamreceive
                    streamReceive.Seek(0, SeekOrigin.Begin);
                    streamReceive.Write(streamMerge.GetBuffer(), frameOffset, count - frameOffset);
                    buffering = true;
                    return;
                }

            }
        }

        public async Task CloseAsync ()
        {
#if LOG_WEBSOCKET
            Debug.LogWarning("CloseAsync");
#endif
            cts.Cancel();
            ctsPing.Cancel();
            onConnectionClose();
            await SendClose();
        }

        void HandlePayload (int opCode)
        {
            switch ((OpCode)opCode)
            {
                case OpCode.ConnectionClose:
                    _ = CloseAsync();
#if LOG_WEBSOCKET
                    Debug.LogWarning("Connection closed by the server");
#endif
                    break;

                case OpCode.Ping:
#if LOG_WEBSOCKET
                    Debug.Log("<color=orange>Received Ping</color>"); 
#endif
                    SendPong();
                    break;

                case OpCode.Pong:
#if LOG_WEBSOCKET
                    Debug.Log("<color=blue>Received Pong</color>");
#endif
                    keepAlive = true;
                    break;

                case OpCode.BinaryFrame:
                    OnReceiveBinary();
                    break;

                default:
#if LOG_WEBSOCKET
                    Debug.LogError("Unhandled opcode: " + opCode);
#endif
                    break;
            }
        }

        void OnReceiveBinary ()
        {
#if LOG_WEBSOCKET
            Debug.Log("<color=green>Received Binary Frame</color>");
#endif
            onReceive(streamMessage.GetBuffer(), (int)streamMessage.Position);
        }

        public bool Send (byte[] buffer, int index, int count)
        {
            streamSend.Seek(0, SeekOrigin.Begin);
            byte headerInfo = (1 << 7) | (int)OpCode.BinaryFrame;
            streamSend.WriteByte(headerInfo);

            if (count < 126)
            {
                streamSend.WriteByte((byte)count);
            }
            else
            {
                if (count < 65535)
                {
                    streamSend.WriteByte(126);

                    streamSend.WriteByte((byte)(count >> 8));
                    streamSend.WriteByte((byte)(count & 255));
                }
                else
                {
#if LOG_WEBSOCKET
                    Debug.LogError("payload too big, max is 65535");
#endif
                    return false;
                }
            }
            streamSend.Write(buffer, index, count);
#if LOG_WEBSOCKET
            LogBuffer(streamSend.GetBuffer(), (int)streamSend.Position);
#endif
            client.GetStream().Write(streamSend.GetBuffer(), 0, (int)streamSend.Position);
            return true;
        }

        void SendPing ()
        {
#if LOG_WEBSOCKET
            Debug.Log("Send ping");
#endif
            streamSend.Seek(0, SeekOrigin.Begin);
            byte headerInfo = (1 << 7) | (int)OpCode.Ping;
            streamSend.WriteByte(headerInfo);
            streamSend.WriteByte(0);//mask 0 and payload size 0
            var stream = client.GetStream();
            stream.Write(streamSend.GetBuffer(), 0, (int)streamSend.Position);
            keepAlive = false;
        }

        void SendPong ()
        {
            streamSend.Seek(0, SeekOrigin.Begin);
            byte headerInfo = (1 << 7) | (int)OpCode.Pong;
            streamSend.WriteByte(headerInfo);
            streamSend.WriteByte(0);//mask 0 and payload size 0
            var stream = client.GetStream();

            stream.Write(streamSend.GetBuffer(), 0, (int)streamSend.Position);
        }

        async Task SendClose ()
        {
            streamSend.Seek(0, SeekOrigin.Begin);
            byte headerInfo = (1 << 7) | (int)OpCode.ConnectionClose;
            streamSend.WriteByte(headerInfo);
            streamSend.WriteByte(0);//mask 0 and payload size 0
            var stream = client.GetStream();

            try
            {
                var timeoutTask = Task.Delay(connectTimeoutSeconds);
                Task sendTask = stream.WriteAsync(streamSend.GetBuffer(), 0, (int)streamSend.Position);

                //if cancelTask throws exception
                await Task.WhenAny(sendTask, timeoutTask);
            }
            catch (System.Exception e)
            {
#if LOG_WEBSOCKET
                Debug.LogWarning(e);
#endif
                return;
            }
        }

#if LOG_WEBSOCKET
        void LogBuffer (byte[] b)
        {
            string str = "";
            for (int i = 0; i < b.Length; i++)
            {
                str += b[i] + " ";
            }
            Debug.Log("buf[" + b.Length + "]: " + str);
        }
        void LogBuffer (int receivedCount)
        {
            var b = streamMerge.GetBuffer();
            string str = "";
            for (int i = 0; i < receivedCount; i++)
            {
                str += b[i] + " ";
            }
            Debug.Log("Received[" + receivedCount + "]: " + str);
        }

        void LogBuffer (byte[] buffer, int count)
        {
            string s = "";
            for (int i = 0; i < count; i++)
                s += buffer[i] + " ";
            Debug.Log(s);
        }
#endif

        enum Error
        {
            Connect,
            Send,
            HeaderCorrupted
        }

        enum OpCode
        {
            ContinuationFrame = 0,
            TextFrame = 1,
            BinaryFrame = 2,
            NonControlFrame1 = 3,
            NonControlFrame2 = 4,
            NonControlFrame3 = 5,
            NonControlFrame4 = 6,
            NonControlFrame5 = 7,
            ConnectionClose = 8,
            Ping = 9,
            Pong = 10,
            ControlFrame1 = 11,
            ControlFrame2 = 12,
            ControlFrame3 = 13,
            ControlFrame4 = 14,
            ControlFrame5 = 15,
            ControlFrame6 = 16
        }



    }

    public enum ConnectionResult
    {
        Open,
        Exception,
        ExceptionHeader,
        CantReach,
        Timeout
    }
}