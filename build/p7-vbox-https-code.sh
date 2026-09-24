#!/bin/bash
# Print only the HTTP status code of the VBox-hosted HTTPS service from
# WSL (OpenSSL curl via the Windows-host gateway). Used by
# scripts/test-vbox-phase7.ps1.
GW=$(ip route show default | awk '{print $3; exit}')
curl -k -s -o /dev/null -w '%{http_code}' --max-time 40 "https://$GW:8444/health"
