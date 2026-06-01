#include "ngfilter.h"
#include <QJsonDocument>
#include <QJsonArray>

NgFilter::NgFilter(QObject *parent)
    : QObject(parent)
    , m_settings(QStringLiteral("jkcnsl"), QStringLiteral("jkcnsl-qt"))
{
    load();
}

void NgFilter::load() {
    const QByteArray raw =
        m_settings.value(QStringLiteral("ngUsers"), QStringLiteral("[]")).toString().toUtf8();
    const auto arr = QJsonDocument::fromJson(raw).array();
    m_users.clear();
    for (const auto &v : arr)
        m_users.insert(v.toString());
}

void NgFilter::save() {
    QJsonArray arr;
    for (const auto &u : std::as_const(m_users))
        arr.append(u);
    m_settings.setValue(QStringLiteral("ngUsers"),
        QString::fromUtf8(QJsonDocument(arr).toJson(QJsonDocument::Compact)));
}

QStringList NgFilter::users() const {
    QStringList list(m_users.begin(), m_users.end());
    list.sort();
    return list;
}

bool NgFilter::isBlocked(const QString &userId) const {
    return m_users.contains(userId);
}

void NgFilter::addUser(const QString &userId) {
    if (userId.isEmpty() || m_users.contains(userId)) return;
    m_users.insert(userId);
    save();
    emit usersChanged();
}

void NgFilter::removeUser(const QString &userId) {
    if (!m_users.remove(userId)) return;
    save();
    emit usersChanged();
}
