// NeutrinoOS DDK - SSH authentication guard (Phase 7 security)
//
// Two jobs, both shared by every sshd connection:
//
//   1. Brute-force lockout: failed password/publickey attempts are
//      counted per source IP; when an IP reaches BanThreshold failures
//      it is banned for BanSeconds (new connections are refused at
//      accept time). A successful login clears the IP's counter.
//
//   2. Audit trail: notable auth events (accepted connection, auth
//      success, auth failure with running count, ban, banned reject,
//      over-limit disconnect) are appended to /var/log/auth.log with a
//      wall-clock timestamp from the CMOS RTC.
//
// Thresholds come from /etc/ssh/sshd_config (BanThreshold=,
// BanSeconds=) read by SshService.LoadConfig; defaults are 5 and 300.
//
// The table is deliberately tiny (8 slots) and NOT locked: like all
// services it runs cooperatively on one kernel thread.

using System;
using System.IO;
using ProtonOS.DDK.Kernel;

namespace ProtonOS.DDK.Services.Ssh;

/// <summary>Per-IP SSH auth failure tracking + /var/log/auth.log audit.</summary>
public static class SshAuthGuard
{
    private const int Slots = 8;
    private const string LogPath = "/var/log/auth.log";

    // ==================== configuration (set by SshService.LoadConfig) ====================

    /// <summary>Failures before the IP is temporarily banned (default 5).</summary>
    public static int BanThreshold = 5;

    /// <summary>Ban duration in seconds (default 300).</summary>
    public static int BanSeconds = 300;

    // ==================== state ====================

    private struct Slot
    {
        public uint Ip;              // 0 = free
        public int Failures;
        public ulong BannedUntilMs;  // 0 = not banned
    }

    private static readonly Slot[] _slots = new Slot[Slots];

    // ==================== queries ====================

    /// <summary>True when the IP is currently banned (expired bans auto-clear).</summary>
    public static bool IsBanned(uint ip)
    {
        int idx = Find(ip);
        if (idx < 0)
            return false;
        if (_slots[idx].BannedUntilMs == 0)
            return false;
        if (Timer.GetUptimeMilliseconds() >= _slots[idx].BannedUntilMs)
        {
            _slots[idx] = default;
            return false;
        }
        return true;
    }

    /// <summary>Number of currently banned addresses (for status/tests).</summary>
    public static int BannedCount()
    {
        int n = 0;
        for (int i = 0; i < Slots; i++)
        {
            if (_slots[i].Ip != 0 && IsBanned(_slots[i].Ip))
                n++;
        }
        return n;
    }

    // ==================== events ====================

    /// <summary>Log an accepted connection.</summary>
    public static void LogAccept(uint ip)
    {
        Log("connection from " + IpToStr(ip));
    }

    /// <summary>
    /// Record a failed authentication; bans the IP when the threshold is
    /// reached. Returns the IP's running failure count (post-increment).
    /// </summary>
    public static int RecordFailure(uint ip, string user, string method)
    {
        int idx = Find(ip);
        if (idx < 0)
        {
            idx = FindFree();
            if (idx < 0)
                idx = 0;                 // overwrite slot 0 when the table is full
            _slots[idx] = default;
            _slots[idx].Ip = ip;
        }

        _slots[idx].Failures++;

        Log("auth fail from " + IpToStr(ip) + " user=" + (user == null ? "?" : user) +
            " method=" + (method == null ? "?" : method) +
            " failures=" + IntToStr(_slots[idx].Failures) + "/" + IntToStr(BanThreshold));

        if (_slots[idx].Failures >= BanThreshold)
        {
            _slots[idx].BannedUntilMs = Timer.GetUptimeMilliseconds() + (ulong)BanSeconds * 1000;
            _slots[idx].Failures = 0;
            Log("banning " + IpToStr(ip) + " for " + IntToStr(BanSeconds) + "s");
        }

        return _slots[idx].Failures;
    }

