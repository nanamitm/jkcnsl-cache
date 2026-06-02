#pragma once
#include <QObject>
#include <QSettings>

class AppSettings : public QObject {
    Q_OBJECT
    Q_PROPERTY(QString serverUrl READ serverUrl WRITE setServerUrl NOTIFY serverUrlChanged)
    Q_PROPERTY(QString theme     READ theme     WRITE setTheme     NOTIFY themeChanged)
    Q_PROPERTY(double  fontScale             READ fontScale             WRITE setFontScale             NOTIFY fontScaleChanged)
    Q_PROPERTY(QString mfaTrustedDeviceToken READ mfaTrustedDeviceToken WRITE setMfaTrustedDeviceToken NOTIFY mfaTrustedDeviceTokenChanged)
    Q_PROPERTY(QString userSession           READ userSession           WRITE setUserSession           NOTIFY userSessionChanged)
    Q_PROPERTY(int     scrollSpeed          READ scrollSpeed          WRITE setScrollSpeed          NOTIFY scrollSpeedChanged)
    Q_PROPERTY(double  scrollRange          READ scrollRange          WRITE setScrollRange          NOTIFY scrollRangeChanged)
    Q_PROPERTY(bool    genreColorEnabled    READ genreColorEnabled    WRITE setGenreColorEnabled    NOTIFY genreColorEnabledChanged)
    Q_PROPERTY(bool    commentOverlayMode   READ commentOverlayMode   WRITE setCommentOverlayMode   NOTIFY commentOverlayModeChanged)

public:
    explicit AppSettings(QObject *parent = nullptr);

    QString serverUrl() const;
    void    setServerUrl(const QString &url);

    QString theme() const;
    void    setTheme(const QString &t);

    double fontScale() const;
    void   setFontScale(double scale);

    QString mfaTrustedDeviceToken() const;
    void    setMfaTrustedDeviceToken(const QString &token);

    QString userSession() const;
    void    setUserSession(const QString &session);

    int    scrollSpeed() const;
    void   setScrollSpeed(int ms);
    double scrollRange() const;
    void   setScrollRange(double r);
    bool   genreColorEnabled() const;
    void   setGenreColorEnabled(bool v);
    bool   commentOverlayMode() const;
    void   setCommentOverlayMode(bool v);

signals:
    void serverUrlChanged();
    void themeChanged();
    void fontScaleChanged();
    void mfaTrustedDeviceTokenChanged();
    void userSessionChanged();
    void scrollSpeedChanged();
    void scrollRangeChanged();
    void genreColorEnabledChanged();
    void commentOverlayModeChanged();

private:
    QSettings m_settings;
};
