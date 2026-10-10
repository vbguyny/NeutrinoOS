// NeutrinoOS DDK - SFTP v3 subsystem server (SSH file transfer)
//
// Implements the "sftp" subsystem of the SSH connection protocol so
// standard clients (OpenSSH `sftp`/`scp`, WinSCP, ...) can list, upload,
// download, rename and delete files on the boot volume.
//
// The client speaks SFTP packets over the session channel's data stream:
//   uint32 length | byte type | payload...
// This server consumes whole packets from the channel feed and answers
// through SshConnection.SendChannelBytes. Everything is synchronous and
// bounded: no threads, no exceptions - failures map to SFTP status codes.
//
// SIZE: files stream through chunked (offset-based) boot-volume I/O
// (DDK/Kernel/BootFiles.cs -> Kernel_BootFileReadRange/WriteRange), so
// whole files never enter the guest heap and transfers of any size
// work. Per-request buffers stay at 32 KiB, safely below the
// 85000-byte LOH threshold; oversized client write packets (OpenSSH
// >= 9 sends up to 256 KiB per WRITE) are streamed straight to disk.
// Offsets are int: positions beyond 2 GiB fail with FAILURE (files
// themselves may be larger).
//
// Supported: REALPATH, STAT/LSTAT/FSTAT, OPEN, CLOSE, READ, WRITE,
// OPENDIR, READDIR, REMOVE, MKDIR, RMDIR, RENAME, SETSTAT/FSETSTAT
// (accepted, no-op). Symlinks and extended requests are refused.

using System;
using System.IO;
using NeutrinoOS.DDK.Kernel;

namespace NeutrinoOS.DDK.Services.Ssh;

/// <summary>SFTP v3 server for one SSH session channel (see file header).</summary>
public sealed class SftpServer
{
    private const int MaxHandles = 12;
    private const int RxCapacity = 65536;
    private const int ReadChunk = 32768;
    private const int MaxPathDepth = 24;

    // SFTP message types.
    private const byte TInit = 1;
    private const byte TVersion = 2;
    private const byte TOpen = 3;
    private const byte TClose = 4;
    private const byte TRead = 5;
    private const byte TWrite = 6;
    private const byte TLstat = 7;
    private const byte TFstat = 8;
    private const byte TSetstat = 9;
    private const byte TFsetstat = 10;
    private const byte TOpendir = 11;
    private const byte TReaddir = 12;
    private const byte TRemove = 13;
    private const byte TMkdir = 14;
    private const byte TRmdir = 15;
    private const byte TRealpath = 16;
    private const byte TStat = 17;
    private const byte TRename = 18;
    private const byte TReadlink = 19;
    private const byte TSymlink = 20;
    private const byte TExtended = 200;

    private const byte TStatus = 101;
    private const byte THandle = 102;
    private const byte TData = 103;
    private const byte TName = 104;
    private const byte TAttrs = 105;

    // SFTP status codes.
    private const uint StOk = 0;
    private const uint StEof = 1;
    private const uint StNoFile = 2;
    private const uint StPermission = 3;
    private const uint StFailure = 4;
    private const uint StBadMessage = 5;
    private const uint StOpUnsupported = 8;

    private readonly SshConnection _conn;
    private readonly byte[] _rx = new byte[RxCapacity];
    private int _rxLen;
    private bool _dead;

    // Handle table (index = handle value sent to the client).
    private readonly int[] _hKind = new int[MaxHandles];        // 0 free, 1 file, 2 dir
    private readonly bool[] _hRead = new bool[MaxHandles];
    private readonly bool[] _hWrite = new bool[MaxHandles];
    private readonly string[] _hPath = new string[MaxHandles];  // path for kind 1
    private readonly string[][] _dirEntries = new string[MaxHandles][];
    private readonly int[] _dirPos = new int[MaxHandles];

