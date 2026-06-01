#include "channelmodel.h"
#include <QJsonArray>
#include <QJsonValue>

ChannelModel::ChannelModel(QObject *parent)
    : QAbstractListModel(parent)
{}

int ChannelModel::rowCount(const QModelIndex &parent) const {
    return parent.isValid() ? 0 : m_channels.size();
}

QVariant ChannelModel::data(const QModelIndex &index, int role) const {
    if (!index.isValid() || index.row() >= m_channels.size()) return {};
    const auto &ch = m_channels.at(index.row());
    switch (role) {
    case IdRole:           return ch.id;
    case NameRole:         return ch.name;
    case VideoRole:        return ch.video;
    case BsRole:           return ch.bs;
    case RunningRole:      return ch.running;
    case ForceRole:        return ch.force;
    case ViewersRole:      return ch.viewers;
    case CommentsRole:     return ch.totalComments;
    case LastResNoRole:    return ch.lastResNo;
    case ProgramTitleRole: return ch.program.valid ? ch.program.title : QString();
    case ProgramStartRole: return ch.program.valid ? ch.program.startAt : QDateTime();
    case ProgramEndRole:   return ch.program.valid ? ch.program.endAt   : QDateTime();
    case ProgramGenreRole:     return ch.program.valid ? ch.program.genreName : QString();
    case ProgramGenreCodeRole: return ch.program.valid ? ch.program.genreCode : QString();
    case HasProgramRole:       return ch.program.valid;
    case SourcesRole:      return getSources(index.row());
    }
    return {};
}

QHash<int, QByteArray> ChannelModel::roleNames() const {
    return {
        {IdRole,           "channelId"},
        {NameRole,         "name"},
        {VideoRole,        "video"},
        {BsRole,           "bs"},
        {RunningRole,      "running"},
        {ForceRole,        "force"},
        {ViewersRole,      "viewers"},
        {CommentsRole,     "comments"},
        {LastResNoRole,    "lastResNo"},
        {ProgramTitleRole, "programTitle"},
        {ProgramStartRole, "programStart"},
        {ProgramEndRole,   "programEnd"},
        {ProgramGenreRole,     "programGenre"},
        {ProgramGenreCodeRole, "programGenreCode"},
        {HasProgramRole,       "hasProgram"},
        {SourcesRole,      "sources"},
    };
}

QVariantList ChannelModel::getSourcesByVideo(const QString &video) const {
    auto it = m_videoToRow.find(video);
    return it != m_videoToRow.end() ? getSources(it.value()) : QVariantList{};
}

QVariantList ChannelModel::getSources(int row) const {
    if (row < 0 || row >= m_channels.size()) return {};
    QVariantList list;
    for (const auto &src : m_channels.at(row).sources)
        list.append(src.toVariantMap());
    return list;
}

ProgramInfo ChannelModel::parseProgram(const QJsonValue &v) {
    ProgramInfo p;
    if (v.isNull() || !v.isObject()) return p;
    const auto o = v.toObject();
    p.title     = o["title"].toString();
    p.startAt   = QDateTime::fromString(o["startAt"].toString(), Qt::ISODate);
    p.endAt     = QDateTime::fromString(o["endAt"].toString(), Qt::ISODate);
    p.source    = o["source"].toString();
    p.genreCode = o["genreCode"].toString();
    p.genreName = o["genreName"].toString();
    p.stale     = o["stale"].toBool();
    p.valid     = true;
    return p;
}

ChannelSource ChannelModel::parseSource(const QJsonObject &o) {
    ChannelSource s;
    s.key           = o["key"].toString();
    s.sourceType    = o["sourceType"].toString();
    s.label         = o["label"].toString();
    s.configured    = o["configured"].toBool();
    s.running       = o["running"].toBool();
    s.currentTarget = o["currentTarget"].toString();
    s.force         = o["force"].toInt();
    s.viewers       = o["viewers"].toInt();
    s.totalComments = static_cast<qint64>(o["totalComments"].toDouble());
    s.lastResNo     = static_cast<qint64>(o["lastResNo"].toDouble());
    s.isReserved    = o["isReserved"].toBool();
    s.status        = o["status"].toString();
    s.statusText    = o["statusText"].toString();
    s.commentable   = o["commentable"].toBool();
    s.requiresAuth  = o["requiresAuth"].toBool();
    s.watchUrl      = o["watchUrl"].toString();
    s.commentUrl    = o["commentUrl"].toString();
    return s;
}

void ChannelModel::applySnapshot(const QJsonObject &msg) {
    beginResetModel();
    m_channels.clear();
    m_videoToRow.clear();

    const auto arr = msg["channels"].toArray();
    m_channels.reserve(arr.size());
    int row = 0;
    for (const auto &v : arr) {
        const auto o = v.toObject();
        ChannelInfo ch;
        ch.id            = o["id"].toInt();
        ch.name          = o["name"].toString();
        ch.video         = o["video"].toString();
        ch.bs            = o["bs"].toBool();
        ch.running       = o["running"].toBool();
        ch.force         = o["force"].toInt();
        ch.viewers       = o["viewers"].toInt();
        ch.totalComments = static_cast<qint64>(o["comments"].toDouble());
        ch.lastResNo     = static_cast<qint64>(o["lastResNo"].toDouble());
        ch.program       = parseProgram(o["program"]);
        for (const auto &sv : o["sources"].toArray())
            ch.sources.append(parseSource(sv.toObject()));
        m_videoToRow[ch.video] = row++;
        m_channels.append(std::move(ch));
    }
    endResetModel();
}

void ChannelModel::applyStats(const QJsonObject &msg) {
    const auto arr = msg["channels"].toArray();
    for (const auto &v : arr) {
        const auto o = v.toObject();
        const QString video = o["video"].toString();
        auto it = m_videoToRow.find(video);
        if (it == m_videoToRow.end()) continue;
        int row = it.value();
        auto &ch = m_channels[row];
        ch.force         = o["force"].toInt();
        ch.viewers       = o["viewers"].toInt();
        ch.totalComments = static_cast<qint64>(o["comments"].toDouble());
        ch.lastResNo     = static_cast<qint64>(o["lastResNo"].toDouble());
        const auto idx = index(row);
        emit dataChanged(idx, idx, {ForceRole, ViewersRole, CommentsRole, LastResNoRole});
    }
}

void ChannelModel::applyPrograms(const QJsonObject &msg) {
    const auto arr = msg["channels"].toArray();
    for (const auto &v : arr) {
        const auto o = v.toObject();
        const QString video = o["video"].toString();
        auto it = m_videoToRow.find(video);
        if (it == m_videoToRow.end()) continue;
        int row = it.value();
        m_channels[row].program = parseProgram(o["program"]);
        const auto idx = index(row);
        emit dataChanged(idx, idx,
            {ProgramTitleRole, ProgramStartRole, ProgramEndRole, ProgramGenreRole, HasProgramRole});
    }
}
