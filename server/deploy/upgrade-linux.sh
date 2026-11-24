#!/usr/bin/env sh
# Mosaic.Server Linux 原地升级脚本（随服务端发布，0.1.2 起）。
#
# 用法（需要 root）：
#   sudo ./upgrade-linux.sh Mosaic.Server-0.1.2-linux-x64.tar.gz [SHA256SUMS]
#
# 流程：校验包 → 停服务 → 备份数据目录（库 + master.key + attachments）→ 目录整体替换
#       → 启动 → 健康检查 → 打印迁移版本。健康检查失败自动回滚到上一版本。
#
# 可用环境变量覆盖：
#   MOSAIC_SERVICE      systemd 单元名        （默认 mosaic-server）
#   MOSAIC_APP_DIR      程序目录              （默认 /opt/mosaic-server）
#   MOSAIC_BACKUP_ROOT  备份根目录            （默认 /var/backups/mosaic-server）
#   MOSAIC_HEALTH_URL   健康检查地址          （默认 http://127.0.0.1:5080/health）
#   MOSAIC_DATA_DIR     数据目录（覆盖自动探测）
#   MOSAIC_SKIP_ATTACHMENTS_BACKUP=1 备份时跳过 attachments 目录（磁盘不足时用）
#   MOSAIC_KEEP_PREVIOUS=2           保留的上一版本目录数量
set -eu

