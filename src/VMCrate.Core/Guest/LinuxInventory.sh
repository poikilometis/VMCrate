#!/bin/sh
set -eu
export LC_ALL=C

release_value() {
    awk -v key="$1=" 'index($0, key) == 1 {
        value = substr($0, length(key) + 1)
        sub(/^"/, "", value)
        sub(/"$/, "", value)
        print value
        exit
    }' /etc/os-release
}

id=$(release_value ID)
release=$(release_value VERSION_ID)
case "$id" in
    ""|*[!a-zA-Z0-9_-]*) echo 'Unknown Linux distribution.' >&2; exit 1 ;;
esac
case "$release" in
    *[!a-zA-Z0-9._-]*) echo 'Invalid distribution version.' >&2; exit 1 ;;
esac

ecosystem="LinuxLocal:$id"
if [ -n "$release" ]; then ecosystem="$ecosystem:$release"; fi
umask 077
rows=$(mktemp)
trap 'rm -f "$rows" "$rows.sorted"' EXIT
trap 'exit 1' HUP INT TERM

case "$id" in
    debian|ubuntu|kali)
        case "$id:$release" in
            debian:[0-9]|debian:[0-9][0-9]) ecosystem="Debian:$release" ;;
            ubuntu:[0-9][0-9].04)
                # Ubuntu LTS versions ship in April of even-numbered years.
                case "$release" in ?[02468].04) ecosystem="Ubuntu:$release:LTS" ;; esac ;;
        esac
        dpkg-query -W -f='${db:Status-Status}\t${source:Package}\t${source:Version}\n' > "$rows"
        awk -F '\t' '$1 == "installed" { print $2 "\t" $3 }' "$rows" > "$rows.sorted"
        mv "$rows.sorted" "$rows"
        ;;
    alpine)
        awk '
            BEGIN { RS = ""; FS = "\n" }
            {
                n = v = ""
                for (i = 1; i <= NF; ++i) {
                    if ($i ~ /^P:/) n = substr($i, 3)
                    if ($i ~ /^V:/) v = substr($i, 3)
                }
                if (n != "" && v != "") print n "\t" v
            }
        ' /lib/apk/db/installed > "$rows"
        ;;
    arch|endeavouros|manjaro)
        pacman -Q > "$rows"
        awk '{ print $1 "\t" $2 }' "$rows" > "$rows.sorted"
        mv "$rows.sorted" "$rows"
        ;;
    *)
        if ! command -v rpm >/dev/null 2>&1; then
            echo 'Package manager is unsupported.' >&2
            exit 1
        fi
        rpm -qa --queryformat '%{NAME}\t%{EVR}\n' > "$rows"
        ;;
esac

sort -u "$rows" > "$rows.sorted"
awk -F '\t' -v ecosystem="$ecosystem" '
    $0 !~ /^[a-zA-Z0-9][a-zA-Z0-9._+~:/@^-]*\t[a-zA-Z0-9][a-zA-Z0-9._+~:/@^-]*$/ ||
        length($1) > 180 || length($2) > 128 || NR > 5000 {
        invalid = 1
        exit
    }
    { lines[NR] = "{\"name\":\"" $1 "\",\"version\":\"" $2 "\"}" }
    END {
        if (invalid || NR == 0 || length(ecosystem) > 64) {
            print "Package inventory is empty, invalid or too large." > "/dev/stderr"
            exit 1
        }
        printf "{\"version\":1,\"ecosystem\":\"%s\",\"packages\":[\n", ecosystem
        for (i = 1; i <= NR; ++i) printf "%s%s\n", lines[i], i < NR ? "," : ""
        print "]}"
    }
' "$rows.sorted"
