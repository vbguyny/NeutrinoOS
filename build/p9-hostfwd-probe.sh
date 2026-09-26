#!/bin/bash
# Probe which QEMU hostfwd guest-address syntaxes are accepted.
try() {
  local rule="$1"
  local err
  err=$(timeout 3 qemu-system-x86_64 -machine none \
    -netdev "user,id=n0,ipv6=on,hostfwd=$rule" \
    -device virtio-net-pci,netdev=n0,disable-legacy=on \
    -display none -serial none -no-reboot 2>&1 | head -2)
  if echo "$err" | grep -q 'Invalid host forwarding'; then
    echo "REJECT: $rule -> $(echo $err | cut -c1-120)"
  else
    echo "ACCEPT: $rule"
  fi
}

try 'tcp::8080-[fec0::5054:ff:fe12:3456]:80'
try 'tcp::8080-fec0::5054:ff:fe12:3456:80'
try 'tcp:0.0.0.0:8080-[fec0::5054:ff:fe12:3456]:80'
try 'tcp::8080-[fec0:0:0:0:5054:ff:fe12:3456]:80'
try 'tcp::8080:10.0.2.15:80'
try 'tcp::8080-10.0.2.15:80'
try 'udp::8081-[fec0::5054:ff:fe12:3456]:443'
try 'tcp::8080-[fe80::5054:ff:fe12:3456]:80'
