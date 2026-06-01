#include "commentwsclient.h"
#include <QJsonDocument>
#include <QJsonObject>
#include <QNetworkRequest>

CommentWsClient::CommentWsClient(QObject *parent)
    : QObject(parent)
{
    connect(&m_ws, &QWebSocket::connected,
            this, &CommentWsClient::onConnected);
    connect(&m_ws, &QWebSocket::disconnected,
            this, &CommentWsClient::onDisconnected);
    connect(&m_ws, &QWebSocket::textMessageReceived,
            this, &CommentWsClient::onTextMessage);
    connect(&m_ws, &QWebSocket::errorOccurred,
            this, [this](QAbstractSocket::SocketError) {
                if (m_shouldConnect && !m_reconnectTimer.isActive())
                    scheduleReconnect();
            });

    m_reconnectTimer.setSingleShot(true);
    connect(&m_reconnectTimer, &QTimer::timeout, this, [this]() {
        if (m_shouldConnect) doConnect();
    });
}

void CommentWsClient::doConnect() {
    QString wsUrl = m_baseUrl;
    wsUrl.replace(QStringLiteral("https://"), QStringLiteral("wss://"));
    wsUrl.replace(QStringLiteral("http://"),  QStringLiteral("ws://"));
    wsUrl += QStringLiteral("/comment/") + m_channel;
    QNetworkRequest req{QUrl(wsUrl)};
    req.setRawHeader("Sec-WebSocket-Protocol", "msg.nicovideo.jp#json");
    m_ws.open(req);
}

void CommentWsClient::connectTo(const QString &serverBaseUrl, const QString &channel) {
    m_ws.abort();
    m_reconnectTimer.stop();
    m_baseUrl  = serverBaseUrl;
    m_channel  = channel;
    m_shouldConnect = true;
    m_reconnectMs   = 1000;
    emit channelChanged();
    doConnect();
}

void CommentWsClient::disconnectNow() {
    m_shouldConnect = false;
    m_reconnectTimer.stop();
    m_ws.close();
}

void CommentWsClient::onConnected() {
    m_connected = true;
    m_reconnectMs = 1000;
    emit connectedChanged();
}

void CommentWsClient::onDisconnected() {
    const bool was = m_connected;
    m_connected = false;
    if (was) emit connectedChanged();
    if (m_shouldConnect) scheduleReconnect();
}

void CommentWsClient::onTextMessage(const QString &message) {
    const auto doc = QJsonDocument::fromJson(message.toUtf8());
    if (!doc.isObject()) return;
    const auto obj = doc.object();
    if (!obj.contains(QStringLiteral("chat"))) return;

    const auto chat = obj[QStringLiteral("chat")].toObject();
    QVariantMap map;
    map[QStringLiteral("thread")]    = chat[QStringLiteral("thread")].toString();
    map[QStringLiteral("no")]        = chat[QStringLiteral("no")].toInt();
    map[QStringLiteral("vpos")]      = chat[QStringLiteral("vpos")].toInt();
    map[QStringLiteral("date")]      = static_cast<qint64>(chat[QStringLiteral("date")].toDouble());
    map[QStringLiteral("userId")]    = chat[QStringLiteral("user_id")].toString();
    map[QStringLiteral("premium")]   = chat[QStringLiteral("premium")].toInt();
    map[QStringLiteral("anonymity")] = chat[QStringLiteral("anonymity")].toInt();
    map[QStringLiteral("content")]   = chat[QStringLiteral("content")].toString();
    map[QStringLiteral("mail")]      = chat[QStringLiteral("mail")].toString();
    emit chatReceived(map);
}

void CommentWsClient::scheduleReconnect() {
    m_reconnectTimer.start(m_reconnectMs);
    m_reconnectMs = qMin(m_reconnectMs * 2, 30'000);
}
