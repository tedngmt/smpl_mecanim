/*
    Minimal receive-oriented RFC 6455 WebSocket client.

    This project predates Unity's UPM package manager and System.Net.WebSockets
    (Unity 5.6 / .NET 3.5 scripting runtime), so there is no off-the-shelf WebSocket
    client available. This class implements just enough of the protocol to connect
    to a standard WebSocket server (e.g. Python's `websockets` library, used by
    MG-MotionLLM's utils/unity_stream.py) and receive text frames:
      - HTTP Upgrade handshake (with Sec-WebSocket-Accept verification)
      - Text frame decoding (including continuation frames)
      - Ping -> Pong reply
      - Close handling

    All socket I/O runs on a background thread; received messages are queued and
    must be drained from the main thread via TryDequeue (Unity APIs are not
    thread-safe).
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MinimalWebSocket
{
    public class WebSocketClient
    {
        const string RfcGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        public bool IsConnected { get; private set; }

        readonly Queue<string> _incoming = new Queue<string>();
        readonly object _incomingLock = new object();
        readonly object _writeLock = new object();

        TcpClient _tcp;
        NetworkStream _stream;
        Thread _readThread;
        volatile bool _stop;

        public void Connect(string host, int port, string path = "/")
        {
            _stop = false;
            _tcp = new TcpClient();
            _tcp.NoDelay = true;
            _tcp.Connect(host, port);
            _stream = _tcp.GetStream();

            byte[] keyBytes = new byte[16];
            new Random().NextBytes(keyBytes);
            string key = Convert.ToBase64String(keyBytes);

            string request =
                "GET " + path + " HTTP/1.1\r\n" +
                "Host: " + host + ":" + port + "\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Key: " + key + "\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n";
            byte[] reqBytes = Encoding.ASCII.GetBytes(request);
            _stream.Write(reqBytes, 0, reqBytes.Length);

            string response = ReadHttpResponse(_stream);
            if (response.IndexOf(" 101 ", StringComparison.Ordinal) < 0)
                throw new IOException("WebSocket handshake failed: " + response);

            string accept = ExtractHeader(response, "Sec-WebSocket-Accept");
            if (accept == null || accept != ComputeAcceptKey(key))
                throw new IOException("WebSocket handshake failed: bad Sec-WebSocket-Accept");

            IsConnected = true;
            _readThread = new Thread(ReadLoop);
            _readThread.IsBackground = true;
            _readThread.Start();
        }

        public bool TryDequeue(out string message)
        {
            lock (_incomingLock)
            {
                if (_incoming.Count > 0)
                {
                    message = _incoming.Dequeue();
                    return true;
                }
            }
            message = null;
            return false;
        }

        public void Disconnect()
        {
            if (_stop) return;
            _stop = true;
            IsConnected = false;
            try { SendFrame(0x8, new byte[0]); } catch { /* best effort */ }
            try { if (_tcp != null) _tcp.Close(); } catch { /* best effort */ }
        }

        void ReadLoop()
        {
            try
            {
                var messageBuffer = new MemoryStream();
                while (!_stop)
                {
                    int b0 = _stream.ReadByte();
                    if (b0 == -1) break;
                    int b1 = _stream.ReadByte();
                    if (b1 == -1) break;

                    bool fin = (b0 & 0x80) != 0;
                    int opcode = b0 & 0x0F;
                    bool masked = (b1 & 0x80) != 0;
                    long len = b1 & 0x7F;

                    if (len == 126)
                    {
                        byte[] ext = ReadExact(2);
                        len = (ext[0] << 8) | ext[1];
                    }
                    else if (len == 127)
                    {
                        byte[] ext = ReadExact(8);
                        len = 0;
                        for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
                    }

                    byte[] maskKey = masked ? ReadExact(4) : null;
                    byte[] payload = len > 0 ? ReadExact((int)len) : new byte[0];
                    if (masked)
                    {
                        for (int i = 0; i < payload.Length; i++)
                            payload[i] ^= maskKey[i % 4];
                    }

                    switch (opcode)
                    {
                        case 0x1: // text
                        case 0x0: // continuation
                            messageBuffer.Write(payload, 0, payload.Length);
                            if (fin)
                            {
                                string text = Encoding.UTF8.GetString(messageBuffer.ToArray());
                                messageBuffer.SetLength(0);
                                lock (_incomingLock) { _incoming.Enqueue(text); }
                            }
                            break;

                        case 0x8: // close
                            _stop = true;
                            break;

                        case 0x9: // ping
                            SendFrame(0xA, payload);
                            break;

                        case 0xA: // pong
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // Connection lost or torn down; caller detects via IsConnected and reconnects.
            }
            finally
            {
                IsConnected = false;
            }
        }

        byte[] ReadExact(int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = _stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                    throw new IOException("Connection closed while reading frame");
                offset += read;
            }
            return buffer;
        }

        void SendFrame(byte opcode, byte[] payload)
        {
            byte[] maskKey = new byte[4];
            new Random().NextBytes(maskKey);

            using (var ms = new MemoryStream())
            {
                ms.WriteByte((byte)(0x80 | opcode));

                int len = payload.Length;
                if (len < 126)
                {
                    ms.WriteByte((byte)(0x80 | len));
                }
                else if (len <= 65535)
                {
                    ms.WriteByte((byte)(0x80 | 126));
                    ms.WriteByte((byte)(len >> 8));
                    ms.WriteByte((byte)(len & 0xFF));
                }
                else
                {
                    ms.WriteByte((byte)(0x80 | 127));
                    for (int i = 7; i >= 0; i--)
                        ms.WriteByte((byte)((len >> (8 * i)) & 0xFF));
                }

                ms.Write(maskKey, 0, 4);
                byte[] masked = new byte[payload.Length];
                for (int i = 0; i < payload.Length; i++)
                    masked[i] = (byte)(payload[i] ^ maskKey[i % 4]);
                ms.Write(masked, 0, masked.Length);

                byte[] frame = ms.ToArray();
                lock (_writeLock) { _stream.Write(frame, 0, frame.Length); }
            }
        }

        static string ReadHttpResponse(NetworkStream stream)
        {
            var bytes = new List<byte>(256);
            while (true)
            {
                int b = stream.ReadByte();
                if (b == -1) throw new IOException("Connection closed during handshake");
                bytes.Add((byte)b);
                int n = bytes.Count;
                if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
                    break;
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        static string ExtractHeader(string response, string name)
        {
            string prefix = name + ":";
            foreach (string raw in response.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length > prefix.Length &&
                    line.Substring(0, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(prefix.Length).Trim();
            }
            return null;
        }

        static string ComputeAcceptKey(string key)
        {
            using (var sha1 = SHA1.Create())
            {
                byte[] hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(key + RfcGuid));
                return Convert.ToBase64String(hash);
            }
        }
    }
}