    // Streaming state for oversized WRITE packets: modern clients (OpenSSH
    // >= 9, WinSCP) send up to 256 KiB of file data per WRITE, larger than
    // the receive buffer. Such packets are consumed straight from the
    // channel feed and written through a 32 KiB scratch buffer (heap-safe:
    // stays well below the 85 KB large-object threshold).
    private bool _big;
    private uint _bigId;
    private int _bigSlot;
    private int _bigRemaining;
    private int _bigFilePos;
    private int _bigBufLen;
    private bool _bigErr;
    private readonly byte[] _bigBuf = new byte[32768];

    /// <summary>Create the subsystem handler for one session channel.</summary>
    public SftpServer(SshConnection conn)
    {
        _conn = conn;
    }

    /// <summary>Feed raw channel data; parses and answers whole SFTP packets.</summary>
    public void Feed(byte[] data)
    {
        if (_dead)
            return;

        int i = 0;
        while (i < data.Length)
        {
            // Oversized WRITE in progress: bytes belong to its payload and
            // go straight to disk instead of the receive buffer.
            if (_big)
            {
                int take = data.Length - i;
                if (take > _bigRemaining)
                    take = _bigRemaining;
                BigConsume(data, i, take);
                i += take;
                _bigRemaining -= take;
                if (_bigRemaining == 0)
                {
                    FlushBigBuf();
                    Status(_bigId, _bigErr ? StFailure : StOk, "");
                    _big = false;
                }
                continue;
            }

            int space = _rx.Length - _rxLen;
            int n = data.Length - i;
            if (n > space)
                n = space;
            if (n > 0)
            {
                Array.Copy(data, i, _rx, _rxLen, n);
                _rxLen += n;
                i += n;
            }

            int passes = 0;
            while (passes < 16 && !_conn.OutputCongested && ParseOne())
                passes++;
            if (_dead)
                return;

            if (n == 0 && !_big)
            {
                // Buffer full without a complete packet: unframeable stream.
                _dead = true;
                _conn.Close();
                return;
            }
        }
    }

    /// <summary>
    /// Resume parsing buffered request bytes once output congestion
    /// clears. Feed stops mid-stream when replies pile up; without this
    /// re-poke the remaining pipelined requests sitting in the receive
    /// buffer would never be answered (deadlock: the client sends no new
    /// requests while its pipeline is full, so no further Feed arrives).
    /// </summary>
    public void Resume()
    {
        if (_dead)
            return;
        int passes = 0;
        while (passes < 16 && !_conn.OutputCongested && ParseOne())
            passes++;
    }

    /// <summary>Close all open handles (called when the channel ends).</summary>
    public void Close()
    {
        for (int i = 0; i < MaxHandles; i++)
        {
            // File writes flush per request; there is nothing to close.
            _hKind[i] = 0;
            _hPath[i] = null;
            _dirEntries[i] = null;
        }
        _big = false;
        _bigBufLen = 0;
    }

    // ==================== Packet framing ====================

    private bool ParseOne()
    {
        if (_big)
            return false;
        if (_rxLen < 4)
            return false;
        uint len = ((uint)_rx[0] << 24) | ((uint)_rx[1] << 16) | ((uint)_rx[2] << 8) | _rx[3];
        if (len < 1)
        {
            _dead = true;
            _conn.Close();
            return false;
        }
        if (len > (uint)(RxCapacity - 4))
            return BeginBig(len);
        int total = (int)len + 4;
        if (_rxLen < total)
            return false;

        var r = new SshReader(_rx, 4, (int)len);
        byte type = r.ReadByte();
        HandleMessage(type, r);

        for (int i = total; i < _rxLen; i++)
            _rx[i - total] = _rx[i];
        _rxLen -= total;
        return !_dead;
    }

    // ==================== Oversized (streamed) WRITE packets ====================

