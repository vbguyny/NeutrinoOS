// ProtonOS DDK - SSH connection state machine (Phase 6)
//
// One SshConnection drives a single client through the full session:
// version exchange, binary packet protocol (plain, then aes*-ctr with
// hmac-sha2-*[-etm]), curve25519-sha256 key exchange with an
// ssh-ed25519 host key, password/publickey authentication against the
// UserDatabase, and a session channel (interactive shell line editor
// or one-shot exec) running commands through the kernel shell bridge.
//
// Everything is non-blocking: the service Tick() pumps the network
// stack and calls Tick() here; inbound bytes accumulate in a small
// buffer and the state machine consumes whole packets as they arrive.

using System;
using ProtonOS.DDK.Crypto;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Network.Sockets;
using ProtonOS.DDK.Network.Stack;
using ProtonOS.DDK.Users;

namespace ProtonOS.DDK.Services.Ssh;

/// <summary>Server-side SSH connection (see file header).</summary>
public sealed unsafe class SshConnection
{
    private const int StVersion = 0;
    private const int StKexInit = 1;
    private const int StEcdh = 2;
    private const int StNewKeysWait = 3;
    private const int StUserauth = 4;
    private const int StConnected = 5;
    private const int StClosed = 6;

    private const int RxCapacity = 32768;
    private const int OutCapacity = 65536;
    private const uint OurWindow = 2 * 1024 * 1024;

    private readonly TcpSocket _sock;
    private int _state;

    private readonly byte[] _rx = new byte[RxCapacity];
    private int _rxLen;
    private bool _rxPeekDecrypted;      // non-ETM: first 4 bytes already decrypted
    private uint _inSeq;
    private uint _outSeq;

    private readonly byte[] _serverVersion;
    private string _clientVersion;
    private byte[] _clientVersionBytes;

    private byte[] _clientKexInit;
    private byte[] _serverKexInit;
    private bool _dropNextPacket;

    private SshPacketCrypto _inCrypto;    // null = plaintext
    private SshPacketCrypto _outCrypto;

    private string _encC2S = "aes128-ctr";
    private string _encS2C = "aes128-ctr";
    private string _macC2S = "hmac-sha2-256";
    private string _macS2C = "hmac-sha2-256";
    private bool _etmC2S;
    private bool _etmS2C;

    private byte[] _kMpint;
    private byte[] _h;
    private byte[] _sessionId;
    private SshPacketCrypto _pendingIn;

    private string _user;
    private bool _authed;

    private bool _channelOpen;
    private uint _clientChannelId;
    private uint _ourChannelId = 1;
    private uint _remoteWindow;
    private int _remoteMaxPacket = 32768;
    private uint _ourWindowConsumed;

    private bool _sessionActive;
    private bool _sessionDone;
    private bool _sentClose;
    private bool _clientClosed;

    private readonly char[] _line = new char[512];
    private int _lineLen;
    private readonly string[] _history = new string[8];
    private int _historyCount;
    private int _historyBrowse = -1;
    private int _escState;
    private int _exitCode;

    private readonly byte[] _pendingOut = new byte[OutCapacity];
    private int _pendingLen;

    private ulong _lastActivityMs;

    /// <summary>True once the connection is finished.</summary>
    public bool IsClosed => _state == StClosed;

    /// <summary>Whether an authenticated session is running.</summary>
    public bool Authenticated => _authed;

    /// <summary>Login name once known; null before authentication.</summary>
    public string UserName => _user;

    /// <summary>Create a connection over an accepted socket. The server
    /// identification string is sent immediately.</summary>
    public SshConnection(TcpSocket sock, string serverVersionText)
    {
        _sock = sock;
        _state = StVersion;
        _lastActivityMs = Timer.GetUptimeMilliseconds();

        // Version string for the exchange hash carries no CR/LF
        // (RFC 4253 section 8); wire form appends CR LF.
        var bare = new byte[serverVersionText.Length];
        for (int i = 0; i < serverVersionText.Length; i++)
            bare[i] = (byte)serverVersionText[i];
        _serverVersion = bare;

        var ver = new byte[bare.Length + 2];
        for (int i = 0; i < bare.Length; i++)
            ver[i] = bare[i];
        ver[bare.Length] = 0x0D;
        ver[bare.Length + 1] = 0x0A;
        // Pointer overload: the JIT world cannot resolve the implicit
        // byte[] -> ReadOnlySpan<byte> conversion operator.
        fixed (byte* p = ver)
        {
            sock.Send(p, ver.Length);
        }
    }

    // ==================== Tick ====================

    /// <summary>Run a bounded slice: read available bytes, advance the
    /// state machine, flush pending output.</summary>
    public void Tick()
    {
        if (_state == StClosed)
            return;

        if (_sock.State == TcpState.Closed)
        {
            _state = StClosed;
            return;
        }

        ReadAvailable();

        int passes = 0;
        bool progress = true;
        while (progress && passes < 32 && _state != StClosed)
        {
            progress = ProcessOne();
            passes++;
        }

        FlushPendingOut();

        if (_sock.State == TcpState.Closed)
            _state = StClosed;

        ulong now = Timer.GetUptimeMilliseconds();
        if (_state != StClosed && now - _lastActivityMs > 600000)
            Close();   // 10 minute idle timeout
    }

