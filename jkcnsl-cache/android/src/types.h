#pragma once
#include <QString>
#include <QDateTime>
#include <QList>
#include <QVariantMap>

struct ProgramInfo {
    QString   title;
    QDateTime startAt;
    QDateTime endAt;
    QString   source;
    QString   genreCode;
    QString   genreName;
    bool      stale = false;
    bool      valid = false;
};

struct ChannelSource {
    QString key;
    QString sourceType;   // official / unofficial / refuge / local / unknown
    QString label;
    bool    configured    = false;
    bool    running       = false;
    QString currentTarget;
    int     force         = 0;
    int     viewers       = 0;
    qint64  totalComments = 0;
    qint64  lastResNo     = 0;
    bool    isReserved    = false;
    QString status;
    QString statusText;
    bool    commentable   = false;
    bool    requiresAuth  = false;
    QString watchUrl;
    QString commentUrl;

    QVariantMap toVariantMap() const {
        return {
            {QStringLiteral("key"),           key},
            {QStringLiteral("sourceType"),    sourceType},
            {QStringLiteral("label"),         label},
            {QStringLiteral("configured"),    configured},
            {QStringLiteral("running"),       running},
            {QStringLiteral("currentTarget"), currentTarget},
            {QStringLiteral("force"),         force},
            {QStringLiteral("viewers"),       viewers},
            {QStringLiteral("totalComments"), totalComments},
            {QStringLiteral("lastResNo"),     lastResNo},
            {QStringLiteral("status"),        status},
            {QStringLiteral("statusText"),    statusText},
            {QStringLiteral("commentable"),   commentable},
            {QStringLiteral("requiresAuth"),  requiresAuth},
            {QStringLiteral("watchUrl"),      watchUrl},
            {QStringLiteral("commentUrl"),    commentUrl},
        };
    }
};

struct ChannelInfo {
    int                 id            = 0;
    QString             name;
    QString             video;
    bool                bs            = false;
    bool                running       = false;
    int                 force         = 0;
    int                 viewers       = 0;
    qint64              totalComments = 0;
    qint64              lastResNo     = 0;
    ProgramInfo         program;
    QList<ChannelSource> sources;
};
