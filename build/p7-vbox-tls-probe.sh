#!/bin/bash
# Probe the VBox-hosted HTTPS service from WSL (OpenSSL curl) via the
# Windows-host gateway. Windows-native curl uses schannel, whose TLS 1.3
# ClientHello the NeutrinoOS TLS server rejects (no matching
# signature_algorithms); OpenSSL's hello works.
GW=$(ip route show default | awk '{print $3; exit}')
echo "gateway=$GW"
curl -k -s -o /dev/null -w 'https_code=%{http_code} time=%{time_total}\n' --max-time 40 "https://$GW:8444/health"
echo "--- http sanity:"
curl -s -o /dev/null -w 'http_code=%{http_code}\n' --max-time 15 "http://$GW:8080/health"
