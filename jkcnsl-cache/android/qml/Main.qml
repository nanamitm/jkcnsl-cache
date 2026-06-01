import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import "Colors.js" as Colors

ApplicationWindow {
    id: window
    visible: true
    width: 400
    height: 800
    title: "jkcnsl"

    readonly property var clr: Colors.get(settings.theme)

    Material.theme:      (settings.theme === "white" || settings.theme === "cream")
                         ? Material.Light : Material.Dark
    Material.accent:     Material.Blue
    Material.background: clr.bg

    // ─── エラーSnackbar ───────────────────────────────────────────────
    function showError(msg) {
        snackLabel.text = msg
        snackBar.visible = true
        snackTimer.restart()
    }

    Connections {
        target: channelsWs
        function onErrorMessage(text) { window.showError(text) }
    }

    Rectangle {
        id: snackBar
        visible: false
        z: 100
        anchors { bottom: parent.bottom; horizontalCenter: parent.horizontalCenter; bottomMargin: 16 }
        width: Math.min(parent.width - 32, snackLabel.implicitWidth + 32)
        height: 44
        radius: 4
        color: "#323232"

        Label {
            id: snackLabel
            anchors.centerIn: parent
            color: "#ffffff"
            font.pixelSize: 13
            elide: Text.ElideRight
            width: parent.width - 16
            horizontalAlignment: Text.AlignHCenter
        }

        Timer {
            id: snackTimer
            interval: 3500
            onTriggered: snackBar.visible = false
        }

        Behavior on visible { NumberAnimation { duration: 150 } }
    }

    // ─── ナビゲーション ───────────────────────────────────────────────
    StackView {
        id: rootStack
        anchors.fill: parent
        initialItem: mainPageComp
    }

    Component {
        id: mainPageComp
        Page {
            background: Rectangle { color: window.clr.bg }

            header: ToolBar {
                Material.background: window.clr.header
                RowLayout {
                    anchors.fill: parent
                    anchors.leftMargin: 16
                    anchors.rightMargin: 12

                    Label {
                        text: ["チャンネル一覧", "番組表", "設定"][tabBar.currentIndex] ?? ""
                        font.pixelSize: 18
                        font.weight: Font.Medium
                        color: window.clr.text
                        Layout.fillWidth: true
                    }

                    Label {
                        text: channelsWs.connected ? "⬤" : "○"
                        color: channelsWs.connected ? "#4caf50" : "#f44336"
                        font.pixelSize: 13
                    }
                }
            }

            SwipeView {
                id: swipeView
                anchors.fill: parent
                currentIndex: tabBar.currentIndex
                onCurrentIndexChanged: tabBar.currentIndex = currentIndex

                ChannelListPage {
                    onChannelSelected: function(video, name) {
                        commentWs.connectTo(settings.serverUrl, video)
                        watchWs.connectTo(settings.serverUrl, video, settings.userSession)
                        commentModel.clear()
                        rootStack.push(commentPageComp, { channelVideo: video, channelName: name })
                    }
                }

                SchedulePage {}

                SettingsPage {
                    onApplyServerUrl: {
                        channelsWs.disconnectNow()
                        channelsWs.connectTo(settings.serverUrl)
                    }
                    onOpenLoginPage: rootStack.push(loginPageComp)
                }
            }

            footer: TabBar {
                id: tabBar
                currentIndex: swipeView.currentIndex
                onCurrentIndexChanged: swipeView.currentIndex = currentIndex
                Material.background: window.clr.header

                TabButton { text: "チャンネル" }
                TabButton { text: "番組表" }
                TabButton { text: "設定" }
            }
        }
    }

    Component {
        id: commentPageComp
        CommentPage {}
    }

    Component {
        id: loginPageComp
        LoginPage {}
    }
}
