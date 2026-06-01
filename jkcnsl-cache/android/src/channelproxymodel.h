#pragma once
#include <QSortFilterProxyModel>
#include <QSet>
#include <QSettings>

class ChannelProxyModel : public QSortFilterProxyModel {
    Q_OBJECT
    Q_PROPERTY(QString searchText  READ searchText  WRITE setSearchText  NOTIFY searchTextChanged)
    Q_PROPERTY(int     bsFilter    READ bsFilter    WRITE setBsFilter    NOTIFY bsFilterChanged)
    Q_PROPERTY(bool    sortByForce READ sortByForce WRITE setSortByForce NOTIFY sortByForceChanged)
    Q_PROPERTY(bool    manageMode           READ manageMode           WRITE setManageMode           NOTIFY manageModeChanged)
    Q_PROPERTY(int     hiddenCount          READ hiddenCount                                         NOTIFY hiddenChannelsChanged)
    Q_PROPERTY(bool    terrestrialCollapsed READ terrestrialCollapsed WRITE setTerrestrialCollapsed NOTIFY terrestrialCollapsedChanged)
    Q_PROPERTY(bool    bsCollapsed          READ bsCollapsed          WRITE setBsCollapsed          NOTIFY bsCollapsedChanged)

public:
    explicit ChannelProxyModel(QObject *parent = nullptr);

    QString searchText()  const { return m_searchText; }
    int     bsFilter()    const { return m_bsFilter; }
    bool    sortByForce() const { return m_sortByForce; }
    bool    manageMode()           const { return m_manageMode; }
    int     hiddenCount()          const { return m_hiddenChannels.size(); }
    bool    terrestrialCollapsed() const { return m_terrestrialCollapsed; }
    bool    bsCollapsed()          const { return m_bsCollapsed; }

    void setSearchText(const QString &t);
    void setBsFilter(int f);
    void setSortByForce(bool s);
    void setManageMode(bool m);
    void setTerrestrialCollapsed(bool c);
    void setBsCollapsed(bool c);

    Q_INVOKABLE QVariantList getSourcesByVideo(const QString &video) const;
    Q_INVOKABLE bool isChannelHidden(const QString &video) const;
    Q_INVOKABLE void setChannelHidden(const QString &video, bool hidden);
    Q_INVOKABLE void clearHidden();

signals:
    void searchTextChanged();
    void bsFilterChanged();
    void sortByForceChanged();
    void manageModeChanged();
    void hiddenChannelsChanged();
    void terrestrialCollapsedChanged();
    void bsCollapsedChanged();

protected:
    bool filterAcceptsRow(int sourceRow, const QModelIndex &sourceParent) const override;
    bool lessThan(const QModelIndex &left, const QModelIndex &right) const override;

private:
    void loadHidden();
    void saveHidden();

    QString      m_searchText;
    int          m_bsFilter     = 0;
    bool         m_sortByForce  = false;
    bool         m_manageMode            = false;
    bool         m_terrestrialCollapsed = false;
    bool         m_bsCollapsed          = false;
    QSet<QString> m_hiddenChannels;
    QSettings    m_settings;
};
