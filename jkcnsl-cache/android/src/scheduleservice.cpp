#include "scheduleservice.h"
#include <QJsonDocument>
#include <QJsonObject>
#include <QJsonArray>
#include <QNetworkRequest>
#include <QNetworkReply>
#include <QUrl>
#include <QUrlQuery>

ScheduleService::ScheduleService(QObject *parent)
    : QObject(parent)
{}

void ScheduleService::fetch(const QString &baseUrl, const QString &date) {
    if (m_loading) return;
    setLoading(true);
    m_error.clear();
    emit errorChanged();

    QUrl url(baseUrl + QStringLiteral("/api/programs/schedule"));
    if (!date.isEmpty()) {
        QUrlQuery q;
        q.addQueryItem(QStringLiteral("date"), date);
        url.setQuery(q);
    }

    auto *reply = m_nam.get(QNetworkRequest(url));
    connect(reply, &QNetworkReply::finished, this, [this, reply]() {
        reply->deleteLater();
        setLoading(false);
        if (reply->error() != QNetworkReply::NoError) {
            m_error = reply->errorString();
            emit errorChanged();
            return;
        }
        parseResponse(reply->readAll());
    });
}

void ScheduleService::setLoading(bool v) {
    if (m_loading == v) return;
    m_loading = v;
    emit loadingChanged();
}

static QVariantMap programToVariant(const QJsonObject &o) {
    return {
        {QStringLiteral("title"),     o[QStringLiteral("title")].toString()},
        {QStringLiteral("startAt"),   o[QStringLiteral("startAt")].toString()},
        {QStringLiteral("endAt"),     o[QStringLiteral("endAt")].toString()},
        {QStringLiteral("genreCode"), o[QStringLiteral("genreCode")].toString()},
        {QStringLiteral("genreName"), o[QStringLiteral("genreName")].toString()},
        {QStringLiteral("source"),    o[QStringLiteral("source")].toString()},
    };
}

void ScheduleService::parseResponse(const QByteArray &data) {
    const auto doc = QJsonDocument::fromJson(data);
    if (!doc.isObject()) return;
    const auto root = doc.object();

    QVariantList channels;
    for (const auto &cv : root[QStringLiteral("channels")].toArray()) {
        const auto ch = cv.toObject();
        QVariantList programs;
        for (const auto &pv : ch[QStringLiteral("programs")].toArray())
            programs.append(programToVariant(pv.toObject()));
        channels.append(QVariantMap{
            {QStringLiteral("id"),       ch[QStringLiteral("id")].toInt()},
            {QStringLiteral("video"),    ch[QStringLiteral("video")].toString()},
            {QStringLiteral("name"),     ch[QStringLiteral("name")].toString()},
            {QStringLiteral("bs"),       ch[QStringLiteral("bs")].toBool()},
            {QStringLiteral("programs"), programs},
        });
    }

    m_data = {
        {QStringLiteral("date"),               root[QStringLiteral("date")].toString()},
        {QStringLiteral("broadcastStartHour"), root[QStringLiteral("broadcastStartHour")].toInt(4)},
        {QStringLiteral("startAt"),            root[QStringLiteral("startAt")].toString()},
        {QStringLiteral("endAt"),              root[QStringLiteral("endAt")].toString()},
        {QStringLiteral("loaded"),             root[QStringLiteral("loaded")].toBool()},
        {QStringLiteral("channels"),           channels},
    };
    emit scheduleDataChanged();
}
