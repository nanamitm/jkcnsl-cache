import QtQuick

Item {
    id: root

    property string name: "clear"
    property color color: "white"
    property real strokeWidth: 2

    implicitWidth: 20
    implicitHeight: 20

    Canvas {
        id: canvas
        anchors.fill: parent
        antialiasing: true

        onPaint: {
            const ctx = getContext("2d")
            const w = width
            const h = height
            ctx.clearRect(0, 0, w, h)
            ctx.strokeStyle = root.color
            ctx.fillStyle = root.color
            ctx.lineWidth = root.strokeWidth
            ctx.lineCap = "round"
            ctx.lineJoin = "round"

            if (root.name === "clear") {
                ctx.beginPath()
                ctx.moveTo(w * 0.32, h * 0.32)
                ctx.lineTo(w * 0.68, h * 0.68)
                ctx.moveTo(w * 0.68, h * 0.32)
                ctx.lineTo(w * 0.32, h * 0.68)
                ctx.stroke()
            } else if (root.name === "back" || root.name === "next") {
                const dir = root.name === "back" ? 1 : -1
                const cx = w * 0.5
                ctx.beginPath()
                ctx.moveTo(cx + dir * w * 0.18, h * 0.24)
                ctx.lineTo(cx - dir * w * 0.18, h * 0.5)
                ctx.lineTo(cx + dir * w * 0.18, h * 0.76)
                ctx.stroke()
            } else if (root.name === "caretRight") {
                ctx.beginPath()
                ctx.moveTo(w * 0.36, h * 0.24)
                ctx.lineTo(w * 0.68, h * 0.5)
                ctx.lineTo(w * 0.36, h * 0.76)
                ctx.closePath()
                ctx.fill()
            } else if (root.name === "caretDown") {
                ctx.beginPath()
                ctx.moveTo(w * 0.24, h * 0.36)
                ctx.lineTo(w * 0.76, h * 0.36)
                ctx.lineTo(w * 0.5, h * 0.68)
                ctx.closePath()
                ctx.fill()
            } else if (root.name === "sliders") {
                const rows = [
                    { y: h * 0.28, knob: w * 0.68 },
                    { y: h * 0.50, knob: w * 0.38 },
                    { y: h * 0.72, knob: w * 0.56 }
                ]
                for (const row of rows) {
                    ctx.beginPath()
                    ctx.moveTo(w * 0.22, row.y)
                    ctx.lineTo(w * 0.78, row.y)
                    ctx.stroke()

                    ctx.beginPath()
                    ctx.arc(row.knob, row.y, Math.max(2, w * 0.08), 0, Math.PI * 2)
                    ctx.fill()
                }
            } else if (root.name === "list") {
                const rows = [h * 0.3, h * 0.5, h * 0.7]
                for (const y of rows) {
                    ctx.beginPath()
                    ctx.arc(w * 0.25, y, Math.max(1.6, w * 0.045), 0, Math.PI * 2)
                    ctx.fill()

                    ctx.beginPath()
                    ctx.moveTo(w * 0.38, y)
                    ctx.lineTo(w * 0.78, y)
                    ctx.stroke()
                }
            } else if (root.name === "danmaku") {
                const rows = [
                    { y: h * 0.32, start: w * 0.20, end: w * 0.72 },
                    { y: h * 0.50, start: w * 0.35, end: w * 0.84 },
                    { y: h * 0.68, start: w * 0.12, end: w * 0.58 }
                ]
                for (const row of rows) {
                    ctx.beginPath()
                    ctx.moveTo(row.start, row.y)
                    ctx.lineTo(row.end, row.y)
                    ctx.stroke()

                    ctx.beginPath()
                    ctx.moveTo(row.end, row.y)
                    ctx.lineTo(row.end - w * 0.1, row.y - h * 0.08)
                    ctx.moveTo(row.end, row.y)
                    ctx.lineTo(row.end - w * 0.1, row.y + h * 0.08)
                    ctx.stroke()
                }
            } else if (root.name === "sort") {
                const bars = [
                    { y: h * 0.30, x2: w * 0.78 },
                    { y: h * 0.50, x2: w * 0.62 },
                    { y: h * 0.70, x2: w * 0.46 }
                ]
                for (const bar of bars) {
                    ctx.beginPath()
                    ctx.moveTo(w * 0.24, bar.y)
                    ctx.lineTo(bar.x2, bar.y)
                    ctx.stroke()
                }
            } else if (root.name === "eye" || root.name === "eyeOff") {
                ctx.beginPath()
                ctx.moveTo(w * 0.16, h * 0.5)
                ctx.bezierCurveTo(w * 0.30, h * 0.28, w * 0.70, h * 0.28, w * 0.84, h * 0.5)
                ctx.bezierCurveTo(w * 0.70, h * 0.72, w * 0.30, h * 0.72, w * 0.16, h * 0.5)
                ctx.stroke()
                ctx.beginPath()
                ctx.arc(w * 0.5, h * 0.5, Math.max(2, w * 0.09), 0, Math.PI * 2)
                ctx.fill()
                if (root.name === "eyeOff") {
                    ctx.beginPath()
                    ctx.moveTo(w * 0.24, h * 0.78)
                    ctx.lineTo(w * 0.76, h * 0.22)
                    ctx.stroke()
                }
            } else if (root.name === "style") {
                const dots = [
                    { x: w * 0.32, y: h * 0.35 },
                    { x: w * 0.52, y: h * 0.28 },
                    { x: w * 0.66, y: h * 0.46 }
                ]
                for (const dot of dots) {
                    ctx.beginPath()
                    ctx.arc(dot.x, dot.y, Math.max(2, w * 0.055), 0, Math.PI * 2)
                    ctx.fill()
                }
                ctx.beginPath()
                ctx.arc(w * 0.48, h * 0.52, w * 0.30, Math.PI * 0.18, Math.PI * 1.82)
                ctx.stroke()
                ctx.beginPath()
                ctx.arc(w * 0.66, h * 0.66, Math.max(2, w * 0.07), 0, Math.PI * 2)
                ctx.fill()
            } else if (root.name === "send") {
                ctx.beginPath()
                ctx.moveTo(w * 0.20, h * 0.22)
                ctx.lineTo(w * 0.82, h * 0.50)
                ctx.lineTo(w * 0.20, h * 0.78)
                ctx.lineTo(w * 0.34, h * 0.52)
                ctx.closePath()
                ctx.stroke()
            } else if (root.name === "filter") {
                ctx.beginPath()
                ctx.moveTo(w * 0.20, h * 0.24)
                ctx.lineTo(w * 0.80, h * 0.24)
                ctx.lineTo(w * 0.58, h * 0.50)
                ctx.lineTo(w * 0.58, h * 0.76)
                ctx.lineTo(w * 0.42, h * 0.66)
                ctx.lineTo(w * 0.42, h * 0.50)
                ctx.closePath()
                ctx.stroke()
            } else if (root.name === "trash") {
                ctx.beginPath()
                ctx.moveTo(w * 0.34, h * 0.30)
                ctx.lineTo(w * 0.66, h * 0.30)
                ctx.moveTo(w * 0.40, h * 0.22)
                ctx.lineTo(w * 0.60, h * 0.22)
                ctx.moveTo(w * 0.28, h * 0.34)
                ctx.lineTo(w * 0.72, h * 0.34)
                ctx.stroke()
                ctx.strokeRect(w * 0.34, h * 0.38, w * 0.32, h * 0.42)
                ctx.beginPath()
                ctx.moveTo(w * 0.44, h * 0.46)
                ctx.lineTo(w * 0.44, h * 0.70)
                ctx.moveTo(w * 0.56, h * 0.46)
                ctx.lineTo(w * 0.56, h * 0.70)
                ctx.stroke()
            }
        }
    }

    onNameChanged: canvas.requestPaint()
    onColorChanged: canvas.requestPaint()
    onStrokeWidthChanged: canvas.requestPaint()
    onWidthChanged: canvas.requestPaint()
    onHeightChanged: canvas.requestPaint()
}
