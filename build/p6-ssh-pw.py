#!/usr/bin/env python3
"""Drive an interactive password login to the NeutrinoOS sshd via a pty.

Usage: p6-ssh-pw.py <password> [host] [port]
Runs:  ssh -p <port> -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null
          -o PreferredAuthentications=password -o PubkeyAuthentication=no
          -o LogLevel=ERROR user@<host> "echo PW-OK"
Exits 0 when PW-OK appears in the session output.
"""
import os, pty, select, sys, time

pw = sys.argv[1]
host = sys.argv[2] if len(sys.argv) > 2 else "127.0.0.1"
port = sys.argv[3] if len(sys.argv) > 3 else "2222"
argv = [
    "ssh", "-p", port,
    "-o", "StrictHostKeyChecking=no",
    "-o", "UserKnownHostsFile=/dev/null",
    "-o", "PreferredAuthentications=password",
    "-o", "PubkeyAuthentication=no",
    "-o", "LogLevel=ERROR",
    "-o", "ConnectTimeout=15",
    "user@%s" % host, "echo PW-OK",
]

pid, fd = pty.fork()
if pid == 0:  # child
    os.execvp(argv[0], argv)
    os._exit(127)

buf = b""
sent = False
deadline = time.time() + float(os.environ.get("SSH_PW_DEADLINE", "45"))
while time.time() < deadline:
    r, _, _ = select.select([fd], [], [], 0.5)
    if r:
        try:
            chunk = os.read(fd, 4096)
        except OSError:
            break
        if not chunk:
            break
        buf += chunk
        if not sent and b"assword" in buf:
            os.write(fd, pw.encode() + b"\n")
            sent = True
    done, status = os.waitpid(pid, os.WNOHANG)
    if done:
        break
try:
    os.close(fd)
except OSError:
    pass

# Do not leave a hung ssh client behind when we bail out on the deadline.
try:
    os.kill(pid, 9)
except OSError:
    pass
try:
    os.waitpid(pid, os.WNOHANG)
except OSError:
    pass

text = buf.decode("utf-8", "replace").replace("\r", "")
print(text)
ok = sent and "PW-OK" in text
print("[p6-ssh-pw] password-sent=%s PW-OK=%s" % (sent, "PW-OK" in text))
sys.exit(0 if ok else 1)
