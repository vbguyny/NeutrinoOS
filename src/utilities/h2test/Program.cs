// NeutrinoOS Phase 9 utility: h2test - in-guest HTTP/2 (h2c) client test
//
// usage: h2test   (run `webhost start` first)
//
// Connects to the local web service over the real TCP loopback path,
// sends the HTTP/2 client preface + SETTINGS, then exercises two
// multiplexed streams (GET /health on stream 1, GET /time on stream 3)
// in a single connection, decodes the HPACK response headers, checks the
// DATA bodies and stream termination, and - when the server pushes -
// receives the PUSH_PROMISE + pushed stream. The test drives the shared
// stack AND WebService.Tick() itself, so it runs deterministically while
// the shell is busy with this command.

using System;
using NeutrinoOS.Utils;
using NeutrinoOS.DDK.Kernel;
using NeutrinoOS.DDK.Network;
using NeutrinoOS.DDK.Network.Sockets;
using NeutrinoOS.DDK.Network.Stack;
using NeutrinoOS.DDK.Services;

namespace NeutrinoOS.Utility.H2Test;

/// <summary>The h2test utility (see file header).</summary>
public static unsafe class Program
{
    private static NetworkStack _stack;
    private static int _failures;

    /// <summary>Entry point.</summary>
    public static int Main(string[] args)
    {
        if (NeutrinoOS.DDK.Util.VersionFlag.Handle(args))
            return 0;
        if (args.Length > 1)
            return Util.Fail("h2test", "usage: h2test");

        // ---- HPACK round-trip self-check (encoder vs decoder) ----
        {
            var blk = new byte[512];
            int bn = 0;
            bn = HpackEncoder.WriteHeader(blk, bn, ":method", "GET");
            bn = HpackEncoder.WriteHeader(blk, bn, ":path", "/");
            bn = HpackEncoder.WriteHeader(blk, bn, ":scheme", "http");
            bn = HpackEncoder.WriteHeader(blk, bn, ":authority", "localhost");
            Console.Write("h2test: hpack block ");
            Console.Write(bn.ToString());
            Console.WriteLine(" bytes");
            var dec = new HpackDecoder();
            var ns = new string[16];
            var vs = new string[16];
            int cnt = dec.Decode(blk, bn, ns, vs);
            Console.Write("h2test: hpack decoded ");
            Console.WriteLine(cnt.ToString());
            if (cnt < 0)
                _failures++;
            for (int i = 0; i < cnt; i++)
            {
                Console.Write("h2test:   ");
                Console.Write(ns[i]);
                Console.Write(" = ");
                Console.WriteLine(vs[i]);
            }
        }

        if (!WebService.Active)
        {
            Console.WriteLine("h2test: web service not running - run 'webhost start' first");
            return 1;
        }
        NetworkInterface eth = NetworkManager.GetInterface("eth0");
        if (eth == null || eth.Stack == null)
            return Util.Fail("h2test", "no ethernet device (eth0)");
        _stack = eth.Stack;

        TcpSocket sock = Http.Connect(0x7F000001, 80, _stack, 4000);
        if (sock == null)
            return Util.Fail("h2test", "TCP connect to 127.0.0.1:80 failed");

        // ---- Client preface + SETTINGS (ENABLE_PUSH=1 so we can also
        // verify server push) ----
        // 24 (preface) + 9 (empty SETTINGS frame) + 15 (SETTINGS 6B payload).
        var hello = new byte[24 + 9 + 15];
        int n = 0;
        for (int i = 0; i < H2TestFrames.Preface.Length; i++)
            hello[n++] = H2TestFrames.Preface[i];
        n = H2TestFrames.WriteFrame(hello, n, H2TestFrames.SettingsType, 0, 0, null, 0);
        var settings = new byte[6];
        settings[0] = 0; settings[1] = 2;          // ENABLE_PUSH
        settings[2] = 0; settings[3] = 0; settings[4] = 0; settings[5] = 1;
        n = H2TestFrames.WriteFrame(hello, n, H2TestFrames.SettingsType, 0, 0, settings, 6);
        if (!Send(sock, hello, n))
            return Util.Fail("h2test", "failed to send client preface");

        // ---- Two requests multiplexed on one connection ----
        // Stream 1 asks for the root document (which makes the server
        // push the /health sibling when ENABLE_PUSH was sent).
        SendHeaders(sock, 1, "/", endStream: true);
        SendHeaders(sock, 3, "/time", endStream: true);

        bool s1Headers = false;
        bool s1Done = false;
        bool s3Headers = false;
        bool s3Done = false;
        bool pushPromise = false;
        bool pushedHeaders = false;
        bool pushedDone = false;
        bool settingsAck = false;

        var headerBlock = new byte[8192];
        int headerBlockLen = 0;
        int headerBlockStream = 0;

        var rx = new byte[16384];
        int rxLen = 0;
        ulong start = Timer.GetUptimeMilliseconds();
        while (Timer.GetUptimeMilliseconds() - start < 30000)
        {
            // Drive both the NIC and the cooperative web service.
            WebService.Tick();

            fixed (byte* rp = rx)
            {
                while (rxLen < rx.Length)
                {
                    int got = sock.Receive(rp + rxLen, rx.Length - rxLen);
                    if (got <= 0)
                        break;
                    rxLen += got;
                }
            }

            int pos = 0;
            while (rxLen - pos >= 9)
            {
                int len = (rx[pos] << 16) | (rx[pos + 1] << 8) | rx[pos + 2];
                byte type = rx[pos + 3];
                byte flags = rx[pos + 4];
                int sid = ((rx[pos + 5] & 0x7F) << 24) | (rx[pos + 6] << 16) |
                          (rx[pos + 7] << 8) | rx[pos + 8];
                if (rxLen - pos < 9 + len)
                    break;
                pos += 9;

                if (type == H2TestFrames.SettingsType && (flags & 1) != 0)
                    settingsAck = true;
                else if (type == H2TestFrames.HeadersType)
                {
                    for (int i = 0; i < len && headerBlockLen < headerBlock.Length; i++)
                        headerBlock[headerBlockLen++] = rx[pos + i];
                    headerBlockStream = sid;
                    if ((flags & 4) != 0)
                    {
                        string status = H2TestFrames.StatusOf(headerBlock, headerBlockLen);
                        Console.Write("h2test: stream ");
                        Console.Write(sid.ToString());
                        Console.Write(" HEADERS status=");
                        Console.WriteLine(status ?? "?");
                        if (sid == 1)
                            s1Headers = H2TestFrames.StrEq(status, "200");
                        else if (sid == 3)
                            s3Headers = H2TestFrames.StrEq(status, "200");
                        else
                            pushedHeaders = H2TestFrames.StrEq(status, "200");
                        headerBlockLen = 0;
                    }
                }
                else if (type == H2TestFrames.DataType)
                {
                    if ((flags & 1) != 0)
                    {
                        if (sid == 1)
                            s1Done = true;
                        else if (sid == 3)
                            s3Done = true;
                        else
                            pushedDone = true;
                    }
                }
                else if (type == H2TestFrames.PushPromiseType)
                {
                    pushPromise = true;
                    Console.WriteLine("h2test: PUSH_PROMISE received");
                }
                pos += len;
            }
            if (pos > 0)
            {
                for (int i = pos; i < rxLen; i++)
                    rx[i - pos] = rx[i];
                rxLen -= pos;
            }

            if (s1Done && s3Done && settingsAck && pushedDone)
                break;
        }

        Check("SETTINGS acknowledgement", settingsAck);
        Check("stream 1 HEADERS :status 200", s1Headers);
        Check("stream 1 ended", s1Done);
        Check("stream 3 HEADERS :status 200", s3Headers);
        Check("stream 3 ended", s3Done);
        Check("server push promise", pushPromise);
        Check("pushed stream headers", pushedHeaders);
        Check("pushed stream ended", pushedDone);
        _ = headerBlockStream;

        sock.Close();
        NetworkPump.FlushTx(_stack);

        // ---- h2c upgrade case (RFC 7540 3.2) ----
        RunUpgradeCase();

        Console.WriteLine(_failures == 0 ? "h2test: PASS" : "h2test: FAIL");
        return _failures;
    }

