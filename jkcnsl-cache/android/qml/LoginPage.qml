import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import "Colors.js" as Colors

Page {
    id: root

    readonly property var clr: Colors.get(settings.theme)
    background: Rectangle { color: clr.bg }

    // MFA フェーズに入ったとき true
    property bool mfaPhase: false
    property string pendingMfaToken: ""

    header: ToolBar {
        Material.background: root.clr.header
        RowLayout {
            anchors.fill: parent
            anchors.leftMargin: 4
            ToolButton {
                contentItem: UiIcon {
                    name: "back"
                    color: root.clr.text
                    strokeWidth: 2.4
                }
                onClicked: root.StackView.view.pop()
            }
            Label {
                text: root.mfaPhase ? "2段階認証" : "ニコニコログイン"
                font.pixelSize: 18
                font.weight: Font.Medium
                color: root.clr.text
                Layout.fillWidth: true
            }
        }
    }

    // ログイン成功 → 設定保存して戻る
    Connections {
        target: loginService
        function onLoginSuccess(userSession, mfaTrustedDeviceToken) {
            if (mfaTrustedDeviceToken.length > 0)
                settings.mfaTrustedDeviceToken = mfaTrustedDeviceToken
            window.showError("ログイン成功")
            root.StackView.view.pop()
        }
        function onMfaRequired(mfaToken) {
            root.pendingMfaToken = mfaToken
            root.mfaPhase = true
        }
    }

    ScrollView {
        anchors.fill: parent
        contentWidth: availableWidth

        ColumnLayout {
            width: parent.width
            spacing: 16
            anchors.margins: 24

            // ─── 通常ログインフォーム ──────────────────────────────────
            Item { height: 8 }

            // エラー表示
            Rectangle {
                visible: loginService.error.length > 0
                Layout.fillWidth: true
                height: errLabel.implicitHeight + 16
                color: "#b71c1c"
                radius: 4
                Label {
                    id: errLabel
                    anchors { fill: parent; margins: 8 }
                    text: loginService.error
                    color: "#ffffff"
                    wrapMode: Text.WordWrap
                    font.pixelSize: 13
                }
            }

            // ─── メール + パスワード (mfaPhase でない時) ──────────────
            ColumnLayout {
                visible: !root.mfaPhase
                Layout.fillWidth: true
                spacing: 12

                Label {
                    text: "メールアドレス"
                    color: root.clr.sub
                    font.pixelSize: 12
                }
                TextField {
                    id: emailField
                    Layout.fillWidth: true
                    placeholderText: "example@email.com"
                    color: root.clr.text
                    placeholderTextColor: root.clr.sub
                    font.pixelSize: 15
                    inputMethodHints: Qt.ImhEmailCharactersOnly | Qt.ImhNoAutoUppercase
                    background: Rectangle {
                        color: root.clr.bg2
                        radius: 6
                    }
                    leftPadding: 12; rightPadding: 12
                    Material.accent: Material.Blue
                    KeyNavigation.tab: passwordField
                }

                Label {
                    text: "パスワード"
                    color: root.clr.sub
                    font.pixelSize: 12
                }
                TextField {
                    id: passwordField
                    Layout.fillWidth: true
                    placeholderText: "パスワード"
                    echoMode: TextInput.Password
                    color: root.clr.text
                    placeholderTextColor: root.clr.sub
                    font.pixelSize: 15
                    background: Rectangle {
                        color: root.clr.bg2
                        radius: 6
                    }
                    leftPadding: 12; rightPadding: 12
                    Material.accent: Material.Blue
                    onAccepted: loginButton.clicked()
                }

                Button {
                    id: loginButton
                    Layout.fillWidth: true
                    text: loginService.loading ? "ログイン中..." : "ログイン"
                    enabled: !loginService.loading
                             && emailField.text.trim().length > 0
                             && passwordField.text.length > 0
                    Material.background: Material.Blue
                    font.pixelSize: 15
                    onClicked: loginService.login(
                        settings.serverUrl,
                        emailField.text.trim(),
                        passwordField.text,
                        settings.mfaTrustedDeviceToken
                    )
                }
            }

            // ─── MFA フォーム ──────────────────────────────────────────
            ColumnLayout {
                visible: root.mfaPhase
                Layout.fillWidth: true
                spacing: 12

                Label {
                    text: "認証アプリに表示されている6桁のコードを入力してください"
                    color: root.clr.text
                    font.pixelSize: 13
                    wrapMode: Text.WordWrap
                    Layout.fillWidth: true
                }

                TextField {
                    id: otpField
                    Layout.fillWidth: true
                    placeholderText: "000000"
                    color: root.clr.text
                    placeholderTextColor: root.clr.sub
                    font.pixelSize: 22
                    horizontalAlignment: Text.AlignHCenter
                    inputMethodHints: Qt.ImhDigitsOnly
                    maximumLength: 6
                    background: Rectangle { color: root.clr.bg2; radius: 6 }
                    leftPadding: 12; rightPadding: 12
                    Material.accent: Material.Blue
                    onAccepted: mfaButton.clicked()
                }

                RowLayout {
                    Layout.fillWidth: true
                    CheckBox {
                        id: trustDeviceCheck
                        text: "このデバイスを信頼する"
                        checked: true
                        Material.foreground: root.clr.text
                    }
                }

                Button {
                    id: mfaButton
                    Layout.fillWidth: true
                    text: loginService.loading ? "送信中..." : "認証"
                    enabled: !loginService.loading && otpField.text.length === 6
                    Material.background: Material.Blue
                    font.pixelSize: 15
                    onClicked: loginService.submitMfa(
                        settings.serverUrl,
                        root.pendingMfaToken,
                        otpField.text,
                        trustDeviceCheck.checked
                    )
                }

                Button {
                    Layout.fillWidth: true
                    text: "< パスワード入力に戻る"
                    flat: true
                    Material.foreground: root.clr.sub
                    onClicked: {
                        root.mfaPhase = false
                        root.pendingMfaToken = ""
                        otpField.clear()
                    }
                }
            }
        }
    }
}