    /// <summary>Close the connection (releases the socket).</summary>
    public void Close()
    {
        if (_state == StClosed)
            return;
        _state = StClosed;
        _sock.Close();
    }

    private void ReadAvailable()
    {
        fixed (byte* p = _rx)
        {
            while (_rxLen < RxCapacity)
            {
                int n = _sock.Receive(p + _rxLen, RxCapacity - _rxLen);
                if (n <= 0)
                    break;
                _rxLen += n;
                _lastActivityMs = Timer.GetUptimeMilliseconds();
                if (n < RxCapacity - _rxLen)
                    break;
            }
        }
    }

    private void Consume(int count)
    {
        for (int i = count; i < _rxLen; i++)
            _rx[i - count] = _rx[i];
        _rxLen -= count;
        _rxPeekDecrypted = false;
    }

    // ==================== Packet layer ====================

    /// <summary>Try to extract one packet payload (message type first).</summary>
    private bool TryReadPacket(out byte[] payload)
    {
        payload = null;
        int blockSize = _inCrypto != null ? 16 : 8;

        if (_inCrypto == null)
        {
            // Plain packet.
            if (_rxLen < 4)
                return false;
            uint len = ((uint)_rx[0] << 24) | ((uint)_rx[1] << 16) | ((uint)_rx[2] << 8) | _rx[3];
            if (len < 1 || len > 262144 || (len + 4) % 8 != 0)
            {
                Fail("bad packet length");
                return false;
            }
            int total = (int)len + 4;
            if (_rxLen < total)
                return false;
            int pad = _rx[4];
            if (pad < 4 || pad + 1 > (int)len)
            {
                Fail("bad padding");
                return false;
            }
            int payloadLen = (int)len - 1 - pad;
            payload = new byte[payloadLen];
            for (int i = 0; i < payloadLen; i++)
                payload[i] = _rx[5 + i];
            Consume(total);
            _inSeq++;
            return true;
        }

        if (_inCrypto.IsEtm)
        {
            // Length is cleartext; MAC over (seq || len || ciphertext).
            // OpenSSH aligns packet_length (the encrypted part) to the
            // cipher block size for EtM, and the receiving side requires
            // len % block_size == 0 (packet.c: "need % block_size").
            if (_rxLen < 4)
                return false;
            uint len = ((uint)_rx[0] << 24) | ((uint)_rx[1] << 16) | ((uint)_rx[2] << 8) | _rx[3];
            if (len < 5 || len > 262144 || len % 16 != 0)
            {
                Fail("bad etm packet length");
                return false;
            }
            int total = 4 + (int)len + _inCrypto.MacLength;
            if (_rxLen < total)
                return false;
            _inCrypto.SetSequence(_inSeq);
            if (!_inCrypto.VerifyMac(_rx, 0, 4 + (int)len, _rx, 4 + (int)len))
            {
                Fail("bad MAC");
                return false;
            }
            var content = new byte[len];
            for (int i = 0; i < (int)len; i++)
                content[i] = _rx[4 + i];
            _inCrypto.Xor(content, 0, content.Length);
            int pad = content[0];
            if (pad < 4 || pad + 1 > (int)len)
            {
                Fail("bad etm padding");
                return false;
            }
            int payloadLen = (int)len - 1 - pad;
            payload = new byte[payloadLen];
            for (int i = 0; i < payloadLen; i++)
                payload[i] = content[1 + i];
            Consume(total);
            _inSeq++;
            return true;
        }
        else
        {
            // Encrypt-then-decrypt whole-packet MAC (RFC 4253 classic mode).
            if (_rxLen < 4)
                return false;
            _inCrypto.SetSequence(_inSeq);
            if (!_rxPeekDecrypted)
            {
                var first4 = new byte[4];
                for (int i = 0; i < 4; i++)
                    first4[i] = _rx[i];
                _inCrypto.Xor(first4, 0, 4);
                for (int i = 0; i < 4; i++)
                    _rx[i] = first4[i];
                _rxPeekDecrypted = true;
            }
            uint len = ((uint)_rx[0] << 24) | ((uint)_rx[1] << 16) | ((uint)_rx[2] << 8) | _rx[3];
            if (len < 1 || len > 262144 || (len + 4) % 16 != 0)
            {
                Fail("bad mac-mode packet length");
                return false;
            }
            int total = 4 + (int)len + _inCrypto.MacLength;
            if (_rxLen < total)
                return false;

            if (len > 4)
                _inCrypto.Xor(_rx, 4, (int)len - 4);
            if (!_inCrypto.VerifyMac(_rx, 0, 4 + (int)len, _rx, 4 + (int)len))
            {
                Fail("bad MAC");
                return false;
            }
            int pad = _rx[4];
            if (pad < 4 || pad + 1 > (int)len)
            {
                Fail("bad padding");
                return false;
            }
            int payloadLen = (int)len - 1 - pad;
            payload = new byte[payloadLen];
            for (int i = 0; i < payloadLen; i++)
                payload[i] = _rx[5 + i];
            Consume(total);
            _inSeq++;
            return true;
        }
    }

