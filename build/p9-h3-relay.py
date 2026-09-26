#!/usr/bin/env python3
"""Raw-aioquic relay: drive a real aioquic HTTP/3 client against the
NeutrinoOS guest over UDP, logging every QUIC event and state change so
server-flight rejections are visible."""
import logging
import socket
import ssl
import sys
import time
import traceback

from aioquic.h3.connection import H3_ALPN, H3Connection
from aioquic.quic.configuration import QuicConfiguration
from aioquic.quic.connection import QuicConnection
from aioquic.quic.events import HandshakeCompleted, ConnectionTerminated

logging.basicConfig(level=logging.DEBUG, format="%(levelname)s %(name)s %(message)s")

GUEST = ("10.0.2.15", 443)
PATH = sys.argv[1] if len(sys.argv) > 1 else "/health"

cfg = QuicConfiguration(is_client=True, alpn_protocols=H3_ALPN,
                        verify_mode=ssl.CERT_NONE)
conn = QuicConnection(configuration=cfg)
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.settimeout(0.05)

now = time.monotonic()
conn.connect(GUEST, now)
h3 = H3Connection(conn)
status = None
body = b""
stream_id = None
done = False


def pump_tx():
    for dg, addr in conn.datagrams_to_send(now=time.monotonic()):
        sock.sendto(dg, GUEST)
        print(">> sent %d bytes" % len(dg))


def handle_events():
    global status, body, stream_id, done
    while True:
        ev = conn.next_event()
        if ev is None:
            break
        print("EVENT:", type(ev).__name__, ev)
        if isinstance(ev, HandshakeCompleted):
            stream_id = conn.get_next_available_stream_id()
            h3.send_headers(stream_id=stream_id,
                            headers=[(b":method", b"GET"), (b":scheme", b"https"),
                                     (b":authority", b"10.0.2.15"),
                                     (b":path", PATH.encode())],
                            end_stream=True)
            pump_tx()
        try:
            for he in h3.handle_event(ev):
                if he.stream_id != stream_id:
                    continue
                if getattr(he, "headers", None):
                    for k, v in he.headers:
                        if k == b":status":
                            status = v
                if getattr(he, "data", None):
                    body += he.data
                if getattr(he, "stream_ended", False):
                    done = True
        except Exception:
            traceback.print_exc()
        if isinstance(ev, ConnectionTerminated):
            print("TERMINATED:", ev)
            done = True


start = time.monotonic()
pump_tx()
while time.monotonic() - start < 20 and not done:
    try:
        data, addr = sock.recvfrom(2048)
        print("<< got %d bytes" % len(data))
        conn.receive_datagram(data, GUEST, now=time.monotonic())
        print("STATE: state=%s close_pending=%s epoch=%s" % (
            conn._state, conn._close_pending, getattr(conn.tls, "state", "?")))
        print("SPACES:", {str(k): s.expected_packet_number for k, s in conn._spaces.items()})
        print("CRYPTOS:", [str(k) for k in conn._cryptos.keys()])
        print("CIDS:", [(c.cid.hex(), c.sequence_number) for c in conn._host_cids])
        import aioquic.tls as _t
        try:
            hs = conn._cryptos[_t.Epoch.HANDSHAKE].recv
            print("HANDSHAKE RECV SECRET:", hs.secret.hex())
            print("HANDSHAKE RECV SUITE:", hs.cipher_suite)
        except Exception as _e:
            print("secret dump failed:", _e)
        try:
            ini = conn._cryptos_initial[1].recv
            print("INITIAL RECV SECRET:", ini.secret.hex())
        except Exception as _e:
            print("initial dump failed:", _e)
    except socket.timeout:
        pass
    handle_events()
    pump_tx()
    time.sleep(0.01)

print("status:", status.decode() if status else "none")
print("body:", body[:120])
print("RESULT:", "PASS" if status == b"200" else "FAIL")
