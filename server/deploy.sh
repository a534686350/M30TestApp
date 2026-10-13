#!/usr/bin/env bash
# 把总控制台部署到服务器（在 Windows 上通过 Git Bash 运行）。
#
#   bash server/deploy.sh              # 部署/更新
#   bash server/deploy.sh --status     # 只看状态
#
# 凭据放在 artifacts/m30_secrets.txt（已 gitignore，不会入库）；
# 首次运行会自动生成，并把仓库里的 m30-hub.service 模板渲染成真实单元文件再上传。
set -euo pipefail

HOST="${M30_HUB_HOST:-54.206.27.158}"
USER_="${M30_HUB_SSH_USER:-root}"
ASKPASS="/tmp/askpass.sh"
SECRETS="artifacts/m30_secrets.txt"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

SSHOPT=(-o UserKnownHostsFile=/tmp/kh_m30 -o PreferredAuthentications=password
        -o PubkeyAuthentication=no -o ConnectTimeout=20)
export SSH_ASKPASS="$ASKPASS" SSH_ASKPASS_REQUIRE=force DISPLAY=:0

if [ ! -x "$ASKPASS" ]; then
  echo "!! 缺少 $ASKPASS（里面写一行 echo '<服务器密码>'）"; exit 1
fi

if [ "${1:-}" = "--status" ]; then
  ssh "${SSHOPT[@]}" "$USER_@$HOST" \
    "systemctl is-active m30-hub; ss -tlnp | grep ':80 ' || true; \
     curl -s -m 5 http://127.0.0.1/health; echo; \
     journalctl -u m30-hub -n 8 --no-pager | tail -8"
  exit 0
fi

# ── 凭据：没有就生成 ───────────────────────────────────────────────
if [ ! -f "$SECRETS" ]; then
  python - <<'PY'
import secrets, string
a = string.ascii_letters + string.digits
open('artifacts/m30_secrets.txt', 'w', encoding='utf-8').write(
    'CONSOLE_USER=m30\n'
    'CONSOLE_PASS=%s\n' % ''.join(secrets.choice(a) for _ in range(12)) +
    'STATION_TOKEN=%s\n' % ''.join(secrets.choice(a) for _ in range(24)))
PY
  echo "已生成 $SECRETS（里面是控制台账号 / 站点令牌）"
fi
# shellcheck disable=SC1090
source "$SECRETS"

# ── 用模板渲染出真实单元文件（模板里是占位符，不把密码写进仓库） ────
sed -e "s|__PASSWORD__|$CONSOLE_PASS|" -e "s|__TOKEN__|$STATION_TOKEN|" \
    server/m30-hub.service > artifacts/m30-hub.local.service

echo "==> 上传并重启（$HOST）"
ssh "${SSHOPT[@]}" "$USER_@$HOST" "mkdir -p /opt/m30hub"
scp "${SSHOPT[@]}" server/station_hub.py      "$USER_@$HOST:/opt/m30hub/"
scp "${SSHOPT[@]}" artifacts/m30-hub.local.service "$USER_@$HOST:/etc/systemd/system/m30-hub.service"
ssh "${SSHOPT[@]}" "$USER_@$HOST" \
  "systemctl daemon-reload && systemctl enable --now m30-hub && systemctl restart m30-hub && sleep 2 && systemctl is-active m30-hub"

echo
echo "控制台地址 : http://$HOST/  （用户 $CONSOLE_USER / 密码 $CONSOLE_PASS）"
echo "站点令牌   : $STATION_TOKEN   ← 上位机「站点标识」里要填一样的值"
echo
echo "提醒：AWS 安全组需要放行 80 端口，否则外网访问不了。"
