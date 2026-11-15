#pragma once
#include <QObject>
#include <QWebSocket>
#include <QTimer>
#include <QVariantList>

class QJsonObject;

class CommentWsClient : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool connected READ isConnected NOTIFY connectedChanged)
    Q_PROPERTY(QString channel READ channel NOTIFY channelChanged)
    // 5ch ソースのとき、サーバーが選んでいるスレの一覧（{id,title,url,programMatch,finished} の配列）
    Q_PROPERTY(QVariantList nichanThreads READ nichanThreads NOTIFY nichanThreadsChanged)

public:
    explicit CommentWsClient(QObject *parent = nullptr);
    bool    isConnected() const { return m_connected; }
    QString channel()     const { return m_channel; }
    QVariantList nichanThreads() const { return m_nichanThreads; }

public slots:
    void connectTo(const QString &serverBaseUrl, const QString &channel);
    void disconnectNow();

signals:
    void chatReceived(const QVariantMap &chat);
    void connectedChanged();
    void channelChanged();
    void nichanThreadsChanged();

private slots:
    void onConnected();
    void onDisconnected();
    void onTextMessage(const QString &message);

private:
    void scheduleReconnect();
    void doConnect();
    void handleNichanThreads(const QJsonObject &message);
    void clearNichanThreads();

    QWebSocket m_ws;
    QVariantList m_nichanThreads;
    QTimer     m_reconnectTimer;
    QString    m_channel;
    QString    m_baseUrl;
    bool       m_connected     = false;
    bool       m_shouldConnect = false;
    int        m_reconnectMs   = 1000;
};
