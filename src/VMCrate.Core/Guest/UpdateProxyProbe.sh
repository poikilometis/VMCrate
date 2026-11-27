#!/usr/bin/env bash

address='@PROXY_ADDRESS@'
port='@PROXY_PORT@'
relay='@RELAY_PORT@'
repository='@REPOSITORY@'

for tool in ip curl timeout; do
    command -v "$tool" >/dev/null || { printf 'Required command missing: %s\n' "$tool" >&2; exit 1; }
done

route=2
gateway=0
route_info=$(ip -4 route get "$address" 2>/dev/null)
if [[ $? == 0 && -n "$route_info" ]]; then
    route=1
fi

tcp_test() {
    if timeout 5 bash -c 'exec 3<>"/dev/tcp/$1/$2"' _ "$1" "$2" 2>/dev/null; then
        printf 1
    else
        printf 2
    fi
}

proxy_tcp=$(tcp_test "$address" "$port")
if [[ "$route_info" =~ via[[:space:]]+([0-9.]+)[[:space:]]+dev[[:space:]]+([^[:space:]]+) ]]; then
    neighbor=$(ip -4 neigh show to "${BASH_REMATCH[1]}" dev "${BASH_REMATCH[2]}" 2>/dev/null)
    case "$neighbor" in
        *INCOMPLETE*|*FAILED*) gateway=2 ;;
        *lladdr*) gateway=1 ;;
    esac
fi

relay_tcp=0
proxy="http://$address:$port"
config() {
    if [[ -n "$username" ]]; then
        local value="$username:$password"
        value=${value//\\/\\\\}
        value=${value//\"/\\\"}
        printf 'proxy-user = "%s"\n' "$value"
    fi
}

username=''
password=''
if [[ -n "$relay" ]]; then
    proxy="http://127.0.0.1:$relay"
    relay_tcp=$(tcp_test 127.0.0.1 "$relay")
elif [[ -t 0 ]]; then
    read -r -p 'HTTP proxy user (Enter for none): ' username
    if [[ -n "$username" ]]; then
        read -r -s -p 'HTTP proxy password: ' password
        printf '\n' >&2
        if [[ "$username" == *:* || "$username$password" =~ [[:cntrl:]] ]]; then
            printf 'Unsupported character in proxy credentials.\n' >&2
            exit 1
        fi
    fi
fi

codes=$(config | curl -q --config - --proxy "$proxy" --noproxy '' --proto '=https' \
    --head --silent --output /dev/null --connect-timeout 5 --max-time 15 \
    --write-out '%{http_connect} %{http_code}' "$repository")
result=$?
unset username password
if [[ ! "$codes" =~ ^[0-9]{3}\ [0-9]{3}$ ]]; then
    codes='000 000'
fi
printf 'VMCRATE_PROXY1 %s %s %s %s %s %s\n' "$route" "$gateway" "$proxy_tcp" "$relay_tcp" "$codes" "$result"
