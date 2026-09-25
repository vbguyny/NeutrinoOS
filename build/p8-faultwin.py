#!/usr/bin/env python3
"""Decode the kernel fault dump windows ('retcode:' / 'code:') from a log.

Prints per-byte addresses and byte values, marks the return address, and
tries to locate 'FF D0' (call rax) occurrences with their positions.
"""
import re
import sys

def decode(tag, addr_hex, hexstr):
    bs = bytes.fromhex(hexstr.replace(' ', '').replace('\n', ''))
    addr = int(addr_hex, 16)
    print(f'--- {tag} window @ {addr:#x} len={len(bs)} ---')
    for i, b in enumerate(bs):
        note = ''
        if i % 8 == 0:
            note = ' <<'
        print(f'{addr + i:08X}: {b:02X}{note}')
    print('--- summary ---')
    print('window end   =', hex(addr + len(bs)))
    for i in range(len(bs) - 1):
        if bs[i] == 0xFF and bs[i + 1] == 0xD0:
            print(f'call rax (FF D0) at {addr + i:08X}, returns to {addr + i + 2:08X}')

def main():
    log = open(sys.argv[1], 'rb').read().decode('utf-8', 'replace')
    rets = re.findall(r'ret=(0x[0-9A-Fa-f]+)', log)
    print('ret= values:', rets)
    for m in re.finditer(r'retcode:([0-9A-Fa-f]+)((?:[0-9A-Fa-f]{2} ?)+)', log):
        decode('retcode', m.group(1), m.group(2))
    for m in re.finditer(r'code:([0-9A-Fa-f]+)((?:[0-9A-Fa-f]{2} ?)+)', log):
        decode('code', m.group(1), m.group(2))

if __name__ == '__main__':
    main()
