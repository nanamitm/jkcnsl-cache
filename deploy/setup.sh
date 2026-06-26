#!/bin/bash
# jkcnsl-cache セットアップスクリプト
#
# 使用方法:
#   ws モード（LAN 向け・nginx 不要）:
#     ./setup.sh --mode ws [--port 5000] [--status-port 5001]
#
#   wss モード（インターネット公開・nginx + Let's Encrypt）:
#     ./setup.sh --mode wss --domain your-domain.example.com [--email admin@example.com] [--status-port 5001]
#
# このスクリプトは jkcnsl-cache.csproj と同じフォルダ、または
# プロジェクトルート直下の deploy/ フォルダから実行できます。

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# --- .csproj の場所を自動検出 ---
if [ -f "$SCRIPT_DIR/jkcnsl-cache.csproj" ]; then
    PROJECT_CSPROJ="$SCRIPT_DIR/jkcnsl-cache.csproj"
elif [ -f "$SCRIPT_DIR/../jkcnsl-cache/jkcnsl-cache.csproj" ]; then
    PROJECT_CSPROJ="$SCRIPT_DIR/../jkcnsl-cache/jkcnsl-cache.csproj"
else
    echo "エラー: jkcnsl-cache.csproj が見つかりません"
    exit 1
fi

# --- deploy ファイルの場所を自動検出 ---
if [ -f "$SCRIPT_DIR/jkcnsl-cache.service" ]; then
    DEPLOY_DIR="$SCRIPT_DIR"
elif [ -f "$SCRIPT_DIR/deploy/jkcnsl-cache.service" ]; then
    DEPLOY_DIR="$SCRIPT_DIR/deploy"
elif [ -f "$SCRIPT_DIR/../deploy/jkcnsl-cache.service" ]; then
    DEPLOY_DIR="$SCRIPT_DIR/../deploy"
else
    echo "エラー: jkcnsl-cache.service が見つかりません"
    exit 1
fi

MODE=""
DOMAIN=""
EMAIL="admin@example.com"
PORT="5000"
STATUS_PORT="5001"
APP_DIR="/opt/jkcnsl-cache"

# --- 引数パース ---
while [[ $# -gt 0 ]]; do
    case "$1" in
        --mode)        MODE="$2";        shift 2 ;;
        --domain)      DOMAIN="$2";      shift 2 ;;
        --email)       EMAIL="$2";       shift 2 ;;
        --port)        PORT="$2";        shift 2 ;;
        --status-port) STATUS_PORT="$2"; shift 2 ;;
        *) echo "不明なオプション: $1"; exit 1 ;;
    esac
done

if [[ -z "$MODE" ]]; then
    echo "エラー: --mode ws または --mode wss を指定してください"
    exit 1
fi
if [[ "$MODE" == "wss" && -z "$DOMAIN" ]]; then
    echo "エラー: wss モードでは --domain が必要です"
    exit 1
fi
if [[ "$MODE" != "ws" && "$MODE" != "wss" ]]; then
    echo "エラー: --mode は ws または wss を指定してください"
    exit 1
fi
if [[ "$MODE" == "wss" && ! -f "$DEPLOY_DIR/nginx.conf" ]]; then
    echo "エラー: nginx.conf が見つかりません ($DEPLOY_DIR)"
    exit 1
fi

echo "モード: $MODE"
echo "プロジェクト: $PROJECT_CSPROJ"
echo "deploy ディレクトリ: $DEPLOY_DIR"

install_dotnet_sdk() {
    if command -v dotnet &>/dev/null && dotnet --list-sdks 2>/dev/null | grep -q '^8\.'; then
        echo ".NET SDK 8.x は既にインストール済みです"
        return
    fi

    echo ".NET SDK 8.0 を APT からインストールします"
    if apt install -y dotnet-sdk-8.0; then
        return
    fi

    echo "APT で dotnet-sdk-8.0 が見つからないため dotnet-install.sh にフォールバックします"
    apt install -y ca-certificates curl
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh
    mkdir -p /opt/dotnet
    /tmp/dotnet-install.sh --channel 8.0 --install-dir /opt/dotnet
    ln -sf /opt/dotnet/dotnet /usr/local/bin/dotnet
    export DOTNET_ROOT=/opt/dotnet
    export PATH="/opt/dotnet:$PATH"
    dotnet --info
}

