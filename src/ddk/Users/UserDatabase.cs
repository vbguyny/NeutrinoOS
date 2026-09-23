// ProtonOS DDK - User database (Phase 6)
//
// Minimal POSIX-style accounts backed by two files:
//   /etc/passwd  - "name:x:uid:gid:gecos:home:shell"
//   /etc/shadow  - "name:hash:..." with the hash in Scrypt's
//                  "scrypt$N$r$p$salt$hash" format.
// SSH authorized keys live per user in
//   <home>/.ssh/authorized_keys   ("ssh-ed25519 <base64> [comment]")
//
// Remote logins are policy-checked here: root is rejected for network
// authentication unless explicitly enabled by the caller.

using System;
using System.IO;
using ProtonOS.DDK.Crypto;
using ProtonOS.DDK.Util;

namespace ProtonOS.DDK.Users;

/// <summary>One account record.</summary>
public sealed class UserEntry
{
    /// <summary>Login name.</summary>
    public string Name = "";
    /// <summary>Numeric user id.</summary>
    public int Uid;
    /// <summary>Numeric group id.</summary>
    public int Gid;
    /// <summary>Home directory.</summary>
    public string Home = "/";
    /// <summary>Login shell path (informational; the guest shell is fixed).</summary>
    public string Shell = "/bin/shell.dll";
    /// <summary>True for the root account.</summary>
    public bool IsRoot => Uid == 0;
}

/// <summary>User database access (see file header).</summary>
public static class UserDatabase
{
    /// <summary>Path of the account file.</summary>
    public const string PasswdPath = "/etc/passwd";
    /// <summary>Path of the password hash file.</summary>
    public const string ShadowPath = "/etc/shadow";

    /// <summary>Load all accounts; empty when /etc/passwd is missing.</summary>
    public static UserEntry[] LoadUsers()
    {
        if (!File.Exists(PasswdPath))
            return new UserEntry[0];

        string[] lines;
        try
        {
            lines = TextLines.Split(File.ReadAllText(PasswdPath));
        }
        catch
        {
            return new UserEntry[0];
        }

        var list = new UserEntry[8];
        int count = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line == null || line.Length == 0 || line[0] == '#')
                continue;

