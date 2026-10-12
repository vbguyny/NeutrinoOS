// NeutrinoOS Web - Kestrel port Milestone M3: the HTTP/1.1 request head
// parser + chunked-body decoder.
//
// The shape follows Kestrel's HttpParser
// (Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.HttpParser): scan
// the raw request bytes without materializing per-header strings, classify
// the request, and hand back only what the host needs (method/target,
// framing fields, flags). Unlike Kestrel's incremental cursor state machine,
// this parser re-scans the bounded per-connection request buffer (4 KiB,
// WebHost.MaxRequestBytes) once per tick - one linear pass, zero
// allocations for header scanning, and trivially consistent with the
// cooperative pump (no resumable state to keep in sync).
//
// The scan uses byte[] + index arithmetic rather than Span parameters: the
// Tier-0 JIT passes span arguments as 16-byte structs, and this path runs on
// every request - plain indexing is the cheap, proven form here.
//
// Tier-0 JIT notes applied (see the repo memory notes "jit-hazards"):
//   - no `out bool` parameters (stale-slot miscompile observed in M2):
//     flags travel as an int bitmask; positions/lengths as out ints.
//   - small straight-line helpers; no large string[] diagnostics.

namespace NeutrinoOS.DDK.Web;

/// <summary>Byte-level HTTP/1.1 head parser + chunked decoder for WebHost.</summary>
internal static class HttpParser
{
    // ParseRequestHead results.
    public const int ResultIncomplete = 0;   // head not fully buffered yet
    public const int ResultComplete = 1;     // head parsed; headEnd is set
    public const int ResultBad = 2;          // malformed request line/headers

    // TryDecodeChunked results (negative: not a consumed length).
    public const int DecodeIncomplete = -1;  // more bytes needed
    public const int DecodeMalformed = -2;   // bad chunk framing
    public const int DecodeTooLarge = -3;    // decoded body exceeds the cap

    // Head flag bits.
    public const int FlagHttp11 = 1;
    public const int FlagConnClose = 2;
    public const int FlagExpectContinue = 4;
    public const int FlagChunked = 8;
    public const int FlagExpectUnsupported = 16;   // Expect present, not 100-continue

    /// <summary>Trailer lines accepted after the last chunk (ignored).</summary>
    private const int MaxTrailerLines = 16;

    /// <summary>
    /// Parses "METHOD SP TARGET SP HTTP/x.y CRLF *(header CRLF) CRLF" from
    /// buf[0, len). On ResultComplete: method/target are materialized,
    /// headEnd = index of the first body byte, contentLength is taken from
    /// Content-Length (0 when absent/undecodable).
    /// </summary>
    public static int ParseRequestHead(byte[] buf, int len, out string method,
        out string target, out int contentLength, out int headEnd, out int flags)
    {
        method = null;
        target = null;
        contentLength = 0;
        headEnd = 0;
        flags = 0;

        int headLen = FindHeadEnd(buf, len);   // CR index of the final CRLFCRLF
        if (headLen < 0)
            return ResultIncomplete;

        int lineEnd = FindCrlf(buf, 0, headLen);
        if (lineEnd < 0)
            lineEnd = headLen;   // no headers: the request line is the head

        int sp1 = FindByte(buf, 0, lineEnd, 32);
        int sp2 = sp1 < 0 ? -1 : FindByte(buf, sp1 + 1, lineEnd, 32);
        if (sp1 <= 0 || sp2 <= sp1 + 1)
            return ResultBad;
        method = CharsOf(buf, 0, sp1);
        target = CharsOf(buf, sp1 + 1, sp2 - sp1 - 1);
        if (IsHttp11(buf, sp2 + 1, lineEnd))
            flags |= FlagHttp11;

        int pos = lineEnd + 2;
        while (pos < headLen)
        {
            int end = FindCrlf(buf, pos, headLen);
            if (end < 0)
                end = headLen;   // last header line: its CRLF is past headLen
            if (end == pos)
                break;           // defensive: blank line mid-head
            int colon = FindByte(buf, pos, end, 58);
            if (colon > pos)
            {
                int nameEnd = TrimEndWs(buf, pos, colon);
                int valStart = SkipWs(buf, colon + 1, end);
                int valEnd = TrimEndWs(buf, valStart, end);
                if (NameIs(buf, pos, nameEnd, "content-length"))
                    contentLength = ParseDec(buf, valStart, valEnd) & 0xFFFFF;
                else if (NameIs(buf, pos, nameEnd, "connection"))
                {
                    if (TokenIn(buf, valStart, valEnd, "close"))
                        flags |= FlagConnClose;
                }
                else if (NameIs(buf, pos, nameEnd, "transfer-encoding"))
                {
                    if (TokenIn(buf, valStart, valEnd, "chunked"))
                        flags |= FlagChunked;
                }
                else if (NameIs(buf, pos, nameEnd, "expect"))
                {
                    if (TokenIn(buf, valStart, valEnd, "100-continue"))
                        flags |= FlagExpectContinue;
                    else
                        flags |= FlagExpectUnsupported;
                }
            }
            pos = end + 2;
        }

        headEnd = headLen + 4;
        return ResultComplete;
    }

