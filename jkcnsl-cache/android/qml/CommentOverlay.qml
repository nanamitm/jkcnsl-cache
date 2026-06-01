// 弾幕スタイルのスクロールコメントオーバーレイ
import QtQuick

Item {
    id: root
    clip: true

    // ─── 定数 ─────────────────────────────────────────────────────────
    readonly property int    trackH:          28
    readonly property int    trackCount:      Math.max(1, Math.floor(height / trackH))
    readonly property int    staticTracks:    Math.max(1, Math.floor(trackCount / 4))  // ue/shita 用トラック数
    readonly property int    scrollMs:        (typeof settings !== "undefined") ? settings.scrollSpeed : 7000
    readonly property double scrollRange:     (typeof settings !== "undefined") ? settings.scrollRange : 0.5
    readonly property int    staticDisplayMs: 3000  // ue/shita の表示時間 (ms)

    // ─── トラック管理 ─────────────────────────────────────────────────
    property var scrollTrackReady: []   // naka: 右→左スクロール用
    property var ueTrackReady:     []   // ue:   上固定用 (上から何番目)
    property var shitaTrackReady:  []   // shita: 下固定用 (下から何番目)

    // ─── 公開 API ─────────────────────────────────────────────────────
    function addComment(text, color, fontSize, position) {
        if (!text) return
        const pos = position || "naka"
        if (pos === "ue" || pos === "shita")
            addStaticComment(text, color, fontSize, pos)
        else
            addScrollComment(text, color, fontSize)
    }

    // ─── スクロールコメント (naka) ────────────────────────────────────
    function addScrollComment(text, color, fontSize) {
        ensureScrollTracks()
        const track = pickScrollTrack()
        const obj = scrollComp.createObject(root, {
            text:      text,
            color:     color || "#ffffff",
            pixelSize: fontSize || 14,
            x:         root.width,
            y:         track * trackH,
        })
        if (!obj) return
        Qt.callLater(function() {
            if (!obj) return
            const w   = Math.max(obj.implicitWidth, 20)
            const dur = scrollMs + Math.round(w / 2)
            obj.startAnim(root.width, -w, dur)
            root.scrollTrackReady[track] = Date.now() + dur * root.scrollRange
        })
    }

    function ensureScrollTracks() {
        const n = Math.min(trackCount, 24)
        while (scrollTrackReady.length < n) scrollTrackReady.push(0)
    }

    function pickScrollTrack() {
        const now = Date.now()
        const n   = Math.min(trackCount, scrollTrackReady.length)
        for (let i = 0; i < n; i++) {
            if (scrollTrackReady[i] <= now) return i
        }
        let minTime = Infinity, minIdx = 0
        for (let i = 0; i < n; i++) {
            if (scrollTrackReady[i] < minTime) { minTime = scrollTrackReady[i]; minIdx = i }
        }
        return minIdx
    }

    // ─── 固定コメント (ue / shita) ────────────────────────────────────
    function addStaticComment(text, color, fontSize, position) {
        const tracks = position === "ue" ? ueTrackReady : shitaTrackReady
        const n      = staticTracks
        while (tracks.length < n) tracks.push(0)

        // 空きトラックを探す (全埋まりなら最速)
        const now = Date.now()
        let track = 0
        let minTime = Infinity
        for (let i = 0; i < n; i++) {
            if (tracks[i] <= now) { track = i; break }
            if (tracks[i] < minTime) { minTime = tracks[i]; track = i }
        }

        // ue: 上から track 番目、shita: 下から track 番目
        const yPos = position === "ue"
            ? track * trackH
            : root.height - (track + 1) * trackH

        const obj = staticComp.createObject(root, {
            text:      text,
            color:     color || "#ffffff",
            pixelSize: fontSize || 14,
            y:         yPos,
        })
        if (!obj) return

        tracks[track] = Date.now() + staticDisplayMs
        Qt.callLater(function() { if (obj) obj.startDisplay(staticDisplayMs) })
    }

    // ─── スクロールコンポーネント ─────────────────────────────────────
    Component {
        id: scrollComp
        Text {
            id: txt
            property real pixelSize: 14
            z: 1
            font.pixelSize: pixelSize
            style: Text.Outline
            styleColor: "#000000"
            textFormat: Text.PlainText

            function startAnim(fromX, toX, dur) {
                anim.from = fromX; anim.to = toX; anim.duration = dur; anim.start()
            }
            NumberAnimation on x {
                id: anim; running: false
                onFinished: txt.destroy()
            }
        }
    }

    // ─── 固定コンポーネント (ue/shita) ───────────────────────────────
    Component {
        id: staticComp
        Text {
            id: stxt
            property real pixelSize: 14
            z: 2
            width: root.width
            horizontalAlignment: Text.AlignHCenter
            font.pixelSize: pixelSize
            font.bold: true
            style: Text.Outline
            styleColor: "#000000"
            textFormat: Text.PlainText

            function startDisplay(dur) {
                holdTimer.interval = Math.max(100, dur - 400)
                holdTimer.start()
            }

            // 表示キープ → フェードアウト → 破棄
            Timer {
                id: holdTimer
                onTriggered: fadeOut.start()
            }
            NumberAnimation {
                id: fadeOut
                target: stxt; property: "opacity"
                from: 1.0; to: 0.0; duration: 400
                onFinished: stxt.destroy()
            }
        }
    }
}
