#include "commentmodel.h"

CommentModel::CommentModel(QObject *parent)
    : QAbstractListModel(parent)
{}

int CommentModel::rowCount(const QModelIndex &parent) const {
    return parent.isValid() ? 0 : m_comments.size();
}

QVariant CommentModel::data(const QModelIndex &index, int role) const {
    if (!index.isValid() || index.row() >= m_comments.size()) return {};
    const auto &c = m_comments.at(index.row());
    switch (role) {
    case NoRole:        return c[QStringLiteral("no")];
    case ContentRole:   return c[QStringLiteral("content")];
    case UserIdRole:    return c[QStringLiteral("userId")];
    case PremiumRole:   return c[QStringLiteral("premium")];
    case AnonymityRole: return c[QStringLiteral("anonymity")];
    case MailRole:      return c[QStringLiteral("mail")];
    case DateRole:      return c[QStringLiteral("date")];
    }
    return {};
}

QHash<int, QByteArray> CommentModel::roleNames() const {
    return {
        {NoRole,        "no"},
        {ContentRole,   "content"},
        {UserIdRole,    "userId"},
        {PremiumRole,   "premium"},
        {AnonymityRole, "anonymity"},
        {MailRole,      "mail"},
        {DateRole,      "date"},
    };
}

void CommentModel::clear() {
    if (m_comments.isEmpty()) return;
    beginResetModel();
    m_comments.clear();
    endResetModel();
    emit countChanged();
}

void CommentModel::addChat(const QVariantMap &chat) {
    if (chat[QStringLiteral("content")].toString().isEmpty()) return;

    if (m_comments.size() >= MaxComments) {
        beginRemoveRows({}, 0, 0);
        m_comments.removeFirst();
        endRemoveRows();
    }
    const int last = m_comments.size();
    beginInsertRows({}, last, last);
    m_comments.append(chat);
    endInsertRows();
    emit countChanged();
}