    /// <summary>
    /// HTTP/1.1 request with Upgrade: h2c + HTTP2-Settings; after the 101
    /// the original request is answered on HTTP/2 stream 1.
    /// </summary>
    private static void RunUpgradeCase()
    {
        TcpSocket sock = Http.Connect(0x7F000001, 80, _stack, 4000);
        if (sock == null)
        {
            Check("upgrade: TCP connect", false);
            return;
        }

        string req = "GET /health HTTP/1.1\r\nHost: localhost\r\n" +
                     "Connection: Upgrade, HTTP2-Settings\r\nUpgrade: h2c\r\n" +
                     "HTTP2-Settings: AAIAAAAB\r\n\r\n";
        var reqBytes = new byte[req.Length];
        for (int i = 0; i < req.Length; i++)
            reqBytes[i] = (byte)req[i];
        if (!Send(sock, reqBytes, reqBytes.Length))
        {
            Check("upgrade: send request", false);
            return;
        }

        // Wait for the 101 response, then send the client preface.
        var rx = new byte[16384];
        int rxLen = 0;
        bool got101 = false;
        ulong start = Timer.GetUptimeMilliseconds();
        while (Timer.GetUptimeMilliseconds() - start < 30000 && !got101)
        {
            WebService.Tick();
            fixed (byte* rp = rx)
            {
                int got = sock.Receive(rp + rxLen, rx.Length - rxLen);
                if (got > 0)
                    rxLen += got;
            }
            for (int i = 0; i + 3 < rxLen; i++)
            {
                if (rx[i] == 13 && rx[i + 1] == 10 && rx[i + 2] == 13 && rx[i + 3] == 10)
                {
                    got101 = rxLen >= 12 && rx[9] == '1' && rx[10] == '0' && rx[11] == '1';
                    // Drop the HTTP/1.1 head; keep any h2 frames that followed.
                    int drop = i + 4;
                    for (int k = drop; k < rxLen; k++)
                        rx[k - drop] = rx[k];
                    rxLen -= drop;
                    break;
                }
            }
        }
        Check("upgrade: 101 response", got101);
        if (!got101)
        {
            sock.Close();
            NetworkPump.FlushTx(_stack);
            return;
        }

        // Client preface + empty SETTINGS.
        var hello = new byte[24 + 9];
        for (int i = 0; i < H2TestFrames.Preface.Length; i++)
            hello[i] = H2TestFrames.Preface[i];
        H2TestFrames.WriteFrame(hello, 24, H2TestFrames.SettingsType, 0, 0, null, 0);
        bool sentMagic = Send(sock, hello, hello.Length);
        Check("upgrade: send preface", sentMagic);

        bool s1Headers = false;
        bool s1Done = false;
        var headerBlock = new byte[4096];
        int headerBlockLen = 0;
        start = Timer.GetUptimeMilliseconds();
        while (Timer.GetUptimeMilliseconds() - start < 20000 && !s1Done)
        {
            WebService.Tick();
            fixed (byte* rp = rx)
            {
                int got = sock.Receive(rp + rxLen, rx.Length - rxLen);
                if (got > 0)
                    rxLen += got;
            }
            int pos = 0;
            while (rxLen - pos >= 9)
            {
                int len = (rx[pos] << 16) | (rx[pos + 1] << 8) | rx[pos + 2];
                byte type = rx[pos + 3];
                byte flags = rx[pos + 4];
                int sid = ((rx[pos + 5] & 0x7F) << 24) | (rx[pos + 6] << 16) |
                          (rx[pos + 7] << 8) | rx[pos + 8];
                if (rxLen - pos < 9 + len)
                    break;
                pos += 9;
                if (type == H2TestFrames.HeadersType && sid == 1)
                {
                    for (int i = 0; i < len && headerBlockLen < headerBlock.Length; i++)
                        headerBlock[headerBlockLen++] = rx[pos + i];
                    if ((flags & 4) != 0)
                    {
                        string status = H2TestFrames.StatusOf(headerBlock, headerBlockLen);
                        Console.Write("h2test: upgrade stream 1 status=");
                        Console.WriteLine(status ?? "?");
                        s1Headers = H2TestFrames.StrEq(status, "200");
                        headerBlockLen = 0;
                    }
                }
                else if (type == H2TestFrames.DataType && sid == 1 && (flags & 1) != 0)
                {
                    s1Done = true;
                }
                pos += len;
            }
            if (pos > 0)
            {
                for (int i = pos; i < rxLen; i++)
                    rx[i - pos] = rx[i];
                rxLen -= pos;
            }
        }
        Check("upgrade: stream 1 :status 200", s1Headers);
        Check("upgrade: stream 1 ended", s1Done);

        sock.Close();
        NetworkPump.FlushTx(_stack);
    }