    /// <summary>Send one packet whose payload already starts with the
    /// message type byte.</summary>
    private void SendPayload(byte[] payload)
    {
        bool etmOut = _outCrypto != null && _outCrypto.IsEtm;
        int blockSize = _outCrypto != null ? 16 : 8;
        int contentLen = 1 + payload.Length;      // padding_length + payload
        int padLen;
        if (etmOut)
        {
            // EtM: OpenSSH aligns packet_length (the encrypted part)
            // to the block size, not (4 + packet_length).
            padLen = blockSize - (contentLen % blockSize);
            if (padLen < 4)
                padLen += blockSize;
        }
        else
        {
            int baseLen = 4 + contentLen;
            padLen = blockSize - (baseLen % blockSize);
            if (padLen < 4)
                padLen += blockSize;
        }
        int packetLenValue = contentLen + padLen;

        var packet = new byte[4 + packetLenValue];
        packet[0] = (byte)(packetLenValue >> 24);
        packet[1] = (byte)(packetLenValue >> 16);
        packet[2] = (byte)(packetLenValue >> 8);
        packet[3] = (byte)packetLenValue;
        packet[4] = (byte)padLen;
        for (int i = 0; i < payload.Length; i++)
            packet[5 + i] = payload[i];
        var padding = Csprng.GetBytes(padLen);
        for (int i = 0; i < padLen; i++)
            packet[5 + payload.Length + i] = padding[i];

        byte[] wire;
        if (_outCrypto == null)
        {
            wire = packet;
        }
        else
        {
            _outCrypto.SetSequence(_outSeq);
            if (_outCrypto.IsEtm)
            {
                var content = new byte[packetLenValue];
                for (int i = 0; i < packetLenValue; i++)
                    content[i] = packet[4 + i];
                _outCrypto.Xor(content, 0, content.Length);
                wire = new byte[4 + packetLenValue + _outCrypto.MacLength];
                wire[0] = packet[0];
                wire[1] = packet[1];
                wire[2] = packet[2];
                wire[3] = packet[3];
                for (int i = 0; i < packetLenValue; i++)
                    wire[4 + i] = content[i];
                var mac = _outCrypto.Mac(wire, 0, 4 + packetLenValue);
                for (int i = 0; i < _outCrypto.MacLength; i++)
                    wire[4 + packetLenValue + i] = mac[i];
            }
            else
            {
                var plain = packet;
                _outCrypto.Xor(packet, 0, packet.Length);
                wire = new byte[packet.Length + _outCrypto.MacLength];
                for (int i = 0; i < packet.Length; i++)
                    wire[i] = packet[i];
                var mac = _outCrypto.Mac(plain, 0, plain.Length);
                for (int i = 0; i < _outCrypto.MacLength; i++)
                    wire[packet.Length + i] = mac[i];
            }
        }

        fixed (byte* p = wire)
        {
            _sock.Send(p, wire.Length);
        }
        _outSeq++;
        _lastActivityMs = Timer.GetUptimeMilliseconds();
    }

    private void Fail(string reason)
    {
        // Low-volume operational log: one line per failed connection.
        Console.WriteLine("[sshd] connection failed: " + reason);
        _state = StClosed;
        _sock.Close();
    }

    // ==================== State machine ====================

    private bool ProcessOne()
    {
        switch (_state)
        {
            case StVersion:
                return DoVersion();
            case StKexInit:
            case StEcdh:
            case StNewKeysWait:
            case StUserauth:
            case StConnected:
                return DoPacket();
            default:
                return false;
        }
    }

    private bool DoVersion()
    {
        int nl = -1;
        for (int i = 0; i < _rxLen; i++)
        {
            if (_rx[i] == 0x0A)
            {
                nl = i;
                break;
            }
        }
        if (nl < 0)
        {
            if (_rxLen > 255)
                Fail("version line too long");
            return false;
        }

        int len = nl;
        if (len > 0 && _rx[len - 1] == 0x0D)
            len--;
        _clientVersionBytes = new byte[len];
        for (int i = 0; i < len; i++)
            _clientVersionBytes[i] = _rx[i];
        var vchars = new char[len];
        for (int i = 0; i < len; i++)
            vchars[i] = (char)_rx[i];
        _clientVersion = new string(vchars);
        Consume(nl + 1);

        SendKexInit();
        _state = StKexInit;
        return true;
    }

