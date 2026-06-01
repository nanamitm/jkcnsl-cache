import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import "Colors.js" as Colors

ItemDelegate {
    id: root

    property string channelName:      ""
    property string channelVideo:     ""
    property int    force:            0
    property int    viewers:          0
    property bool   running:          false
    property string programTitle:     ""
    property string programGenreCode: ""

    // 管理モード用
    property bool manageMode: false
    property bool isHidden:   false

    signal hideToggled()   // 管理モードでトグルボタンが押されたとき

    readonly property var clr: Colors.get(settings.theme)

    height: 64
    padding: 0; leftPadding: 0; rightPadding: 0
    opacity: root.isHidden && !root.manageMode ? 0.0 : (root.isHidden ? 0.4 : 1.0)

    background: Rectangle {
        color: root.pressed ? Qt.lighter(root.clr.bg2, 1.1) : root.clr.bg2
        Rectangle {
            anchors.bottom: parent.bottom
            width: parent.width; height: 1
            color: root.clr.border
        }
    }

    contentItem: RowLayout {
        anchors.fill: parent
        anchors.leftMargin: 10
        anchors.rightMargin: root.manageMode ? 4 : 12
        spacing: 8

        // ジャンルカラーバー
        Rectangle {
            width: 3; height: 44; radius: 2
            color: root.running
                   ? (settings.genreColorEnabled && root.programGenreCode
                      ? Colors.genreColor(root.programGenreCode) : "#4caf50")
                   : root.clr.border
        }

        // チャンネル名・番組名
        ColumnLayout {
            Layout.fillWidth: true
            spacing: 3

            Label {
                text: root.channelName + (root.isHidden && root.manageMode ? "  (非表示)" : "")
                font.pixelSize: 14
                font.weight: Font.Medium
                color: root.isHidden && root.manageMode ? root.clr.sub : root.clr.text
                elide: Text.ElideRight
                Layout.fillWidth: true
            }

            Label {
                text: root.programTitle || root.channelVideo
                font.pixelSize: 11
                color: root.clr.sub
                elide: Text.ElideRight
                Layout.fillWidth: true
            }
        }

        // 通常モード: 勢い・視聴者数
        ColumnLayout {
            visible: !root.manageMode
            spacing: 3
            Layout.minimumWidth: 68

            Label {
                text: "勢い " + root.force.toLocaleString()
                font.pixelSize: 11
                color: root.force > 0 ? root.clr.accent : root.clr.border
                horizontalAlignment: Text.AlignRight
                Layout.fillWidth: true
            }

            Label {
                text: root.viewers > 0 ? root.viewers.toLocaleString() + " 人" : "—"
                font.pixelSize: 10
                color: root.clr.sub
                horizontalAlignment: Text.AlignRight
                Layout.fillWidth: true
            }
        }

        // 管理モード: 表示/非表示トグルボタン
        ToolButton {
            visible: root.manageMode
            text: root.isHidden ? "表示" : "非表示"
            font.pixelSize: 12
            Material.foreground: root.isHidden ? Material.accentColor : "#f44336"
            onClicked: root.hideToggled()
        }
    }
}
