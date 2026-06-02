import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import "Colors.js" as Colors

Page {
    signal channelSelected(string video, string name)

    readonly property var clr: Colors.get(settings.theme)
    background: Rectangle { color: clr.bg }

    Component.onCompleted: channelModel.searchText = ""

    header: Column {
        width: parent.width

        // ─── フィルター・ソート・管理バー ──────────────────────────────
        Rectangle {
            width: parent.width
            height: 36
            color: clr.header

            RowLayout {
                anchors { fill: parent; leftMargin: 8; rightMargin: 8 }
                spacing: 4

                // 管理モード中は「完了」「全て表示」のみ
                RowLayout {
                    visible: channelModel.manageMode
                    spacing: 4

                    Label {
                        text: "チャンネル管理"
                        color: clr.text
                        font.pixelSize: 12
                        font.weight: Font.Medium
                    }

                    Item { Layout.fillWidth: true }

                    Button {
                        visible: channelModel.hiddenCount > 0
                        text: "全て表示"
                        flat: true
                        font.pixelSize: 12
                        Material.foreground: Material.accentColor
                        leftPadding: 8; rightPadding: 8
                        onClicked: channelModel.clearHidden()
                    }

                    Button {
                        text: "完了"
                        flat: true
                        font.pixelSize: 12
                        Material.foreground: clr.text
                        leftPadding: 8; rightPadding: 8
                        onClicked: channelModel.manageMode = false
                    }
                }

                // 通常モードのフィルター群
                RowLayout {
                    visible: !channelModel.manageMode
                    spacing: 4
                    Layout.fillWidth: true

                    // 放送波フィルター
                    Repeater {
                        model: [["全て", 0], ["地上波", 1], ["BS", 2]]
                        Button {
                            text: modelData[0]
                            flat: true
                            highlighted: channelModel.bsFilter === modelData[1]
                            font.pixelSize: 12
                            leftPadding: 8; rightPadding: 8
                            topPadding: 4;  bottomPadding: 4
                            Material.foreground: highlighted ? Material.accentColor : clr.sub
                            onClicked: channelModel.bsFilter = modelData[1]
                        }
                    }

                    Item { Layout.fillWidth: true }

                    // 非表示件数バッジ
                    Label {
                        visible: channelModel.hiddenCount > 0
                        text: channelModel.hiddenCount + "件非表示"
                        font.pixelSize: 10
                        color: clr.sub
                    }

                    // 勢い順ソート
                    ToolButton {
                        contentItem: UiIcon {
                            name: "sort"
                            color: channelModel.sortByForce ? Material.accentColor : clr.sub
                            strokeWidth: 2
                        }
                        implicitWidth: 36
                        implicitHeight: 32
                        padding: 6
                        Material.foreground: channelModel.sortByForce ? Material.accentColor : clr.sub
                        ToolTip.visible: pressed
                        ToolTip.text: channelModel.sortByForce ? "勢い順" : "並順"
                        onClicked: channelModel.sortByForce = !channelModel.sortByForce
                    }

                    // 管理モードへ
                    ToolButton {
                        contentItem: UiIcon {
                            name: "sliders"
                            color: clr.sub
                            strokeWidth: 1.8
                        }
                        implicitWidth: 36
                        implicitHeight: 32
                        padding: 6
                        Material.foreground: clr.sub
                        onClicked: channelModel.manageMode = true
                    }
                }
            }
        }
    }

    ListView {
        id: listView
        anchors.fill: parent
        model: channelModel
        spacing: 0
        clip: true
        reuseItems: true

        section.property: (channelModel.sortByForce || channelModel.manageMode) ? "" : "bs"
        section.criteria: ViewSection.FullString
        section.delegate: Rectangle {
            width: listView.width
            height: 30
            color: clr.header

            readonly property bool isBS: section === "true"
            readonly property bool collapsed: isBS ? channelModel.bsCollapsed
                                                   : channelModel.terrestrialCollapsed

            RowLayout {
                anchors { fill: parent; leftMargin: 16; rightMargin: 12 }
                spacing: 4

                UiIcon {
                    Layout.preferredWidth: 10
                    Layout.preferredHeight: 10
                    name: parent.parent.collapsed ? "caretRight" : "caretDown"
                    color: clr.sub
                }

                Label {
                    text: parent.parent.isBS ? "BS" : "地上波"
                    font.pixelSize: 11; font.weight: Font.Medium; font.letterSpacing: 0.8
                    color: clr.sub
                    Layout.fillWidth: true
                }
            }

            TapHandler {
                onTapped: {
                    if (parent.isBS)
                        channelModel.bsCollapsed = !channelModel.bsCollapsed
                    else
                        channelModel.terrestrialCollapsed = !channelModel.terrestrialCollapsed
                }
            }
        }

        delegate: ChannelItem {
            width: listView.width
            channelName:      model.name
            channelVideo:     model.video
            force:            model.force
            viewers:          model.viewers
            running:          model.running
            sources:          model.sources
            programTitle:     model.hasProgram ? model.programTitle : ""
            programGenreCode: model.programGenreCode ?? ""

            manageMode: channelModel.manageMode
            // hiddenCount を参照することで hiddenChannelsChanged 発火時に再評価される
            isHidden: { channelModel.hiddenCount; return channelModel.isChannelHidden(model.video) }

            onClicked: {
                if (!channelModel.manageMode)
                    channelSelected(model.video, model.name)
            }

            onHideToggled: channelModel.setChannelHidden(model.video,
                               !channelModel.isChannelHidden(model.video))
        }

        ScrollBar.vertical: ScrollBar { policy: ScrollBar.AsNeeded }
    }
}