    private void SendKexInit()
    {
        var w = new SshWriter(512);
        w.WriteByte(20);
        var cookie = Csprng.GetBytes(16);
        w.WriteRaw(cookie, 0, 16);
        w.WriteString("curve25519-sha256");
        w.WriteString("ssh-ed25519");
        w.WriteString("aes128-ctr,aes256-ctr");
        w.WriteString("aes128-ctr,aes256-ctr");
        w.WriteString("hmac-sha2-256-etm@openssh.com,hmac-sha2-512-etm@openssh.com,hmac-sha2-256,hmac-sha2-512");
        w.WriteString("hmac-sha2-256-etm@openssh.com,hmac-sha2-512-etm@openssh.com,hmac-sha2-256,hmac-sha2-512");
        w.WriteString("none");
        w.WriteString("none");
        w.WriteString("");
        w.WriteString("");
        w.WriteBool(false);
        w.WriteU32(0);

        _serverKexInit = w.ToArray();
        SendPayload(_serverKexInit);
    }

    private bool DoPacket()
    {
        byte[] payload;
        if (!TryReadPacket(out payload))
            return false;

        if (_dropNextPacket)
        {
            _dropNextPacket = false;
            return true;
        }
        if (payload == null || payload.Length == 0)
            return true;

        byte msg = payload[0];
        switch (_state)
        {
            case StKexInit:
                if (msg != 20)
                    return true;   // ignore IGNORE/DEBUG
                return OnClientKexInit(payload);
            case StEcdh:
                if (msg != 30)
                    return true;
                return OnEcdhInit(payload);
            case StNewKeysWait:
                if (msg != 21)
                    return true;
                return OnClientNewKeys();
            case StUserauth:
                return OnUserauthMessage(msg, payload);
            case StConnected:
                return OnConnectionMessage(msg, payload);
            default:
                return false;
        }
    }

    private bool OnClientKexInit(byte[] payload)
    {
        _clientKexInit = payload;
        var r = new SshReader(payload, 1, payload.Length - 1);
        // cookie
        for (int i = 0; i < 16; i++)
            r.ReadByte();
        string kexList = r.ReadNameList();
        string hostKeyList = r.ReadNameList();
        string encC2S = r.ReadNameList();
        string encS2C = r.ReadNameList();
        string macC2S = r.ReadNameList();
        string macS2C = r.ReadNameList();
        string compC2S = r.ReadNameList();
        string compS2C = r.ReadNameList();
        r.ReadNameList();
        r.ReadNameList();
        bool firstFollows = r.ReadBool();
        r.ReadU32();

        var kex = SshNames.PickFirstMutual(kexList, new[] { "curve25519-sha256" });
        var hostKey = SshNames.PickFirstMutual(hostKeyList, new[] { "ssh-ed25519" });
        var encIn = SshNames.PickFirstMutual(encC2S, new[] { "aes128-ctr", "aes256-ctr" });
        var encOut = SshNames.PickFirstMutual(encS2C, new[] { "aes128-ctr", "aes256-ctr" });
        var macIn = SshNames.PickFirstMutual(macC2S, new[]
        {
            "hmac-sha2-256-etm@openssh.com", "hmac-sha2-512-etm@openssh.com",
            "hmac-sha2-256", "hmac-sha2-512",
        });
        var macOut = SshNames.PickFirstMutual(macS2C, new[]
        {
            "hmac-sha2-256-etm@openssh.com", "hmac-sha2-512-etm@openssh.com",
            "hmac-sha2-256", "hmac-sha2-512",
        });
        // Compression: negotiate properly - the client's list is a name-list
        // like "none,zlib@openssh.com", never exactly "none".
        var compIn = SshNames.PickFirstMutual(compC2S, new[] { "none" });
        var compOut = SshNames.PickFirstMutual(compS2C, new[] { "none" });

        if (kex == null || hostKey == null || encIn == null || encOut == null ||
            macIn == null || macOut == null || compIn == null || compOut == null)
        {
            Disconnect(3, "no common algorithms");
            return false;
        }

        _encC2S = encIn;
        _encS2C = encOut;
        _macC2S = macIn;
        _macS2C = macOut;
        _etmC2S = macIn == "hmac-sha2-256-etm@openssh.com" || macIn == "hmac-sha2-512-etm@openssh.com";
        _etmS2C = macOut == "hmac-sha2-256-etm@openssh.com" || macOut == "hmac-sha2-512-etm@openssh.com";

        if (firstFollows)
        {
            // The client sent a guessed KEX_ECDH_INIT right after KEXINIT.
            // It must be ignored only when the guess (the client's first
            // kex entry) differs from the negotiated algorithm (RFC 4253 7.1).
            string clientFirst = SshNames.FirstItem(kexList);
            _dropNextPacket = !SshNames.Eq(clientFirst, kex);
        }

        _state = StEcdh;
        return true;
    }