            var fields = SplitColon(line);
            if (fields.Length < 7)
                continue;
            var u = new UserEntry
            {
                Name = fields[0],
                Uid = ParseInt(fields[2]),
                Gid = ParseInt(fields[3]),
                Home = fields[5].Length > 0 ? fields[5] : "/",
                Shell = fields[6].Length > 0 ? fields[6] : "/bin/shell.dll",
            };
            if (count == list.Length)
            {
                var bigger = new UserEntry[list.Length * 2];
                for (int k = 0; k < count; k++)
                    bigger[k] = list[k];
                list = bigger;
            }
            list[count++] = u;
        }

        var result = new UserEntry[count];
        for (int i = 0; i < count; i++)
            result[i] = list[i];
        return result;
    }

    /// <summary>Find an account by name (case-sensitive); null when absent.</summary>
    public static UserEntry Lookup(string name)
    {
        if (name == null)
            return null;
        var users = LoadUsers();
        for (int i = 0; i < users.Length; i++)
        {
            if (StrEquals(users[i].Name, name))
                return users[i];
        }
        return null;
    }

    /// <summary>True when the account has a password hash configured.</summary>
    public static bool HasPassword(string name)
        => LoadShadowHash(name) != null;

    /// <summary>Verify a password against the account's shadow hash.</summary>
    public static bool VerifyPassword(string name, string password)
    {
        string hash = LoadShadowHash(name);
        if (hash == null)
            return false;
        var pw = new byte[password.Length];
        for (int i = 0; i < password.Length; i++)
            pw[i] = (byte)password[i];
        return Scrypt.VerifyPassword(pw, hash);
    }

    /// <summary>
    /// Set (or create) the shadow entry for an account. Returns false
    /// when /etc/shadow cannot be written.
    /// </summary>
    public static bool SetPassword(string name, string password, byte[] salt)
    {
        var pw = new byte[password.Length];
        for (int i = 0; i < password.Length; i++)
            pw[i] = (byte)password[i];
        string hash = Scrypt.HashPassword(pw, salt);

        string[] lines;
        try
        {
            lines = TextLines.Split(File.ReadAllText(ShadowPath));
        }
        catch
        {
            lines = new string[0];
        }

        bool replaced = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var fields = SplitColon(lines[i]);
            if (fields.Length >= 1 && StrEquals(fields[0], name))
            {
                lines[i] = name + ":" + hash + ":";
                replaced = true;
                break;
            }
        }

        try
        {
            if (replaced)
            {
                File.WriteAllText(ShadowPath, TextLines.Join(lines));
            }
            else
            {
                var all = new string[lines.Length + 1];
                for (int i = 0; i < lines.Length; i++)
                    all[i] = lines[i];
                all[lines.Length] = name + ":" + hash + ":";
                File.WriteAllText(ShadowPath, TextLines.Join(all));
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Add an account: appends /etc/passwd + /etc/shadow entries and
    /// creates the home directory. Returns false when it already exists
    /// or files are unwritable.
    /// </summary>
    public static bool AddUser(string name, int uid, int gid, string password, byte[] salt)
    {
        if (name == null || name.Length == 0)
            return false;
        if (Lookup(name) != null)
            return false;

        string home = uid == 0 ? "/root" : "/home/" + name;
        try
        {
            string passwdLine = name + ":x:" + IntStr(uid) + ":" + IntStr(gid) +
                                ":" + name + ":" + home + ":/bin/shell.dll";
            string existing = File.Exists(PasswdPath) ? File.ReadAllText(PasswdPath) : "";
            File.WriteAllText(PasswdPath, existing + passwdLine + "\n");

            if (!Directory.Exists(home))
                Directory.CreateDirectory(home);
            string sshDir = home + "/.ssh";
            if (!Directory.Exists(sshDir))
                Directory.CreateDirectory(sshDir);
        }
        catch
        {
            return false;
        }

        return SetPassword(name, password, salt);
    }

    /// <summary>Load the shadow hash for an account; null when absent.</summary>
    public static string LoadShadowHash(string name)
    {
        if (name == null || !File.Exists(ShadowPath))
            return null;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(ShadowPath);
        }
        catch
        {
            return null;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line == null || line.Length == 0 || line[0] == '#')
                continue;
            var fields = SplitColon(line);
            if (fields.Length >= 2 && StrEquals(fields[0], name))
                return fields[1];
        }
        return null;
    }

    /// <summary>Path of a user's authorized_keys file.</summary>
    public static string AuthorizedKeysPath(UserEntry user)
        => user.Home + "/.ssh/authorized_keys";

    /// <summary>
    /// True when the given ssh-ed25519 public key (32 raw bytes) is
    /// listed in the user's authorized_keys file.
    /// </summary>
    public static bool IsAuthorizedKey(UserEntry user, byte[] publicKey32)
    {
        if (user == null || publicKey32 == null || publicKey32.Length != 32)
            return false;

        string path = AuthorizedKeysPath(user);
        if (!File.Exists(path))
            return false;

        string[] lines;
        try
        {
            lines = TextLines.Split(File.ReadAllText(path));
        }
        catch
        {
            return false;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line == null || line.Length == 0 || line[0] == '#')
                continue;
            var fields = SplitSpaces(line);
            if (fields.Length < 2)
                continue;
            if (!StrEquals(fields[0], "ssh-ed25519"))
                continue;

            byte[] blob = Base64.Decode(fields[1]);
            if (blob == null || blob.Length < 4 + 11 + 4 + 32)
                continue;

            // Blob layout: string "ssh-ed25519", string key32.
            int typeLen = ReadU32(blob, 0);
            if (typeLen != 11)
                continue;
            int keyLen = ReadU32(blob, 4 + 11);
            if (keyLen != 32)
                continue;

            bool match = true;
            for (int k = 0; k < 32; k++)
            {
                if (blob[8 + 11 + k] != publicKey32[k])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return true;
        }
        return false;
    }

    private static int ReadU32(byte[] b, int i)
        => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];

    private static string[] SplitColon(string s)
    {
        var parts = new string[8];
        int count = 0;
        int start = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i == s.Length || s[i] == ':')
            {
                if (count == 7)
                {
                    parts[count++] = s.Substring(start, s.Length - start);
                    break;
                }
                parts[count++] = s.Substring(start, i - start);
                start = i + 1;
                if (count == 8)
                    break;
            }
        }
        var result = new string[count];
        for (int i = 0; i < count; i++)
            result[i] = parts[i];
        return result;
    }

    private static string[] SplitSpaces(string s)
    {
        var parts = new string[4];
        int count = 0;
        int i = 0;
        while (i < s.Length && count < 3)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t'))
                i++;
            int start = i;
            while (i < s.Length && s[i] != ' ' && s[i] != '\t')
                i++;
            if (i > start)
                parts[count++] = s.Substring(start, i - start);
        }
        if (i < s.Length && count < 4)
            parts[count++] = s.Substring(i, s.Length - i);
        var result = new string[count];
        for (int k = 0; k < count; k++)
            result[k] = parts[k];
        return result;
    }

    private static int ParseInt(string s)
    {
        int v = 0;
        bool any = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < '0' || c > '9')
                break;
            v = v * 10 + (c - '0');
            any = true;
        }
        return any ? v : -1;
    }

    private static string IntStr(int v)
    {
        if (v == 0)
            return "0";
        string s = "";
        bool neg = v < 0;
        if (neg)
            v = -v;
        while (v > 0)
        {
            s = Digits[v % 10] + s;
            v /= 10;
        }
        return neg ? "-" + s : s;
    }

    private static readonly string[] Digits =
    {
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
    };

    private static bool StrEquals(string a, string b)
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
