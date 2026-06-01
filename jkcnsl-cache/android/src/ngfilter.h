#pragma once
#include <QObject>
#include <QSet>
#include <QStringList>
#include <QSettings>

class NgFilter : public QObject {
    Q_OBJECT
    Q_PROPERTY(QStringList users READ users NOTIFY usersChanged)

public:
    explicit NgFilter(QObject *parent = nullptr);

    QStringList users() const;
    Q_INVOKABLE bool isBlocked(const QString &userId) const;
    Q_INVOKABLE void addUser(const QString &userId);
    Q_INVOKABLE void removeUser(const QString &userId);

signals:
    void usersChanged();

private:
    void load();
    void save();

    QSet<QString> m_users;
    QSettings     m_settings;
};