    private bool OnEcdhInit(byte[] payload)
    {
        var r = new SshReader(payload, 1, payload.Length - 1);
        byte[] qc = r.ReadString();
        if (qc == null || qc.Length != 32)
        {
            Disconnect(3, "bad client key");
            return false;
        }

        var seed = Csprng.GetBytes(32);
        var qs = X25519.ScalarMultBase(seed);
        var k = X25519.ScalarMult(seed, qc);

        bool allZero = true;
        for (int i = 0; i < 32; i++)
        {
            if (k[i] != 0)
            {
                allZero = false;
                break;
            }
        }
        if (allZero)
        {
            Disconnect(3, "degenerate key exchange");
            return false;
        }

        _kMpint = SshAlgorithms.MpintEncode(k);

        var hostPub = Ed25519.PublicKeyFromSeed(SshService.HostSeed);
        var hk = new SshWriter(64);
        hk.WriteString("ssh-ed25519");
        hk.WriteString(hostPub);
        var hostKeyBlob = hk.ToArray();

        _h = SshAlgorithms.ExchangeHash(_clientVersionBytes, _serverVersion,
            _clientKexInit, _serverKexInit, hostKeyBlob, qc, qs, _kMpint);
        if (_sessionId == null)
            _sessionId = _h;

        var sig = Ed25519.Sign(SshService.HostSeed, _h);
        var sigBlobW = new SshWriter(80);
        sigBlobW.WriteString("ssh-ed25519");
        sigBlobW.WriteString(sig);
        var sigBlob = sigBlobW.ToArray();

        var reply = new SshWriter(256);
        reply.WriteByte(31);
        reply.WriteString(hostKeyBlob);
        reply.WriteString(qs);
        reply.WriteString(sigBlob);
        SendPayload(reply.ToArray());

        var newkeys = new byte[] { 21 };
        SendPayload(newkeys);

        // Prepare (but do not yet activate) the inbound cipher.
        // Key lengths are per-algorithm, per-direction (OpenSSH mac_setup:
        // hmac-sha2-256 -> 32-byte MAC key, hmac-sha2-512 -> 64). The KDF
        // output length IS the key; deriving extra bytes changes the key.
        var macKindIn = _macC2S == "hmac-sha2-512" || _macC2S == "hmac-sha2-512-etm@openssh.com"
            ? HashKind.Sha512 : HashKind.Sha256;
        var macKindOut = _macS2C == "hmac-sha2-512" || _macS2C == "hmac-sha2-512-etm@openssh.com"
            ? HashKind.Sha512 : HashKind.Sha256;
        var km = SshAlgorithms.Derive(_kMpint, _h, _sessionId,
            _encC2S == "aes256-ctr" ? 32 : 16,
            _encS2C == "aes256-ctr" ? 32 : 16,
            16,
            macKindIn == HashKind.Sha512 ? 64 : 32,
            macKindOut == HashKind.Sha512 ? 64 : 32);
        _pendingIn = new SshPacketCrypto(km.KeyC2S, km.IvC2S, km.MacC2S, macKindIn, _etmC2S);
        _outCrypto = new SshPacketCrypto(km.KeyS2C, km.IvS2C, km.MacS2C, macKindOut, _etmS2C);

        _state = StNewKeysWait;
        return true;
    }

    private bool OnClientNewKeys()
    {
        _inCrypto = _pendingIn;
        _pendingIn = null;
        _state = StUserauth;
        return true;
    }

    private bool OnUserauthMessage(byte msg, byte[] payload)
    {
        if (msg == 5)
        {
            var r = new SshReader(payload, 1, payload.Length - 1);
            string service = r.ReadAscii();
            if (service == "ssh-userauth")
            {
                var w = new SshWriter(32);
                w.WriteByte(6);
                w.WriteString("ssh-userauth");
                SendPayload(w.ToArray());
            }
            else
            {
                Disconnect(7, "service not available");
                return false;
            }
            return true;
        }

        if (msg == 50)
            return OnUserauthRequest(payload);
        return true;
    }

    private bool OnUserauthRequest(byte[] payload)
    {
        var r = new SshReader(payload, 1, payload.Length - 1);
        string user = r.ReadAscii();
        string service = r.ReadAscii();
        string method = r.ReadAscii();
        if (user == null || service == null || method == null)
            return true;

        if (service == "ssh-connection")
        {
            if (method == "password")
            {
                bool changing = r.ReadBool();
                string password = r.ReadAscii();
                if (!changing && password != null && CheckPassword(user, password))
                {
                    _user = user;
                    _authed = true;
                    SendUserauthResult(true);
                    _state = StConnected;
                    return true;
                }
                SendUserauthFailure();
                return true;
            }
            if (method == "publickey")
            {
                bool hasSig = r.ReadBool();
                string algo = r.ReadAscii();
                byte[] keyBlob = r.ReadString();
                byte[] key32 = ExtractEd25519(keyBlob);
                if (algo != "ssh-ed25519" || key32 == null)
                {
                    SendUserauthFailure();
                    return true;
                }

                bool authorized = CheckPublicKey(user, key32);
                if (!hasSig)
                {
                    if (authorized)
                    {
                        var ok = new SshWriter(96);
                        ok.WriteByte(60);
                        ok.WriteString(algo);
                        ok.WriteString(keyBlob);
                        SendPayload(ok.ToArray());
                    }
                    else
                    {
                        SendUserauthFailure();
                    }
                    return true;
                }

                byte[] sigBlob = r.ReadString();
                byte[] sig64 = ExtractEd25519(sigBlob);
                bool valid = false;
                if (sig64 != null)
                {
                    var signed = new SshWriter(256);
                    signed.WriteString(_sessionId);
                    signed.WriteByte(50);
                    signed.WriteString(user);
                    signed.WriteString("ssh-connection");
                    signed.WriteString("publickey");
                    signed.WriteBool(true);
                    signed.WriteString(algo);
                    signed.WriteString(keyBlob);
                    valid = Ed25519.Verify(key32, signed.ToArray(), sig64);
                }

                if (authorized && valid)
                {
                    _user = user;
                    _authed = true;
                    SendUserauthResult(true);
                    _state = StConnected;
                    return true;
                }
                SendUserauthFailure();
                return true;
            }
        }

        SendUserauthFailure();
        return true;
    }