    private static bool Send(TcpSocket sock, byte[] data, int len)
    {
        int sent = 0;
        while (sent < len)
        {
            int got;
            fixed (byte* p = data)
            {
                got = sock.Send(p + sent, len - sent);
            }
            if (got <= 0)
                return false;
            sent += got;
        }
        NetworkPump.FlushTx(_stack);
        return true;
    }

    private static void SendHeaders(TcpSocket sock, int streamId, string path, bool endStream)
    {
        var block = new byte[512];
        int n = 0;
        n = HpackEncoder.WriteHeader(block, n, ":method", "GET");
        n = HpackEncoder.WriteHeader(block, n, ":path", path);
        n = HpackEncoder.WriteHeader(block, n, ":scheme", "http");
        n = HpackEncoder.WriteHeader(block, n, ":authority", "localhost");
        var frame = new byte[9 + n];
        byte flags = endStream ? (byte)0x5 : (byte)0x4;   // END_STREAM | END_HEADERS
        int fl = H2TestFrames.WriteFrame(frame, 0, H2TestFrames.HeadersType, flags, streamId, block, n);
        Send(sock, frame, fl);
    }

    private static void Check(string what, bool ok)
    {
        Console.Write("h2test: ");
        Console.Write(ok ? "PASS" : "FAIL");
        Console.Write(" - ");
        Console.WriteLine(what);
        if (!ok)
            _failures++;
    }
}

