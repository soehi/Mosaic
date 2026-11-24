#!/usr/bin/env sh
# 一键上线：把本地构建出的服务端发布包上传到服务器，执行原地升级脚本，再核验线上版本。
#
# 用法（在 Git Bash / Linux 上运行）：
#   MOSAIC_USER=[USER] MOSAIC_PORT=[PORT] MOSAIC_KEY=/path/to/key sh deploy-online.sh
#   口令登录时不要设 MOSAIC_KEY，ssh 会要求交互式输入；需要 sudo 口令时加 MOSAIC_TTY=1 并把 stdin 留给终端。
#
# 可覆盖变量：
#   MOSAIC_HOST           服务器地址          （默认 bansonic.top）
#   MOSAIC_USER           登录用户            （必填）
#   MOSAIC_PORT           SSH 端口            （默认 22）
#   MOSAIC_KEY            私钥路径            （默认用 ssh 自身配置与 agent）
#   MOSAIC_REMOTE_DIR     上传暂存目录        （默认 /root/mosaic-upgrade）
#   MOSAIC_VER            发布包版本          （默认 0.1.2）
#   MOSAIC_PKG            发布包路径          （默认 <repo>/server/artifacts/Mosaic.Server-<ver>-linux-x64.tar.gz）
#   MOSAIC_PUBLIC_URL     线上站点根地址      （默认 https://mosaic.bansonic.top）
#   MOSAIC_PUBLIC_HEALTH  线上健康检查地址    （默认 $MOSAIC_PUBLIC_URL/health）
#   MOSAIC_TTY=1          为远端命令分配 tty（sudo 需要交互输入口令时用）
#
# 注意：脚本走 SSH + systemd，只适用于自建主机安装。线上 mosaic.bansonic.top 是 Coolify
# 4.5.3 的 docker compose 应用（无 mosaic-server.service），请改用 Coolify 面板或
# POST /api/v1/deploy?uuid=<APP_UUID>&force=false 触发构建，见 server/README.md「Coolify」小节。
set -eu