# --- 1. パッケージインストール ---
apt update
if [[ "$MODE" == "wss" ]]; then
    apt install -y nginx certbot python3-certbot-nginx
else
    apt install -y ca-certificates curl
fi
install_dotnet_sdk

# --- 2. 実行ユーザー作成 ---
useradd -r -s /sbin/nologin jkcnsl || true

# --- 3. Node.js インストール（NodeSource）---
if ! command -v node &>/dev/null; then
    curl -fsSL https://deb.nodesource.com/setup_lts.x | bash -
    apt install -y nodejs
fi

# --- 3b. frontend build ---
PROJECT_DIR="$(dirname "$PROJECT_CSPROJ")"
CLIENT_DIR="$PROJECT_DIR/client"
if [ -d "$CLIENT_DIR" ]; then
    pushd "$CLIENT_DIR" > /dev/null
    npm ci && npm run build
    popd > /dev/null
fi

# --- 4. ビルドして配置 ---
dotnet publish "$PROJECT_CSPROJ" \
    -c Release -r linux-x64 \
    --self-contained true \
    /p:PublishSingleFile=true \
    /p:IncludeNativeLibrariesForSelfExtract=true \
    -o /tmp/jkcnsl-cache-publish

mkdir -p "$APP_DIR"
install /tmp/jkcnsl-cache-publish/jkcnsl-cache "$APP_DIR/jkcnsl-cache"
cp /tmp/jkcnsl-cache-publish/appsettings.json "$APP_DIR/appsettings.json"
if [ -d /tmp/jkcnsl-cache-publish/wwwroot ]; then
    cp -r /tmp/jkcnsl-cache-publish/wwwroot "$APP_DIR/"
fi

# --- 5. appsettings.json の各種設定を更新 ---
if [[ "$MODE" == "wss" ]]; then
    BIND_ADDR="127.0.0.1"
    PUBLIC_URL="wss://$DOMAIN"
else
    BIND_ADDR="0.0.0.0"
    LAN_IP=$(hostname -I | awk '{print $1}')
    PUBLIC_URL="ws://$LAN_IP:$PORT"
fi

sed -i "s|\"BindAddress\": \"[^\"]*\"|\"BindAddress\": \"$BIND_ADDR\"|"   "$APP_DIR/appsettings.json"
sed -i "s|\"MainPort\": [0-9]*|\"MainPort\": $PORT|"                       "$APP_DIR/appsettings.json"
sed -i "s|\"StatusPort\": [0-9]*|\"StatusPort\": $STATUS_PORT|"            "$APP_DIR/appsettings.json"
sed -i "s|\"PublicBaseUrl\": \"[^\"]*\"|\"PublicBaseUrl\": \"$PUBLIC_URL\"|" "$APP_DIR/appsettings.json"

chown -R jkcnsl:jkcnsl "$APP_DIR"

# --- 6. systemd サービス登録 ---
cp "$DEPLOY_DIR/jkcnsl-cache.service" /etc/systemd/system/jkcnsl-cache.service

systemctl daemon-reload
systemctl enable jkcnsl-cache
systemctl start jkcnsl-cache

# --- 7. nginx 設定（wss モードのみ）---
if [[ "$MODE" == "wss" ]]; then
    sed "s/your-vps.example.com/$DOMAIN/g" "$DEPLOY_DIR/nginx.conf" > "/etc/nginx/sites-available/$DOMAIN"
    ln -sf "/etc/nginx/sites-available/$DOMAIN" "/etc/nginx/sites-enabled/$DOMAIN"
    nginx -t
    systemctl reload nginx

    # --- 8. TLS 証明書取得（Let's Encrypt）---
    certbot --nginx -d "$DOMAIN" --non-interactive --agree-tos -m "$EMAIL"
    systemctl reload nginx
fi

echo ""
echo "セットアップ完了 (モード: $MODE)"
echo "PublicBaseUrl: $PUBLIC_URL"
echo "メインポート:   $PORT"
echo "ステータスポート: $STATUS_PORT"
echo ""
echo "appsettings.json を編集してチャンネルの上流URLを設定してください:"
echo "  $APP_DIR/appsettings.json"
echo ""
echo "設定後にサービスを再起動:"
echo "  systemctl restart jkcnsl-cache"
