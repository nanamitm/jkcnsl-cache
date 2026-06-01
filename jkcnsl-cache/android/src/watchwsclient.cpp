#include "watchwsclient.h"
#include <QJsonDocument>
#include <QJsonObject>
#include <QJsonArray>

WatchWsClient::WatchWsClient(QObject *parent)
    : QObject(parent)
{
    connect(&m_ws, &QWebSocket::connected,
            this, &WatchWsClient::onConnected);
    connect(&m_ws, &QWebSocket::disconnected,
            this, &WatchWsClient::onDisconnected);
    connect(&m_ws, &QWebSocket::textMessageReceived,
            this, &WatchWsClient::onTextMessage);
    connect(&m_ws, &QWebSocket::errorOccurred,
            this, [this](QAbstractSocket::SocketError) {
                if (m_shouldConnect && !m_reconnectTimer.isActive())
                    scheduleReconnect();
            });

    m_keepSeatTimer.setSingleShot(false);
    connect(&m_keepSeatTimer, &QTimer::timeout, this, [this]() {
        if (m_ws.state() == QAbstractSocket::ConnectedState)
            m_ws.sendTextMessage(QStringLiteral(R"({"type":"keepSeat"})"));
    });

    m_reconnectTimer.setSingleShot(true);
    connect(&m_reconnectTimer, &QTimer::timeout, this, [this]() {
        if (m_shouldConnect) {
            QString wsUrl = m_baseUrl;
            wsUrl.replace(QStringLiteral("https://"), QStringLiteral("wss://"));
            wsUrl.replace(QStringLiteral("http://"),  QStringLiteral("ws://"));
            wsUrl += QStringLiteral("/watch/") + m_channel;
            m_ws.open(QUrl(wsUrl));
        }
    });
}

void WatchWsClient::connectTo(const QString &serverBaseUrl,
                               const QString &channel,
                               const QString &userSession)
{
    m_ws.abort();
    m_keepSeatTimer.stop();
    m_reconnectTimer.stop();

    m_baseUrl     = serverBaseUrl;
    m_channel     = channel;
    m_userSession = userSession;
    m_shouldConnect  = true;
    m_reconnectMs    = 1000;
    m_vposBaseTime   = {};

    emit channelChanged();

    QString wsUrl = serverBaseUrl;
    wsUrl.replace(QStringLiteral("https://"), QStringLiteral("wss://"));
    wsUrl.replace(QStringLiteral("http://"),  QStringLiteral("ws://"));
    wsUrl += QStringLiteral("/watch/") + channel;
    m_ws.open(QUrl(wsUrl));
}

void WatchWsClient::disconnectNow() {
    m_shouldConnect = false;
    m_keepSeatTimer.stop();
    m_reconnectTimer.stop();
    m_ws.close();
}

// 接続後すぐにクライアントから初期メッセージを送る
void WatchWsClient::sendHandshake() {
    QJsonObject room;
    room[QStringLiteral("commentable")] = true;

    QJsonObject data;
    data[QStringLiteral("room")] = room;
    if (!m_userSession.isEmpty())
        data[QStringLiteral("cookie")] = m_userSession;

    QJsonObject msg;
    msg[QStringLiteral("data")] = data;

    m_ws.sendTextMessage(QString::fromUtf8(
        QJsonDocument(msg).toJson(QJsonDocument::Compact)));
}

void WatchWsClient::onConnected() {
    m_connected   = true;
    m_commentable = false;
    m_reconnectMs = 1000;
    emit connectedChanged();
    sendHandshake();
}

void WatchWsClient::onDisconnected() {
    const bool was = m_connected;
    m_connected   = false;
    m_commentable = false;
    m_keepSeatTimer.stop();
    if (was) {
        emit connectedChanged();
        emit commentableChanged();
    }
    if (m_shouldConnect) scheduleReconnect();
}

void WatchWsClient::onTextMessage(const QString &message) {
    const auto doc = QJsonDocument::fromJson(message.toUtf8());
    if (!doc.isObject()) return;
    const auto obj  = doc.object();
    const QString type = obj[QStringLiteral("type")].toString();
    const auto data = obj[QStringLiteral("data")].toObject();

    if (type == QLatin1String("seat")) {
        const int interval = data[QStringLiteral("keepIntervalSec")].toInt(30);
        m_keepSeatTimer.start(interval * 1000);

    } else if (type == QLatin1String("room")) {
        // vposBaseTime を取得
        const QString vposStr = data[QStringLiteral("vposBaseTime")].toString();
        if (!vposStr.isEmpty())
            m_vposBaseTime = QDateTime::fromString(vposStr, Qt::ISODate);

        // commentable は handshake 応答に含まれる場合もある
        if (!m_commentable) {
            m_commentable = true;
            emit commentableChanged();
        }

    } else if (type == QLatin1String("postCommentResult")) {
        // 投稿成功: chat.no を返す
        const auto chat = data[QStringLiteral("chat")].toObject();
        emit postSuccess(chat[QStringLiteral("no")].toInt());

    } else if (type == QLatin1String("error")) {
        const QString code = data[QStringLiteral("code")].toString();
        emit postError(code);
    }
}

void WatchWsClient::postComment(const QString &text,
                                 bool anonymous,
                                 const QString &color,
                                 const QString &size,
                                 const QString &position)
{
    if (!m_connected || text.trimmed().isEmpty()) return;

    QStringList mailParts;
    if (anonymous)        mailParts << QStringLiteral("184");
    if (!color.isEmpty()) mailParts << color;
    if (!size.isEmpty())  mailParts << size;
    if (!position.isEmpty()) mailParts << position;

    QJsonObject postData;
    postData[QStringLiteral("text")]        = text;
    postData[QStringLiteral("vpos")]        = currentVpos();
    postData[QStringLiteral("isAnonymous")] = anonymous;
    if (!color.isEmpty())    postData[QStringLiteral("color")]    = color;
    if (!size.isEmpty())     postData[QStringLiteral("size")]     = size;
    if (!position.isEmpty()) postData[QStringLiteral("position")] = position;
    if (!mailParts.isEmpty())
        postData[QStringLiteral("mail")] = mailParts.join(QLatin1Char(' '));

    QJsonObject msg;
    msg[QStringLiteral("type")] = QStringLiteral("postComment");
    msg[QStringLiteral("data")] = postData;

    m_ws.sendTextMessage(QString::fromUtf8(
        QJsonDocument(msg).toJson(QJsonDocument::Compact)));
}

qint64 WatchWsClient::currentVpos() const {
    if (!m_vposBaseTime.isValid()) return 0;
    const qint64 ms = m_vposBaseTime.toUTC().msecsTo(QDateTime::currentDateTimeUtc());
    return qMax(0LL, ms / 10);  // centiseconds
}

void WatchWsClient::scheduleReconnect() {
    m_reconnectTimer.start(m_reconnectMs);
    m_reconnectMs = qMin(m_reconnectMs * 2, 30'000);
}
