#include "appsettings.h"
#include <QtMath>

AppSettings::AppSettings(QObject *parent)
    : QObject(parent)
    , m_settings(QStringLiteral("jkcnsl"), QStringLiteral("jkcnsl-qt"))
{}

QString AppSettings::serverUrl() const {
    return m_settings.value(QStringLiteral("serverUrl"), QStringLiteral("http://localhost:5000")).toString();
}

void AppSettings::setServerUrl(const QString &url) {
    if (serverUrl() == url) return;
    m_settings.setValue(QStringLiteral("serverUrl"), url);
    emit serverUrlChanged();
}

QString AppSettings::theme() const {
    return m_settings.value(QStringLiteral("theme"), QStringLiteral("navy")).toString();
}

void AppSettings::setTheme(const QString &t) {
    if (theme() == t) return;
    m_settings.setValue(QStringLiteral("theme"), t);
    emit themeChanged();
}

double AppSettings::fontScale() const {
    return m_settings.value(QStringLiteral("fontScale"), 1.0).toDouble();
}

void AppSettings::setFontScale(double scale) {
    if (qFuzzyCompare(fontScale(), scale)) return;
    m_settings.setValue(QStringLiteral("fontScale"), scale);
    emit fontScaleChanged();
}

QString AppSettings::mfaTrustedDeviceToken() const {
    return m_settings.value(QStringLiteral("mfaTrustedDeviceToken")).toString();
}

void AppSettings::setMfaTrustedDeviceToken(const QString &token) {
    if (mfaTrustedDeviceToken() == token) return;
    m_settings.setValue(QStringLiteral("mfaTrustedDeviceToken"), token);
    emit mfaTrustedDeviceTokenChanged();
}

QString AppSettings::userSession() const {
    return m_settings.value(QStringLiteral("userSession")).toString();
}

void AppSettings::setUserSession(const QString &session) {
    if (userSession() == session) return;
    m_settings.setValue(QStringLiteral("userSession"), session);
    emit userSessionChanged();
}

int AppSettings::scrollSpeed() const {
    return qBound(2000, m_settings.value(QStringLiteral("scrollSpeed"), 7000).toInt(), 12000);
}
void AppSettings::setScrollSpeed(int ms) {
    ms = qBound(2000, ms, 12000);
    if (scrollSpeed() == ms) return;
    m_settings.setValue(QStringLiteral("scrollSpeed"), ms);
    emit scrollSpeedChanged();
}

double AppSettings::scrollRange() const {
    return qBound(0.3, m_settings.value(QStringLiteral("scrollRange"), 0.5).toDouble(), 1.0);
}
void AppSettings::setScrollRange(double r) {
    r = qBound(0.3, r, 1.0);
    if (qFuzzyCompare(scrollRange(), r)) return;
    m_settings.setValue(QStringLiteral("scrollRange"), r);
    emit scrollRangeChanged();
}

bool AppSettings::genreColorEnabled() const {
    return m_settings.value(QStringLiteral("genreColorEnabled"), true).toBool();
}
void AppSettings::setGenreColorEnabled(bool v) {
    if (genreColorEnabled() == v) return;
    m_settings.setValue(QStringLiteral("genreColorEnabled"), v);
    emit genreColorEnabledChanged();
}
