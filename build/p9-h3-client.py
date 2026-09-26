#!/usr/bin/env python3
"""HTTP/3 GET client for the NeutrinoOS QUIC server (aioquic)."""
import asyncio
import logging
import ssl
import sys
import traceback

logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")

from aioquic.asyncio.client import connect
from aioquic.asyncio.protocol import QuicConnectionProtocol
from aioquic.h3.connection import H3_ALPN, H3Connection
from aioquic.quic.configuration import QuicConfiguration
from aioquic.quic.events import HandshakeCompleted, ConnectionTerminated

HOST = sys.argv[1] if len(sys.argv) > 1 else "10.0.2.15"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 443
PATH = sys.argv[3] if len(sys.argv) > 3 else "/health"


class H3Client(QuicConnectionProtocol):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.http = H3Connection(self._quic)
        self.done = asyncio.Event()
        self.status = None
        self.body = b""
        self.stream_id = None

    def quic_event_received(self, event):
        if isinstance(event, HandshakeCompleted):
            print("handshake complete, alpn=%s" % event.alpn_protocol)
            self.stream_id = self._quic.get_next_available_stream_id()
            self.http.send_headers(
                stream_id=self.stream_id,
                headers=[
                    (b":method", b"GET"),
                    (b":scheme", b"https"),
                    (b":authority", HOST.encode()),
                    (b":path", PATH.encode()),
                    (b"user-agent", b"neutrino-h3-test"),
                ],
                end_stream=True,
            )
            self.transmit()
        try:
            for he in self.http.handle_event(event):
                if he.stream_id != self.stream_id:
                    continue
                if getattr(he, "headers", None):
                    for k, v in he.headers:
                        if k == b":status":
                            self.status = v
                if getattr(he, "data", None):
                    self.body += he.data
                if getattr(he, "stream_ended", False):
                    self.done.set()
        except Exception:
            traceback.print_exc()
        if isinstance(event, ConnectionTerminated):
            print("connection terminated: %s" % event.error_code)
            self.done.set()


async def run():
    cfg = QuicConfiguration(is_client=True, alpn_protocols=H3_ALPN,
                            verify_mode=ssl.CERT_NONE)
    try:
        async with connect(HOST, PORT, configuration=cfg,
                           create_protocol=H3Client) as client:
            try:
                await asyncio.wait_for(client.done.wait(), 12)
            except asyncio.TimeoutError:
                print("TIMEOUT waiting for response")
            status = client.status.decode() if client.status else "none"
            print("status: %s" % status)
            print("body: %s" % client.body[:120])
            print("RESULT: %s" % ("PASS" if client.status == b"200" else "FAIL"))
    except Exception:
        print("EXCEPTION:")
        traceback.print_exc()
        print("RESULT: FAIL")


asyncio.run(run())
