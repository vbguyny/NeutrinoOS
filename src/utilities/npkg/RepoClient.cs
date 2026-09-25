// NeutrinoOS Phase 8 - npkg: repository client (fetch + verify)
//
// Repositories are plain directories served over HTTP or read straight
// from the local filesystem:
//
//   repository.json       RepositoryIndex (JSON)
//   repository.json.sig   Ed25519 signature over the raw JSON bytes
//   repo.pub              repository signing public key (32 bytes or hex)
//   <filename>            package files (.npkg)
//
// URLs may be "http://host[:port]/path", "file:///path" or an absolute
// "/path". HTTP uses HTTP/1.1 GET over the DDK TCP sockets (the same
// path wget takes), handling Content-Length and chunked bodies; only
// http:// is supported (no TLS in Phase 8). Nothing is printed on
// success; failures raise an Exception with a printable message.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NeutrinoOS.Packaging;
using NeutrinoOS.Utils;
using ProtonOS.DDK.Crypto;
using ProtonOS.DDK.Kernel;
using ProtonOS.DDK.Network;
using ProtonOS.DDK.Network.Sockets;
using ProtonOS.DDK.Network.Stack;

namespace NeutrinoOS.Utility.Npkg;

/// <summary>Fetches repository metadata and package files, verifying signatures and hashes.</summary>
public static unsafe class RepoClient
{
    /// <summary>
    /// Fetches a URL (http://, file:///... or /absolute/path) and returns
    /// the raw bytes. Throws on transport errors and non-200 responses.
    /// </summary>
    public static byte[] FetchUrl(string url)
    {
        if (url == null || url.Length == 0)
            throw new Exception("empty URL");

        if (Str.Starts(url, "file://"))
            return ReadLocal(url.Substring(7), url);
        if (url[0] == '/')
            return ReadLocal(url, url);
        if (Str.Starts(url, "http://"))
            return HttpGet(url);

        throw new Exception("unsupported URL: " + url + " (expected http://, file:/// or /path)");
    }

