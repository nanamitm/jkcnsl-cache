#include "loginservice.h"
#include <QJsonDocument>
#include <QJsonObject>
#include <QNetworkReply>
#include <QNetworkRequest>

LoginService::LoginService(QObject *parent)
    : QObject(parent)
{}

void LoginService::login(const QString &baseUrl,
                         const QString &email,
                         const QString &password,
                         const QString &mfaTrustedDeviceToken)
{
    if (m_loading) return;
    setLoading(true);
    setError({});

    QJsonObject body;
    body[QStringLiteral("email")]    = email;
    body[QStringLiteral("password")] = password;
    if (!mfaTrustedDeviceToken.isEmpty())
        body[QStringLiteral("mfaTrustedDeviceToken")] = mfaTrustedDeviceToken;

    QNetworkRequest req(QUrl(baseUrl + QStringLiteral("/api/login")));
    req.setHeader(QNetworkRequest::ContentTypeHeader, QStringLiteral("application/json"));
    auto *reply = m_nam.post(req, QJsonDocument(body).toJson(QJsonDocument::Compact));
    connect(reply, &QNetworkReply::finished, this, [this, reply]() {
        reply->deleteLater();
        setLoading(false);
        handleLoginReply(reply);
    });
}

void LoginService::submitMfa(const QString &baseUrl,
                              const QString &mfaToken,
                              const QString &otp,
                              bool trustDevice)
{
    if (m_loading) return;
    setLoading(true);
    setError({});

    QJsonObject body;
    body[QStringLiteral("mfaToken")]    = mfaToken;
    body[QStringLiteral("otp")]         = otp;
    body[QStringLiteral("trustDevice")] = trustDevice;

    QNetworkRequest req(QUrl(baseUrl + QStringLiteral("/api/login/mfa")));
    req.setHeader(QNetworkRequest::ContentTypeHeader, QStringLiteral("application/json"));
    auto *reply = m_nam.post(req, QJsonDocument(body).toJson(QJsonDocument::Compact));
    connect(reply, &QNetworkReply::finished, this, [this, reply]() {
        reply->deleteLater();
        setLoading(false);
        handleLoginReply(reply);
    });
}

void LoginService::handleLoginReply(QNetworkReply *reply) {
    if (reply->error() != QNetworkReply::NoError) {
        setError(reply->errorString());
        return;
    }
    const auto obj = QJsonDocument::fromJson(reply->readAll()).object();

    if (obj.contains(QStringLiteral("error"))) {
        setError(obj[QStringLiteral("error")].toString());
        return;
    }
    if (obj[QStringLiteral("mfaRequired")].toBool()) {
        emit mfaRequired(obj[QStringLiteral("mfaToken")].toString());
        return;
    }
    emit loginSuccess(
        obj[QStringLiteral("userSession")].toString(),
        obj[QStringLiteral("mfaTrustedDeviceToken")].toString()
    );
}

void LoginService::setLoading(bool v) {
    if (m_loading == v) return;
    m_loading = v;
    emit loadingChanged();
}

void LoginService::setError(const QString &e) {
    if (m_error == e) return;
    m_error = e;
    emit errorChanged();
}
