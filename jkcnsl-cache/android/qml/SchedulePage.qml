import QtQuick
import QtQuick.Controls
import QtQuick.Controls.Material
import QtQuick.Layouts
import "Colors.js" as Colors

Page {
    id: root
    readonly property var clr: Colors.get(settings.theme)
    background: Rectangle { color: clr.bg }

    // レイアウト定数
    readonly property int  nameColW:    80
    readonly property int  rowH:        50
    readonly property int  timeHeaderH: 28
    readonly property real pxPerMin:    2.0
    readonly property int  totalMins:   1440

    // 選択日・放送開始時刻
    property string selectedDate: Qt.formatDate(new Date(), "yyyy-MM-dd")
    property int    startHour:    4

    // 現在時刻ライン X 座標 (-1 = 非表示)
    property real nowX: -1

    // ─── フィルター状態 ───────────────────────────────────────────────
    property bool filterVisible:  false
    property int  bsFilter:       0          // 0=全て 1=地上波 2=BS
    property var  selectedGenres: ({})       // {code: true} 空=全て表示

    readonly property bool hasActiveFilter:
        bsFilter !== 0 || Object.keys(selectedGenres).length > 0

    // スケジュールデータから全チャンネルを取得
    readonly property var allChannels: {
        const d = scheduleService.scheduleData
        return (d && d.channels) ? d.channels : []
    }

    // BSフィルター適用済みチャンネルリスト
    readonly property var filteredChannels: {
        if (bsFilter === 0) return allChannels
        return allChannels.filter(function(c) {
            return bsFilter === 2 ? c.bs : !c.bs
        })
    }

    // スケジュールデータ内の全ジャンル (重複なし、コード順)
    readonly property var availableGenres: {
        const seen = {}
        const result = []
        for (const ch of allChannels) {
            for (const p of (ch.programs || [])) {
                if (p.genreCode && p.genreName && !seen[p.genreCode]) {
                    seen[p.genreCode] = true
                    result.push({ code: p.genreCode, name: p.genreName })
                }
            }
        }
        result.sort(function(a, b) { return a.code < b.code ? -1 : 1 })
        return result
    }

    function isGenreActive(code) {
        return Object.keys(selectedGenres).length === 0 || selectedGenres[code] === true
    }
    function toggleGenre(code) {
        const g = Object.assign({}, selectedGenres)
        if (g[code]) delete g[code]; else g[code] = true
        selectedGenres = g
    }
    function clearFilters() {
        bsFilter = 0
        selectedGenres = {}
    }
    function reload() {
        if (settings.serverUrl.length > 0)
            scheduleService.fetch(settings.serverUrl, selectedDate)
    }

    // ─── 現在時刻計算 ─────────────────────────────────────────────────
    function calcNowX() {
        const now = new Date()
        const p   = selectedDate.split("-")
        const gridStart = new Date(+p[0], +p[1] - 1, +p[2], startHour, 0, 0, 0)
        const gridEnd   = new Date(gridStart.getTime() + 86400000)
        if (now < gridStart || now >= gridEnd) return -1
        return Math.round((now - gridStart) / 60000 * pxPerMin)
    }
    function updateNowX(scrollToNow) {
        nowX = calcNowX()
        if (scrollToNow && nowX >= 0)
            Qt.callLater(() => gridFlick.contentX =
                Math.max(0, nowX - gridFlick.width * 0.3))
    }

    Timer { interval: 60000; running: true; repeat: true; onTriggered: root.updateNowX(false) }

    // ─── 日付ナビ (ヘッダー) ──────────────────────────────────────────
    header: ToolBar {
        Material.background: root.clr.header
        RowLayout {
            anchors.fill: parent
            anchors.leftMargin: 4
            anchors.rightMargin: 4

            ToolButton {
                contentItem: UiIcon {
                    name: "back"
                    color: root.clr.text
                    strokeWidth: 2.2
                }
                onClicked: root.selectedDate = root.shiftDate(root.selectedDate, -1)
            }

            Label {
                text: root.selectedDate
                font.pixelSize: 15; font.weight: Font.Medium
                color: root.clr.text; horizontalAlignment: Text.AlignHCenter
                Layout.fillWidth: true
                MouseArea {
                    anchors.fill: parent
                    onClicked: root.selectedDate = Qt.formatDate(new Date(), "yyyy-MM-dd")
                }
            }

            ToolButton {
                contentItem: UiIcon {
                    name: "next"
                    color: root.clr.text
                    strokeWidth: 2.2
                }
                onClicked: root.selectedDate = root.shiftDate(root.selectedDate, +1)
            }

            // フィルターボタン
            ToolButton {
                contentItem: UiIcon {
                    name: "filter"
                    color: root.hasActiveFilter || root.filterVisible ? Material.accentColor : root.clr.sub
                    strokeWidth: 2
                }
                implicitWidth: 36
                implicitHeight: 32
                Material.foreground: root.hasActiveFilter ? Material.accentColor
                                   : root.filterVisible   ? Material.accentColor
                                   :                        root.clr.sub
                ToolTip.visible: pressed
                ToolTip.text: "絞込"
                onClicked: root.filterVisible = !root.filterVisible
            }
        }
    }

    // 日付変更
    onSelectedDateChanged: {
        nowX = -1
        selectedGenres = {}
        reload()
    }

    Component.onCompleted: {
        reload()
    }

    Connections {
        target: scheduleService
        function onScheduleDataChanged() {
            const d = scheduleService.scheduleData
            if (d && d.broadcastStartHour !== undefined)
                root.startHour = d.broadcastStartHour
            root.updateNowX(true)
        }
    }

    // ─── ローディング / エラー ────────────────────────────────────────
    BusyIndicator {
        anchors.centerIn: parent
        visible: scheduleService.loading; running: visible
    }
    Label {
        anchors.centerIn: parent
        visible: !scheduleService.loading && scheduleService.error.length > 0
        text: "取得失敗: " + scheduleService.error
        color: "#f44336"; wrapMode: Text.WordWrap
        width: parent.width - 32; horizontalAlignment: Text.AlignHCenter
    }

    // ─── メインコンテンツ ─────────────────────────────────────────────
    Item {
        anchors.fill: parent
        visible: !scheduleService.loading && scheduleService.error.length === 0

        // ─── フィルターパネル ─────────────────────────────────────────
        Rectangle {
            id: filterPanel
            anchors { top: parent.top; left: parent.left; right: parent.right }
            height: root.filterVisible ? filterCol.implicitHeight + 20 : 0
            color: root.clr.bg2
            clip: true
            z: 10

            Behavior on height { NumberAnimation { duration: 180; easing.type: Easing.OutCubic } }

            Column {
                id: filterCol
                anchors { left: parent.left; right: parent.right; top: parent.top; margins: 12 }
                spacing: 10

                // 放送波
                RowLayout {
                    width: parent.width
                    Label {
                        text: "放送波"
                        color: root.clr.sub; font.pixelSize: 12
                        Layout.minimumWidth: 52
                    }
                    Repeater {
                        model: [["全て", 0], ["地上波", 1], ["BS", 2]]
                        Button {
                            text: modelData[0]; flat: true
                            highlighted: root.bsFilter === modelData[1]
                            font.pixelSize: 12
                            leftPadding: 10; rightPadding: 10; topPadding: 3; bottomPadding: 3
                            Material.foreground: highlighted ? Material.accentColor : root.clr.sub
                            onClicked: root.bsFilter = modelData[1]
                        }
                    }
                }

                // ジャンル
                Column {
                    width: parent.width
                    spacing: 6
                    visible: root.availableGenres.length > 0

                    Label {
                        text: "ジャンル"
                        color: root.clr.sub; font.pixelSize: 12
                    }

                    Flow {
                        width: parent.width
                        spacing: 4
                        Repeater {
                            model: root.availableGenres
                            Button {
                                readonly property string genreCode: modelData.code
                                readonly property string genreName: modelData.name
                                text: genreName; flat: true
                                highlighted: root.isGenreActive(genreCode)
                                font.pixelSize: 11
                                leftPadding: 8; rightPadding: 8; topPadding: 3; bottomPadding: 3
                                Material.foreground: highlighted
                                    ? Colors.genreColor(genreCode)
                                    : root.clr.sub
                                onClicked: root.toggleGenre(genreCode)
                            }
                        }
                    }
                }

                // クリアボタン
                RowLayout {
                    width: parent.width
                    visible: root.hasActiveFilter
                    Item { Layout.fillWidth: true }
                    Button {
                        text: "フィルタークリア"; flat: true
                        font.pixelSize: 12
                        Material.foreground: "#f44336"
                        onClicked: root.clearFilters()
                    }
                }
            }
        }

        // ─── タイムグリッド ───────────────────────────────────────────
        Item {
            id: gridArea
            anchors { top: filterPanel.bottom; left: parent.left; right: parent.right; bottom: parent.bottom }

            // 時刻ヘッダー
            Item {
                id: timeHeader
                x: nameColW; y: 0
                width: parent.width - nameColW; height: timeHeaderH
                clip: true; z: 2

                Rectangle { anchors.fill: parent; color: root.clr.bg2 }

                Row {
                    x: -gridFlick.contentX
                    Repeater {
                        model: 25
                        Item {
                            width: 60 * root.pxPerMin; height: timeHeaderH
                            Label {
                                anchors.left: parent.left; anchors.leftMargin: 4
                                anchors.verticalCenter: parent.verticalCenter
                                text: ((root.startHour + index) % 24).toString().padStart(2, "0") + ":00"
                                font.pixelSize: 10; color: root.clr.sub
                            }
                            Rectangle {
                                anchors { right: parent.right; top: parent.top; bottom: parent.bottom }
                                width: 1; color: root.clr.border
                            }
                        }
                    }
                }

                // 現在時刻マーカー
                Item {
                    visible: root.nowX >= 0
                    x: root.nowX - gridFlick.contentX
                    y: 0; width: 0; height: timeHeaderH
                    Rectangle { x: -1; y: 0; width: 2; height: parent.height; color: "#ef5350" }
                    Rectangle {
                        width: 7; height: 7; x: -3; y: parent.height - 9
                        color: "#ef5350"; rotation: 45; antialiasing: true
                    }
                }
            }

            // チャンネル名列
            Item {
                id: nameCol
                x: 0; y: timeHeaderH
                width: nameColW; height: parent.height - timeHeaderH
                clip: true; z: 2

                Rectangle { anchors.fill: parent; color: root.clr.bg2 }

                Column {
                    y: -gridFlick.contentY
                    Repeater {
                        model: root.filteredChannels
                        Rectangle {
                            width: nameColW; height: rowH; color: "transparent"
                            Rectangle {
                                anchors { bottom: parent.bottom; left: parent.left; right: parent.right }
                                height: 1; color: root.clr.border
                            }
                            Label {
                                anchors { fill: parent; margins: 4 }
                                text: modelData.name; font.pixelSize: 10
                                color: root.clr.text; wrapMode: Text.WordWrap
                                verticalAlignment: Text.AlignVCenter
                            }
                        }
                    }
                }
            }

            // メイングリッド
            Flickable {
                id: gridFlick
                x: nameColW; y: timeHeaderH
                width: parent.width - nameColW; height: parent.height - timeHeaderH
                clip: true
                contentWidth:  totalMins * root.pxPerMin
                contentHeight: root.filteredChannels.length * rowH

                Column {
                    Repeater {
                        model: root.filteredChannels
                        ProgramRow {
                            channelData:    modelData
                            rowHeight:      rowH
                            pxPerMin:       root.pxPerMin
                            startHour:      root.startHour
                            clr:            root.clr
                            selectedGenres: root.selectedGenres
                        }
                    }
                }

                // 現在時刻ライン
                Rectangle {
                    visible: root.nowX >= 0
                    x: root.nowX; y: 0
                    width: 2; height: gridFlick.contentHeight
                    color: "#ef5350"; opacity: 0.85; z: 5
                    Rectangle {
                        width: 8; height: 8; x: -3; y: 0
                        radius: 4; color: "#ef5350"
                    }
                }

                ScrollBar.horizontal: ScrollBar { policy: ScrollBar.AsNeeded }
                ScrollBar.vertical:   ScrollBar { policy: ScrollBar.AsNeeded }
            }
        }
    }

    // ─── 番組行コンポーネント ─────────────────────────────────────────
    component ProgramRow: Item {
        property var  channelData
        property int  rowHeight
        property real pxPerMin
        property int  startHour
        property var  clr
        property var  selectedGenres: ({})

        height: rowHeight
        width: 1440 * pxPerMin

        Repeater {
            model: channelData ? channelData.programs : []
            Rectangle {
                property int offsetMin: {
                    const s = new Date(modelData.startAt)
                    const gridStart = new Date(s)
                    gridStart.setHours(startHour, 0, 0, 0)
                    if (s < gridStart)
                        gridStart.setDate(gridStart.getDate() - 1)
                    return Math.max(0, Math.round((s - gridStart) / 60000))
                }
                property int durationMin: {
                    const s = new Date(modelData.startAt)
                    const e = new Date(modelData.endAt)
                    return Math.max(1, Math.round((e - s) / 60000))
                }
                // ジャンルフィルター: 非アクティブなジャンルは暗く
                readonly property bool genreActive: {
                    const keys = Object.keys(selectedGenres)
                    return keys.length === 0 || selectedGenres[modelData.genreCode] === true
                }

                x:       offsetMin * pxPerMin
                width:   durationMin * pxPerMin
                height:  rowHeight
                color:   Colors.genreColor(modelData.genreCode)
                opacity: genreActive ? 0.85 : 0.2
                clip: true
                border.color: Qt.darker(color, 1.4)
                border.width: 1

                Column {
                    anchors { fill: parent; margins: 3 }
                    spacing: 1; clip: true
                    Label {
                        width: parent.width
                        text: modelData.title; font.pixelSize: 10; font.weight: Font.Medium
                        color: "#ffffff"; elide: Text.ElideRight
                    }
                    Label {
                        width: parent.width
                        text: Qt.formatTime(new Date(modelData.startAt), "HH:mm")
                        font.pixelSize: 9; color: Qt.rgba(1, 1, 1, 0.75)
                    }
                }
            }
        }
    }

    // ─── ヘルパー ─────────────────────────────────────────────────────
    function shiftDate(dateStr, days) {
        const d = new Date(dateStr)
        d.setDate(d.getDate() + days)
        return Qt.formatDate(d, "yyyy-MM-dd")
    }
}