    /// <summary>
    /// Packet larger than the receive buffer: only WRITE is ever that big
    /// (up to 256 KiB of file data per request). Parse the fixed header
    /// (id, handle, offset, data length), then stream the payload straight
    /// to disk in scratch-buffer chunks; the reply is sent when the last
    /// payload byte has arrived.
    /// </summary>
    private bool BeginBig(uint len)
    {
        if (_rxLen < 5)
            return false;
        if (_rx[4] != TWrite)
        {
            // Nothing else may legitimately exceed the buffer.
            _dead = true;
            _conn.Close();
            return false;
        }
        const int Prefix = 29;   // len + type + id + hlen + handle + offset + datalen
        if (_rxLen < Prefix)
            return false;

        var r = new SshReader(_rx, 5, Prefix - 5);
        uint id = r.ReadU32();
        uint hlen = r.ReadU32();
        uint hslot = r.ReadU32();
        ulong offset = r.ReadU64();
        uint datalen = r.ReadU32();

        if (hlen != 4 || datalen != len - 25 || datalen > 0x7FFFFFFF)
        {
            // Framing is inconsistent; we cannot stay in sync.
            _dead = true;
            _conn.Close();
            return false;
        }

        int slot = (int)hslot;
        _bigErr = slot < 0 || slot >= MaxHandles || _hKind[slot] != 1
                  || _hPath[slot] == null || !_hWrite[slot]
                  || offset > 0x7FFFFFFF || offset + datalen > 0x80000000UL;

        _big = true;
        _bigId = id;
        _bigSlot = slot;
        _bigFilePos = (int)offset;
        _bigBufLen = 0;

        // Consume whatever payload bytes already arrived; the rest is
        // streamed by Feed as the channel delivers it.
        int avail = _rxLen - Prefix;
        int consumed = 0;
        if (avail > 0)
        {
            consumed = avail;
            BigConsume(_rx, Prefix, avail);
        }
        _rxLen = 0;
        _bigRemaining = (int)datalen - consumed;

        if (_bigRemaining == 0)
        {
            FlushBigBuf();
            Status(_bigId, _bigErr ? StFailure : StOk, "");
            _big = false;
        }
        return true;
    }

    /// <summary>Buffers payload bytes, flushing full 32 KiB scratch chunks to disk.</summary>
    private void BigConsume(byte[] src, int start, int count)
    {
        while (count > 0)
        {
            int space = _bigBuf.Length - _bigBufLen;
            int n = count > space ? space : count;
            Array.Copy(src, start, _bigBuf, _bigBufLen, n);
            _bigBufLen += n;
            start += n;
            count -= n;
            if (_bigBufLen == _bigBuf.Length)
                FlushBigBuf();
        }
    }

    /// <summary>Writes the pending scratch bytes at the running file position.</summary>
    private void FlushBigBuf()
    {
        if (_bigBufLen == 0)
            return;
        if (!_bigErr)
        {
            int w = BootFiles.WriteRange(_hPath[_bigSlot], _bigFilePos, _bigBuf, _bigBufLen);
            if (w != _bigBufLen)
                _bigErr = true;
        }
        _bigFilePos += _bigBufLen;
        _bigBufLen = 0;
    }

    // ==================== Message dispatch ====================

    private void HandleMessage(byte type, SshReader r)
    {
        if (type == TInit)
        {
            uint id = r.ReadU32();
            r.ReadU32();   // client version
            // VERSION has NO id field (draft-ietf-secsh-filexfer-02 3.1):
            // type + uint32 version + extension pairs (none here).
            _ = id;
            var w = new SshWriter(16);
            w.WriteByte(TVersion);
            w.WriteU32(3);
            Send(w);
            return;
        }

        if (type == TOpen) { OnOpen(r); return; }
        if (type == TClose) { OnClose(r); return; }
        if (type == TRead) { OnRead(r); return; }
        if (type == TWrite) { OnWrite(r); return; }
        if (type == TLstat || type == TStat) { OnStat(r); return; }
        if (type == TFstat) { OnFstat(r); return; }
        if (type == TSetstat || type == TFsetstat) { OnSetstat(r); return; }
        if (type == TOpendir) { OnOpendir(r); return; }
        if (type == TReaddir) { OnReaddir(r); return; }
        if (type == TRemove) { OnRemove(r); return; }
        if (type == TMkdir) { OnMkdir(r); return; }
        if (type == TRmdir) { OnRmdir(r); return; }
        if (type == TRealpath) { OnRealpath(r); return; }
        if (type == TRename) { OnRename(r); return; }
        if (type == TReadlink || type == TSymlink || type == TExtended)
        {
            uint id = r.ReadU32();
            Status(id, StOpUnsupported, "not supported");
            return;
        }

        // Unknown message: cannot know the id; answer nothing.
    }

