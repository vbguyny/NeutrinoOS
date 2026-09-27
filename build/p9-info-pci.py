import subprocess, time

q = subprocess.Popen([
    'qemu-system-x86_64', '-machine', 'q35', '-m', '2G', '-cpu', 'max', '-smp', '1',
    '-drive', 'if=pflash,format=raw,readonly=on,file=/usr/share/OVMF/OVMF_CODE_4M.fd',
    '-drive', 'if=pflash,format=raw,file=/tmp/p9nvmevars2.fd',
    '-drive', 'id=bootdisk,if=none,format=raw,file=/root/run.img',
    '-device', 'ide-hd,drive=bootdisk,bus=ide.0',
    '-drive', 'id=nvmedisk,if=none,format=raw,file=/root/nvme.img',
    '-device', 'nvme,drive=nvmedisk,serial=NEUTRINO01',
    '-display', 'none', '-serial', 'none', '-monitor', 'stdio'],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
time.sleep(5)
for cmd in ('info pci\n', 'quit\n'):
    try:
        q.stdin.write(cmd)
        q.stdin.flush()
    except Exception:
        break
    time.sleep(1)
try:
    out, _ = q.communicate(timeout=10)
except Exception:
    q.terminate()
    out, _ = q.communicate(timeout=10)
print(out[:6000])
