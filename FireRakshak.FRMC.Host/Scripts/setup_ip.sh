#!/bin/bash

# setup_ip.sh - Persistent Network Management for FRCM

ROLE_CONFIG="/etc/frcm_network_roles.conf"
IP_CONFIG_DIR="/etc/frcm_network"

mkdir -p "$IP_CONFIG_DIR"

# ---------------------------------------------------------
# Root check
# ---------------------------------------------------------

function check_root() {

    if [[ $EUID -ne 0 ]]; then
        echo "ERROR: This operation requires root privileges."
        exit 1
    fi
}

# ---------------------------------------------------------
# List interfaces
# ---------------------------------------------------------

function list_interfaces() {

    for iface_path in /sys/class/net/*; do

        iface=$(basename "$iface_path")

        [[ "$iface" == "lo" ]] && continue
        [[ "$iface" == docker* ]] && continue
        [[ "$iface" == veth* ]] && continue
        [[ "$iface" == br-* ]] && continue

        mac=$(cat "$iface_path/address" 2>/dev/null | tr '[:lower:]' '[:upper:]')

        operstate=$(cat "$iface_path/operstate" 2>/dev/null)

        ip_addr=$(ip -o -4 addr show "$iface" 2>/dev/null \
            | awk '{print $4}' \
            | cut -d/ -f1 \
            | head -n1)

        echo "IFACE:$iface|MAC:${mac:-N/A}|STATE:${operstate:-unknown}|IP:${ip_addr:-N/A}"
    done
}

# ---------------------------------------------------------
# CIDR to subnet
# ---------------------------------------------------------

function cidr_to_mask() {

    local cidr=$1
    local mask=""
    local full_octets=$((cidr/8))
    local partial_octet=$((cidr%8))

    for ((i=0;i<4;i++)); do

        if [ $i -lt $full_octets ]; then
            mask+="255"

        elif [ $i -eq $full_octets ]; then
            mask+=$((256 - 2**(8-partial_octet)))

        else
            mask+="0"
        fi

        [ $i -lt 3 ] && mask+="."
    done

    echo "$mask"
}

# ---------------------------------------------------------
# Get config
# ---------------------------------------------------------

function get_config() {

    local iface=$1

    ip=$(nmcli -g IP4.ADDRESS device show "$iface" \
        2>/dev/null | head -n1 | cut -d/ -f1)

    cidr=$(nmcli -g IP4.ADDRESS device show "$iface" \
        2>/dev/null | head -n1 | cut -d/ -f2)

    gw=$(nmcli -g IP4.GATEWAY device show "$iface" \
        2>/dev/null | head -n1)

    mask=$(cidr_to_mask "${cidr:-24}")

    echo "IP:${ip:-N/A}|MASK:${mask:-N/A}|GW:${gw:-N/A}"
}

# ---------------------------------------------------------
# Set persistent IP
# ---------------------------------------------------------

function set_ip() {

    check_root

    local iface=$1
    local ip=$2
    local cidr=$3
    local gateway=$4

    echo "Configuring persistent IP for $iface"

    ip link set "$iface" up

    nmcli device set "$iface" managed yes >/dev/null 2>&1

    old_conns=$(nmcli -t -f NAME,DEVICE connection show \
        | grep ":$iface$" \
        | cut -d: -f1)

    for c in $old_conns; do
        nmcli connection delete "$c" >/dev/null 2>&1
    done

    conn="frcm-$iface"

    nmcli connection add \
        type ethernet \
        ifname "$iface" \
        con-name "$conn" \
        autoconnect yes >/dev/null 2>&1

    nmcli connection modify "$conn" \
        ipv4.method manual \
        ipv4.addresses "$ip/$cidr" \
        connection.autoconnect yes \
        connection.autoconnect-priority 100 \
        ipv4.may-fail no \
        ipv6.method ignore

    if [ -n "$gateway" ] &&
       [ "$gateway" != "N/A" ]; then

        nmcli connection modify \
            "$conn" \
            ipv4.gateway "$gateway"
    fi

    nmcli connection up "$conn" >/dev/null 2>&1

    ip link set "$iface" up

cat <<EOF > "$IP_CONFIG_DIR/$iface.conf"
IP=$ip
CIDR=$cidr
GATEWAY=$gateway
PROFILE=$conn
EOF

    echo "SUCCESS"
}

# ---------------------------------------------------------
# Restore after reboot
# ---------------------------------------------------------

function restore_all() {

    check_root

    for conf in "$IP_CONFIG_DIR"/*.conf; do

        [ -e "$conf" ] || continue

        iface=$(basename "$conf" .conf)

        source "$conf"

        echo "Restoring $iface"

        ip link set "$iface" up

        nmcli device set "$iface" managed yes >/dev/null 2>&1

        if [ -n "$PROFILE" ]; then
            nmcli connection up "$PROFILE" >/dev/null 2>&1
        fi
    done

    echo "SUCCESS"
}

# ---------------------------------------------------------
# Install startup service
# ---------------------------------------------------------

function setup_system() {

    check_root

    cp "$0" /usr/local/bin/setup_ip.sh

    chmod +x /usr/local/bin/setup_ip.sh

cat <<EOF > /etc/systemd/system/frcm-network.service
[Unit]
Description=FRCM Network Persistence Service
After=NetworkManager.service network-online.target
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=/usr/local/bin/setup_ip.sh restore
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
EOF

    systemctl daemon-reload

    systemctl enable frcm-network.service

    systemctl restart frcm-network.service

    echo "SUCCESS"
}

# ---------------------------------------------------------
# Save roles
# ---------------------------------------------------------

function save_roles() {

    check_root

cat <<EOF > "$ROLE_CONFIG"
NETRA1_MAC=$1
NETRA1_NAME=$2
NETRA2_MAC=$3
NETRA2_NAME=$4
EOF

    echo "SUCCESS"
}

# ---------------------------------------------------------
# Main
# ---------------------------------------------------------

case "$1" in

list)
    list_interfaces
    ;;

get_config)
    get_config "$2"
    ;;

set)
    set_ip "$2" "$3" "$4" "$5"
    ;;

restore)
    restore_all
    ;;

setup_system)
    setup_system
    ;;

save_roles)
    save_roles "$2" "$3" "$4" "$5"
    ;;

*)
echo "Usage:"
echo "$0 list"
echo "$0 get_config <iface>"
echo "$0 set <iface> <ip> <cidr> [gateway]"
exit 1
;;

esac
