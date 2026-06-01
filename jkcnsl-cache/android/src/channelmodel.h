#pragma once
#include <QAbstractListModel>
#include <QJsonObject>
#include "types.h"

class ChannelModel : public QAbstractListModel {
    Q_OBJECT

public:
    enum Roles {
        IdRole = Qt::UserRole + 1,
        NameRole,
        VideoRole,
        BsRole,
        RunningRole,
        ForceRole,
        ViewersRole,
        CommentsRole,
        LastResNoRole,
        ProgramTitleRole,
        ProgramStartRole,
        ProgramEndRole,
        ProgramGenreRole,      // genreName (表示用)
        ProgramGenreCodeRole,  // genreCode (色分け用)
        HasProgramRole,
        SourcesRole,
    };

    explicit ChannelModel(QObject *parent = nullptr);

    int     rowCount(const QModelIndex &parent = QModelIndex()) const override;
    QVariant data(const QModelIndex &index, int role = Qt::DisplayRole) const override;
    QHash<int, QByteArray> roleNames() const override;

    Q_INVOKABLE QVariantList getSources(int row) const;
    Q_INVOKABLE QVariantList getSourcesByVideo(const QString &video) const;

public slots:
    void applySnapshot(const QJsonObject &msg);
    void applyStats(const QJsonObject &msg);
    void applyPrograms(const QJsonObject &msg);

private:
    static ProgramInfo  parseProgram(const QJsonValue &v);
    static ChannelSource parseSource(const QJsonObject &o);

    QList<ChannelInfo>   m_channels;
    QHash<QString, int>  m_videoToRow;
};
