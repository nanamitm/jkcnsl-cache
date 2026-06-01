.pragma library

var themes = {
    navy:  { bg: "#0d1b2a", bg2: "#0f2035", header: "#0a1628", text: "#dce8f8", sub: "#7090b8", accent: "#42a5f5", border: "#12294a" },
    dark:  { bg: "#121212", bg2: "#1e1e1e", header: "#0a0a0a", text: "#e0e0e0", sub: "#9e9e9e", accent: "#bb86fc", border: "#333333" },
    green: { bg: "#0a1f0a", bg2: "#0f2a0f", header: "#081508", text: "#d8f0d8", sub: "#7a9e7a", accent: "#66bb6a", border: "#1a3a1a" },
    wine:  { bg: "#1a0a0f", bg2: "#250f15", header: "#120708", text: "#f0d8dc", sub: "#9e7880", accent: "#ef9a9a", border: "#3a1a20" },
    white: { bg: "#f5f5f5", bg2: "#ffffff", header: "#e8e8e8", text: "#212121", sub: "#757575", accent: "#1976d2", border: "#e0e0e0" },
    cream: { bg: "#fdf6e3", bg2: "#f5efda", header: "#e8e0c8", text: "#3d3520", sub: "#7d7060", accent: "#8d6e63", border: "#e0d8c0" },
}

function get(name) {
    return themes[name] || themes.navy
}

function genreColor(code) {
    var map = {
        "0x0": "#546e7a", "0x1": "#4caf50", "0x2": "#ff9800",
        "0x3": "#e91e63", "0x4": "#9c27b0", "0x5": "#ffc107",
        "0x6": "#2196f3", "0x7": "#00bcd4", "0x8": "#795548",
        "0x9": "#8bc34a", "0xa": "#ff5722", "0xb": "#607d8b",
        "0xf": "#616161",
    }
    return map[code] || "#37474f"
}

function mailColor(mail) {
    if (!mail) return "#d0d8e8"
    var m = mail.toLowerCase()
    if (m.indexOf("red")    >= 0) return "#ef5350"
    if (m.indexOf("blue")   >= 0) return "#42a5f5"
    if (m.indexOf("green")  >= 0) return "#66bb6a"
    if (m.indexOf("yellow") >= 0) return "#ffca28"
    if (m.indexOf("orange") >= 0) return "#ffa726"
    if (m.indexOf("pink")   >= 0) return "#f48fb1"
    if (m.indexOf("purple") >= 0) return "#ce93d8"
    if (m.indexOf("cyan")   >= 0) return "#4dd0e1"
    if (m.indexOf("white")  >= 0) return "#ffffff"
    return "#d0d8e8"
}

function mailFontSize(mail, base) {
    if (!mail) return base
    var m = mail.toLowerCase()
    if (m.indexOf("big")   >= 0) return base * 1.5
    if (m.indexOf("small") >= 0) return base * 0.8
    return base
}

function mailPosition(mail) {
    if (!mail) return "naka"
    var m = mail.toLowerCase()
    if (m.indexOf("ue")    >= 0) return "ue"
    if (m.indexOf("shita") >= 0) return "shita"
    return "naka"
}
