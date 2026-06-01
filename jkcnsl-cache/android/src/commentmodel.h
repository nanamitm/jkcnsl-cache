#pragma once
#include <QAbstractListModel>
#include <QVariantMap>

class CommentModel : public QAbstractListModel {
    Q_OBJECT
    Q_PROPERTY(int count READ rowCount NOTIFY countChanged)

public:
    enum Roles {
        NoRole = Qt::UserRole + 1,
        ContentRole,
        UserIdRole,
        PremiumRole,
        AnonymityRole,
        MailRole,
        DateRole,
    };

    explicit CommentModel(QObject *parent = nullptr);

    int      rowCount(const QModelIndex &parent = QModelIndex()) const override;
    QVariant data(const QModelIndex &index, int role = Qt::DisplayRole) const override;
    QHash<int, QByteArray> roleNames() const override;

    Q_INVOKABLE void clear();

public slots:
    void addChat(const QVariantMap &chat);

signals:
    void countChanged();

private:
    static constexpr int MaxComments = 500;
    QList<QVariantMap> m_comments;
};
