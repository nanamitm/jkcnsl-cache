#pragma once
#include <QObject>
#include <QNetworkAccessManager>
#include <QVariantMap>

class ScheduleService : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool        loading      READ isLoading   NOTIFY loadingChanged)
    Q_PROPERTY(QVariantMap scheduleData READ scheduleData NOTIFY scheduleDataChanged)
    Q_PROPERTY(QString     error        READ error        NOTIFY errorChanged)

public:
    explicit ScheduleService(QObject *parent = nullptr);

    bool        isLoading()    const { return m_loading; }
    QVariantMap scheduleData() const { return m_data; }
    QString     error()        const { return m_error; }

    Q_INVOKABLE void fetch(const QString &baseUrl, const QString &date = {});

signals:
    void loadingChanged();
    void scheduleDataChanged();
    void errorChanged();

private:
    void setLoading(bool v);
    void parseResponse(const QByteArray &data);

    QNetworkAccessManager m_nam;
    bool        m_loading = false;
    QVariantMap m_data;
    QString     m_error;
};