    /// <summary>
    /// Decodes a chunked body from buf[start, start+len) into body (Latin-1
    /// chars). Returns the raw byte count consumed once the terminating
    /// 0-chunk has been seen, DecodeIncomplete when more data is needed, or
    /// DecodeMalformed / DecodeTooLarge on bad framing / overflow.
    /// </summary>
    public static int TryDecodeChunked(byte[] buf, int start, int len, char[] body,
        out int bodyLen)
    {
        bodyLen = 0;
        int pos = start;
        int end = start + len;
        while (pos < end)
        {
            int lineEnd = FindCrlf(buf, pos, end);
            if (lineEnd < 0)
                return DecodeIncomplete;          // chunk-size line not complete
            int size = ParseHex(buf, pos, lineEnd);
            if (size < 0)
                return DecodeMalformed;
            pos = lineEnd + 2;
            if (size == 0)
            {
                // Last chunk: any number of trailer lines (bounded), then the
                // blank line. Trailer content is ignored.
                int trailers = 0;
                int t = FindCrlf(buf, pos, end);
                while (t > pos)
                {
                    if (++trailers > MaxTrailerLines)
                        return DecodeMalformed;
                    pos = t + 2;
                    t = FindCrlf(buf, pos, end);
                }
                if (t < 0)
                    return DecodeIncomplete;
                return t + 2 - start;             // t == pos: the blank line
            }
            if (size > body.Length - bodyLen)
                return DecodeTooLarge;
            if (pos + size + 2 > end)
                return DecodeIncomplete;          // chunk + its CRLF not all here
            for (int i = 0; i < size; i++)
                body[bodyLen + i] = (char)buf[pos + i];
            bodyLen += size;
            pos += size;
            if (buf[pos] != 13 || buf[pos + 1] != 10)
                return DecodeMalformed;           // chunk not CRLF-terminated
            pos += 2;
        }
        return DecodeIncomplete;
    }

    /// <summary>Materializes bytes [off, off+len) as a Latin-1 string.</summary>
    public static string CharsOf(byte[] buf, int off, int len)
    {
        var chars = new char[len];
        for (int i = 0; i < len; i++)
            chars[i] = (char)buf[off + i];
        return new string(chars);
    }

    /// <summary>Materializes the first len chars of a scratch buffer.</summary>
    public static string StrOf(char[] chars, int len)
    {
        var copy = new char[len];
        for (int i = 0; i < len; i++)
            copy[i] = chars[i];
        return new string(copy);
    }

    // ====================================================================
    // Byte scanning helpers (tiny + straight-line on purpose)
    // ====================================================================

    /// <summary>Index of CRLFCRLF in buf[0, len), or -1.</summary>
    private static int FindHeadEnd(byte[] buf, int len)
    {
        for (int i = 0; i + 3 < len; i++)
        {
            if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10)
                return i;
        }
        return -1;
    }

    /// <summary>Index of CRLF within buf[start, limit), or -1.</summary>
    private static int FindCrlf(byte[] buf, int start, int limit)
    {
        for (int i = start; i + 1 < limit; i++)
        {
            if (buf[i] == 13 && buf[i + 1] == 10)
                return i;
        }
        return -1;
    }

    private static int FindByte(byte[] buf, int start, int limit, int value)
    {
        for (int i = start; i < limit; i++)
        {
            if (buf[i] == value)
                return i;
        }
        return -1;
    }

    private static int TrimEndWs(byte[] buf, int start, int end)
    {
        while (end > start && (buf[end - 1] == 32 || buf[end - 1] == 9))
            end--;
        return end;
    }

    private static int SkipWs(byte[] buf, int start, int end)
    {
        while (start < end && (buf[start] == 32 || buf[start] == 9))
            start++;
        return start;
    }

    /// <summary>Exact "HTTP/1.1" match for bytes [start, end).</summary>
    private static bool IsHttp11(byte[] buf, int start, int end)
    {
        if (end - start != 8)
            return false;
        return buf[start] == 72 && buf[start + 1] == 84 && buf[start + 2] == 84 &&
               buf[start + 3] == 80 && buf[start + 4] == 47 && buf[start + 5] == 49 &&
               buf[start + 6] == 46 && buf[start + 7] == 49;
    }

    private static int Lower(int c)
    {
        return (c >= 65 && c <= 90) ? c + 32 : c;
    }

    /// <summary>Case-insensitive ASCII name equality for bytes [start, end).</summary>
    private static bool NameIs(byte[] buf, int start, int end, string name)
    {
        if (end - start != name.Length)
            return false;
        for (int i = 0; i < name.Length; i++)
        {
            if (Lower(buf[start + i]) != name[i])
                return false;
        }
        return true;
    }

    /// <summary>Case-insensitive ASCII token search within [start, end).</summary>
    private static bool TokenIn(byte[] buf, int start, int end, string token)
    {
        int n = token.Length;
        if (n == 0 || end - start < n)
            return false;
        for (int i = start; i + n <= end; i++)
        {
            bool ok = true;
            for (int j = 0; j < n; j++)
            {
                if (Lower(buf[i + j]) != token[j])
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
                return true;
        }
        return false;
    }

    /// <summary>Decimal digits of [start, end); stops at the first non-digit.</summary>
    private static int ParseDec(byte[] buf, int start, int end)
    {
        int v = 0;
        for (int i = start; i < end; i++)
        {
            int d = buf[i] - 48;
            if (d < 0 || d > 9)
                break;
            if (v > 0xFFFFF)
                return 0xFFFFF;
            v = v * 10 + d;
        }
        return v;
    }

    /// <summary>Hex digits of [start, end); -1 when empty or non-hex.</summary>
    private static int ParseHex(byte[] buf, int start, int end)
    {
        if (end <= start)
            return -1;
        int v = 0;
        for (int i = start; i < end; i++)
        {
            int c = buf[i];
            int d;
            if (c >= 48 && c <= 57)
                d = c - 48;
            else if (c >= 97 && c <= 102)
                d = c - 87;
            else if (c >= 65 && c <= 70)
                d = c - 55;
            else
                return -1;
            v = (v << 4) | d;
            if (v > 0x100000)
                return -1;
        }
        return v;
    }
}
