#pragma once
#include <QObject>
#include <QNetworkAccessManager>

class LoginService : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool    loading  READ isLoading NOTIFY loadingChanged)
    Q_PROPERTY(QString error    READ error     NOTIFY errorChanged)

public:
    explicit LoginService(QObject *parent = nullptr);

    bool    isLoading() const { return m_loading; }
    QString error()     const { return m_error; }

    // メール+パスワードでログイン (mfaTrustedDeviceToken は省略可)
    Q_INVOKABLE void login(const QString &baseUrl,
                           const QString &email,
                           const QString &password,
                           const QString &mfaTrustedDeviceToken = {});

    // 2FA ワンタイムパスワード送信
    Q_INVOKABLE void submitMfa(const QString &baseUrl,
                               const QString &mfaToken,
                               const QString &otp,
                               bool trustDevice = true);

signals:
    void loadingChanged();
    void errorChanged();
    // 2FA が必要なとき: mfaToken を渡す
    void mfaRequired(const QString &mfaToken);
    // ログイン成功: userSession, mfaTrustedDeviceToken を渡す
    void loginSuccess(const QString &userSession,
                      const QString &mfaTrustedDeviceToken);

private:
    void setLoading(bool v);
    void setError(const QString &e);
    void handleLoginReply(QNetworkReply *reply);

    QNetworkAccessManager m_nam;
    bool    m_loading = false;
    QString m_error;
};