    /// <summary>Record a successful login (clears failures; logs when previously counted).</summary>
    public static void RecordSuccess(uint ip, string user)
    {
        int idx = Find(ip);
        if (idx >= 0)
            _slots[idx] = default;
        Log("auth success from " + IpToStr(ip) + " user=" + (user == null ? "?" : user));
    }

    /// <summary>Log a refused connection from a banned IP.</summary>
    public static void LogBannedReject(uint ip)
    {
        Log("refused banned " + IpToStr(ip));
    }

    /// <summary>Log a disconnect due to too many failures on one connection.</summary>
    public static void LogTooManyFailures(uint ip)
    {
        Log("disconnect " + IpToStr(ip) + ": too many authentication failures");
    }

    /// <summary>Reset all state (tests).</summary>
    public static void DebugReset()
    {
        for (int i = 0; i < Slots; i++)
            _slots[i] = default;
    }

    // ==================== audit file ====================

    /// <summary>Append a timestamped line to /var/log/auth.log (best effort).</summary>
    public static void Log(string message)
    {
        try
        {
            // korlib's Directory.CreateDirectory is single-level (documented
            // deviation from the BCL), so create /var before /var/log.
            if (!Directory.Exists("/var"))
                Directory.CreateDirectory("/var");
            if (!Directory.Exists("/var/log"))
                Directory.CreateDirectory("/var/log");
            File.AppendAllText(LogPath, Timestamp() + " sshd: " + message + "\n");
        }
        catch
        {
            // Never let audit I/O take the service down.
        }
    }

    private static string Timestamp()
    {
        int year, month, day, hour, minute, second;
        SysInfo.GetWallClock(out year, out month, out day, out hour, out minute, out second);

        // The CMOS RTC occasionally returns stray fields under QEMU/TCG
        // (observed: second = 2^33 + n). Validate before trusting it and
        // fall back to the uptime form so the audit trail stays parseable.
        bool valid = year >= 1970 && year <= 2999 &&
                     month >= 1 && month <= 12 &&
                     day >= 1 && day <= 31 &&
                     hour >= 0 && hour < 24 &&
                     minute >= 0 && minute < 60 &&
                     second >= 0 && second < 60;
        if (!valid)
            return "[uptime " + IntToStr((int)(Timer.GetUptimeMilliseconds() / 1000)) + "s]";

        return "[" + IntToStr(year) + "-" + Pad2(month) + "-" + Pad2(day) + " " +
               Pad2(hour) + ":" + Pad2(minute) + ":" + Pad2(second) + "]";
    }

    // ==================== helpers ====================

    private static int Find(uint ip)
    {
        for (int i = 0; i < Slots; i++)
        {
            if (_slots[i].Ip == ip)
                return i;
        }
        return -1;
    }

    private static int FindFree()
    {
        for (int i = 0; i < Slots; i++)
        {
            if (_slots[i].Ip == 0)
                return i;
        }
        return -1;
    }

    /// <summary>Dotted-quad formatter (no string formatting APIs in the JIT world).</summary>
    public static string IpToStr(uint ip)
    {
        return IntToStr((int)((ip >> 24) & 0xFF)) + "." +
               IntToStr((int)((ip >> 16) & 0xFF)) + "." +
               IntToStr((int)((ip >> 8) & 0xFF)) + "." +
               IntToStr((int)(ip & 0xFF));
    }

    private static string Pad2(int v)
    {
        if (v < 0)
            v = 0;
        if (v < 10)
            return "0" + IntToStr(v);
        return IntToStr(v);
    }

    private static string IntToStr(int value)
    {
        if (value == 0)
            return "0";
        bool neg = value < 0;
        if (neg)
            value = -value;
        var digits = new char[12];
        int n = 0;
        while (value > 0)
        {
            digits[n++] = (char)('0' + (value % 10));
            value /= 10;
        }
        var buf = new char[n + (neg ? 1 : 0)];
        int p = 0;
        if (neg)
            buf[p++] = '-';
        for (int i = n - 1; i >= 0; i--)
            buf[p++] = digits[i];
        return new string(buf);
    }
}