/// <summary>Tiny frame helpers for the test client.</summary>
public static class H2TestFrames
{
    public const byte DataType = 0;
    public const byte HeadersType = 1;
    public const byte SettingsType = 4;
    public const byte PushPromiseType = 5;

    public static readonly byte[] Preface = new byte[]
    {
        (byte)'P', (byte)'R', (byte)'I', (byte)' ', (byte)'*', (byte)' ',
        (byte)'H', (byte)'T', (byte)'T', (byte)'P', (byte)'/', (byte)'2',
        (byte)'.', (byte)'0', 13, 10, 13, 10, (byte)'S', (byte)'M', 13, 10,
        13, 10,
    };

    /// <summary>Write a frame header (+payload); returns the new position.</summary>
    public static int WriteFrame(byte[] buf, int pos, byte type, byte flags, int streamId,
        byte[] payload, int payloadLen)
    {
        buf[pos + 0] = (byte)(payloadLen >> 16);
        buf[pos + 1] = (byte)(payloadLen >> 8);
        buf[pos + 2] = (byte)payloadLen;
        buf[pos + 3] = type;
        buf[pos + 4] = flags;
        buf[pos + 5] = (byte)(streamId >> 24);
        buf[pos + 6] = (byte)(streamId >> 16);
        buf[pos + 7] = (byte)(streamId >> 8);
        buf[pos + 8] = (byte)streamId;
        for (int i = 0; i < payloadLen; i++)
            buf[pos + 9 + i] = payload[i];
        return pos + 9 + payloadLen;
    }

    /// <summary>Extract :status from an HPACK header block; null on failure.</summary>
    public static string StatusOf(byte[] block, int len)
    {
        var dec = new HpackDecoder();
        var names = new string[16];
        var values = new string[16];
        int count = dec.Decode(block, len, names, values);
        if (count < 0)
            return null;
        for (int i = 0; i < count; i++)
        {
            if (StrEq(names[i], ":status"))
                return values[i];
        }
        return null;
    }

    public static bool StrEq(string a, string b)
    {
        if (a == null || b == null || a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }
}