    private bool CheckPassword(string user, string password)
    {
        var entry = UserDatabase.Lookup(user);
        if (entry == null)
            return false;
        if (entry.IsRoot)
            return false;   // root logins are console-only by policy
        return UserDatabase.VerifyPassword(user, password);
    }

    private bool CheckPublicKey(string user, byte[] key32)
    {
        var entry = UserDatabase.Lookup(user);
        if (entry == null)
            return false;
        if (entry.IsRoot)
            return false;
        return UserDatabase.IsAuthorizedKey(entry, key32);
    }

    private static byte[] ExtractEd25519(byte[] blob)
    {
        if (blob == null || blob.Length < 4 + 11 + 4 + 32)
            return null;
        var r = new SshReader(blob);
        string type = r.ReadAscii();
        byte[] key = r.ReadString();
        if (type != "ssh-ed25519" || key == null)
            return null;
        return key;
    }

    private void SendUserauthResult(bool success)
    {
        var w = new SshWriter(8);
        w.WriteByte(success ? (byte)52 : (byte)51);
        if (!success)
            w.WriteString("password,publickey");
        if (!success)
            w.WriteBool(false);
        SendPayload(w.ToArray());
    }

    private void SendUserauthFailure() => SendUserauthResult(false);

    // ==================== Connection protocol ====================

    private bool OnConnectionMessage(byte msg, byte[] payload)
    {
        switch (msg)
        {
            case 1:   // client DISCONNECT
                _state = StClosed;
                return false;
            case 80:  // GLOBAL_REQUEST
            {
                var r = new SshReader(payload, 1, payload.Length - 1);
                r.ReadAscii();
                bool wantReply = r.ReadBool();
                if (wantReply)
                {
                    var w = new SshWriter(4);
                    w.WriteByte(82);
                    SendPayload(w.ToArray());
                }
                return true;
            }
            case 90:  // CHANNEL_OPEN
                return OnChannelOpen(payload);
            case 93:  // WINDOW_ADJUST
            {
                var r = new SshReader(payload, 1, payload.Length - 1);
                uint recip = r.ReadU32();
                uint add = r.ReadU32();
                if (recip == _ourChannelId)
                {
                    _remoteWindow += add;
                    FlushPendingOut();
                }
                return true;
            }
            case 94:  // CHANNEL_DATA
            {
                var r = new SshReader(payload, 1, payload.Length - 1);
                uint recip = r.ReadU32();
                byte[] data = r.ReadString();
                if (recip != _ourChannelId || data == null)
                    return true;
                _ourWindowConsumed += (uint)data.Length;
                if (_ourWindowConsumed > OurWindow / 2)
                {
                    SendWindowAdjust(_ourWindowConsumed);
                    _ourWindowConsumed = 0;
                }
                if (_sessionActive && !_execMode && !_sessionDone)
                    ProcessSessionInput(data);
                return true;
            }
            case 96:  // CHANNEL_EOF
            {
                var r = new SshReader(payload, 1, payload.Length - 1);
                uint recip = r.ReadU32();
                if (recip == _ourChannelId && _sessionActive && !_sessionDone)
                {
                    if (!_execMode)
                        EndSession(0);
                }
                return true;
            }
            case 97:  // CHANNEL_CLOSE
            {
                var r = new SshReader(payload, 1, payload.Length - 1);
                uint recip = r.ReadU32();
                if (recip == _ourChannelId)
                {
                    _clientClosed = true;
                    if (!_sentClose)
                        SendChannelClose();
                    _state = StClosed;
                    _sock.Close();
                }
                return true;
            }
            case 98:  // CHANNEL_REQUEST
                return OnChannelRequest(payload);
            case 99:  // CHANNEL_SUCCESS
            case 100: // CHANNEL_FAILURE
            case 2:   // IGNORE
            case 3:   // UNIMPLEMENTED
            case 4:   // DEBUG
            case 7:   // EXT_INFO
                return true;
            default:
                return true;
        }
    }