    // ==================== Operations ====================

    private void OnOpen(SshReader r)
    {
        uint id = r.ReadU32();
        string path = r.ReadAscii();
        uint pflags = r.ReadU32();
        SkipAttrs(r);

        if (path == null || path.Length == 0)
        {
            Status(id, StNoFile, "no path");
            return;
        }
        string full = Canon(path);

        bool wantRead = (pflags & 1) != 0;
        bool wantWrite = (pflags & 2) != 0;
        bool append = (pflags & 4) != 0;
        bool creat = (pflags & 8) != 0;
        bool trunc = (pflags & 16) != 0;
        bool excl = (pflags & 32) != 0;

        if (!wantRead && !wantWrite && !append)
        {
            Status(id, StBadMessage, "no access flag");
            return;
        }
        if (!wantWrite && (creat || trunc || append))
        {
            Status(id, StPermission, "write flags without write access");
            return;
        }
        if (Directory.Exists(full))
        {
            Status(id, StFailure, "is a directory");
            return;
        }

        bool exists = File.Exists(full);
        if (excl && creat && exists)
        {
            Status(id, StFailure, "file exists");
            return;
        }
        if (!creat && !exists)
        {
            Status(id, StNoFile, "no such file");
            return;
        }
        if (creat && !exists && !ParentExists(full))
        {
            Status(id, StNoFile, "no such directory");
            return;
        }

        int slot = FindFreeHandle();
        if (slot < 0)
        {
            Status(id, StFailure, "no free handles");
            return;
        }

        // Establish the file on disk: create when missing, truncate when
        // asked. The empty whole-file write maps to driver mode Create
        // (= create or truncate) and allocates no data, so it stays
        // heap-safe (unlike the chunked path, which never truncates).
        if ((creat && !exists) || (trunc && exists))
            File.WriteAllBytes(full, new byte[0]);

        _hPath[slot] = full;
        _hKind[slot] = 1;
        _hRead[slot] = wantRead;
        _hWrite[slot] = wantWrite || append;

        var w = Body(THandle, id);
        w.WriteU32(4);                 // handle string: 4 bytes
        w.WriteU32((uint)slot);
        Send(w);
    }

    private void OnClose(SshReader r)
    {
        uint id = r.ReadU32();
        int slot = ReadHandle(r);
        if (slot < 0)
        {
            Status(id, StFailure, "bad handle");
            return;
        }
        // File writes flush per request; nothing to close for files.
        _hPath[slot] = null;
        _dirEntries[slot] = null;
        _hKind[slot] = 0;
        Status(id, StOk, "");
    }

    private void OnRead(SshReader r)
    {
        uint id = r.ReadU32();
        int slot = ReadHandle(r);
        ulong offset = r.ReadU64();
        uint want = r.ReadU32();
        if (slot < 0 || _hKind[slot] != 1 || _hPath[slot] == null || !_hRead[slot])
        {
            Status(id, StFailure, "bad handle");
            return;
        }
        if (offset > 0x7FFFFFFF)
        {
            // Chunked offsets are int; past 2 GiB there is no data.
            Status(id, StEof, "");
            return;
        }

        int n = want > (uint)ReadChunk ? ReadChunk : (int)want;
        if (n <= 0)
            n = ReadChunk;
        var buf = new byte[n];
        int got = BootFiles.ReadRange(_hPath[slot], (int)offset, buf, n);
        if (got < 0)
        {
            Status(id, StFailure, "read failed");
            return;
        }
        if (got == 0)
        {
            Status(id, StEof, "");
            return;
        }

        var w = Body(TData, id);
        w.WriteU32((uint)got);
        w.WriteRaw(buf, 0, got);
        Send(w);
    }

