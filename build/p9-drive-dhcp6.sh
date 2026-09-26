#!/bin/bash
# Drive the GDB-paused guest: bring v6 up, then run dhcp6.
sleep 1
printf 'ifconfig eth0 up\n' > /root/p9dbgv6.in
sleep 25
printf 'dhcp6\n' > /root/p9dbgv6.in
sleep 12
echo driven
