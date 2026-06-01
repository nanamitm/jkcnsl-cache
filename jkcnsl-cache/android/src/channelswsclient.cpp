#include "channelswsclient.h"
#include <QJsonDocument>

ChannelsWsClient::ChannelsWsClient(QObject *parent)
    : QObject(parent)
{
    connect(&m_ws, &QWebSocket::connected,
            this, &ChannelsWsClient::onConnected);
    connect(&m_ws, &QWebSocket::disconnected,
            this, &ChannelsWsClient::onDisconnected);
    connect(&m_ws, &QWebSocket::textMessageReceived,
            this, &ChannelsWsClient::onTextMessage);
    connect(&m_ws, &QWebSocket::errorOccurred,
            this, &ChannelsWsClient::onError);

    m_reconnectTimer.setSingleShot(true);
    connect(&m_reconnectTimer, &QTimer::timeout, this, [this]() {
        if (m_shouldConnect)
            m_ws.open(QUrl(buildWsUrl()));
    });
}

QString ChannelsWsClient::buildWsUrl() const {
    QString url = m_baseUrl;
    url.replace(QStringLiteral("https://"), QStringLiteral("wss://"));
    url.replace(QStringLiteral("http://"),  QStringLiteral("ws://"));
    return url + QStringLiteral("/api/channels/ws");
}

void ChannelsWsClient::connectTo(const QString &serverBaseUrl) {
    m_baseUrl = serverBaseUrl;
    m_shouldConnect = true;
    m_reconnectMs = 1000;
    m_reconnectTimer.stop();
    m_ws.abort();
    m_ws.open(QUrl(buildWsUrl()));
}

void ChannelsWsClient::disconnectNow() {
    m_shouldConnect = false;
    m_reconnectTimer.stop();
    m_ws.close();
}

void ChannelsWsClient::onConnected() {
    m_connected = true;
    m_reconnectMs = 1000;
    emit connectedChanged();
}

void ChannelsWsClient::onDisconnected() {
    const bool was = m_connected;
    m_connected = false;
    if (was) emit connectedChanged();
    if (m_shouldConnect) scheduleReconnect();
}

void ChannelsWsClient::onTextMessage(const QString &message) {
    const auto doc = QJsonDocument::fromJson(message.toUtf8());
    if (!doc.isObject()) return;
    const auto obj = doc.object();
    const QString type = obj[QStringLiteral("type")].toString();
    if (type == QLatin1String("snapshot"))
        emit snapshotReceived(obj);
    else if (type == QLatin1String("stats"))
        emit statsReceived(obj);
    else if (type == QLatin1String("programs"))
        emit programsReceived(obj);
}

void ChannelsWsClient::onError(QAbstractSocket::SocketError) {
    emit errorMessage(m_ws.errorString());
    if (m_shouldConnect && !m_reconnectTimer.isActive())
        scheduleReconnect();
}

void ChannelsWsClient::scheduleReconnect() {
    m_reconnectTimer.start(m_reconnectMs);
    m_reconnectMs = qMin(m_reconnectMs * 2, 30'000);
}
