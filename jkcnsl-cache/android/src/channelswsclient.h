#pragma once
#include <QObject>
#include <QWebSocket>
#include <QTimer>
#include <QJsonObject>

class ChannelsWsClient : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool connected READ isConnected NOTIFY connectedChanged)

public:
    explicit ChannelsWsClient(QObject *parent = nullptr);
    bool isConnected() const { return m_connected; }

public slots:
    void connectTo(const QString &serverBaseUrl);
    void disconnectNow();

signals:
    void snapshotReceived(const QJsonObject &msg);
    void statsReceived(const QJsonObject &msg);
    void programsReceived(const QJsonObject &msg);
    void connectedChanged();
    void errorMessage(const QString &text);

private slots:
    void onConnected();
    void onDisconnected();
    void onTextMessage(const QString &message);
    void onError(QAbstractSocket::SocketError err);

private:
    void scheduleReconnect();
    QString buildWsUrl() const;

    QWebSocket m_ws;
    QTimer     m_reconnectTimer;
    QString    m_baseUrl;
    bool       m_connected    = false;
    bool       m_shouldConnect = false;
    int        m_reconnectMs  = 1000;
};
