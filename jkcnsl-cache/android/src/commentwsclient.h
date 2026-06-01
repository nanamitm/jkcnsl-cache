#pragma once
#include <QObject>
#include <QWebSocket>
#include <QTimer>

class CommentWsClient : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool connected READ isConnected NOTIFY connectedChanged)
    Q_PROPERTY(QString channel READ channel NOTIFY channelChanged)

public:
    explicit CommentWsClient(QObject *parent = nullptr);
    bool    isConnected() const { return m_connected; }
    QString channel()     const { return m_channel; }

public slots:
    void connectTo(const QString &serverBaseUrl, const QString &channel);
    void disconnectNow();

signals:
    void chatReceived(const QVariantMap &chat);
    void connectedChanged();
    void channelChanged();

private slots:
    void onConnected();
    void onDisconnected();
    void onTextMessage(const QString &message);

private:
    void scheduleReconnect();
    void doConnect();

    QWebSocket m_ws;
    QTimer     m_reconnectTimer;
    QString    m_channel;
    QString    m_baseUrl;
    bool       m_connected     = false;
    bool       m_shouldConnect = false;
    int        m_reconnectMs   = 1000;
};