    private bool OnChannelOpen(byte[] payload)
    {
        var r = new SshReader(payload, 1, payload.Length - 1);
        string type = r.ReadAscii();
        uint sender = r.ReadU32();
        uint window = r.ReadU32();
        uint maxPacket = r.ReadU32();

        if (type != "session" || _channelOpen)
        {
            var fail = new SshWriter(64);
            fail.WriteByte(92);
            fail.WriteU32(sender);
            fail.WriteU32(3);   // unknown channel type / already open
            fail.WriteString("channel refused");
            fail.WriteString("");
            SendPayload(fail.ToArray());
            return true;
        }

        _channelOpen = true;
        _clientChannelId = sender;
        _remoteWindow = window;
        _remoteMaxPacket = (int)(maxPacket < 32768 ? maxPacket : 32768);
        if (_remoteMaxPacket < 1024)
            _remoteMaxPacket = 1024;

        var ok = new SshWriter(40);
        ok.WriteByte(91);
        ok.WriteU32(sender);
        ok.WriteU32(_ourChannelId);
        ok.WriteU32(OurWindow);
        ok.WriteU32(32768);
        SendPayload(ok.ToArray());
        return true;
    }

    private bool OnChannelRequest(byte[] payload)
    {
        var r = new SshReader(payload, 1, payload.Length - 1);
        uint recip = r.ReadU32();
        string request = r.ReadAscii();
        if (recip != _ourChannelId || request == null)
            return true;
        bool wantReply = r.ReadBool();

        if (request == "pty-req")
        {
            r.ReadAscii();   // terminal type
            r.ReadU32();     // columns
            r.ReadU32();     // rows
            r.ReadU32();     // pixel width
            r.ReadU32();     // pixel height
            r.ReadString();  // modes
            if (wantReply)
                SendChannelReply(true);
            return true;
        }
        if (request == "env")
        {
            r.ReadAscii();
            r.ReadAscii();
            if (wantReply)
                SendChannelReply(true);
            return true;
        }
        if (request == "shell")
        {
            if (wantReply)
                SendChannelReply(true);
            StartShell();
            return true;
        }
        if (request == "exec")
        {
            string command = r.ReadAscii();
            if (wantReply)
                SendChannelReply(true);
            RunExec(command == null ? "" : command);
            return true;
        }
        if (request == "window-change")
        {
            r.ReadU32();
            r.ReadU32();
            r.ReadU32();
            r.ReadU32();
            if (wantReply)
                SendChannelReply(true);
            return true;
        }

        if (wantReply)
            SendChannelReply(false);
        return true;
    }

    private void SendChannelReply(bool success)
    {
        var w = new SshWriter(8);
        w.WriteByte(success ? (byte)99 : (byte)100);
        w.WriteU32(_clientChannelId);
        SendPayload(w.ToArray());
    }

    // ==================== Session (shell / exec) ====================

    private bool _execMode;

    private void StartShell()
    {
        _sessionActive = true;
        _execMode = false;
        _exitCode = 0;
        SendSessionText("Welcome to NeutrinoOS (managed-C# operating system)\r\n");
        SendSessionText("Remote shell over SSH. Type 'help' for commands, 'exit' to log out.\r\n\r\n");
        SendPrompt();
    }

    private void RunExec(string command)
    {
        _sessionActive = true;
        _execMode = true;
        int code;
        string output = ShellBridge.Exec(command, out code);
        _exitCode = code < 0 ? 1 : code;
        SendSessionText(TranslateNewlines(output));
        EndSession(_exitCode);
    }

    private void SendPrompt()
    {
        string user = _user == null ? "user" : _user;
        SendSessionText(user + "@neutrinoos:~$ ");
    }

    private void ProcessSessionInput(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];

            if (_escState == 1)
            {
                _escState = b == 0x5B ? 2 : 0;
                continue;
            }
            if (_escState == 2)
            {
                _escState = 0;
                if (b == 0x41)
                    BrowseHistory(1);
                else if (b == 0x42)
                    BrowseHistory(-1);
                continue;
            }

