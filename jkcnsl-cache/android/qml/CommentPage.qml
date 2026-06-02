import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import "Colors.js" as Colors

Page {
    id: root
    property string channelVideo: ""
    property string channelName:  ""

    readonly property var clr: Colors.get(settings.theme)
    background: Rectangle { color: clr.bg }

    property bool overlayMode: settings.commentOverlayMode
    readonly property bool loggedIn: settings.userSession.length > 0
    readonly property var selectedSource: {
        channelModel.sourceRevision
        const all = channelModel.getSourcesByVideo(root.channelVideo)
        if (!all || all.length === 0)
            return null
        const current = commentWs.channel
        for (const source of all) {
            if (source.key === current)
                return source
        }
        return all[0]
    }
    readonly property bool selectedSourceCommentable:
        selectedSource ? selectedSource.commentable : false
    readonly property bool selectedSourceRequiresAuth:
        selectedSource ? selectedSource.requiresAuth : false
    readonly property bool postLoginRequired:
        watchWs.connected && selectedSourceCommentable && selectedSourceRequiresAuth && !loggedIn
    readonly property bool postInputVisible:
        watchWs.connected && selectedSourceCommentable && (!selectedSourceRequiresAuth || loggedIn)

    // 新着コメントをオーバーレイに流す
    Connections {
        target: commentWs
        function onChatReceived(chat) {
            if (root.overlayMode && !ngFilter.isBlocked(chat.userId)) {
                const fs  = Colors.mailFontSize(chat.mail, 14 * (settings.fontScale || 1))
                const col = Colors.mailColor(chat.mail)
                const pos = Colors.mailPosition(chat.mail)
                overlay.addComment(chat.content, col, fs, pos)
            }
        }
    }

    // 投稿結果フィードバック
    Connections {
        target: watchWs
        function onPostSuccess(commentNo) {
            postFeedback.show("#4caf50", "投稿しました (#" + commentNo + ")")
        }
        function onPostError(code) {
            const msg = code === "POST_TOO_FAST"              ? "投稿が早すぎます"
                      : code === "COMMENT_POST_NOT_ALLOWED"   ? "このソースはコメント投稿不可"
                      : "投稿失敗: " + code
            postFeedback.show("#f44336", msg)
        }
    }

    // ─── ヘッダー ─────────────────────────────────────────────────────
    header: ToolBar {
        Material.background: root.clr.header
        RowLayout {
            anchors.fill: parent
            anchors.leftMargin: 4
            anchors.rightMargin: 8

            ToolButton {
                contentItem: UiIcon {
                    name: "back"
                    color: root.clr.text
                    strokeWidth: 2.4
                }
                onClicked: {
                    commentWs.disconnectNow()
                    watchWs.disconnectNow()
                    root.StackView.view.pop()
                }
            }

            Label {
                text: root.channelName
                font.pixelSize: 17
                font.weight: Font.Medium
                color: root.clr.text
                elide: Text.ElideRight
                Layout.fillWidth: true
            }

            ToolButton {
                contentItem: UiIcon {
                    name: root.overlayMode ? "danmaku" : "list"
                    color: root.overlayMode ? Material.accentColor : root.clr.sub
                    strokeWidth: 2
                }
                implicitWidth: 36
                implicitHeight: 32
                Material.foreground: root.overlayMode ? Material.accentColor : root.clr.sub
                ToolTip.visible: pressed
                ToolTip.text: root.overlayMode ? "弾幕表示" : "一覧表示"
                onClicked: settings.commentOverlayMode = !settings.commentOverlayMode
            }

            Rectangle {
                Layout.preferredWidth: 10
                Layout.preferredHeight: 10
                radius: 5
                color: commentWs.connected ? "#4caf50" : "transparent"
                border.color: commentWs.connected ? "#4caf50" : "#f44336"
                border.width: 2
            }
        }
    }

    // ─── フッター: オプション + 投稿バー + ソース選択バー ────────────
    footer: Column {
        width: parent.width

        // 色/サイズ/位置オプションバー (トグル表示)
        Rectangle {
            id: optionsBar
            width: parent.width
            height: watchWs.connected && optionsVisible ? optionsCol.implicitHeight + 12 : 0
            visible: height > 0
            color: root.clr.bg2
            clip: true
            Behavior on height { NumberAnimation { duration: 150 } }

            property bool optionsVisible: false

            // 選択状態
            property string postColor:    ""   // "" = white (default)
            property string postSize:     ""   // "" = medium (default)
            property string postPosition: ""   // "" = naka (default)

            readonly property var colorDefs: [
                {v: "",        label: "白",  c: "#e0e0e0"},
                {v: "red",     label: "赤",  c: "#f44336"},
                {v: "pink",    label: "桃",  c: "#f48fb1"},
                {v: "orange",  label: "橙",  c: "#ff9800"},
                {v: "yellow",  label: "黄",  c: "#ffeb3b"},
                {v: "green",   label: "緑",  c: "#4caf50"},
                {v: "cyan",    label: "水",  c: "#00bcd4"},
                {v: "blue",    label: "青",  c: "#2196f3"},
                {v: "purple",  label: "紫",  c: "#9c27b0"},
                {v: "black",   label: "黒",  c: "#424242"},
            ]

            Column {
                id: optionsCol
                anchors { left: parent.left; right: parent.right; top: parent.top; margins: 8 }
                spacing: 6

                // 色
                RowLayout {
                    width: parent.width
                    Label { text: "色"; font.pixelSize: 11; color: root.clr.sub; Layout.minimumWidth: 28 }
                    Flow {
                        Layout.fillWidth: true
                        spacing: 4
                        Repeater {
                            model: optionsBar.colorDefs
                            Rectangle {
                                width: 26; height: 26; radius: 13
                                color: modelData.c
                                border.color: optionsBar.postColor === modelData.v ? "#ffffff" : "transparent"
                                border.width: 2
                                Label {
                                    anchors.centerIn: parent
                                    text: modelData.label
                                    font.pixelSize: 9
                                    color: modelData.v === "" || modelData.v === "yellow" || modelData.v === "orange" ? "#333" : "#fff"
                                }
                                TapHandler { onTapped: optionsBar.postColor = modelData.v }
                            }
                        }
                    }
                }

                // サイズ・位置
                RowLayout {
                    width: parent.width
                    Label { text: "サイズ"; font.pixelSize: 11; color: root.clr.sub; Layout.minimumWidth: 44 }
                    Repeater {
                        model: [{v:"small",label:"小"},{v:"",label:"中"},{v:"big",label:"大"}]
                        Button {
                            text: modelData.label; flat: true
                            highlighted: optionsBar.postSize === modelData.v
                            font.pixelSize: 12; leftPadding: 10; rightPadding: 10
                            topPadding: 2; bottomPadding: 2
                            Material.foreground: highlighted ? Material.accentColor : root.clr.sub
                            onClicked: optionsBar.postSize = modelData.v
                        }
                    }
                    Item { Layout.fillWidth: true }
                    Label { text: "位置"; font.pixelSize: 11; color: root.clr.sub }
                    Repeater {
                        model: [{v:"ue",label:"上"},{v:"",label:"中"},{v:"shita",label:"下"}]
                        Button {
                            text: modelData.label; flat: true
                            highlighted: optionsBar.postPosition === modelData.v
                            font.pixelSize: 12; leftPadding: 10; rightPadding: 10
                            topPadding: 2; bottomPadding: 2
                            Material.foreground: highlighted ? Material.accentColor : root.clr.sub
                            onClicked: optionsBar.postPosition = modelData.v
                        }
                    }
                }
            }
        }

        Rectangle {
            width: parent.width
            height: root.postLoginRequired ? 42 : 0
            visible: height > 0
            color: root.clr.header
            clip: true

            Label {
                anchors.centerIn: parent
                text: "コメント投稿にはログインが必要です"
                color: root.clr.sub
                font.pixelSize: 12
            }
        }

        // コメント投稿バー
        Rectangle {
            id: inputBar
            width: parent.width
            height: root.postInputVisible ? 52 : 0
            visible: height > 0
            color: root.clr.header
            clip: true

            Behavior on height { NumberAnimation { duration: 150 } }

            RowLayout {
                anchors { fill: parent; leftMargin: 8; rightMargin: 8 }
                spacing: 6

                CheckBox {
                    id: anonCheck
                    checked: true
                    text: "184"
                    font.pixelSize: 12
                    Material.foreground: root.clr.text
                    padding: 0
                }

                TextField {
                    id: commentInput
                    Layout.fillWidth: true
                    placeholderText: watchWs.commentable ? "コメントを入力..." : "接続中..."
                    enabled: root.postInputVisible && watchWs.commentable
                    color: optionsBar.postColor !== "" && optionsBar.postColor !== "black"
                           ? Colors.mailColor(optionsBar.postColor) : root.clr.text
                    placeholderTextColor: root.clr.sub
                    font.pixelSize: 14
                    background: Rectangle { color: root.clr.bg2; radius: 4 }
                    leftPadding: 10; rightPadding: 10
                    Material.accent: Material.Blue
                    onAccepted: sendBtn.clicked()
                }

                // オプション切り替えボタン (選択中は色付き)
                ToolButton {
                    id: styleBtn
                    readonly property bool active: optionsBar.postColor !== "" || optionsBar.postSize !== "" || optionsBar.postPosition !== ""
                    contentItem: UiIcon {
                        name: "style"
                        color: styleBtn.active ? Material.accentColor : root.clr.sub
                        strokeWidth: 2
                    }
                    implicitWidth: 38
                    implicitHeight: 34
                    Material.foreground: (optionsBar.postColor !== "" || optionsBar.postSize !== "" || optionsBar.postPosition !== "")
                                         ? Material.accentColor : root.clr.sub
                    ToolTip.visible: pressed
                    ToolTip.text: "装飾"
                    onClicked: optionsBar.optionsVisible = !optionsBar.optionsVisible
                }

                ToolButton {
                    id: sendBtn
                    contentItem: UiIcon {
                        name: "send"
                        color: sendBtn.enabled ? Material.accentColor : root.clr.border
                        strokeWidth: 2
                    }
                    implicitWidth: 38
                    implicitHeight: 34
                    enabled: root.postInputVisible && watchWs.commentable && commentInput.text.trim().length > 0
                    Material.foreground: enabled ? Material.accentColor : root.clr.border
                    ToolTip.visible: pressed
                    ToolTip.text: "送信"
                    onClicked: {
                        const text = commentInput.text.trim()
                        if (!text) return
                        watchWs.postComment(text, anonCheck.checked,
                            optionsBar.postColor, optionsBar.postSize, optionsBar.postPosition)
                        commentInput.clear()
                    }
                }
            }
        }

        // ソース選択バー
        Rectangle {
            id: sourceBar
            width: parent.width
            height: sourceRepeater.count > 0 ? 44 : 0
            visible: height > 0
            color: root.clr.header

            property var sources: {
                channelModel.sourceRevision
                return channelModel.getSourcesByVideo(root.channelVideo)
            }

            ScrollView {
                anchors.fill: parent
                ScrollBar.horizontal.policy: ScrollBar.AsNeeded
                ScrollBar.vertical.policy: ScrollBar.AlwaysOff

                Row {
                    height: 44
                    leftPadding: 8
                    spacing: 4

                    Repeater {
                        id: sourceRepeater
                        model: {
                            const all = sourceBar.sources
                            return all ? all.filter(function(s) { return s.configured }) : []
                        }
                        Button {
                            property var src: modelData
                            text: src.label + (src.running ? " ON" : "")
                            flat: true
                            highlighted: commentWs.channel === src.key
                            font.pixelSize: 11
                            leftPadding: 8; rightPadding: 8
                            anchors.verticalCenter: parent.verticalCenter
                            enabled: src.commentable
                            Material.foreground: {
                                if (!src.commentable) return root.clr.sub
                                if (highlighted)      return Material.accentColor
                                return root.clr.text
                            }
                            onClicked: {
                                if (highlighted) return
                                commentWs.connectTo(settings.serverUrl, src.key)
                                watchWs.connectTo(settings.serverUrl, src.key, settings.userSession)
                                commentModel.clear()
                            }
                        }
                    }
                }
            }
        }
    }

    // ─── コンテンツ ───────────────────────────────────────────────────
    Item {
        anchors.fill: parent

        // リストモード
        ListView {
            id: commentList
            anchors.fill: parent
            model: commentModel
            clip: true
            spacing: 0
            visible: !root.overlayMode

            delegate: Rectangle {
                width: commentList.width
                height: commentLabel.implicitHeight + 12
                color: model.index % 2 === 0 ? root.clr.bg2 : root.clr.bg

                Text {
                    id: commentLabel
                    anchors { left: parent.left; right: parent.right; verticalCenter: parent.verticalCenter; margins: 10 }
                    text:           model.content
                    color:          Colors.mailColor(model.mail)
                    font.pixelSize: Colors.mailFontSize(model.mail, 13 * (settings.fontScale || 1))
                    wrapMode:       Text.WordWrap
                }

                TapHandler {
                    longPressThreshold: 0.7
                    onLongPressed: {
                        if (model.userId && model.userId.length > 0) {
                            ngFilter.addUser(model.userId)
                            window.showError("NG登録: " + model.userId)
                        }
                    }
                }
            }

            onCountChanged: Qt.callLater(() => positionViewAtEnd())
            ScrollBar.vertical: ScrollBar { policy: ScrollBar.AsNeeded }
        }

        // 弾幕オーバーレイモード
        Rectangle {
            anchors.fill: parent
            color: root.clr.bg
            visible: root.overlayMode
            CommentOverlay { id: overlay; anchors.fill: parent }
        }

        // 投稿フィードバックバナー (コンテンツ最下部に重ねて表示)
        Rectangle {
            id: postFeedback
            z: 10
            visible: false
            anchors { bottom: parent.bottom; horizontalCenter: parent.horizontalCenter; bottomMargin: 8 }
            width: feedbackLabel.implicitWidth + 24
            height: 32; radius: 4
            color: "#323232"

            function show(bgColor, msg) {
                color = bgColor
                feedbackLabel.text = msg
                visible = true
                feedbackTimer.restart()
            }

            Label { id: feedbackLabel; anchors.centerIn: parent; color: "#fff"; font.pixelSize: 12 }
            Timer { id: feedbackTimer; interval: 2500; onTriggered: postFeedback.visible = false }
        }
    }
}
