#include <QGuiApplication>
#include <QQmlApplicationEngine>
#include <QQmlContext>
#include <QtQuickControls2/QQuickStyle>

#include "channelmodel.h"
#include "channelproxymodel.h"
#include "channelswsclient.h"
#include "commentwsclient.h"
#include "commentmodel.h"
#include "scheduleservice.h"
#include "ngfilter.h"
#include "watchwsclient.h"
#include "loginservice.h"
#include "appsettings.h"

int main(int argc, char *argv[]) {
    QGuiApplication app(argc, argv);
    app.setApplicationName(QStringLiteral("jkcnsl"));
    app.setOrganizationName(QStringLiteral("jkcnsl"));

    QQuickStyle::setStyle(QStringLiteral("Material"));

    AppSettings       settings;
    ChannelModel      channelModel;
    ChannelProxyModel proxyModel;
    ChannelsWsClient  channelsWs;
    CommentWsClient   commentWs;
    CommentModel      commentModel;
    ScheduleService   scheduleService;
    NgFilter          ngFilter;
    LoginService      loginService;
    WatchWsClient     watchWs;

    proxyModel.setSourceModel(&channelModel);

    QObject::connect(&channelsWs, &ChannelsWsClient::snapshotReceived,
                     &channelModel, &ChannelModel::applySnapshot);
    QObject::connect(&channelsWs, &ChannelsWsClient::statsReceived,
                     &channelModel, &ChannelModel::applyStats);
    QObject::connect(&channelsWs, &ChannelsWsClient::programsReceived,
                     &channelModel, &ChannelModel::applyPrograms);

    // NG フィルタリングしてからモデルへ追加
    // ログイン成功 → userSession / mfaTrustedDeviceToken を保存
    QObject::connect(&loginService, &LoginService::loginSuccess,
                     &settings, [&settings](const QString &userSession,
                                             const QString &mfaTrustedDeviceToken) {
                         if (!userSession.isEmpty())           settings.setUserSession(userSession);
                         if (!mfaTrustedDeviceToken.isEmpty()) settings.setMfaTrustedDeviceToken(mfaTrustedDeviceToken);
                     });

    QObject::connect(&commentWs, &CommentWsClient::chatReceived,
                     &commentModel, [&commentModel, &ngFilter](const QVariantMap &chat) {
                         if (!ngFilter.isBlocked(chat.value(QStringLiteral("userId")).toString()))
                             commentModel.addChat(chat);
                     });

    QQmlApplicationEngine engine;
    auto *ctx = engine.rootContext();
    ctx->setContextProperty(QStringLiteral("settings"),         &settings);
    ctx->setContextProperty(QStringLiteral("channelModel"),     &proxyModel);   // proxy を公開
    ctx->setContextProperty(QStringLiteral("channelsWs"),       &channelsWs);
    ctx->setContextProperty(QStringLiteral("commentWs"),        &commentWs);
    ctx->setContextProperty(QStringLiteral("commentModel"),     &commentModel);
    ctx->setContextProperty(QStringLiteral("scheduleService"),  &scheduleService);
    ctx->setContextProperty(QStringLiteral("ngFilter"),         &ngFilter);
    ctx->setContextProperty(QStringLiteral("loginService"),     &loginService);
    ctx->setContextProperty(QStringLiteral("watchWs"),          &watchWs);

    const QUrl url(QStringLiteral("qrc:/qt/qml/JkcnslApp/qml/Main.qml"));
    QObject::connect(&engine, &QQmlApplicationEngine::objectCreationFailed,
                     &app, []() { QCoreApplication::exit(-1); }, Qt::QueuedConnection);
    engine.load(url);

    if (engine.rootObjects().isEmpty())
        return -1;

    channelsWs.connectTo(settings.serverUrl());

    return app.exec();
}
