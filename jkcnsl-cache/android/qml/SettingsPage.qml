import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import "Colors.js" as Colors

Page {
    signal applyServerUrl()
    signal openLoginPage()

    readonly property var clr: Colors.get(settings.theme)
    background: Rectangle { color: clr.bg }

    ScrollView {
        anchors.fill: parent
        contentWidth: availableWidth

        ColumnLayout {
            width: parent.width
            spacing: 0

            // ─── サーバー設定 ──────────────────────────────────────────
            SectionHeader { text: "サーバー設定" }

            SettingRow {
                label: "URL"
                Layout.fillWidth: true
                TextField {
                    id: urlField
                    text: settings.serverUrl
                    placeholderText: "http://localhost:5000"
                    font.pixelSize: 13
                    color: clr.text
                    placeholderTextColor: clr.sub
                    background: Item {}
                    Layout.fillWidth: true
                    inputMethodHints: Qt.ImhUrlCharactersOnly | Qt.ImhNoAutoUppercase
                    onEditingFinished: { settings.serverUrl = text.trim(); applyServerUrl() }
                }
            }

            // ─── 表示設定 ──────────────────────────────────────────────
            SectionHeader { text: "表示設定" }

            SettingRow {
                label: "テーマ"
                Layout.fillWidth: true
                Flow {
                    Layout.fillWidth: true
                    spacing: 6
                    Repeater {
                        model: [
                            { value: "navy",  label: "ネイビー" },
                            { value: "dark",  label: "ダーク" },
                            { value: "green", label: "グリーン" },
                            { value: "wine",  label: "ワイン" },
                            { value: "white", label: "ホワイト" },
                            { value: "cream", label: "クリーム" },
                        ]
                        Button {
                            text: modelData.label
                            flat: true
                            highlighted: settings.theme === modelData.value
                            font.pixelSize: 12
                            leftPadding: 10; rightPadding: 10
                            topPadding: 4;   bottomPadding: 4
                            Material.foreground: highlighted ? Material.accentColor : clr.sub
                            onClicked: settings.theme = modelData.value
                        }
                    }
                }
            }

            SettingRow {
                label: "フォントサイズ"
                Layout.fillWidth: true
                RowLayout {
                    Label {
                        text: fontSlider.value.toFixed(1) + "x"
                        color: clr.sub; font.pixelSize: 12; Layout.minimumWidth: 36
                    }
                    Slider {
                        id: fontSlider
                        from: 0.5; to: 2.0; stepSize: 0.1
                        value: settings.fontScale
                        Layout.fillWidth: true
                        onMoved: settings.fontScale = value
                    }
                }
            }

            SettingRow {
                label: "ジャンルカラー"
                Layout.fillWidth: true
                Switch {
                    checked: settings.genreColorEnabled
                    onToggled: settings.genreColorEnabled = checked
                    Material.accent: Material.Blue
                }
            }

            // ─── 弾幕設定 ──────────────────────────────────────────────
            SectionHeader { text: "弾幕オーバーレイ" }

            SettingRow {
                label: "スクロール速度"
                Layout.fillWidth: true
                RowLayout {
                    Label {
                        text: (scrollSpeedSlider.value / 1000).toFixed(1) + "秒"
                        color: clr.sub; font.pixelSize: 12; Layout.minimumWidth: 44
                    }
                    Slider {
                        id: scrollSpeedSlider
                        from: 2000; to: 12000; stepSize: 500
                        value: settings.scrollSpeed
                        Layout.fillWidth: true
                        onMoved: settings.scrollSpeed = value
                    }
                }
            }

            SettingRow {
                label: "トラック再使用"
                Layout.fillWidth: true
                RowLayout {
                    Label {
                        text: Math.round(scrollRangeSlider.value * 100) + "%"
                        color: clr.sub; font.pixelSize: 12; Layout.minimumWidth: 44
                    }
                    Slider {
                        id: scrollRangeSlider
                        from: 0.3; to: 1.0; stepSize: 0.05
                        value: settings.scrollRange
                        Layout.fillWidth: true
                        onMoved: settings.scrollRange = value
                    }
                }
            }

            // ─── NG ユーザー ───────────────────────────────────────────
            SectionHeader { text: "NG ユーザー" }

            SettingRow {
                label: "追加"
                Layout.fillWidth: true
                RowLayout {
                    Layout.fillWidth: true
                    TextField {
                        id: ngInput
                        placeholderText: "ユーザーID"
                        font.pixelSize: 13
                        color: clr.text
                        placeholderTextColor: clr.sub
                        background: Item {}
                        Layout.fillWidth: true
                    }
                    Button {
                        text: "NG"
                        flat: true
                        font.pixelSize: 12
                        Material.foreground: Material.accentColor
                        enabled: ngInput.text.trim().length > 0
                        onClicked: {
                            ngFilter.addUser(ngInput.text.trim())
                            ngInput.clear()
                        }
                    }
                }
            }

            // NGリスト
            Repeater {
                model: ngFilter.users
                Rectangle {
                    Layout.fillWidth: true
                    height: 44
                    color: clr.bg2
                    Rectangle {
                        anchors.bottom: parent.bottom
                        width: parent.width; height: 1
                        color: clr.border
                    }
                    RowLayout {
                        anchors { fill: parent; leftMargin: 16; rightMargin: 8 }
                        Label {
                            text: modelData
                            font.pixelSize: 13
                            color: clr.text
                            Layout.fillWidth: true
                        }
                        ToolButton {
                            text: "✕"
                            font.pixelSize: 14
                            Material.foreground: "#f44336"
                            onClicked: ngFilter.removeUser(modelData)
                        }
                    }
                }
            }

            Label {
                visible: ngFilter.users.length === 0
                text: "NG ユーザーなし"
                font.pixelSize: 12
                color: clr.sub
                Layout.alignment: Qt.AlignHCenter
                topPadding: 8; bottomPadding: 8
            }

            // ─── ニコニコ認証 ──────────────────────────────────────────
            SectionHeader { text: "ニコニコ認証" }

            Rectangle {
                Layout.fillWidth: true
                height: authCol.implicitHeight + 24
                color: clr.bg2
                ColumnLayout {
                    id: authCol
                    anchors { left: parent.left; right: parent.right; verticalCenter: parent.verticalCenter; margins: 16 }
                    spacing: 10
                    Label {
                        text: settings.mfaTrustedDeviceToken.length > 0
                              ? "デバイストークン保存済み (自動ログイン有効)"
                              : "未認証 — 公式・非公式ソースには認証が必要です"
                        color: settings.mfaTrustedDeviceToken.length > 0 ? "#4caf50" : clr.sub
                        font.pixelSize: 12
                        wrapMode: Text.WordWrap
                        Layout.fillWidth: true
                    }
                    RowLayout {
                        Layout.fillWidth: true
                        Button {
                            text: "ログイン..."
                            flat: true
                            font.pixelSize: 13
                            Material.foreground: Material.accentColor
                            onClicked: openLoginPage()
                        }
                        Button {
                            visible: settings.mfaTrustedDeviceToken.length > 0
                            text: "トークン削除"
                            flat: true
                            font.pixelSize: 13
                            Material.foreground: "#f44336"
                            onClicked: settings.mfaTrustedDeviceToken = ""
                        }
                    }
                }
            }

            // ─── 接続状態 ──────────────────────────────────────────────
            SectionHeader { text: "接続状態" }

            Rectangle {
                Layout.fillWidth: true
                height: statusCol.implicitHeight + 24
                color: clr.bg2
                ColumnLayout {
                    id: statusCol
                    anchors { left: parent.left; right: parent.right; verticalCenter: parent.verticalCenter; margins: 16 }
                    spacing: 6
                    Label {
                        text: "チャンネルWS: " + (channelsWs.connected ? "接続中 ⬤" : "未接続 ○")
                        color: channelsWs.connected ? "#4caf50" : "#f44336"
                        font.pixelSize: 13
                    }
                    Label {
                        text: "コメントWS: " + (commentWs.connected
                              ? "接続中 ⬤ (" + commentWs.channel + ")"
                              : "未接続 ○")
                        color: commentWs.connected ? "#4caf50" : clr.sub
                        font.pixelSize: 13
                    }
                }
            }

            Item { height: 24 }
        }
    }

    // ─── 内部コンポーネント ────────────────────────────────────────────
    component SectionHeader: Label {
        required text
        topPadding: 20; bottomPadding: 6; leftPadding: 16
        font.pixelSize: 11; font.weight: Font.Medium; font.letterSpacing: 0.8
        color: clr.sub
        Layout.fillWidth: true
        background: Rectangle { color: clr.bg }
    }

    component SettingRow: Rectangle {
        property string label: ""
        default property alias content: inner.children
        height: inner.implicitHeight + 20
        color: clr.bg2
        RowLayout {
            id: inner
            anchors { left: parent.left; right: parent.right; verticalCenter: parent.verticalCenter; margins: 16 }
            spacing: 12
            Label {
                text: parent.parent.label
                color: clr.text
                font.pixelSize: 13
                Layout.minimumWidth: 96
            }
        }
    }
}
