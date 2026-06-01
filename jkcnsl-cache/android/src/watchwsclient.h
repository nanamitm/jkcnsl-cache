#pragma once
#include <QObject>
#include <QWebSocket>
#include <QTimer>
#include <QDateTime>

class WatchWsClient : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool    connected   READ isConnected   NOTIFY connectedChanged)
    Q_PROPERTY(bool    commentable READ isCommentable NOTIFY commentableChanged)
    Q_PROPERTY(QString channel     READ channel       NOTIFY channelChanged)

public:
    explicit WatchWsClient(QObject *parent = nullptr);

    bool    isConnected()   const { return m_connected; }
    bool    isCommentable() const { return m_commentable; }
    QString channel()       const { return m_channel; }

public slots:
    void connectTo(const QString &serverBaseUrl,
                   const QString &channel,
                   const QString &userSession = {});
    void disconnectNow();

    // text: コメント本文
    // anonymous: true = 184 (匿名)
    // color/size: "red" "blue" "big" "small" 等 (空文字なら省略)
    Q_INVOKABLE void postComment(const QString &text,
                                  bool anonymous        = true,
                                  const QString &color    = {},
                                  const QString &size     = {},
                                  const QString &position = {});

signals:
    void connectedChanged();
    void commentableChanged();
    void channelChanged();
    void postSuccess(int commentNo);        // 投稿成功 (コメント番号)
    void postError(const QString &code);   // 投稿失敗 (エラーコード)

private slots:
    void onConnected();
    void onDisconnected();
    void onTextMessage(const QString &message);

private:
    void sendHandshake();
    void scheduleReconnect();
    qint64 currentVpos() const;

    QWebSocket m_ws;
    QTimer     m_keepSeatTimer;
    QTimer     m_reconnectTimer;
    QString    m_channel;
    QString    m_baseUrl;
    QString    m_userSession;
    bool       m_connected     = false;
    bool       m_commentable   = false;
    bool       m_shouldConnect = false;
    QDateTime  m_vposBaseTime;
    int        m_reconnectMs   = 1000;
};
