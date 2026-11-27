#!/bin/sh
set -eu
export LC_ALL=C
command -v ip >/dev/null
v4=$(ip -4 route show default | awk 'END { print NR }')
v6=$(ip -6 route show default | awk 'END { print NR }')
adapters=$(ip -o link show up | awk -F ': ' '$2 !~ /^lo(@|$)/ { n++ } END { print n+0 }')
dns=$(awk '$1 == "nameserver" { n++ } END { print n+0 }' /etc/resolv.conf)
printf 'VMCRATE_ISOLATION1 %s %s %s %s\n' "$adapters" "$v4" "$v6" "$dns"