    private void OnWrite(SshReader r)
    {
        uint id = r.ReadU32();
        int slot = ReadHandle(r);
        ulong offset = r.ReadU64();
        byte[] data = r.ReadString();
        if (slot < 0 || _hKind[slot] != 1 || _hPath[slot] == null || data == null || !_hWrite[slot])
        {
            Status(id, StFailure, "bad handle");
            return;
        }
        if (offset > 0x7FFFFFFF || offset + (ulong)data.Length > 0x80000000UL)
        {
            Status(id, StFailure, "position beyond 2 GiB");
            return;
        }

        int written = BootFiles.WriteRange(_hPath[slot], (int)offset, data, data.Length);
        if (written != data.Length)
        {
            Status(id, StFailure, "write failed");
            return;
        }
        Status(id, StOk, "");
    }

    private void OnStat(SshReader r)
    {
        uint id = r.ReadU32();
        string path = r.ReadAscii();
        if (path == null || path.Length == 0)
        {
            Status(id, StNoFile, "no path");
            return;
        }
        string full = Canon(path);

        if (Directory.Exists(full))
        {
            var w = Body(TAttrs, id);
            WriteAttrs(w, 0, true);
            Send(w);
            return;
        }
        if (File.Exists(full))
        {
            var w = Body(TAttrs, id);
            WriteAttrs(w, (ulong)FileSize(full), false);
            Send(w);
            return;
        }
        Status(id, StNoFile, "no such file");
    }

    private void OnFstat(SshReader r)
    {
        uint id = r.ReadU32();
        int slot = ReadHandle(r);
        if (slot < 0)
        {
            Status(id, StFailure, "bad handle");
            return;
        }
        var w = Body(TAttrs, id);
        if (_hKind[slot] == 1 && _hPath[slot] != null)
            WriteAttrs(w, File.Exists(_hPath[slot]) ? (ulong)FileSize(_hPath[slot]) : 0, false);
        else if (_hKind[slot] == 2)
            WriteAttrs(w, 0, true);
        else
        {
            Status(id, StFailure, "bad handle");
            return;
        }
        Send(w);
    }

    private void OnSetstat(SshReader r)
    {
        uint id = r.ReadU32();
        r.ReadString();    // SETSTAT carries a path, FSETSTAT a handle
        SkipAttrs(r);
        // Permissions/ownership are not enforceable on the FAT boot volume;
        // accept the request so clients (scp -p, sftp chmod) keep working.
        Status(id, StOk, "");
    }

    private void OnOpendir(SshReader r)
    {
        uint id = r.ReadU32();
        string path = r.ReadAscii();
        if (path == null || path.Length == 0 || !Directory.Exists(Canon(path)))
        {
            Status(id, StNoFile, "no such directory");
            return;
        }
        int slot = FindFreeHandle();
        if (slot < 0)
        {
            Status(id, StFailure, "no free handles");
            return;
        }

        string full = (path == null || path.Length == 0) ? HomeDir() : Canon(path);
        string[] names = Directory.GetFileSystemEntries(full);
        var entries = new string[names.Length + 2];
        entries[0] = ".";
        entries[1] = "..";
        for (int i = 0; i < names.Length; i++)
            entries[i + 2] = names[i];
        _dirEntries[slot] = entries;
        _dirPos[slot] = 0;
        _hKind[slot] = 2;

        var w = Body(THandle, id);
        w.WriteU32(4);                 // handle string: 4 bytes
        w.WriteU32((uint)slot);
        Send(w);
    }

