import base64
d = open('/tmp/cert.pem', 'rb').read()
body = b''.join(d.split(b'\n')[1:-2])
b = base64.b64decode(body)
for i in range(len(b) - 10):
    if b[i] == 0x17 and b[i + 1] == 13:
        print('UTCTime at', i, b[i:i + 18])
        print('  str:', b[i + 2:i + 15])
