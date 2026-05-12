#!/bin/bash
# jkcnsl-cache アンインストールスクリプト
# setup.sh でセットアップした環境を削除します

set -e
APP_DIR="/opt/jkcnsl-cache"

echo "jkcnsl-cache をアンインストールします..."

# --- 1. systemd サービス停止・削除 ---
if systemctl is-active --quiet jkcnsl-cache 2>/dev/null; then
    echo "サービスを停止します..."
    systemctl stop jkcnsl-cache
fi
if systemctl is-enabled --quiet jkcnsl-cache 2>/dev/null; then
    systemctl disable jkcnsl-cache
fi
if [ -f /etc/systemd/system/jkcnsl-cache.service ]; then
    rm /etc/systemd/system/jkcnsl-cache.service
    systemctl daemon-reload
    echo "systemd サービスを削除しました"
fi

# --- 2. nginx 設定削除 ---
NGINX_REMOVED=0
for f in /etc/nginx/sites-enabled/*; do
    if [ -L "$f" ] && readlink "$f" | grep -q jkcnsl-cache 2>/dev/null; then
        rm "$f"
        NGINX_REMOVED=1
    fi
done
for f in /etc/nginx/sites-available/*; do
    if [ -f "$f" ] && grep -q jkcnsl-cache "$f" 2>/dev/null; then
        rm "$f"
        NGINX_REMOVED=1
    fi
done
if [ "$NGINX_REMOVED" -eq 1 ] && command -v nginx &>/dev/null; then
    nginx -t && systemctl reload nginx
    echo "nginx 設定を削除しました"
fi

# --- 3. アプリディレクトリ削除 ---
if [ -d "$APP_DIR" ]; then
    rm -rf "$APP_DIR"
    echo "アプリディレクトリを削除しました: $APP_DIR"
fi

# --- 4. 実行ユーザー削除 ---
if id jkcnsl &>/dev/null; then
    userdel jkcnsl
    echo "ユーザー jkcnsl を削除しました"
fi

echo ""
echo "アンインストール完了。"
echo ""
echo "以下は自動削除されません（他のサービスで利用中の可能性があるため）："
echo "  - dotnet-sdk-8.0 パッケージ"
echo "  - nginx パッケージ"
echo "  - certbot / TLS 証明書"
echo ""
echo "手動で削除する場合："
echo "  apt remove --purge nginx certbot python3-certbot-nginx dotnet-sdk-8.0"