    private void OnReaddir(SshReader r)
    {
        uint id = r.ReadU32();
        int slot = ReadHandle(r);
        if (slot < 0 || _hKind[slot] != 2 || _dirEntries[slot] == null)
        {
            Status(id, StFailure, "bad handle");
            return;
        }
        var entries = _dirEntries[slot];
        int pos = _dirPos[slot];
        if (pos >= entries.Length)
        {
            Status(id, StEof, "");
            return;
        }

        int count = entries.Length - pos;
        if (count > 64)
            count = 64;
        var w = Body(TName, id);
        w.WriteU32((uint)count);
        for (int i = 0; i < count; i++)
        {
            string full = entries[pos + i];
            string name = LastSegment(full);
            w.WriteString(name);
            w.WriteString(name);   // longname: plain name (clients display it)
            if (name == "." || name == "..")
                WriteAttrs(w, 0, true);
            else if (Directory.Exists(full))
                WriteAttrs(w, 0, true);
            else
                WriteAttrs(w, (ulong)FileSize(full), false);
        }
        _dirPos[slot] = pos + count;
        Send(w);
    }

    private void OnRemove(SshReader r)
    {
        uint id = r.ReadU32();
        string path = r.ReadAscii();
        string full = path == null ? null : Canon(path);
        if (full == null || !File.Exists(full))
        {
            Status(id, StNoFile, "no such file");
            return;
        }
        File.Delete(full);
        Status(id, StOk, "");
    }

    private void OnMkdir(SshReader r)
    {
        uint id = r.ReadU32();
        string path = r.ReadAscii();
        SkipAttrs(r);
        string full = path == null ? null : Canon(path);
        if (full == null || full.Length < 2)
        {
            Status(id, StFailure, "bad path");
            return;
        }
        if (Directory.Exists(full) || File.Exists(full))
        {
            Status(id, StFailure, "already exists");
            return;
        }
        if (!ParentExists(full))
        {
            Status(id, StNoFile, "no such directory");
            return;
        }
        Directory.CreateDirectory(full);
        Status(id, StOk, "");
    }

    private void OnRmdir(SshReader r)
    {
        uint id = r.ReadU32();
        string path = r.ReadAscii();
        string full = path == null ? null : Canon(path);
        if (full == null || full.Length < 2 || !Directory.Exists(full))
        {
            Status(id, StNoFile, "no such directory");
            return;
        }
        string[] entries = Directory.GetFileSystemEntries(full);
        if (entries.Length > 0)
        {
            Status(id, StFailure, "directory not empty");
            return;
        }
        Directory.Delete(full);
        Status(id, StOk, "");
    }

    private void OnRealpath(SshReader r)
    {
        uint id = r.ReadU32();
        string path = r.ReadAscii();
        string full = (path == null || path.Length == 0) ? HomeDir() : Canon(path);

        var w = Body(TName, id);
        w.WriteU32(1);
        w.WriteString(full);
        w.WriteString(full);
        w.WriteU32(0);   // empty attrs
        Send(w);
    }

    private void OnRename(SshReader r)
    {
        uint id = r.ReadU32();
        string from = r.ReadAscii();
        string to = r.ReadAscii();
        string src = from == null ? null : Canon(from);
        string dst = to == null ? null : Canon(to);
        if (src == null || dst == null)
        {
            Status(id, StBadMessage, "bad path");
            return;
        }
        if (Directory.Exists(src))
        {
            Status(id, StOpUnsupported, "directory rename not supported");
            return;
        }
        if (!File.Exists(src))
        {
            Status(id, StNoFile, "no such file");
            return;
        }
        if (!ParentExists(dst))
        {
            Status(id, StNoFile, "no such directory");
            return;
        }
        if (File.Exists(dst))
            File.Delete(dst);
        File.Move(src, dst);
        Status(id, StOk, "");
    }

    // ==================== Paths ====================