            if (b == 0x1B)
            {
                _escState = 1;
                continue;
            }
            if (b == 0x0D || b == 0x0A)
            {
                SendSessionText("\r\n");
                ExecuteSessionLine();
                continue;
            }
            if (b == 0x7F || b == 0x08)
            {
                if (_lineLen > 0)
                {
                    _lineLen--;
                    SendSessionText("\b \b");
                }
                continue;
            }
            if (b == 0x03)
            {
                SendSessionText("^C\r\n");
                _lineLen = 0;
                SendPrompt();
                continue;
            }
            if (b == 0x04)
            {
                if (_lineLen == 0)
                {
                    SendSessionText("logout\r\n");
                    EndSession(0);
                    return;
                }
                continue;
            }
            if (b >= 32 && b < 127)
            {
                if (_lineLen < _line.Length - 1)
                {
                    _line[_lineLen++] = (char)b;
                    var echo = new byte[1];
                    echo[0] = b;
                    SendSessionBytes(echo);
                }
            }
        }
    }

    private void BrowseHistory(int direction)
    {
        if (_historyCount == 0)
            return;
        if (_historyBrowse < 0)
            _historyBrowse = _historyCount;

        if (direction > 0)
        {
            if (_historyBrowse > 0)
                _historyBrowse--;
        }
        else
        {
            if (_historyBrowse < _historyCount - 1)
                _historyBrowse++;
            else
            {
                _historyBrowse = _historyCount;
                ReplaceLine("");
                return;
            }
        }
        ReplaceLine(_historyBrowse < _historyCount ? _history[_historyBrowse] : "");
    }

    private void ReplaceLine(string text)
    {
        // Erase the current line on the client, then draw the new one.
        SendSessionText("\r                                                                \r");
        _lineLen = 0;
        if (text == null)
            return;
        for (int i = 0; i < text.Length && _lineLen < _line.Length - 1; i++)
        {
            _line[_lineLen++] = text[i];
        }
        SendSessionText(text);
    }

    private void ExecuteSessionLine()
    {
        string command = new string(_line, 0, _lineLen);
        _lineLen = 0;
        _historyBrowse = -1;

        // Manual trim (String.Trim() is unresolvable in the guest JIT).
        int ts = 0;
        int te = command.Length;
        while (ts < te && (command[ts] == ' ' || command[ts] == '\t'))
            ts++;
        while (te > ts && (command[te - 1] == ' ' || command[te - 1] == '\t'))
            te--;
        string trimmed = ts == 0 && te == command.Length
            ? command
            : command.Substring(ts, te - ts);
        if (trimmed.Length == 0)
        {
            SendPrompt();
            return;
        }

        // History ring.
        if (_historyCount < _history.Length)
        {
            _history[_historyCount++] = trimmed;
        }
        else
        {
            for (int i = 1; i < _history.Length; i++)
                _history[i - 1] = _history[i];
            _history[_history.Length - 1] = trimmed;
        }

        int code;
        string output = ShellBridge.Exec(trimmed, out code);
        SendSessionText(TranslateNewlines(output));

        if (IsLogout(trimmed))
        {
            SendSessionText("logout\r\n");
            EndSession(0);
            return;
        }

        SendPrompt();
    }

    private static bool IsLogout(string command)
        => command == "exit" || command == "logout";

    private static string TranslateNewlines(string s)
    {
        // Build with an explicit char buffer: `string + char` is not
        // supported by the guest JIT.
        int extra = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\n' && (i == 0 || s[i - 1] != '\r'))
                extra++;
        }
        var chars = new char[s.Length + extra];
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\n' && (i == 0 || s[i - 1] != '\r'))
            {
                chars[n++] = '\r';
                chars[n++] = '\n';
            }
            else
            {
                chars[n++] = c;
            }
        }
        return new string(chars, 0, n);
    }

    private void EndSession(int exitCode)
    {
        if (_sessionDone)
            return;
        _sessionDone = true;

        var eof = new SshWriter(8);
        eof.WriteByte(96);
        eof.WriteU32(_clientChannelId);
        SendPayload(eof.ToArray());

        var status = new SshWriter(32);
        status.WriteByte(98);
        status.WriteU32(_clientChannelId);
        status.WriteString("exit-status");
        status.WriteBool(false);
        status.WriteU32((uint)exitCode);
        SendPayload(status.ToArray());

        SendChannelClose();
    }

    private void SendChannelClose()
    {
        if (_sentClose)
            return;
        _sentClose = true;
        var w = new SshWriter(8);
        w.WriteByte(97);
        w.WriteU32(_clientChannelId);
        SendPayload(w.ToArray());
    }

    private void SendWindowAdjust(uint add)
    {
        var w = new SshWriter(12);
        w.WriteByte(93);
        w.WriteU32(_clientChannelId);
        w.WriteU32(add);
        SendPayload(w.ToArray());
    }

    // ==================== Output queue ====================

    private void SendSessionText(string text)
    {
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
            bytes[i] = (byte)text[i];
        SendSessionBytes(bytes);
    }

    private void SendSessionBytes(byte[] bytes)
    {
        if (!_channelOpen || _sentClose)
            return;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (_pendingLen >= _pendingOut.Length)
                break;   // cap: drop beyond 64 KiB of queued output
            _pendingOut[_pendingLen++] = bytes[i];
        }
        FlushPendingOut();
    }

    private void FlushPendingOut()
    {
        if (!_channelOpen || _sentClose)
            return;
        while (_pendingLen > 0 && _remoteWindow > 0)
        {
            int chunk = _pendingLen;
            if (chunk > (int)_remoteWindow)
                chunk = (int)_remoteWindow;
            int maxChunk = _remoteMaxPacket - 64;
            if (maxChunk < 128)
                maxChunk = 128;
            if (chunk > maxChunk)
                chunk = maxChunk;

            var w = new SshWriter(16 + chunk);
            w.WriteByte(94);
            w.WriteU32(_clientChannelId);
            w.WriteU32((uint)chunk);
            w.WriteRaw(_pendingOut, 0, chunk);
            SendPayload(w.ToArray());

            _remoteWindow -= (uint)chunk;
            for (int i = chunk; i < _pendingLen; i++)
                _pendingOut[i - chunk] = _pendingOut[i];
            _pendingLen -= chunk;
        }
    }

    private void Disconnect(int reason, string description)
    {
        var w = new SshWriter(96);
        w.WriteByte(1);
        w.WriteU32((uint)reason);
        w.WriteString(description);
        w.WriteString("");
        SendPayload(w.ToArray());
        _state = StClosed;
        _sock.Close();
    }
}
