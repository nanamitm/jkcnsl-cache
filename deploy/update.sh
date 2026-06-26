#!/bin/bash
# jkcnsl-cache アップデートスクリプト
#
# 使用方法:
#   ./update.sh
#
# appsettings.json は上書きしません。

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="/opt/jkcnsl-cache"
SERVICE="jkcnsl-cache"

# --- .csproj の場所を自動検出 ---
if [ -f "$SCRIPT_DIR/jkcnsl-cache.csproj" ]; then
    PROJECT_CSPROJ="$SCRIPT_DIR/jkcnsl-cache.csproj"
elif [ -f "$SCRIPT_DIR/../jkcnsl-cache/jkcnsl-cache.csproj" ]; then
    PROJECT_CSPROJ="$SCRIPT_DIR/../jkcnsl-cache/jkcnsl-cache.csproj"
else
    echo "エラー: jkcnsl-cache.csproj が見つかりません"
    exit 1
fi

if [ ! -d "$APP_DIR" ]; then
    echo "エラー: $APP_DIR が見つかりません。先に setup.sh を実行してください"
    exit 1
fi

echo "=== jkcnsl-cache アップデート ==="
echo "プロジェクト: $PROJECT_CSPROJ"
echo "配置先: $APP_DIR"
echo ""

# --- 0. Node.js 確認・フロントエンドビルド ---
echo "[1/5] フロントエンドビルド中..."
if ! command -v node &>/dev/null; then
    curl -fsSL https://deb.nodesource.com/setup_lts.x | bash -
    apt install -y nodejs
fi
PROJECT_DIR="$(dirname "$PROJECT_CSPROJ")"
CLIENT_DIR="$PROJECT_DIR/client"
if [ -d "$CLIENT_DIR" ]; then
    pushd "$CLIENT_DIR" > /dev/null
    npm ci && npm run build
    popd > /dev/null
    echo "      フロントエンドビルド完了"
fi

# --- 1. ビルド（失敗したらここで止まる）---
echo "[2/5] ビルド中..."
dotnet publish "$PROJECT_CSPROJ" \
    -c Release -r linux-x64 \
    --self-contained true \
    /p:PublishSingleFile=true \
    /p:IncludeNativeLibrariesForSelfExtract=true \
    -o /tmp/jkcnsl-cache-publish
echo "      ビルド完了"

# --- 2. サービス停止 ---
echo "[3/5] サービス停止中..."
systemctl stop "$SERVICE"

# --- 3. バイナリ差し替え ---
echo "[4/5] バイナリ差し替え中..."
install /tmp/jkcnsl-cache-publish/jkcnsl-cache "$APP_DIR/jkcnsl-cache"
chown jkcnsl:jkcnsl "$APP_DIR/jkcnsl-cache"
if [ -d /tmp/jkcnsl-cache-publish/wwwroot ]; then
    cp -r /tmp/jkcnsl-cache-publish/wwwroot "$APP_DIR/"
    chown -R jkcnsl:jkcnsl "$APP_DIR/wwwroot"
fi
echo "      appsettings.json はそのまま保持します"

# --- 4. サービス起動・確認 ---
echo "[5/5] サービス起動中..."
systemctl start "$SERVICE"
sleep 2
if systemctl is-active --quiet "$SERVICE"; then
    echo "      起動確認 OK"
else
    echo "エラー: サービスの起動に失敗しました"
    journalctl -u "$SERVICE" -n 20 --no-pager
    exit 1
fi

echo ""
echo "アップデート完了"