    /// <summary>
    /// Fetches and verifies a repository index. Non-throwing variant:
    /// returns false and a printable error instead of raising (exceptions
    /// raised inside this method do not reliably unwind to JIT callers on
    /// the Tier-0 JIT, so the caller-facing API avoids throws).
    /// </summary>
    public static bool TryFetchIndex(RepoConfig repo, out RepositoryIndex index, out string error)
    {
        index = null;
        error = null;
        try
        {
            string indexUrl = Str.JoinPath(repo.Url, "repository.json");
            byte[] indexBytes = FetchUrl(indexUrl);

            byte[] signature = TryFetch(indexUrl + ".sig");
            if (signature != null && signature.Length > 0)
            {
                byte[] publicKeyFile = TryFetch(Str.JoinPath(repo.Url, "repo.pub"));
                if (publicKeyFile == null || publicKeyFile.Length == 0)
                {
                    error = "repository.json.sig present but repo.pub is missing";
                    return false;
                }

                // repo.pub may be the raw 32-byte key or 64 ASCII hex
                // characters (optionally followed by a newline).
                byte[] publicKey = NormalizePublicKeyFile(publicKeyFile);
                if (publicKey == null)
                {
                    error = "repo.pub is neither a 32-byte key nor 64 hex characters";
                    return false;
                }

                bool valid;
                try { valid = Ed25519.Verify(publicKey, indexBytes, signature); }
                catch (Exception) { valid = false; }
                if (!valid)
                {
                    error = "index signature verification failed";
                    return false;
                }

                string fingerprint = NpkgPackage.Fingerprint(publicKey);
                if (repo.Fingerprint != null && repo.Fingerprint.Length > 0
                    && !Str.EqualIgnoreCase(fingerprint, repo.Fingerprint))
                {
                    error = "key fingerprint mismatch (got " + fingerprint
                        + ", pinned " + repo.Fingerprint + ")";
                    return false;
                }
            }

            JsonObject obj = Jsn.ParseObject(Encoding.UTF8.GetString(indexBytes));
            if (obj == null)
            {
                error = "invalid repository.json";
                return false;
            }
            try
            {
                index = RepositoryIndex.FromJson(obj);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Fetches and verifies a repository index (throwing wrapper around
    /// <see cref="TryFetchIndex"/> for callers that prefer exceptions).
    /// </summary>
    public static RepositoryIndex FetchIndex(RepoConfig repo)
    {
        RepositoryIndex index;
        string error;
        if (!TryFetchIndex(repo, out index, out error))
            throw new Exception("repository " + repo.Name + ": " + error);
        return index;
    }

    /// <summary>
    /// Normalizes a repository public key file: 32 raw bytes pass through;
    /// longer content is treated as ASCII hex (whitespace tolerated) and
    /// decoded. Returns null when neither form is valid. Shared by the
    /// fetch path and 'npkg repo add' so fingerprints always match.
    /// </summary>
    public static byte[] NormalizePublicKeyFile(byte[] fileBytes)
    {
        if (fileBytes == null || fileBytes.Length == 0)
            return null;
        if (fileBytes.Length == Ed25519.PublicKeySize)
            return fileBytes;

        var sb = new StringBuilder(fileBytes.Length);
        for (int i = 0; i < fileBytes.Length; i++)
        {
            byte b = fileBytes[i];
            if (b >= 32 && b < 127)
                sb.Append((char)b);
        }
        string text = Str.TrimAscii(sb.ToString());
        if (text.Length == Ed25519.PublicKeySize * 2 && Str.IsHex(text))
        {
            try { return TextConv.HexDecode(text); }
            catch (Exception) { return null; }
        }
        return null;
    }

    /// <summary>
    /// Downloads a package and checks it against the index's SHA-256.
    /// Throws "checksum mismatch" when the bytes do not match.
    /// </summary>
    public static byte[] FetchPackage(RepoConfig repo, RepoPackageInfo info)
    {
        if (info == null || info.Filename == null || info.Filename.Length == 0)
            throw new Exception("repository entry for " + (info == null ? "?" : info.Name) + " has no filename");

        byte[] bytes = FetchUrl(Str.JoinPath(repo.Url, info.Filename));
        if (info.Sha256 == null || info.Sha256.Length == 0)
            throw new Exception("repository entry for " + info.Name + " has no sha256 to verify against");

        string sha256 = NpkgPackage.Sha256Hex(bytes);
        if (!Str.EqualIgnoreCase(sha256, info.Sha256))
            throw new Exception("checksum mismatch for " + info.Name + " " + info.Version.ToString()
                + " (expected " + info.Sha256 + ", got " + sha256 + ")");
        return bytes;
    }

    private static byte[] ReadLocal(string path, string url)
    {
        if (!File.Exists(path))
            throw new Exception("cannot open " + url + ": no such file");
        return File.ReadAllBytes(path);
    }

    private static byte[] TryFetch(string url)
    {
        try { return FetchUrl(url); }
        catch (Exception) { return null; }
    }

    private static byte[] HttpGet(string url)
    {
        string host;
        int port;
        string path;
        string urlError;
        if (!Http.ParseUrl(url, out host, out port, out path, out urlError))
            throw new Exception(urlError);

        NetworkInterface eth = NetworkManager.GetInterface("eth0");
        if (eth == null || eth.Stack == null)
            throw new Exception("network device not available (start QEMU with a virtio-net NIC)");
        NetworkStack stack = eth.Stack;

        uint ip = Http.ParseIP(host);
        if (ip == 0)
        {
            var resolver = new DnsResolver(stack);
            ip = resolver.Resolve(host, 5000,
                new DnsResolver.TransmitFrameDelegate(NetworkPump.TransmitAdapter),
                new DnsResolver.ReceiveFrameDelegate(NetworkPump.ReceiveAdapter));
            if (ip == 0)
                throw new Exception(host + ": unknown host");
        }

        TcpSocket sock = Http.Connect(ip, port, stack, 5000);
        if (sock == null)
            throw new Exception("connection to " + host + ":" + TextConv.LongToString(port) + " timed out");

        string request = "GET " + path + " HTTP/1.1\r\n"
            + "Host: " + host + "\r\n"
            + "User-Agent: NeutrinoOS-npkg/1.0\r\n"
            + "Accept: */*\r\n"
            + "Connection: close\r\n\r\n";
        byte[] requestBytes = Http.AsciiBytes(request);
        fixed (byte* p = requestBytes)
        {
            int sent = sock.Send(p, requestBytes.Length);
            if (sent != requestBytes.Length)
            {
                sock.Close();
                throw new Exception("failed to send HTTP request to " + host);
            }
        }
        NetworkPump.FlushTx(stack);

        byte[] raw = ReadAll(sock, stack, 60000);
        sock.Close();
        return ParseResponse(url, raw);
    }

    private static byte[] ReadAll(TcpSocket sock, NetworkStack stack, int timeoutMs)
    {
        byte[] buffer = new byte[65536];
        int length = 0;
        byte* chunk = stackalloc byte[1460];
        ulong start = Timer.GetUptimeMilliseconds();
        while (Timer.GetUptimeMilliseconds() - start < (ulong)timeoutMs)
        {
            NetworkPump.Pump(stack, 8);
            int n = sock.Receive(chunk, 1460);
            if (n > 0)
            {
                if (length + n > buffer.Length)
                {
                    int size = buffer.Length;
                    while (size < length + n)
                        size *= 2;
                    byte[] bigger = new byte[size];
                    for (int i = 0; i < length; i++)
                        bigger[i] = buffer[i];
                    buffer = bigger;
                }
                for (int i = 0; i < n; i++)
                    buffer[length + i] = chunk[i];
                length += n;
            }
            else if (!sock.Connected && sock.Available == 0)
            {
                break;
            }
        }

        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
            result[i] = buffer[i];
        return result;
    }

    private static byte[] ParseResponse(string url, byte[] raw)
    {
        int bodyStart = -1;
        for (int i = 0; i + 3 < raw.Length; i++)
        {
            if (raw[i] == 13 && raw[i + 1] == 10 && raw[i + 2] == 13 && raw[i + 3] == 10)
            {
                bodyStart = i + 4;
                break;
            }
        }
        if (bodyStart < 0)
            throw new Exception("malformed HTTP response from " + url);

        int statusEnd = 0;
        while (statusEnd < raw.Length && raw[statusEnd] != 10)
            statusEnd++;
        string statusLine = Str.TrimAscii(Ascii(raw, 0, statusEnd));

        string code = StatusCode(statusLine);
        if (code != "200")
            throw new Exception("HTTP " + code + " for " + url);

        long contentLength = -1;
        bool chunked = false;
        int lineStart = statusEnd + 1;
        while (lineStart < bodyStart)
        {
            int lineEnd = lineStart;
            while (lineEnd < raw.Length && raw[lineEnd] != 10)
                lineEnd++;
            string line = Str.TrimAscii(Ascii(raw, lineStart, lineEnd));
            int colon = line.IndexOf(":");
            if (colon > 0)
            {
                string name = Str.TrimAscii(line.Substring(0, colon)).ToLower();
                string value = Str.TrimAscii(line.Substring(colon + 1));
                if (name == "content-length")
                {
                    int parsed;
                    if (Util.TryParseInt(value, out parsed))
                        contentLength = parsed;
                }
                else if (name == "transfer-encoding")
                {
                    if (Str.Contains(value.ToLower(), "chunked"))
                        chunked = true;
                }
            }
            lineStart = lineEnd + 1;
        }

        byte[] body = Slice(raw, bodyStart, raw.Length);
        if (chunked)
        {
            body = Dechunk(body, url);
        }
        else if (contentLength >= 0)
        {
            if (body.Length < contentLength)
                throw new Exception("truncated HTTP response from " + url);
            if (body.Length > contentLength)
                body = Slice(raw, bodyStart, bodyStart + (int)contentLength);
        }
        return body;
    }

    private static string StatusCode(string statusLine)
    {
        int i = 0;
        while (i < statusLine.Length && statusLine[i] != ' ')
            i++;
        while (i < statusLine.Length && statusLine[i] == ' ')
            i++;
        int start = i;
        while (i < statusLine.Length && statusLine[i] >= '0' && statusLine[i] <= '9')
            i++;
        if (i == start)
            return "000";
        return statusLine.Substring(start, i - start);
    }

    private static byte[] Dechunk(byte[] data, string url)
    {
        var output = new List<byte>();
        int i = 0;
        while (i < data.Length)
        {
            int lineEnd = i;
            while (lineEnd < data.Length && data[lineEnd] != 10)
                lineEnd++;
            string sizeLine = Str.TrimAscii(Ascii(data, i, lineEnd));
            int semicolon = sizeLine.IndexOf(";");
            if (semicolon >= 0)
                sizeLine = sizeLine.Substring(0, semicolon);
            sizeLine = Str.TrimAscii(sizeLine);

            int size = 0;
            bool ok = sizeLine.Length > 0;
            for (int d = 0; d < sizeLine.Length; d++)
            {
                char c = sizeLine[d];
                int value;
                if (c >= '0' && c <= '9') value = c - '0';
                else if (c >= 'a' && c <= 'f') value = c - 'a' + 10;
                else if (c >= 'A' && c <= 'F') value = c - 'A' + 10;
                else { ok = false; break; }
                size = size * 16 + value;
            }
            if (!ok)
                throw new Exception("malformed chunked HTTP response from " + url);

            i = lineEnd + 1;
            if (size == 0)
                break;
            if (i + size > data.Length)
                throw new Exception("truncated chunked HTTP response from " + url);
            for (int k = 0; k < size; k++)
                output.Add(data[i + k]);
            i += size;
            while (i < data.Length && (data[i] == 13 || data[i] == 10))
                i++;
        }
        return output.ToArray();
    }

    private static string Ascii(byte[] data, int start, int end)
    {
        if (end > data.Length)
            end = data.Length;
        var chars = new char[end - start];
        for (int i = start; i < end; i++)
            chars[i - start] = data[i] < 128 ? (char)data[i] : '?';
        return new string(chars);
    }

    private static byte[] Slice(byte[] data, int start, int end)
    {
        if (end > data.Length)
            end = data.Length;
        if (start > end)
            start = end;
        byte[] result = new byte[end - start];
        for (int i = start; i < end; i++)
            result[i - start] = data[i];
        return result;
    }
}