HOST=${MOSAIC_HOST:-bansonic.top}
REMOTE_USER=${MOSAIC_USER:-}
PORT=${MOSAIC_PORT:-22}
KEY=${MOSAIC_KEY:-}
REMOTE_DIR=${MOSAIC_REMOTE_DIR:-/root/mosaic-upgrade}
VER=${MOSAIC_VER:-0.1.2}
PUBLIC_URL=${MOSAIC_PUBLIC_URL:-https://mosaic.bansonic.top}
PUBLIC_HEALTH=${MOSAIC_PUBLIC_HEALTH:-$PUBLIC_URL/health}

ROOT=$(cd "$(dirname "$0")/.." && pwd)
PKG=${MOSAIC_PKG:-$ROOT/artifacts/Mosaic.Server-$VER-linux-x64.tar.gz}
SUMS=$(dirname "$PKG")/SHA256SUMS
SCRIPT=$ROOT/deploy/upgrade-linux.sh

die() { echo "错误：$*" >&2; exit 1; }

[ -n "$REMOTE_USER" ] || die "缺少 MOSAIC_USER。用法：MOSAIC_USER=[USER] MOSAIC_PORT=[PORT] MOSAIC_KEY=[KEY] sh $0"
[ -f "$PKG" ] || die "找不到发布包：$PKG（先跑 publish-linux.ps1 或 publish-linux.sh）"
[ -f "$SUMS" ] || die "找不到校验文件：$SUMS"
[ -f "$SCRIPT" ] || die "找不到升级脚本：$SCRIPT"
command -v ssh >/dev/null 2>&1 || die "未找到 ssh 客户端"
command -v scp >/dev/null 2>&1 || die "未找到 scp 客户端"

TARGET="$REMOTE_USER@$HOST"
SSH_OPTS="-p $PORT -o ConnectTimeout=15 -o StrictHostKeyChecking=accept-new"
[ -n "$KEY" ] && SSH_OPTS="$SSH_OPTS -i $KEY"
[ "${MOSAIC_TTY:-0}" = "1" ] && SSH_OPTS="$SSH_OPTS -tt"

# 1. 本地校验（上传前先确认包没损坏）
( cd "$(dirname "$PKG")" && sha256sum -c "$SUMS" --ignore-missing ) || die "本地 SHA256 校验失败"
echo "本地包校验通过：$(basename "$PKG")"

# 2. 连通性 + 权限探测
echo "连接 $TARGET:$PORT …"
REMOTE_ID=$(ssh $SSH_OPTS "$TARGET" 'id -u; id -un' 2>&1) || die "SSH 连接失败：$REMOTE_ID"
REMOTE_UID=$(echo "$REMOTE_ID" | sed -n '1p')
REMOTE_NAME=$(echo "$REMOTE_ID" | sed -n '2p')
echo "已登录：$REMOTE_NAME (uid=$REMOTE_UID)"
if [ "$REMOTE_UID" = "0" ]; then
    SUDO=""
elif ssh $SSH_OPTS "$TARGET" 'sudo -n true' >/dev/null 2>&1; then
    SUDO="sudo -n "
else
    die "远端用户 $REMOTE_NAME 既不是 root，也没有免密 sudo。请改用 root、或配置 NOPASSWD、或用 MOSAIC_TTY=1 在终端里交互输入口令"
fi

# 3. 上传三个文件
ssh $SSH_OPTS "$TARGET" "mkdir -p '$REMOTE_DIR'" || die "无法创建远端目录 $REMOTE_DIR"
echo "上传发布包（$(wc -c < "$PKG") 字节）…"
scp -P "$PORT" ${KEY:+-i "$KEY"} -o ConnectTimeout=15 "$PKG" "$SUMS" "$SCRIPT" "$TARGET:$REMOTE_DIR/" \
    || die "上传失败"
echo "上传完成 → $TARGET:$REMOTE_DIR"

# 4. 停服备份 + 原地替换 + 健康检查（失败自动回滚）
echo
echo "== 执行原地升级 =="
ssh $SSH_OPTS "$TARGET" "cd '$REMOTE_DIR' && ${SUDO}sh upgrade-linux.sh '$(basename "$PKG")' SHA256SUMS" \
    || die "升级脚本返回非零（若已自动回滚，服务应仍在上一版本运行；详见上方脚本输出）"

# 5. 从公网侧核验
echo
echo "== 公网核验 =="
i=0
while [ $i -lt 30 ]; do
    HEALTH=$(curl -fsS -m 5 "$PUBLIC_HEALTH" 2>/dev/null || true)
    echo "$HEALTH" | grep -q "\"version\":\"$VER" && break
    i=$((i + 1))
    sleep 2
done
if echo "$HEALTH" | grep -q "\"version\":\"$VER"; then
    echo "健康检查：$HEALTH"
else
    die "公网 /health 未返回 $VER：$HEALTH"
fi

CODE=$(curl -s -o /dev/null -w '%{http_code}' -m 5 "$PUBLIC_URL/v1/attachments" 2>/dev/null || echo 000)
echo "/v1/attachments 状态码：$CODE（期望 401；404 表示新路由未生效）"
[ "$CODE" = "401" ] || echo "警告：状态码不是 401，请检查反代是否指到本服务。"

cat <<EOF

上线完成。
回滚（在服务器上执行）：
  systemctl stop mosaic-server
  mv /opt/mosaic-server /opt/mosaic-server.failed && mv /opt/mosaic-server.previous-<STAMP> /opt/mosaic-server
  systemctl start mosaic-server
数据回滚用脚本输出的 /var/backups/mosaic-server/<STAMP>/（可用 MOSAIC_BACKUP_ROOT 覆盖）。
EOF