    /// <summary>Canonicalize a client path: "." and relatives resolve
    /// against the user's home directory, ".." and "." are folded.</summary>
    private string Canon(string path)
    {
        if (path == null || path.Length == 0 || path == ".")
            return HomeDir();

        var stack = new string[MaxPathDepth];
        int depth = 0;

        int i = 0;
        if (path[0] != '/')
        {
            // Seed with the home directory segments.
            string home = HomeDir();
            i = 0;
            while (i < home.Length)
            {
                int j = i;
                while (j < home.Length && home[j] != '/')
                    j++;
                if (j > i && depth < MaxPathDepth)
                    stack[depth++] = home.Substring(i, j - i);
                i = j + 1;
            }
            i = 0;
        }
        else
        {
            i = 1;
        }

        while (i < path.Length)
        {
            int j = i;
            while (j < path.Length && path[j] != '/')
                j++;
            int len = j - i;
            if (len > 0)
            {
                string seg = path.Substring(i, len);
                if (seg == ".")
                {
                    // no-op
                }
                else if (seg == "..")
                {
                    if (depth > 0)
                        depth--;
                }
                else if (depth < MaxPathDepth)
                {
                    stack[depth++] = seg;
                }
            }
            i = j + 1;
        }

        if (depth == 0)
            return "/";
        string result = "";
        for (int k = 0; k < depth; k++)
            result = result + "/" + stack[k];
        return result;
    }

    private string HomeDir()
    {
        string user = _conn.UserName;
        if (user == null || user.Length == 0)
            user = "user";
        string home = "/home/" + user;
        if (!Directory.Exists(home))
            home = "/";
        return home;
    }

    private static bool ParentExists(string full)
    {
        int slash = LastSlash(full);
        if (slash <= 0)
            return true;   // "/x" - root always exists
        string parent = full.Substring(0, slash);
        return Directory.Exists(parent);
    }

    /// <summary>File size via FileInfo (callers guard File.Exists first).</summary>
    private static long FileSize(string path)
    {
        var info = new FileInfo(path);
        return info.Length;
    }

    private static int LastSlash(string s)
    {
        for (int i = s.Length - 1; i >= 0; i--)
        {
            if (s[i] == '/')
                return i;
        }
        return -1;
    }

    private static string LastSegment(string full)
    {
        int slash = LastSlash(full);
        return slash < 0 ? full : full.Substring(slash + 1, full.Length - slash - 1);
    }

    // ==================== Handles / attributes / output ====================

    private int FindFreeHandle()
    {
        for (int i = 0; i < MaxHandles; i++)
        {
            if (_hKind[i] == 0)
                return i;
        }
        return -1;
    }

    private int ReadHandle(SshReader r)
    {
        uint len = r.ReadU32();
        if (len != 4 || r.Remaining < 4)
            return -1;
        uint v = r.ReadU32();
        if (v >= (uint)MaxHandles || _hKind[v] == 0)
            return -1;
        return (int)v;
    }

    private static void SkipAttrs(SshReader r)
    {
        if (r.Remaining < 4)
            return;
        uint flags = r.ReadU32();
        if ((flags & 1) != 0) r.ReadU64();   // size
        if ((flags & 2) != 0) { r.ReadU32(); r.ReadU32(); }   // uid/gid
        if ((flags & 4) != 0) r.ReadU32();   // permissions
        if ((flags & 8) != 0) { r.ReadU32(); r.ReadU32(); }   // atime/mtime
    }

    private static void WriteAttrs(SshWriter w, ulong size, bool isDir)
    {
        w.WriteU32(5);   // flags: SIZE | PERMS
        w.WriteU64(size);
        w.WriteU32(isDir ? 0x41EDu : 0x81A4u);   // 0755 dir / 0644 file
    }

    private static SshWriter Body(byte type, uint id)
    {
        var w = new SshWriter(64);
        w.WriteByte(type);
        w.WriteU32(id);
        return w;
    }

    private void Send(SshWriter body)
    {
        var pkt = new SshWriter(body.Length + 4);
        pkt.WriteU32((uint)body.Length);
        pkt.WriteRaw(body.Data, 0, body.Length);
        _conn.SendChannelBytes(pkt.ToArray());
    }

    private void Status(uint id, uint code, string message)
    {
        var w = Body(TStatus, id);
        w.WriteU32(code);
        w.WriteString(message == null ? "" : message);
        w.WriteString("");
        Send(w);
    }
}
