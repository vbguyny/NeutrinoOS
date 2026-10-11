#!/usr/bin/env python3
"""M3 raw-client checks against the WebHost engine (chunked + 100-continue).

Usage: m3-http-check.py chunked|continue [port]

Connects to 127.0.0.1:<port> (the QEMU hostfwd, default 18080), speaks raw
HTTP/1.1 so the framing is fully deterministic, and asserts the M3 behaviors:
  chunked   -> POST /api/v1/echo with Transfer-Encoding: chunked, two chunks
  continue  -> POST /api/v1/echo with Expect: 100-continue; waits for the
               interim "HTTP/1.1 100 Continue" BEFORE sending the body
Exit code 0 = pass; 1 = fail (reason printed to stdout).
"""

import socket
import sys


def recv_response(sock):
    """Read one response (headers + Content-Length body)."""
    data = b""
    while True:
        head_end = data.find(b"\r\n\r\n")
        if head_end >= 0:
            clen = None
            for line in data[:head_end].split(b"\r\n")[1:]:
                if line.lower().startswith(b"content-length:"):
                    clen = int(line.split(b":", 1)[1].strip())
            if clen is not None and len(data) >= head_end + 4 + clen:
                return data[:head_end + 4 + clen]
        chunk = sock.recv(4096)
        if not chunk:
            return data
        data += chunk


def check_chunked(port):
    s = socket.create_connection(("127.0.0.1", port), timeout=10)
    s.sendall(
        b"POST /api/v1/echo HTTP/1.1\r\n"
        b"Host: m3-check\r\n"
        b"Transfer-Encoding: chunked\r\n"
        b"Connection: close\r\n"
        b"\r\n"
        b"5\r\nhello\r\n"
        b"6\r\n world\r\n"
        b"0\r\n\r\n"
    )
    resp = recv_response(s)
    s.close()
    if b'"content_length":11' not in resp or b'"you_sent":"hello world"' not in resp:
        print("bad echo response: %r" % resp[-220:])
        return 1
    return 0


def check_continue(port):
    s = socket.create_connection(("127.0.0.1", port), timeout=10)
    s.sendall(
        b"POST /api/v1/echo HTTP/1.1\r\n"
        b"Host: m3-check\r\n"
        b"Content-Length: 5\r\n"
        b"Expect: 100-continue\r\n"
        b"Connection: close\r\n"
        b"\r\n"
    )
    s.settimeout(10)
    first = b""
    while b"\r\n\r\n" not in first:
        chunk = s.recv(4096)
        if not chunk:
            print("no interim response: %r" % first)
            return 1
        first += chunk
    if not first.startswith(b"HTTP/1.1 100 Continue"):
        print("unexpected first response: %r" % first[:120])
        return 1
    s.sendall(b"hello")
    data = first.split(b"\r\n\r\n", 1)[1]
    while True:
        chunk = s.recv(4096)
        if not chunk:
            break
        data += chunk
    s.close()
    if b'"you_sent":"hello"' not in data or b'"content_length":5' not in data:
        print("bad final response: %r" % data[-220:])
        return 1
    return 0


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "chunked"
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 18080
    if mode == "chunked":
        return check_chunked(port)
    if mode == "continue":
        return check_continue(port)
    print("unknown mode: %s" % mode)
    return 2


sys.exit(main())