PKG=${1:-}
SUMS=${2:-}
SERVICE=${MOSAIC_SERVICE:-mosaic-server}
BACKUP_ROOT=${MOSAIC_BACKUP_ROOT:-/var/backups/mosaic-server}
HEALTH_URL=${MOSAIC_HEALTH_URL:-http://127.0.0.1:5080/health}
KEEP_PREVIOUS=${MOSAIC_KEEP_PREVIOUS:-2}
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
DB_OVERRIDE=""

die() { echo "错误：$*" >&2; exit 1; }
read_env() { sed -n "s/^[[:space:]]*$1=//p" "$ENV_FILE" | tail -1 | tr -d '"' | tr -d "'" | sed 's/[[:space:]]*$//'; }

[ -n "$PKG" ] || die "用法：$0 <Mosaic.Server-<版本>-linux-x64.tar.gz> [SHA256SUMS]"
[ -f "$PKG" ] || die "找不到发布包：$PKG"
[ "$(id -u)" = "0" ] || die "需要 root 权限运行（sudo $0 ...）"
command -v systemctl >/dev/null 2>&1 || die "未找到 systemctl，本脚本只支持 systemd 部署"
command -v curl >/dev/null 2>&1 || die "未找到 curl，无法做健康检查"
systemctl cat "$SERVICE" >/dev/null 2>&1 || die "systemd 单元 $SERVICE 不存在"

# 发布包版本号取自文件名，用于升级后比对 /health 输出。
PKG_VERSION=$(basename "$PKG" | sed -n 's/^Mosaic\.Server-\([0-9][0-9.]*\)-linux-x64\.tar\.gz$/\1/p')
[ -n "$PKG_VERSION" ] || die "无法从文件名解析版本：$(basename "$PKG")"

# 1. 校验
if [ -n "$SUMS" ]; then
    [ -f "$SUMS" ] || die "找不到校验文件：$SUMS"
    ( cd "$(dirname "$PKG")" && grep -F "  $(basename "$PKG")" "$SUMS" >/dev/null ) || die "校验文件中没有 $(basename "$PKG")"
    ( cd "$(dirname "$PKG")" && sha256sum -c "$SUMS" ) || die "SHA256 校验失败"
fi

# 2. 探测程序目录 / 数据目录 / 运行用户
ENV_FILE=$(systemctl show -p EnvironmentFiles --value "$SERVICE" | sed 's/ (ignore_errors=yes)//;s/ (ignore_errors=no)//' | awk '{print $1}')
SERVICE_USER=$(systemctl show -p User --value "$SERVICE")
[ -n "$SERVICE_USER" ] || SERVICE_USER=root

APP_DIR=${MOSAIC_APP_DIR:-}
if [ -z "$APP_DIR" ]; then
    EXEC_PATH=$(systemctl show -p ExecStart --value "$SERVICE" | sed -n 's/.*path=\([^ ;]*\).*/\1/p' | head -1)
    if [ -z "$EXEC_PATH" ]; then
        EXEC_PATH=$(systemctl show -p ExecStart --value "$SERVICE" | awk 'NR==1{print $1}' | tr -d '"')
    fi
    [ -n "$EXEC_PATH" ] || die "无法从 ExecStart 解析程序路径，请设置 MOSAIC_APP_DIR"
    APP_DIR=$(dirname "$EXEC_PATH")
fi
[ -d "$APP_DIR" ] || die "程序目录不存在：$APP_DIR"

DATA_DIR=${MOSAIC_DATA_DIR:-}
if [ -z "$DATA_DIR" ] && [ -n "$ENV_FILE" ] && [ -f "$ENV_FILE" ]; then
    DATA_DIR=$(read_env MOSAIC_DATA_DIR)
    DB_OVERRIDE=$(read_env MOSAIC_DATABASE_PATH)
fi
if [ -z "$DATA_DIR" ]; then
    if [ -d "$APP_DIR/data" ]; then DATA_DIR="$APP_DIR/data"; else DATA_DIR=/var/lib/mosaic-server; fi
fi
DB_PATH=${DB_OVERRIDE:-$DATA_DIR/mosaic-server.sqlite3}

[ -d "$DATA_DIR" ] || die "数据目录不存在：$DATA_DIR"
[ -f "$DB_PATH" ] || die "找不到数据库：$DB_PATH（确认 MOSAIC_DATA_DIR 是否正确）"
[ -f "$DATA_DIR/master.key" ] || die "数据目录缺少 master.key，拒绝升级（先恢复密钥）"

# 3. 磁盘余量：解压体积 + 数据目录备份体积
PKG_BYTES=$(wc -c < "$PKG")
EXTRACT_BYTES=$(gzip -dc "$PKG" | wc -c)
DATA_BYTES=$(du -sb "$DATA_DIR" | awk '{print $1}')
NEED=$((PKG_BYTES + EXTRACT_BYTES + DATA_BYTES))
FREE=$(df -PB1 "$APP_DIR" | awk 'NR==2{print $4}')
[ "$FREE" -gt "$NEED" ] || die "磁盘余量不足：需要约 $NEED 字节，可用 $FREE 字节"

echo "== Mosaic.Server $PKG_VERSION 升级 =="
echo "发布包   : $(cd "$(dirname "$PKG")" && pwd)/$(basename "$PKG")"
echo "程序目录 : $APP_DIR"
echo "数据目录 : $DATA_DIR（数据库 $DB_PATH）"
echo "运行用户 : $SERVICE_USER"
echo "备份目录 : $BACKUP_ROOT/$STAMP"

# 4. 停服务并备份（停服后复制，避免 WAL 不一致）
systemctl stop "$SERVICE"
mkdir -p "$BACKUP_ROOT/$STAMP"
cp -a "$DB_PATH" "$BACKUP_ROOT/$STAMP/"
for suffix in -wal -shm; do
    if [ -f "$DB_PATH$suffix" ]; then cp -a "$DB_PATH$suffix" "$BACKUP_ROOT/$STAMP/"; fi
done
cp -a "$DATA_DIR/master.key" "$BACKUP_ROOT/$STAMP/"
if [ "${MOSAIC_SKIP_ATTACHMENTS_BACKUP:-0}" != "1" ] && [ -d "$DATA_DIR/attachments" ]; then
    cp -a "$DATA_DIR/attachments" "$BACKUP_ROOT/$STAMP/"
fi
echo "备份完成：$(du -sh "$BACKUP_ROOT/$STAMP" | awk '{print $1}')"

# 5. 目录整体替换（保留上一版本以便回滚）
NEW_DIR="$APP_DIR.new-$STAMP"
OLD_DIR="$APP_DIR.previous-$STAMP"
mkdir -p "$NEW_DIR"
tar -xzf "$PKG" -C "$NEW_DIR"
chmod +x "$NEW_DIR/Mosaic.Server"
chown -R "$SERVICE_USER":"$SERVICE_USER" "$NEW_DIR"
find "$NEW_DIR" -type d -exec chmod 755 {} +

mv "$APP_DIR" "$OLD_DIR"
mv "$NEW_DIR" "$APP_DIR"

# 6. 启动 + 健康检查
systemctl start "$SERVICE"
HEALTH=""
i=0
while [ $i -lt 60 ]; do
    HEALTH=$(curl -fsS -m 3 "$HEALTH_URL" 2>/dev/null || true)
    [ -n "$HEALTH" ] && break
    i=$((i + 1))
    sleep 1
done

rollback() {
    echo "健康检查失败，回滚到上一版本…" >&2
    systemctl stop "$SERVICE" || true
    rm -rf "$APP_DIR.failed-$STAMP"
    mv "$APP_DIR" "$APP_DIR.failed-$STAMP"
    mv "$OLD_DIR" "$APP_DIR"
    systemctl start "$SERVICE" || true
    echo "已回滚。新目录保留在 $APP_DIR.failed-$STAMP，数据备份在 $BACKUP_ROOT/$STAMP" >&2
    echo "确认服务恢复：curl -fsS $HEALTH_URL" >&2
    exit 1
}

[ -n "$HEALTH" ] || rollback
echo "$HEALTH" | grep -qE "\"version\":\"$PKG_VERSION(\+|\\\")" || {
    echo "健康检查返回版本与发布包不一致，期望 $PKG_VERSION，实际：$HEALTH" >&2
    rollback
}

echo
echo "升级成功：$HEALTH"

# 7. 迁移与附件目录核查
if command -v sqlite3 >/dev/null 2>&1; then
    echo "schema_migrations:"
    sqlite3 "$DB_PATH" "select version || ' ' || name from schema_migrations order by version;"
    sqlite3 "$DB_PATH" "select 'attachments table: ' || count(*) from sqlite_master where type='table' and name='attachments';"
else
    echo "提示：未安装 sqlite3，跳过迁移版本核查（可 curl /health 确认版本）"
fi
if [ "${MOSAIC_SKIP_ATTACHMENTS_BACKUP:-0}" != "1" ]; then
    echo "数据目录附件：$(find "$DATA_DIR/attachments" -type f 2>/dev/null | wc -l) 个文件"
fi

# 8. 反向代理上传上限提示（nginx 存在时）
if command -v nginx >/dev/null 2>&1; then
    LIMIT=$(nginx -T 2>/dev/null | grep -E '^\s*client_max_body_size' | awk '{print $2}' | tail -1 || true)
    echo "nginx client_max_body_size: ${LIMIT:-未配置（默认 1m，需 ≥ MOSAIC_ATTACHMENT_MAX_BYTES=50m）}"
fi

# 9. 清理过旧的版本目录
ls -1d "$APP_DIR".previous-* 2>/dev/null | sort | head -n -"$KEEP_PREVIOUS" | while read -r old; do
    if [ -n "$old" ]; then rm -rf "$old"; echo "已删除旧版本目录：$old"; fi
done

cat <<EOF

升级完成（$STAMP）。
回滚方式：
  systemctl stop $SERVICE
  mv $APP_DIR $APP_DIR.failed-$STAMP && mv $OLD_DIR $APP_DIR
  systemctl start $SERVICE
数据回滚（仅在需要时）：
  systemctl stop $SERVICE
  cp -a $BACKUP_ROOT/$STAMP/mosaic-server.sqlite3 $DB_PATH
  rm -f $DB_PATH-wal $DB_PATH-shm
  # 0.1.2 回退到 0.1.1 时还需：sqlite3 $DB_PATH "DROP TABLE attachments; DELETE FROM schema_migrations WHERE version=3;"
  systemctl start $SERVICE
EOF