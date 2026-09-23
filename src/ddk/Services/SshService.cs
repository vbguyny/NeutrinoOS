// ProtonOS DDK - SSH server service (Phase 6)
//
// Cooperative background service driven by the kernel: `sshd` (the
// utility shim) asks the kernel's ServiceRegistry to start it, and the
// kernel calls Tick() from the shell idle hook - no threads, no
// preemption, everything on the boot thread.
//
// On first start the service generates an Ed25519 host key and writes
// it to /etc/ssh/ssh_host_ed25519_key (hex-encoded seed; NeutrinoOS
// format, documented). Optional /etc/ssh/sshd_config (key=value) can
// override the port (default 22).

using System;
using System.IO;
using ProtonOS.DDK.Crypto;
using ProtonOS.DDK.Network;
using ProtonOS.DDK.Network.Sockets;
using ProtonOS.DDK.Network.Stack;
using ProtonOS.DDK.Services.Ssh;
using ProtonOS.DDK.Util;

namespace ProtonOS.DDK.Services;

/// <summary>The SSH server service (see file header).</summary>
public static class SshService
{
    private const string HostKeyPath = "/etc/ssh/ssh_host_ed25519_key";
    private const string HostKeyPubPath = "/etc/ssh/ssh_host_ed25519_key.pub";
    private const string ConfigPath = "/etc/ssh/sshd_config";
    private const int MaxConnections = 4;

    private static TcpServer _listener;
    private static NetworkStack _stack;
    private static readonly SshConnection[] _connections = new SshConnection[MaxConnections];
    private static byte[] _hostSeed;
    private static bool _active;
    private static ushort _port = 22;

    /// <summary>The host key seed (32 bytes); valid after Start.</summary>
    public static byte[] HostSeed => _hostSeed;

    /// <summary>True when the listener is running.</summary>
    public static bool Active => _active;

    // ==================== Service lifecycle ====================

    /// <summary>Start the service; 0 = ok (called by the kernel registry).</summary>
    public static int Start()
    {
        if (_active)
            return 0;

        var eth = NetworkManager.GetInterface("eth0");
        if (eth == null || eth.Stack == null)
        {
            Console.WriteLine("[sshd] no eth0 interface - start QEMU/VBox with a NIC");
            return -20;
        }
        _stack = eth.Stack;

        if (!LoadOrGenerateHostKey())
            return -21;

        LoadConfig();

        _listener = new TcpServer(_stack, _port, true);
        if (!_listener.Start())
        {
            Console.Write("[sshd] bind failed on port ");
            Console.WriteLine(IntToStr(_port));
            _listener = null;
            return -22;
        }

        _active = true;
        Console.Write("[sshd] listening on port ");
        Console.Write(IntToStr(_port));
        Console.WriteLine(" (host key ssh-ed25519)");
        return 0;
    }

    /// <summary>Stop the service (called by the kernel registry).</summary>
    public static void Stop()
    {
        if (!_active)
            return;
        for (int i = 0; i < _connections.Length; i++)
        {
            if (_connections[i] != null)
            {
                _connections[i].Close();
                _connections[i] = null;
            }
        }
        if (_listener != null)
        {
            _listener.Stop();
            _listener = null;
        }
        _active = false;
        Console.WriteLine("[sshd] stopped");
    }

    /// <summary>One bounded work slice (called from the kernel idle hook).</summary>
    public static void Tick()
    {
        if (!_active || _listener == null)
            return;

        NetworkPump.Pump(_stack, 4);

        // Accept new connections into free slots.
        while (_listener.Pending())
        {
            var sock = _listener.Accept();
            if (sock == null)
                break;
            int slot = FindFreeSlot();
            if (slot < 0)
            {
                sock.Close();   // at capacity
                continue;
            }
            _connections[slot] = new SshConnection(sock, "SSH-2.0-NeutrinoOS_1.0");
        }

        // Advance every connection.
        for (int i = 0; i < _connections.Length; i++)
        {
            var c = _connections[i];
            if (c == null)
                continue;
            c.Tick();
            if (c.IsClosed)
                _connections[i] = null;
        }
    }

    private static int FindFreeSlot()
    {
        for (int i = 0; i < _connections.Length; i++)
        {
            if (_connections[i] == null)
                return i;
        }
        return -1;
    }

    // ==================== Host key + config ====================

    private static bool LoadOrGenerateHostKey()
    {
        try
        {
            if (File.Exists(HostKeyPath))
            {
                string hex = TrimStr(File.ReadAllText(HostKeyPath));
                var seed = Scrypt.FromHex(hex);
                if (seed != null && seed.Length == 32)
                {
                    _hostSeed = seed;
                    return true;
                }
            }
        }
        catch
        {
            // fall through to regeneration
        }

        // First boot: generate and persist a fresh host key.
        var fresh = Csprng.GetBytes(32);
        var pub = Ed25519.PublicKeyFromSeed(fresh);

        try
        {
            if (!Directory.Exists("/etc/ssh"))
                Directory.CreateDirectory("/etc/ssh");
            File.WriteAllText(HostKeyPath, Scrypt.ToHex(fresh) + "\n");
            File.WriteAllText(HostKeyPubPath, Scrypt.ToHex(pub) + "\n");
            Console.WriteLine("[sshd] generated new ssh-ed25519 host key (/etc/ssh)");
        }
        catch
        {
            Console.WriteLine("[sshd] warning: could not persist host key");
        }

        _hostSeed = fresh;
        return true;
    }

    private static void LoadConfig()
    {
        _port = 22;
        try
        {
            if (!File.Exists(ConfigPath))
                return;
            string[] lines = ProtonOS.DDK.Util.TextLines.Split(File.ReadAllText(ConfigPath));
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line == null)
                    continue;
                line = TrimStr(line);
                if (line.Length == 0 || line[0] == '#')
                    continue;
                int eq = -1;
                for (int k = 0; k < line.Length; k++)
                {
                    if (line[k] == '=')
                    {
                        eq = k;
                        break;
                    }
                }
                if (eq <= 0)
                    continue;
                string key = TrimStr(line.Substring(0, eq));
                string value = TrimStr(line.Substring(eq + 1));
                if (key == "Port")
                {
                    int p = 0;
                    bool ok = value.Length > 0;
                    for (int k = 0; k < value.Length; k++)
                    {
                        char ch = value[k];
                        if (ch < '0' || ch > '9')
                        {
                            ok = false;
                            break;
                        }
                        p = p * 10 + (ch - '0');
                    }
                    if (ok && p > 0 && p < 65536)
                        _port = (ushort)p;
                }
            }
        }
        catch
        {
            _port = 22;
        }
    }

    private static string TrimStr(string s)
    {
        int start = 0;
        int end = s.Length;
        while (start < end && (s[start] == ' ' || s[start] == '\t' || s[start] == '\r'))
            start++;
        while (end > start && (s[end - 1] == ' ' || s[end - 1] == '\t' || s[end - 1] == '\r'))
            end--;
        if (start == 0 && end == s.Length)
            return s;
        return s.Substring(start, end - start);
    }

    private static string IntToStr(int v)
    {
        if (v == 0)
            return "0";
        var chars = new char[8];
        int n = 0;
        while (v > 0)
        {
            chars[n++] = (char)('0' + v % 10);
            v /= 10;
        }
        // reverse
        for (int i = 0; i < n / 2; i++)
        {
            char t = chars[i];
            chars[i] = chars[n - 1 - i];
            chars[n - 1 - i] = t;
        }
        return new string(chars, 0, n);
    }
}
