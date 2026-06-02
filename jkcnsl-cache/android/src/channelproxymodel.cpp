#include "channelproxymodel.h"
#include "channelmodel.h"
#include <QJsonDocument>
#include <QJsonArray>

QVariantList ChannelProxyModel::getSourcesByVideo(const QString &video) const {
    auto *src = qobject_cast<ChannelModel *>(sourceModel());
    return src ? src->getSourcesByVideo(video) : QVariantList{};
}

ChannelProxyModel::ChannelProxyModel(QObject *parent)
    : QSortFilterProxyModel(parent)
    , m_settings(QStringLiteral("jkcnsl"), QStringLiteral("jkcnsl-qt"))
{
    setDynamicSortFilter(true);
    loadHidden();
}

void ChannelProxyModel::setSourceModel(QAbstractItemModel *model) {
    if (sourceModel()) {
        disconnect(sourceModel(), nullptr, this, nullptr);
    }

    QSortFilterProxyModel::setSourceModel(model);

    if (!model)
        return;

    const auto bumpRevision = [this]() {
        ++m_sourceRevision;
        emit sourceRevisionChanged();
    };
    connect(model, &QAbstractItemModel::modelReset, this, bumpRevision);
    connect(model, &QAbstractItemModel::dataChanged, this,
            [this](const QModelIndex &, const QModelIndex &, const QList<int> &roles) {
                if (roles.isEmpty()
                    || roles.contains(ChannelModel::SourcesRole)
                    || roles.contains(ChannelModel::RunningRole)) {
                    ++m_sourceRevision;
                    emit sourceRevisionChanged();
                }
            });
}

void ChannelProxyModel::loadHidden() {
    const QByteArray raw =
        m_settings.value(QStringLiteral("hiddenChannels"), QStringLiteral("[]"))
            .toString().toUtf8();
    m_hiddenChannels.clear();
    for (const auto &v : QJsonDocument::fromJson(raw).array())
        m_hiddenChannels.insert(v.toString());
}

void ChannelProxyModel::saveHidden() {
    QJsonArray arr;
    for (const auto &v : std::as_const(m_hiddenChannels)) arr.append(v);
    m_settings.setValue(QStringLiteral("hiddenChannels"),
        QString::fromUtf8(QJsonDocument(arr).toJson(QJsonDocument::Compact)));
}

// ─── セッター ─────────────────────────────────────────────────────────

void ChannelProxyModel::setSearchText(const QString &t) {
    if (m_searchText == t) return;
    m_searchText = t;
    beginFilterChange(); endFilterChange();
    emit searchTextChanged();
}

void ChannelProxyModel::setBsFilter(int f) {
    if (m_bsFilter == f) return;
    m_bsFilter = f;
    beginFilterChange(); endFilterChange();
    emit bsFilterChanged();
}

void ChannelProxyModel::setSortByForce(bool s) {
    if (m_sortByForce == s) return;
    m_sortByForce = s;
    s ? sort(0) : sort(-1);
    emit sortByForceChanged();
}

void ChannelProxyModel::setManageMode(bool m) {
    if (m_manageMode == m) return;
    m_manageMode = m;
    beginFilterChange(); endFilterChange();
    emit manageModeChanged();
}

void ChannelProxyModel::setTerrestrialCollapsed(bool c) {
    if (m_terrestrialCollapsed == c) return;
    m_terrestrialCollapsed = c;
    beginFilterChange(); endFilterChange();
    emit terrestrialCollapsedChanged();
}

void ChannelProxyModel::setBsCollapsed(bool c) {
    if (m_bsCollapsed == c) return;
    m_bsCollapsed = c;
    beginFilterChange(); endFilterChange();
    emit bsCollapsedChanged();
}

// ─── 非表示管理 ───────────────────────────────────────────────────────

bool ChannelProxyModel::isChannelHidden(const QString &video) const {
    return m_hiddenChannels.contains(video);
}

void ChannelProxyModel::setChannelHidden(const QString &video, bool hidden) {
    if (hidden == m_hiddenChannels.contains(video)) return;
    if (hidden) m_hiddenChannels.insert(video);
    else        m_hiddenChannels.remove(video);
    saveHidden();
    beginFilterChange(); endFilterChange();
    emit hiddenChannelsChanged();
}

void ChannelProxyModel::clearHidden() {
    if (m_hiddenChannels.isEmpty()) return;
    m_hiddenChannels.clear();
    saveHidden();
    beginFilterChange(); endFilterChange();
    emit hiddenChannelsChanged();
}

// ─── フィルター / ソート ──────────────────────────────────────────────

bool ChannelProxyModel::filterAcceptsRow(int sourceRow, const QModelIndex &sourceParent) const {
    const auto idx = sourceModel()->index(sourceRow, 0, sourceParent);

    // 管理モードでは折りたたみ・非表示を無視して全チャンネル表示
    if (!m_manageMode) {
        const QString video = sourceModel()->data(idx, ChannelModel::VideoRole).toString();
        if (m_hiddenChannels.contains(video)) return false;

        const bool bs = sourceModel()->data(idx, ChannelModel::BsRole).toBool();
        if (bs  && m_bsCollapsed)           return false;
        if (!bs && m_terrestrialCollapsed)  return false;
    }

    if (!m_searchText.isEmpty()) {
        const QString name = sourceModel()->data(idx, ChannelModel::NameRole).toString();
        if (!name.contains(m_searchText, Qt::CaseInsensitive)) return false;
    }

    if (m_bsFilter != 0) {
        const bool bs = sourceModel()->data(idx, ChannelModel::BsRole).toBool();
        if (m_bsFilter == 1 && bs)  return false;
        if (m_bsFilter == 2 && !bs) return false;
    }

    return true;
}

bool ChannelProxyModel::lessThan(const QModelIndex &left, const QModelIndex &right) const {
    if (m_sortByForce) {
        const int lf = sourceModel()->data(left,  ChannelModel::ForceRole).toInt();
        const int rf = sourceModel()->data(right, ChannelModel::ForceRole).toInt();
        return lf > rf;
    }
    return QSortFilterProxyModel::lessThan(left, right);
}
