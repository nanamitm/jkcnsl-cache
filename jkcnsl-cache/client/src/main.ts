import './style.css'
import type {
  StatusResponse,
  ChannelInfo,
  ChannelSource,
  Chat,
  ChannelsStreamMessage,
  ProgramInfo,
  ScheduleResponse,
  ScheduleProgram,
} from './types'
import { CommentOverlay } from './overlay'

const LS_OPACITY = 'jkcnsl-overlay-opacity'
const LS_THEME = 'jkcnsl-theme'
const THEMES = [
  { value: 'navy', label: 'ネイビー' },
  { value: 'dark', label: 'ダーク' },
  { value: 'green', label: 'グリーン' },
  { value: 'wine', label: 'ワイン' },
  { value: 'white', label: 'ホワイト' },
  { value: 'cream', label: 'クリーム' },
]
const THEME_VALUES = new Set(THEMES.map(theme => theme.value))

// ─── State ────────────────────────────────────────────────────────────────────
let channels: ChannelInfo[] = []
let selectedChannel: string | null = null
let channelsWs: WebSocket | null = null
let ws: WebSocket | null = null
let overlay: CommentOverlay | null = null
const streamStats = new Map<string, { force: number; viewers: number; comments: number; lastResNo: number }>()
const streamPrograms = new Map<string, ProgramInfo | null>()
type ViewMode = 'watch' | 'schedule'
let viewMode: ViewMode = 'watch'
let schedule: ScheduleResponse | null = null
let scheduleLoading = false
let schedulePendingDate: string | null = null
const LS_NG          = 'jkcnsl-ng-users'
const LS_FONT_SCALE  = 'jkcnsl-font-scale'
const LS_SCROLL_SPEED = 'jkcnsl-scroll-speed'
const LS_SCROLL_RANGE = 'jkcnsl-scroll-range'
const LS_SCROLL_LIMIT = 'jkcnsl-scroll-limit'
const LS_COLLAPSED_CHANNELS = 'jkcnsl-collapsed-channels'
const LS_SIDEBAR_SEARCH = 'jkcnsl-sidebar-search'
const LS_SIDEBAR_CHANNEL_FILTERS = 'jkcnsl-sidebar-channel-filters'
const LS_SIDEBAR_CHANNEL_FILTERS_SET = 'jkcnsl-sidebar-channel-filters-set'
const LS_CHANNEL_PROGRAM_GENRE_COLOR = 'jkcnsl-channel-program-genre-color'
const LS_SCHEDULE_GENRE_FILTERS = 'jkcnsl-schedule-genre-filters'
const LS_SCHEDULE_CHANNEL_FILTERS = 'jkcnsl-schedule-channel-filters'
const LS_SCHEDULE_CHANNEL_FILTERS_SET = 'jkcnsl-schedule-channel-filters-set'
const LS_SCHEDULE_CHANNEL_FILTERS_KNOWN = 'jkcnsl-schedule-channel-filters-known'
const MIN_SCROLL_DURATION_MS = 2000
const MAX_SCROLL_DURATION_MS = 12000
let savedOpacity      = Math.min(1, Math.max(0, Number(localStorage.getItem(LS_OPACITY) ?? 1)))
let savedFontScale    = Math.min(2, Math.max(0.5, Number(localStorage.getItem(LS_FONT_SCALE) ?? 1)))
let savedScrollDuration = Math.min(MAX_SCROLL_DURATION_MS,
  Math.max(MIN_SCROLL_DURATION_MS, Number(localStorage.getItem(LS_SCROLL_SPEED) ?? MIN_SCROLL_DURATION_MS)))
let savedScrollRange = Math.min(1, Math.max(0.5, Number(localStorage.getItem(LS_SCROLL_RANGE) ?? 0.7)))
let savedScrollLimit = Math.min(400, Math.max(50, Number(localStorage.getItem(LS_SCROLL_LIMIT) ?? 320)))
let savedTheme = localStorage.getItem(LS_THEME) ?? 'navy'
if (savedTheme === 'pastel') savedTheme = 'white'
if (!THEME_VALUES.has(savedTheme)) savedTheme = 'navy'
document.documentElement.dataset.theme = savedTheme
let sidebarSearchText = localStorage.getItem(LS_SIDEBAR_SEARCH) ?? ''
let channelProgramGenreColor = localStorage.getItem(LS_CHANNEL_PROGRAM_GENRE_COLOR) === '1'
const sidebarChannelFilters = new Set<string>(JSON.parse(localStorage.getItem(LS_SIDEBAR_CHANNEL_FILTERS) ?? '[]'))
let sidebarChannelFiltersInitialized = localStorage.getItem(LS_SIDEBAR_CHANNEL_FILTERS_SET) === '1'
const scheduleGenreFilters = new Set<string>(JSON.parse(localStorage.getItem(LS_SCHEDULE_GENRE_FILTERS) ?? '[]'))
const scheduleChannelFilters = new Set<string>(JSON.parse(localStorage.getItem(LS_SCHEDULE_CHANNEL_FILTERS) ?? '[]'))
let scheduleChannelFiltersInitialized = localStorage.getItem(LS_SCHEDULE_CHANNEL_FILTERS_SET) === '1'
const scheduleKnownChannels = new Set<string>(JSON.parse(localStorage.getItem(LS_SCHEDULE_CHANNEL_FILTERS_KNOWN) ?? '[]'))
let scheduleChannelSearchText = ''
let scheduleDrag: {
  active: boolean
  moved: boolean
  startX: number
  startY: number
  scrollLeft: number
  scrollTop: number
} | null = null
let commentLogQueue: Chat[] = []
let commentLogFlushScheduled = false
const COMMENT_LOG_MAX_ITEMS = 100
const COMMENT_LOG_FLUSH_LIMIT = 24
const COMMENT_LOG_ENABLED = true
let channelListRenderPending = false
let channelListPointerInside = false
type WsState = 'idle' | 'connecting' | 'connected' | 'disconnected'
let wsState: WsState = 'idle'
let reconnectAt = 0
let watchWs: WebSocket | null = null
let watchKeepSeatTimer: number | null = null
// text → 投稿時刻(ms)。コメントが届いた時点で照合し自分のものか判定
const pendingOwnTexts = new Map<string, number>()
let vposBaseTime: number | null = null
const LS_COOKIE    = 'jkcnsl-user-cookie'
const LS_MFA_TOKEN = 'jkcnsl-mfa-token'
const LS_LOGIN_EMAIL = 'jkcnsl-login-email'
const LS_POST_MAIL = 'jkcnsl-post-mail'
const DEFAULT_POST_MAIL = 'white naka medium defont'
let userCookie      = localStorage.getItem(LS_COOKIE) ?? ''
let mfaTrustedToken = localStorage.getItem(LS_MFA_TOKEN) ?? ''
let savedLoginEmail = localStorage.getItem(LS_LOGIN_EMAIL) ?? ''
let savedPostMail   = localStorage.getItem(LS_POST_MAIL) ?? DEFAULT_POST_MAIL

const ngSet = new Set<string>(JSON.parse(localStorage.getItem(LS_NG) ?? '[]'))
const collapsedChannels = new Set<string>(JSON.parse(localStorage.getItem(LS_COLLAPSED_CHANNELS) ?? '[]'))
const GENRES = [
  { code: '0x0', name: 'ニュース／報道' },
  { code: '0x1', name: 'スポーツ' },
  { code: '0x2', name: '情報／ワイドショー' },
  { code: '0x3', name: 'ドラマ' },
  { code: '0x4', name: '音楽' },
  { code: '0x5', name: 'バラエティ' },
  { code: '0x6', name: '映画' },
  { code: '0x7', name: 'アニメ／特撮' },
  { code: '0x8', name: 'ドキュメンタリー／教養' },
  { code: '0x9', name: '劇場／公演' },
  { code: '0xA', name: '趣味／教育' },
  { code: '0xB', name: '福祉' },
  { code: '0xF', name: 'その他' },
]

function saveNg() {
  localStorage.setItem(LS_NG, JSON.stringify([...ngSet]))
}

function saveCollapsedChannels() {
  localStorage.setItem(LS_COLLAPSED_CHANNELS, JSON.stringify([...collapsedChannels]))
}

function saveSidebarChannelFilters() {
  localStorage.setItem(LS_SIDEBAR_CHANNEL_FILTERS, JSON.stringify([...sidebarChannelFilters]))
  localStorage.setItem(LS_SIDEBAR_CHANNEL_FILTERS_SET, '1')
  sidebarChannelFiltersInitialized = true
}

function ensureSidebarChannelFiltersInitialized() {
  if (sidebarChannelFiltersInitialized || channels.length === 0)
    return
  for (const ch of channels)
    sidebarChannelFilters.add(ch.video)
  saveSidebarChannelFilters()
}

function renderSidebarChannelOptions() {
  sidebarChannelOptions.innerHTML = ''
  ensureSidebarChannelFiltersInitialized()
  const groups = [
    { label: '地上波', items: channels.filter(ch => !ch.bs) },
    { label: 'BS', items: channels.filter(ch => ch.bs) },
  ]
  for (const group of groups) {
    if (group.items.length === 0) continue
    const header = document.createElement('div')
    header.className = 'sidebar-channel-filter-group'
    header.textContent = group.label
    sidebarChannelOptions.append(header)
    for (const ch of group.items) {
      const label = document.createElement('label')
      label.className = 'sidebar-channel-option'
      const checkbox = document.createElement('input')
      checkbox.type = 'checkbox'
      checkbox.checked = sidebarChannelFilters.has(ch.video)
      checkbox.addEventListener('change', () => {
        if (checkbox.checked) sidebarChannelFilters.add(ch.video)
        else sidebarChannelFilters.delete(ch.video)
        saveSidebarChannelFilters()
        renderChannelList(true)
      })
      const name = document.createElement('span')
      name.textContent = ch.name
      const video = document.createElement('span')
      video.className = 'sidebar-channel-option-video'
      video.textContent = ch.video
      label.append(checkbox, name, video)
      sidebarChannelOptions.append(label)
    }
  }
}

function setSidebarChannelFilters(videos: string[]) {
  sidebarChannelFilters.clear()
  for (const video of videos)
    sidebarChannelFilters.add(video)
  saveSidebarChannelFilters()
  renderSidebarChannelOptions()
  renderChannelList(true)
}

function addSidebarChannelFilters(videos: string[]) {
  for (const video of videos)
    sidebarChannelFilters.add(video)
  saveSidebarChannelFilters()
  renderSidebarChannelOptions()
  renderChannelList(true)
}

function saveScheduleGenreFilters() {
  localStorage.setItem(LS_SCHEDULE_GENRE_FILTERS, JSON.stringify([...scheduleGenreFilters]))
}

function saveScheduleChannelFilters() {
  localStorage.setItem(LS_SCHEDULE_CHANNEL_FILTERS, JSON.stringify([...scheduleChannelFilters]))
  localStorage.setItem(LS_SCHEDULE_CHANNEL_FILTERS_SET, '1')
  localStorage.setItem(LS_SCHEDULE_CHANNEL_FILTERS_KNOWN, JSON.stringify([...scheduleKnownChannels]))
  scheduleChannelFiltersInitialized = true
}

function sourceScheduleChannels() {
  return schedule?.channels.length ? schedule.channels : channels
}

function ensureScheduleChannelFiltersInitialized() {
  const sourceChannels = sourceScheduleChannels()
  if (sourceChannels.length === 0)
    return

  if (!scheduleChannelFiltersInitialized) {
    for (const ch of sourceChannels) {
      scheduleChannelFilters.add(ch.video)
      scheduleKnownChannels.add(ch.video)
    }
    saveScheduleChannelFilters()
    return
  }

  let changed = false
  for (const ch of sourceChannels) {
    if (scheduleKnownChannels.has(ch.video))
      continue
    scheduleChannelFilters.add(ch.video)
    scheduleKnownChannels.add(ch.video)
    changed = true
  }
  if (changed)
    saveScheduleChannelFilters()
  else
    localStorage.setItem(LS_SCHEDULE_CHANNEL_FILTERS_KNOWN, JSON.stringify([...scheduleKnownChannels]))
}

function renderScheduleGenreOptions() {
  scheduleGenreOptions.innerHTML = ''
  for (const genre of GENRES) {
    const label = document.createElement('label')
    label.className = `schedule-genre-option ${genreClass(genre.code)}`
    const checkbox = document.createElement('input')
    checkbox.type = 'checkbox'
    checkbox.checked = scheduleGenreFilters.has(genre.code)
    checkbox.addEventListener('change', () => {
      if (checkbox.checked) scheduleGenreFilters.add(genre.code)
      else scheduleGenreFilters.delete(genre.code)
      saveScheduleGenreFilters()
      renderSchedule()
    })
    const swatch = document.createElement('span')
    swatch.className = 'schedule-genre-swatch'
    const text = document.createElement('span')
    text.textContent = genre.name
    label.append(checkbox, swatch, text)
    scheduleGenreOptions.append(label)
  }
}

function renderScheduleChannelOptions() {
  scheduleChannelOptions.innerHTML = ''
  ensureScheduleChannelFiltersInitialized()
  const query = scheduleChannelSearchText.trim().toLowerCase()
  const sourceChannels = sourceScheduleChannels()
  const groups = [
    { label: '地上波', items: sourceChannels.filter(ch => !ch.bs) },
    { label: 'BS', items: sourceChannels.filter(ch => ch.bs) },
  ]
  for (const group of groups) {
    const items = group.items.filter(ch =>
      !query || ch.name.toLowerCase().includes(query) || ch.video.toLowerCase().includes(query))
    if (items.length === 0) continue
    const header = document.createElement('div')
    header.className = 'schedule-channel-filter-group'
    header.textContent = group.label
    scheduleChannelOptions.append(header)
    for (const ch of items) {
      const label = document.createElement('label')
      label.className = 'schedule-channel-option'
      const checkbox = document.createElement('input')
      checkbox.type = 'checkbox'
      checkbox.checked = scheduleChannelFilters.has(ch.video)
      checkbox.addEventListener('change', () => {
        if (checkbox.checked) scheduleChannelFilters.add(ch.video)
        else scheduleChannelFilters.delete(ch.video)
        saveScheduleChannelFilters()
        renderSchedule()
      })
      const name = document.createElement('span')
      name.textContent = ch.name
      const video = document.createElement('span')
      video.className = 'schedule-channel-option-video'
      video.textContent = ch.video
      label.append(checkbox, name, video)
      scheduleChannelOptions.append(label)
    }
  }
}

function setScheduleChannelFilters(videos: string[]) {
  scheduleChannelFilters.clear()
  for (const video of videos)
    scheduleChannelFilters.add(video)
  saveScheduleChannelFilters()
  renderScheduleChannelOptions()
  renderSchedule()
}

function addScheduleChannelFilters(videos: string[]) {
  for (const video of videos)
    scheduleChannelFilters.add(video)
  saveScheduleChannelFilters()
  renderScheduleChannelOptions()
  renderSchedule()
}

function escapeAttr(value: string) {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('"', '&quot;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
}

// ─── DOM ──────────────────────────────────────────────────────────────────────
const app = document.getElementById('app')!
app.innerHTML = `
<div id="layout">
  <aside id="sidebar">
    <div id="sidebar-header">
      <span>ニコニコ実況</span>
      <button id="sidebar-settings-btn" title="チャンネル一覧設定" type="button">⚙</button>
    </div>
    <div id="view-tabs">
      <button id="watch-tab" class="active" type="button">実況</button>
      <button id="schedule-tab" type="button">番組表</button>
    </div>
    <div id="sidebar-settings-panel" hidden>
      <div class="sidebar-settings-section">
        <div class="sidebar-settings-label">外観</div>
        <label class="sidebar-settings-select">
          <span>テーマ</span>
          <select id="theme-select">
            ${THEMES.map(theme =>
              `<option value="${theme.value}" ${savedTheme === theme.value ? 'selected' : ''}>${theme.label}</option>`)
              .join('')}
          </select>
        </label>
      </div>
      <div class="sidebar-settings-section">
        <div class="sidebar-settings-label">表示</div>
        <label class="sidebar-settings-check">
          <input type="checkbox" id="channel-program-genre-color" ${channelProgramGenreColor ? 'checked' : ''} />
          <span>番組名にジャンル色を使う</span>
        </label>
      </div>
      <div class="sidebar-settings-section">
        <div class="sidebar-settings-head">
          <span>表示チャンネル</span>
          <button id="sidebar-channel-all" type="button">全表示</button>
        </div>
        <div class="sidebar-channel-actions">
          <button id="sidebar-channel-clear" type="button">全解除</button>
          <button id="sidebar-channel-terrestrial" type="button">地上波追加</button>
          <button id="sidebar-channel-bs" type="button">BS追加</button>
        </div>
        <input id="search" type="search" placeholder="チャンネル一覧検索…" value="${escapeAttr(sidebarSearchText)}" />
        <div id="sidebar-channel-options"></div>
      </div>
      <div class="sidebar-settings-section">
        <button id="sidebar-reset" type="button">設定リセット</button>
      </div>
    </div>
    <ul id="ch-list"></ul>
  </aside>
  <main id="main">
    <div id="stage">
      <div id="overlay-area"></div>
      <div id="program-banner"></div>
      <div id="stage-info"></div>
      <div id="settings-btn" title="設定">⚙</div>
      <div id="settings-panel" hidden>
        <div class="settings-row">
          <span>透過率</span>
          <input type="range" id="opacity-slider" min="0" max="100" step="1"
                 value="${Math.round(savedOpacity * 100)}" />
          <span id="opacity-val">${Math.round(savedOpacity * 100)}%</span>
        </div>
        <div class="settings-row">
          <span>文字サイズ</span>
          <input type="range" id="font-slider" min="50" max="200" step="10"
                 value="${Math.round(savedFontScale * 100)}" />
          <span id="font-val">${Math.round(savedFontScale * 100)}%</span>
        </div>
        <div class="settings-row">
          <span>流れる速さ</span>
          <input type="range" id="speed-slider" min="2" max="12" step="1"
                 value="${Math.round(savedScrollDuration / 1000)}" />
          <span id="speed-val">${Math.round(savedScrollDuration / 1000)}秒</span>
        </div>
        <div class="settings-row">
          <span>表示範囲</span>
          <input type="range" id="scroll-range-slider" min="50" max="100" step="5"
                 value="${Math.round(savedScrollRange * 100)}" />
          <span id="scroll-range-val">${Math.round(savedScrollRange * 100)}%</span>
        </div>
        <div class="settings-row">
          <span>同時表示数</span>
          <input type="range" id="scroll-limit-slider" min="50" max="400" step="10"
                 value="${savedScrollLimit}" />
          <span id="scroll-limit-val">${savedScrollLimit}</span>
        </div>
        <div class="settings-divider"></div>
        <div class="settings-section-label">NGユーザー <span id="ng-count"></span></div>
        <ul id="ng-list"></ul>
        <div id="ng-empty">なし</div>
        <div class="settings-divider"></div>
        <div class="settings-section-label">ニコニコアカウント</div>
        <div class="settings-cookie-row">
          <input type="email" id="login-email" placeholder="メールアドレス" autocomplete="email" />
        </div>
        <div class="settings-cookie-row">
          <input type="password" id="login-password" placeholder="パスワード" autocomplete="current-password" />
          <button id="login-btn">ログイン</button>
        </div>
        <div id="mfa-area" hidden>
          <div class="settings-cookie-row">
            <input type="text" id="otp-input" placeholder="6桁のワンタイムパスワード"
                   maxlength="6" inputmode="numeric" autocomplete="one-time-code" />
            <button id="otp-btn">送信</button>
          </div>
          <label class="otp-trust-label">
            <input type="checkbox" id="otp-trust" checked /> この端末を信頼する
          </label>
        </div>
        <div id="cookie-status"></div>
        <div id="cookie-hint">2段階認証に対応</div>
      </div>
    </div>
    <section id="schedule-view" hidden>
      <div id="schedule-toolbar">
        <span id="schedule-status"></span>
        <button id="schedule-settings-btn" title="番組表設定" type="button">⚙</button>
      </div>
      <div id="schedule-settings-panel" hidden>
        <div class="schedule-settings-head">
          <span>ジャンル</span>
          <button id="schedule-genre-clear" type="button">解除</button>
        </div>
        <div id="schedule-genre-options"></div>
        <div class="schedule-settings-divider"></div>
        <div class="schedule-settings-head">
          <span>チャンネル</span>
          <button id="schedule-channel-all" type="button">全表示</button>
        </div>
        <div class="schedule-channel-actions">
          <button id="schedule-channel-clear" type="button">全解除</button>
          <button id="schedule-channel-terrestrial" type="button">地上波追加</button>
          <button id="schedule-channel-bs" type="button">BS追加</button>
        </div>
        <input id="schedule-channel-search" type="search" placeholder="チャンネル検索" />
        <div id="schedule-channel-options"></div>
      </div>
      <div id="schedule-scroll">
        <div id="schedule-grid"></div>
      </div>
    </section>
    <div id="comment-panel">
      <div id="comment-list-wrap"><ul id="comment-list"></ul></div>
      <div id="post-area" hidden>
        <button id="cmd-toggle" title="コマンドを選択" type="button">▼</button>
        <div id="cmd-palette" hidden>
          <div class="cmd-head">
            <span>コマンド</span>
            <button id="cmd-reset" type="button">デフォルト</button>
          </div>
          <div class="cmd-label">色</div>
          <div class="cmd-row" id="cmd-colors"></div>
          <div class="cmd-label">位置</div>
          <div class="cmd-row cmd-segments" id="cmd-positions"></div>
          <div class="cmd-label">サイズ</div>
          <div class="cmd-row cmd-segments" id="cmd-sizes"></div>
          <div class="cmd-label">直接入力</div>
          <input id="post-mail" type="text" placeholder="コマンドを入力" />
        </div>
        <input id="post-text" type="text" placeholder="コメントを入力 (75文字)" maxlength="75" />
        <button id="post-anon" class="anon-on" title="匿名 ON/OFF">184</button>
        <button id="post-btn">投稿</button>
      </div>
      <div id="status-bar"></div>
    </div>
  </main>
</div>
`

const chListEl    = document.getElementById('ch-list')!
const searchEl    = document.getElementById('search') as HTMLInputElement
const sidebarSettingsBtn = document.getElementById('sidebar-settings-btn')!
const sidebarSettingsPanel = document.getElementById('sidebar-settings-panel') as HTMLElement
const sidebarReset = document.getElementById('sidebar-reset')!
const sidebarChannelAll = document.getElementById('sidebar-channel-all')!
const sidebarChannelClear = document.getElementById('sidebar-channel-clear')!
const sidebarChannelTerrestrial = document.getElementById('sidebar-channel-terrestrial')!
const sidebarChannelBs = document.getElementById('sidebar-channel-bs')!
const sidebarChannelOptions = document.getElementById('sidebar-channel-options')!
const themeSelect = document.getElementById('theme-select') as HTMLSelectElement
const watchTab     = document.getElementById('watch-tab')!
const scheduleTab  = document.getElementById('schedule-tab')!
const overlayArea = document.getElementById('overlay-area')!
const programBanner = document.getElementById('program-banner')!
const stageInfo   = document.getElementById('stage-info')!
const commentList = document.getElementById('comment-list')!
const statusBar   = document.getElementById('status-bar')!
const settingsBtn = document.getElementById('settings-btn')!
const settingsPanel = document.getElementById('settings-panel') as HTMLElement
const opacitySlider = document.getElementById('opacity-slider') as HTMLInputElement
const opacityVal    = document.getElementById('opacity-val')!
const ngListEl      = document.getElementById('ng-list')!
const ngEmpty       = document.getElementById('ng-empty')!
const ngCount       = document.getElementById('ng-count')!
const fontSlider    = document.getElementById('font-slider') as HTMLInputElement
const fontVal       = document.getElementById('font-val')!
const speedSlider   = document.getElementById('speed-slider') as HTMLInputElement
const speedVal      = document.getElementById('speed-val')!
const scrollRangeSlider = document.getElementById('scroll-range-slider') as HTMLInputElement
const scrollRangeVal = document.getElementById('scroll-range-val')!
const scrollLimitSlider = document.getElementById('scroll-limit-slider') as HTMLInputElement
const scrollLimitVal = document.getElementById('scroll-limit-val')!
const channelProgramGenreColorInput = document.getElementById('channel-program-genre-color') as HTMLInputElement
const loginEmail    = document.getElementById('login-email') as HTMLInputElement
const loginPassword = document.getElementById('login-password') as HTMLInputElement
const loginBtn      = document.getElementById('login-btn')!
const cookieStatus  = document.getElementById('cookie-status')!
const mfaArea       = document.getElementById('mfa-area') as HTMLElement
const otpInput      = document.getElementById('otp-input') as HTMLInputElement
const otpBtn        = document.getElementById('otp-btn')!
const otpTrust      = document.getElementById('otp-trust') as HTMLInputElement
const postArea      = document.getElementById('post-area') as HTMLElement
const postMail      = document.getElementById('post-mail') as HTMLInputElement
const cmdToggle     = document.getElementById('cmd-toggle')!
const cmdPalette    = document.getElementById('cmd-palette') as HTMLElement
const cmdReset      = document.getElementById('cmd-reset')!
const cmdColors     = document.getElementById('cmd-colors')!
const cmdPositions  = document.getElementById('cmd-positions')!
const cmdSizes      = document.getElementById('cmd-sizes')!
const postText      = document.getElementById('post-text') as HTMLInputElement
const postAnonBtn   = document.getElementById('post-anon')!
const postBtn       = document.getElementById('post-btn')!
const stageEl       = document.getElementById('stage') as HTMLElement
const commentPanel  = document.getElementById('comment-panel') as HTMLElement
const scheduleView  = document.getElementById('schedule-view') as HTMLElement
const scheduleStatus = document.getElementById('schedule-status')!
const scheduleSettingsBtn = document.getElementById('schedule-settings-btn')!
const scheduleSettingsPanel = document.getElementById('schedule-settings-panel') as HTMLElement
const scheduleGenreOptions = document.getElementById('schedule-genre-options')!
const scheduleGenreClear = document.getElementById('schedule-genre-clear')!
const scheduleChannelAll = document.getElementById('schedule-channel-all')!
const scheduleChannelClear = document.getElementById('schedule-channel-clear')!
const scheduleChannelTerrestrial = document.getElementById('schedule-channel-terrestrial')!
const scheduleChannelBs = document.getElementById('schedule-channel-bs')!
const scheduleChannelSearch = document.getElementById('schedule-channel-search') as HTMLInputElement
const scheduleChannelOptions = document.getElementById('schedule-channel-options')!
const scheduleScroll = document.getElementById('schedule-scroll')!
const scheduleGrid  = document.getElementById('schedule-grid')!

chListEl.addEventListener('pointerenter', () => { channelListPointerInside = true })
chListEl.addEventListener('pointerleave', () => {
  channelListPointerInside = false
  if (channelListRenderPending) {
    channelListRenderPending = false
    renderChannelList(true)
  }
})

// ─── Settings ─────────────────────────────────────────────────────────────────
settingsBtn.addEventListener('click', () => {
  settingsPanel.hidden = !settingsPanel.hidden
})
sidebarSettingsBtn.addEventListener('click', () => {
  sidebarSettingsPanel.hidden = !sidebarSettingsPanel.hidden
})
sidebarChannelAll.addEventListener('click', () => setSidebarChannelFilters(channels.map(ch => ch.video)))
sidebarChannelClear.addEventListener('click', () => setSidebarChannelFilters([]))
sidebarChannelTerrestrial.addEventListener('click', () =>
  addSidebarChannelFilters(channels.filter(ch => !ch.bs).map(ch => ch.video)))
sidebarChannelBs.addEventListener('click', () =>
  addSidebarChannelFilters(channels.filter(ch => ch.bs).map(ch => ch.video)))
themeSelect.addEventListener('change', () => {
  savedTheme = themeSelect.value
  localStorage.setItem(LS_THEME, savedTheme)
  document.documentElement.dataset.theme = savedTheme
})
sidebarReset.addEventListener('click', () => {
  if (!confirm('チャンネル一覧の検索・表示設定・折りたたみ状態をリセットしますか？'))
    return
  sidebarSearchText = ''
  searchEl.value = ''
  channelProgramGenreColor = false
  channelProgramGenreColorInput.checked = false
  collapsedChannels.clear()
  sidebarChannelFilters.clear()
  for (const ch of channels)
    sidebarChannelFilters.add(ch.video)
  localStorage.removeItem(LS_SIDEBAR_SEARCH)
  localStorage.setItem(LS_CHANNEL_PROGRAM_GENRE_COLOR, '0')
  saveSidebarChannelFilters()
  saveCollapsedChannels()
  applyChannelProgramGenreColorSetting()
  renderSidebarChannelOptions()
  renderChannelList(true)
})

function applyChannelProgramGenreColorSetting() {
  app.classList.toggle('channel-program-genre-color', channelProgramGenreColor)
}

channelProgramGenreColorInput.addEventListener('change', () => {
  channelProgramGenreColor = channelProgramGenreColorInput.checked
  localStorage.setItem(LS_CHANNEL_PROGRAM_GENRE_COLOR, channelProgramGenreColor ? '1' : '0')
  applyChannelProgramGenreColorSetting()
})

watchTab.addEventListener('click', () => setViewMode('watch'))
scheduleTab.addEventListener('click', () => setViewMode('schedule'))
scheduleSettingsBtn.addEventListener('click', () => {
  scheduleSettingsPanel.hidden = !scheduleSettingsPanel.hidden
})
scheduleGenreClear.addEventListener('click', () => {
  scheduleGenreFilters.clear()
  saveScheduleGenreFilters()
  renderScheduleGenreOptions()
  renderSchedule()
})
scheduleChannelAll.addEventListener('click', () =>
  setScheduleChannelFilters(sourceScheduleChannels().map(ch => ch.video)))
scheduleChannelClear.addEventListener('click', () => setScheduleChannelFilters([]))
scheduleChannelTerrestrial.addEventListener('click', () => {
  addScheduleChannelFilters(sourceScheduleChannels().filter(ch => !ch.bs).map(ch => ch.video))
})
scheduleChannelBs.addEventListener('click', () => {
  addScheduleChannelFilters(sourceScheduleChannels().filter(ch => ch.bs).map(ch => ch.video))
})
scheduleChannelSearch.addEventListener('input', () => {
  scheduleChannelSearchText = scheduleChannelSearch.value
  renderScheduleChannelOptions()
})

function setViewMode(mode: ViewMode) {
  viewMode = mode
  watchTab.classList.toggle('active', mode === 'watch')
  scheduleTab.classList.toggle('active', mode === 'schedule')
  stageEl.hidden = mode !== 'watch'
  commentPanel.hidden = mode !== 'watch'
  scheduleView.hidden = mode !== 'schedule'
  if (mode === 'schedule' && !schedule && !scheduleLoading)
    fetchSchedule()
}

opacitySlider.addEventListener('input', () => {
  savedOpacity = Number(opacitySlider.value) / 100
  opacityVal.textContent = Math.round(savedOpacity * 100) + '%'
  localStorage.setItem(LS_OPACITY, String(savedOpacity))
  if (overlay) overlay.opacity = savedOpacity
})

fontSlider.addEventListener('input', () => {
  savedFontScale = Number(fontSlider.value) / 100
  fontVal.textContent = Math.round(savedFontScale * 100) + '%'
  localStorage.setItem(LS_FONT_SCALE, String(savedFontScale))
  if (overlay) overlay.fontScale = savedFontScale
})

speedSlider.addEventListener('input', () => {
  savedScrollDuration = Number(speedSlider.value) * 1000
  speedVal.textContent = speedSlider.value + '秒'
  localStorage.setItem(LS_SCROLL_SPEED, String(savedScrollDuration))
  if (overlay) overlay.scrollDuration = savedScrollDuration
})

scrollRangeSlider.addEventListener('input', () => {
  savedScrollRange = Number(scrollRangeSlider.value) / 100
  scrollRangeVal.textContent = scrollRangeSlider.value + '%'
  localStorage.setItem(LS_SCROLL_RANGE, String(savedScrollRange))
  if (overlay) overlay.scrollRange = savedScrollRange
})

scrollLimitSlider.addEventListener('input', () => {
  savedScrollLimit = Number(scrollLimitSlider.value)
  scrollLimitVal.textContent = String(savedScrollLimit)
  localStorage.setItem(LS_SCROLL_LIMIT, String(savedScrollLimit))
  if (overlay) overlay.maxScrollComments = savedScrollLimit
})

// ─── Login
loginEmail.value = savedLoginEmail

let pendingMfaToken = ""
let pendingLoginEmail = ""

function updateLoginUi() {
  loginBtn.textContent = userCookie ? 'ログアウト' : 'ログイン'
  if (userCookie) {
    cookieStatus.textContent = '✓ ログイン済み'
    cookieStatus.className = 'cookie-ok'
  }
  updatePostArea()
}

function saveLoginEmail(email: string) {
  savedLoginEmail = email
  localStorage.setItem(LS_LOGIN_EMAIL, email)
  loginEmail.value = email
}

function applySession(userSession: string, mfaTrustedDeviceToken?: string, email?: string) {
  userCookie = 'user_session=' + userSession
  localStorage.setItem(LS_COOKIE, userCookie)
  if (email) saveLoginEmail(email)
  if (mfaTrustedDeviceToken) {
    mfaTrustedToken = mfaTrustedDeviceToken
    localStorage.setItem(LS_MFA_TOKEN, mfaTrustedDeviceToken)
  }
  loginPassword.value = ''
  mfaArea.hidden = true
  cookieStatus.textContent = '✓ ログイン成功'
  cookieStatus.className = 'cookie-ok'
  loginBtn.textContent = 'ログアウト'
  updatePostArea()
  if (selectedChannel) connectWatchWs(selectedChannel)
}

function doLogout() {
  userCookie = ''
  localStorage.removeItem(LS_COOKIE)
  loginPassword.value = ''
  pendingMfaToken = ''
  pendingLoginEmail = ''
  mfaArea.hidden = true
  watchWs?.close()
  watchWs = null
  clearWatchKeepSeatTimer()
  vposBaseTime = null
  pendingOwnTexts.clear()
  cookieStatus.textContent = 'ログアウトしました'
  cookieStatus.className = 'cookie-none'
  loginBtn.textContent = 'ログイン'
  updatePostArea()
}

async function doLogin() {
  const email = loginEmail.value.trim()
  const password = loginPassword.value
  if (!email || !password) return
  loginBtn.textContent = '接続中…'
  loginBtn.setAttribute('disabled', '')
  cookieStatus.textContent = ''
  mfaArea.hidden = true
  try {
    const res = await fetch('/api/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password, mfaTrustedDeviceToken: mfaTrustedToken || null }),
    })
    const json = await res.json() as { userSession?: string; mfaTrustedDeviceToken?: string; mfaRequired?: boolean; mfaToken?: string; error?: string }
    if (json.error) {
      cookieStatus.textContent = json.error
      cookieStatus.className = 'cookie-none'
    } else if (json.mfaRequired && json.mfaToken) {
      pendingMfaToken = json.mfaToken
      pendingLoginEmail = email
      mfaArea.hidden = false
      otpInput.value = ''
      otpInput.focus()
      cookieStatus.textContent = '2段階認証コードを入力してください'
      cookieStatus.className = 'cookie-none'
    } else if (json.userSession) {
      applySession(json.userSession, json.mfaTrustedDeviceToken, email)
    }
  } catch {
    cookieStatus.textContent = 'ネットワークエラー'
    cookieStatus.className = 'cookie-none'
  } finally {
    loginBtn.textContent = userCookie ? 'ログアウト' : 'ログイン'
    loginBtn.removeAttribute('disabled')
  }
}

async function doMfa() {
  const otp = otpInput.value.trim()
  if (!otp || !pendingMfaToken) return
  otpBtn.textContent = '送信中…'
  otpBtn.setAttribute('disabled', '')
  try {
    const res = await fetch('/api/login/mfa', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mfaToken: pendingMfaToken, otp, trustDevice: otpTrust.checked }),
    })
    const json = await res.json() as { userSession?: string; mfaTrustedDeviceToken?: string; error?: string }
    if (json.error) {
      cookieStatus.textContent = json.error
      cookieStatus.className = 'cookie-none'
    } else if (json.userSession) {
      pendingMfaToken = ''
      applySession(json.userSession, json.mfaTrustedDeviceToken, pendingLoginEmail)
      pendingLoginEmail = ''
    }
  } catch {
    cookieStatus.textContent = 'ネットワークエラー'
    cookieStatus.className = 'cookie-none'
  } finally {
    otpBtn.textContent = '送信'
    otpBtn.removeAttribute('disabled')
  }
}

loginBtn.addEventListener('click', () => { if (userCookie) doLogout(); else doLogin() })
loginPassword.addEventListener('keydown', (e) => { if (e.key === 'Enter') doLogin() })
otpBtn.addEventListener('click', doMfa)
otpInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') doMfa() })

// ─── NG list
function renderNgList() {
  ngListEl.innerHTML = ""
  const ids = [...ngSet]
  ngEmpty.hidden = ids.length > 0
  ngCount.textContent = ids.length > 0 ? "(" + ids.length + ")" : ""
  for (const uid of ids) {
    const li = document.createElement("li")
    li.className = "ng-entry"
    const label = document.createElement("span")
    label.className = "ng-uid"
    label.textContent = uid.length > 16 ? uid.slice(0, 14) + "…" : uid
    label.title = uid
    const btn = document.createElement("button")
    btn.className = "ng-remove-btn"
    btn.textContent = "解除"
    btn.addEventListener("click", () => { ngSet.delete(uid); saveNg(); renderNgList() })
    li.append(label, btn)
    ngListEl.appendChild(li)
  }
}

function addNg(uid: string) {
  ngSet.add(uid)
  saveNg()
  renderNgList()
  commentList.querySelectorAll<HTMLElement>("[data-uid]").forEach(el => {
    if (el.dataset.uid === uid) el.remove()
  })
}

renderNgList()

// ─── Channel list ─────────────────────────────────────────────────────────────
async function fetchStatus() {
  try {
    const res = await fetch('/api/status')
    const data: StatusResponse = await res.json()
    channels = mergeStatusChannels(data.channels)
    renderChannelList()
    updateProgramBanner()
    updateStageInfo()
  } catch { /* サーバー未起動時は無視 */ }
}

function mergeStatusChannels(nextChannels: ChannelInfo[]) {
  const merged = nextChannels.map(ch => applyStreamStateToChannel(ch))
  renderScheduleChannelOptions()
  renderSidebarChannelOptions()
  return merged
}

function connectChannelsStream() {
  channelsWs?.close()
  const proto = location.protocol === 'https:' ? 'wss' : 'ws'
  channelsWs = new WebSocket(`${proto}://${location.host}/api/channels/ws`)
  channelsWs.onmessage = e => {
    try {
      handleChannelsStreamMessage(JSON.parse(e.data) as ChannelsStreamMessage)
    } catch { /* ignore */ }
  }
  channelsWs.onclose = () => {
    setTimeout(() => {
      if (!channelsWs || channelsWs.readyState === WebSocket.CLOSED)
        connectChannelsStream()
    }, 5000)
  }
}

function handleChannelsStreamMessage(message: ChannelsStreamMessage) {
  if (message.type === 'snapshot') {
    channels = message.channels.map(channel => applyStreamStateToChannel({
      id: channel.id,
      name: channel.name,
      video: channel.video,
      bs: channel.bs,
      running: false,
      currentTarget: null,
      force: channel.force,
      viewers: channel.viewers,
      totalComments: channel.comments,
      lastResNo: channel.lastResNo,
      program: channel.program,
      sources: channel.sources,
    }))
    applyChannelStats(message.channels)
    applyChannelPrograms(message.channels)
  } else if (message.type === 'stats') {
    applyChannelStats(message.channels)
  } else if (message.type === 'programs') {
    applyChannelPrograms(message.channels)
    if (viewMode === 'schedule')
      fetchSchedule()
  }
  renderChannelList()
  updateProgramBanner()
  updateStageInfo()
}

function applyChannelStats(stats: Array<{ video: string; force: number; viewers: number; comments: number; lastResNo: number }>) {
  for (const stat of stats) {
    streamStats.set(stat.video, {
      force: stat.force,
      viewers: stat.viewers,
      comments: stat.comments,
      lastResNo: stat.lastResNo,
    })
  }
  channels = channels.map(ch => applyStreamStateToChannel(ch))
}

function applyStreamStateToChannel(channel: ChannelInfo): ChannelInfo {
  const stat = streamStats.get(channel.video)
  const program = streamPrograms.has(channel.video)
    ? streamPrograms.get(channel.video) ?? null
    : channel.program ?? null

  if (!stat) return { ...channel, program }

  const sources = channel.sources.map(source => ({ ...source }))
  const configuredSources = sources.filter(source => source.configured)
  if (configuredSources.length === 1) {
    configuredSources[0].force = stat.force
    configuredSources[0].viewers = stat.viewers
    configuredSources[0].totalComments = stat.comments
    configuredSources[0].lastResNo = stat.lastResNo
  }

  return {
    ...channel,
    force: stat.force,
    viewers: stat.viewers,
    totalComments: stat.comments,
    lastResNo: stat.lastResNo,
    program,
    sources,
  }
}

function applyChannelPrograms(programs: Array<{ video: string; program: ProgramInfo | null }>) {
  for (const program of programs)
    streamPrograms.set(program.video, program.program)
  channels = channels.map(ch => applyStreamStateToChannel(ch))
}

function renderChannelList(force = false) {
  if (!force && updateChannelListItems())
    return
  if (!force && channelListPointerInside) {
    channelListRenderPending = true
    return
  }
  channelListRenderPending = false
  ensureSidebarChannelFiltersInitialized()
  const q = searchEl.value.toLowerCase()
  const filtered = channels.filter(c => {
    if (!sidebarChannelFilters.has(c.video))
      return false
    return c.name.toLowerCase().includes(q) ||
      c.video.includes(q) ||
      c.sources.some(s => s.key.toLowerCase().includes(q))
  })
  chListEl.innerHTML = ''
  const groups = [
    { label: '地上波', items: filtered.filter(c => !c.bs) },
    { label: 'BS',    items: filtered.filter(c => c.bs) },
  ]
  for (const g of groups) {
    if (!g.items.length) continue
    const header = document.createElement('li')
    header.className = 'group-header'
    header.textContent = g.label
    chListEl.appendChild(header)
    for (const ch of g.items) {
      const sources = ch.sources
      const configuredSources = sources.filter(s => s.configured)

      if (configuredSources.length > 1) {
        const group = document.createElement('li')
        group.className = 'ch-group-label'
        group.dataset.channelVideo = ch.video
        const collapsed = collapsedChannels.has(ch.video)
        group.classList.toggle('collapsed', collapsed)
        const groupDot = document.createElement('span')
        groupDot.dataset.role = 'dot'
        groupDot.className = 'dot ' + groupStatusClass(configuredSources)
        groupDot.textContent = groupDot.classList.contains('on') ? '●' : '○'
        const groupName = document.createElement('span')
        groupName.className = 'ch-group-name ch-name-wrap'
        appendChannelNameAndProgram(groupName, ch)
        const toggle = document.createElement('span')
        toggle.className = 'ch-group-toggle' + (collapsed ? ' collapsed' : '')
        group.append(groupDot, groupName, toggle)
        group.addEventListener('click', () => {
          if (collapsedChannels.has(ch.video)) collapsedChannels.delete(ch.video)
          else collapsedChannels.add(ch.video)
          saveCollapsedChannels()
          renderChannelList(true)
        })
        chListEl.appendChild(group)
        if (!collapsed) {
          for (const source of sources)
            chListEl.appendChild(createSourceListItem(source, 'ch-sub grouped', ch.video))
        }
      } else {
        const source = configuredSources[0] ?? sources[0]
        const li = document.createElement('li')
        li.className = 'ch-item' +
          (source?.key === selectedChannel ? ' selected' : '') +
          (!source?.configured ? ' disabled' : '')
        li.dataset.video = source?.key ?? ch.video
        li.dataset.channelVideo = ch.video
        if (source?.key) li.dataset.sourceKey = source.key

        const dot = document.createElement('span')
        dot.dataset.role = 'dot'
        dot.className = 'dot ' + sourceStatusClass(source)
        dot.textContent = dot.classList.contains('on') || dot.classList.contains('pending') ? '●' : '○'

        const nameWrap = document.createElement('span')
        nameWrap.className = 'ch-name-wrap'
        const name = document.createElement('span')
        name.className = 'ch-name'
        name.textContent = ch.name
        nameWrap.append(name)
        appendProgramTitle(nameWrap, ch.program ?? null)
        if (source?.configured) {
          const sourceLine = document.createElement('span')
          sourceLine.className = 'ch-source-line'
          sourceLine.dataset.role = 'source-line'
          sourceLine.append(source.label)
          appendSourceTarget(sourceLine, source, true)
          nameWrap.append(sourceLine)
        }

        const forceEl = document.createElement('span')
        forceEl.className = 'force'
        forceEl.dataset.role = 'force'
        forceEl.textContent = source && source.force > 0 ? String(source.force) : ''

        li.append(dot, nameWrap, forceEl)
        if (source?.configured) li.addEventListener('click', () => selectChannel(source.key))
        chListEl.appendChild(li)
      }
    }
  }
  if (filtered.length === 0) {
    const empty = document.createElement('li')
    empty.className = 'ch-empty'
    empty.textContent = sidebarChannelFilters.size === 0
      ? '表示チャンネルが選択されていません'
      : '該当するチャンネルがありません'
    chListEl.appendChild(empty)
  }
}

function updateChannelListItems() {
  if (chListEl.children.length === 0) return false

  const channelsByVideo = new Map(channels.map(channel => [channel.video, channel]))
  let missing = false

  chListEl.querySelectorAll<HTMLElement>('[data-channel-video]').forEach(el => {
    const channel = channelsByVideo.get(el.dataset.channelVideo ?? '')
    if (!channel) {
      missing = true
      return
    }

    if (el.classList.contains('ch-group-label')) {
      updateGroupListItem(el, channel)
      return
    }

    const sourceKey = el.dataset.sourceKey
    const source = sourceKey ? channel.sources.find(item => item.key === sourceKey) : undefined
    if (!source) {
      missing = true
      return
    }
    updateSourceListItem(el, source, channel.program ?? null)
  })

  if (missing && !channelListPointerInside)
    renderChannelList(true)
  return !missing
}

function updateGroupListItem(el: HTMLElement, channel: ChannelInfo) {
  const configuredSources = channel.sources.filter(source => source.configured)
  const dot = el.querySelector<HTMLElement>('[data-role="dot"]')
  if (dot) {
    dot.className = 'dot ' + groupStatusClass(configuredSources)
    dot.textContent = dot.classList.contains('on') ? '●' : '○'
  }
  const nameWrap = el.querySelector<HTMLElement>('.ch-group-name')
  if (nameWrap)
    setProgramTitle(nameWrap, channel.program ?? null)
}

function updateSourceListItem(el: HTMLElement, source: ChannelSource, program: ProgramInfo | null) {
  el.classList.toggle('selected', source.key === selectedChannel)
  el.classList.toggle('disabled', !source.configured)
  el.classList.toggle('official-source', source.sourceType === 'official')
  el.classList.toggle('unofficial-source', source.sourceType === 'unofficial')
  el.classList.toggle('refuge-source', source.sourceType === 'refuge')
  el.classList.toggle('local-source', source.sourceType === 'local')

  const dot = el.querySelector<HTMLElement>('[data-role="dot"]')
  if (dot) {
    dot.className = 'dot ' + sourceStatusClass(source)
    dot.textContent = dot.classList.contains('on') || dot.classList.contains('pending') ? '●' : '○'
  }

  const nameWrap = el.querySelector<HTMLElement>('.ch-name-wrap')
  if (nameWrap)
    setProgramTitle(nameWrap, program)

  const typeLabel = el.querySelector<HTMLElement>('.ch-type-label')
  if (typeLabel)
    typeLabel.textContent = source.configured ? source.label : ''

  const sourceLine = el.querySelector<HTMLElement>('[data-role="source-line"]')
  if (sourceLine) {
    sourceLine.textContent = ''
    sourceLine.append(source.label)
    appendSourceTarget(sourceLine, source, true)
  }

  const target = el.querySelector<HTMLElement>('.ch-sub-target')
  if (target) {
    target.textContent = ''
    appendSourceTarget(target, source)
  }

  const force = el.querySelector<HTMLElement>('[data-role="force"]')
  if (force)
    force.textContent = source.force > 0 ? String(source.force) : ''
}

function appendChannelNameAndProgram(target: HTMLElement, channel: ChannelInfo) {
  const name = document.createElement('span')
  name.className = 'ch-name'
  name.textContent = channel.name
  target.append(name)
  appendProgramTitle(target, channel.program ?? null)
}

function appendProgramTitle(target: HTMLElement, program: ProgramInfo | null) {
  const title = createProgramTitle(program)
  if (title) target.append(title)
}

function setProgramTitle(target: HTMLElement, program: ProgramInfo | null) {
  target.querySelector('.ch-program-title')?.remove()
  const title = createProgramTitle(program)
  if (!title) return
  const name = target.querySelector('.ch-name')
  if (name?.nextSibling)
    target.insertBefore(title, name.nextSibling)
  else
    target.append(title)
}

function createProgramTitle(program: ProgramInfo | null) {
  if (!program?.title) return
  const title = document.createElement('span')
  title.className = `ch-program-title ${programGenreClass(program)}`
  title.textContent = program.title
  title.title = program.genreName ? `${program.genreName}: ${program.title}` : program.title
  return title
}

function programGenreClass(program: ProgramInfo | null | undefined) {
  return genreClass(program?.genreCode)
}

function genreClass(genreCode: string | null | undefined) {
  if (!genreCode) return 'genre-unknown'
  const normalized = genreCode.toLowerCase().replace('0x', '')
  return /^[0-9a-f]$/i.test(normalized) ? `genre-${normalized}` : 'genre-unknown'
}

async function fetchSchedule() {
  if (scheduleLoading) return
  scheduleLoading = true
  if (!schedule)
    scheduleStatus.textContent = '番組表データ取得中…'
  try {
    const res = await fetch('/api/programs/schedule')
    if (!res.ok) throw new Error(await res.text())
    const next = await res.json() as ScheduleResponse
    if (next.loaded || !schedule) {
      schedule = next
      schedulePendingDate = null
    } else {
      schedulePendingDate = next.date
    }
    renderSchedule()
  } catch {
    scheduleStatus.textContent = '取得失敗'
  } finally {
    scheduleLoading = false
  }
}

function renderSchedule() {
  if (!schedule) {
    scheduleGrid.innerHTML = ''
    return
  }
  ensureScheduleChannelFiltersInitialized()

  const start = new Date(schedule.startAt).getTime()
  const end = new Date(schedule.endAt).getTime()
  const totalMinutes = Math.max(1, Math.round((end - start) / 60000))
  const pxPerMinute = 2
  const bodyHeight = totalMinutes * pxPerMinute
  const channelsToShow = schedule.channels.filter(ch => {
    if (!scheduleChannelFilters.has(ch.video))
      return false
    return true
  })
  const hasGenreFilter = scheduleGenreFilters.size > 0

  scheduleGrid.innerHTML = ''
  scheduleGrid.classList.toggle('schedule-filtering', hasGenreFilter)
  scheduleGrid.style.setProperty('--schedule-height', `${bodyHeight}px`)
  scheduleGrid.style.setProperty('--schedule-columns', `96px repeat(${channelsToShow.length}, minmax(138px, 170px))`)

  const corner = document.createElement('div')
  corner.className = 'schedule-corner'
  corner.textContent = '時刻'
  scheduleGrid.append(corner)

  for (const ch of channelsToShow) {
    const header = document.createElement('button')
    header.type = 'button'
    header.className = 'schedule-channel'
    header.textContent = ch.name
    header.title = `${ch.name} (${ch.video})`
    header.addEventListener('click', () => selectFirstConfiguredSource(ch.video))
    scheduleGrid.append(header)
  }

  const timeAxis = document.createElement('div')
  timeAxis.className = 'schedule-time-axis'
  for (let minute = 0; minute <= totalMinutes; minute += 60) {
    const tick = document.createElement('div')
    tick.className = 'schedule-time'
    tick.style.top = `${minute * pxPerMinute}px`
    tick.textContent = formatScheduleTime(new Date(start + minute * 60000))
    timeAxis.append(tick)
  }
  scheduleGrid.append(timeAxis)

  if (channelsToShow.length === 0) {
    const emptySelection = document.createElement('div')
    emptySelection.className = 'schedule-waiting'
    emptySelection.textContent = '表示チャンネルが選択されていません'
    scheduleGrid.append(emptySelection)
  }

  for (const ch of channelsToShow) {
    const column = document.createElement('div')
    column.className = 'schedule-column'
    column.dataset.video = ch.video
    if (ch.programs.length === 0) {
      const empty = document.createElement('div')
      empty.className = 'schedule-empty'
      empty.textContent = '未取得'
      column.append(empty)
    }
    for (const program of ch.programs)
      column.append(createScheduleProgram(program, start, end, pxPerMinute))
    scheduleGrid.append(column)
  }

  if (!schedule.loaded) {
    const waiting = document.createElement('div')
    waiting.className = 'schedule-waiting'
    waiting.textContent = '番組表データ取得待ち'
    scheduleGrid.append(waiting)
  }

  const now = Date.now()
  if (start <= now && now < end) {
    const line = document.createElement('div')
    line.className = 'schedule-now'
    line.style.top = `${((now - start) / 60000) * pxPerMinute}px`
    scheduleGrid.append(line)
  }

  scheduleStatus.textContent = formatScheduleStatus()
  renderScheduleChannelOptions()
}

function formatScheduleStatus() {
  if (!schedule) return ''
  if (schedulePendingDate && schedulePendingDate !== schedule.date)
    return `放送日 ${schedule.date} / 新しい放送日 ${schedulePendingDate} の番組表を待機中`
  if (!schedule.loaded)
    return `放送日 ${schedule.date} / 番組表データ取得待ち`
  const updated = schedule.updatedAt
    ? new Date(schedule.updatedAt).toLocaleTimeString('ja-JP', { hour: '2-digit', minute: '2-digit', second: '2-digit' })
    : '-'
  return `放送日 ${schedule.date} / 番組表更新 ${updated}`
}

function currentBroadcastDateString() {
  const now = new Date()
  const date = new Date(now)
  if (now.getHours() < 5)
    date.setDate(date.getDate() - 1)
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}

function refreshScheduleClock() {
  if (viewMode !== 'schedule') return
  if (!schedule) {
    fetchSchedule()
    return
  }
  const currentBroadcastDate = currentBroadcastDateString()
  if (schedule.date !== currentBroadcastDate || schedulePendingDate) {
    fetchSchedule()
    return
  }
  renderSchedule()
}

function createScheduleProgram(program: ScheduleProgram, rangeStart: number, rangeEnd: number, pxPerMinute: number) {
  const programStart = new Date(program.startAt).getTime()
  const programEnd = new Date(program.endAt).getTime()
  const clippedStart = Math.max(rangeStart, programStart)
  const clippedEnd = Math.min(rangeEnd, programEnd)
  const top = Math.max(0, (clippedStart - rangeStart) / 60000 * pxPerMinute)
  const height = Math.max(12, (clippedEnd - clippedStart) / 60000 * pxPerMinute - 2)

  const card = document.createElement('div')
  const selectedByGenreFilter = !scheduleGenreFilters.size ||
    (program.genreCode !== null && scheduleGenreFilters.has(program.genreCode))
  card.className = `schedule-program ${genreClass(program.genreCode)}${selectedByGenreFilter ? '' : ' dimmed'}`
  card.style.top = `${top}px`
  card.style.height = `${height}px`
  card.title = `${formatScheduleTime(new Date(programStart))}-${formatScheduleTime(new Date(programEnd))} ${program.genreName ? `${program.genreName}: ` : ''}${program.title}`

  const time = document.createElement('div')
  time.className = 'schedule-program-time'
  time.textContent = `${formatScheduleTime(new Date(programStart))}-${formatScheduleTime(new Date(programEnd))}`
  const title = document.createElement('div')
  title.className = 'schedule-program-title'
  title.textContent = program.title
  card.append(time, title)
  return card
}

function formatScheduleTime(date: Date) {
  return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`
}

function selectFirstConfiguredSource(video: string) {
  const channel = channels.find(ch => ch.video === video)
  const source = channel?.sources.find(s => s.configured) ?? channel?.sources[0]
  if (source?.configured)
    selectChannel(source.key)
}

function createSourceListItem(source: ChannelSource, className: string, channelVideo: string) {
  const li = document.createElement('li')
  li.className = className +
    ' ' + sourceClass(source) +
    (source.key === selectedChannel ? ' selected' : '') +
    (!source.configured ? ' disabled' : '')
  li.dataset.sourceKey = source.key
  li.dataset.channelVideo = channelVideo

  const dot = document.createElement('span')
  dot.dataset.role = 'dot'
  dot.className = 'dot ' + sourceStatusClass(source)
  dot.textContent = dot.classList.contains('on') || dot.classList.contains('pending') ? '●' : '○'

  const label = document.createElement('span')
  label.className = 'ch-type-label'
  label.textContent = source.configured ? source.label : ''

  const target = document.createElement('span')
  target.className = 'ch-sub-target'
  appendSourceTarget(target, source)

  const force = document.createElement('span')
  force.className = 'force'
  force.dataset.role = 'force'
  force.textContent = source.force > 0 ? String(source.force) : ''

  li.append(dot, label, target, force)
  if (source.configured) li.addEventListener('click', () => selectChannel(source.key))
  return li
}

function appendSourceTarget(target: HTMLElement, source: ChannelSource, prefixSpace = false) {
  if (!source.configured) return
  if (source.status === 'retryWaiting') {
    target.append(`${prefixSpace ? ' ' : ''}取得失敗`)
    if (isNicovideoWatchTarget(source.currentTarget)) {
      target.append(' ')
      appendNicovideoLink(target, source.currentTarget)
    }
  } else if (source.isReserved && source.scheduledStartUtc) {
    target.append(`${prefixSpace ? ' ' : ''}${formatJst(source.scheduledStartUtc)} 予定`)
    if (isNicovideoWatchTarget(source.currentTarget)) {
      target.append(' ')
      appendNicovideoLink(target, source.currentTarget)
    }
  } else if (source.currentTarget && !source.isReserved) {
    if (prefixSpace) target.append(' ')
    if (isNicovideoWatchTarget(source.currentTarget))
      appendNicovideoLink(target, source.currentTarget)
    else if (source.sourceType === 'refuge')
      target.append(formatRefugeTarget(source.currentTarget))
    else
      target.append(source.currentTarget)
  }
}

function isNicovideoWatchTarget(target: string | null): target is string {
  return target !== null && /^(?:ch|lv)\d+$/.test(target)
}

function appendNicovideoLink(target: HTMLElement, watchTarget: string) {
  const link = document.createElement('a')
  link.href = `https://live.nicovideo.jp/watch/${watchTarget}`
  link.target = '_blank'
  link.rel = 'noreferrer'
  link.textContent = watchTarget
  link.addEventListener('click', e => e.stopPropagation())
  target.appendChild(link)
}

function formatRefugeTarget(target: string) {
  try {
    return new URL(target).hostname
  } catch {
    return target
  }
}

function sourceStatusClass(source: ChannelSource | undefined) {
  if (!source?.configured) return 'off'
  if (source.status === 'retryWaiting') return 'pending'
  if (source.sourceType === 'unofficial' && source.isReserved) return 'pending'
  return source.running ? 'on' : 'off'
}

function groupStatusClass(sources: ChannelSource[]) {
  if (sources.length === 0) return 'off'
  const connected = sources.filter(s => sourceStatusClass(s) === 'on').length
  const active = sources.filter(s => sourceStatusClass(s) !== 'off').length
  if (connected === sources.length) return 'on'
  if (active > 0) return 'pending'
  return 'off'
}

function sourceClass(source: ChannelSource) {
  if (source.sourceType === 'official') return 'official-source'
  if (source.sourceType === 'unofficial') return 'unofficial-source'
  if (source.sourceType === 'refuge') return 'refuge-source'
  if (source.sourceType === 'local') return 'local-source'
  return ''
}

searchEl.addEventListener('input', () => {
  sidebarSearchText = searchEl.value
  localStorage.setItem(LS_SIDEBAR_SEARCH, sidebarSearchText)
  renderChannelList(true)
})

scheduleScroll.addEventListener('mousedown', (e) => {
  if (e.button !== 0) return
  const target = e.target as HTMLElement
  if (target.closest('.schedule-channel, #schedule-settings-panel'))
    return
  scheduleDrag = {
    active: true,
    moved: false,
    startX: e.clientX,
    startY: e.clientY,
    scrollLeft: scheduleScroll.scrollLeft,
    scrollTop: scheduleScroll.scrollTop,
  }
  scheduleScroll.classList.add('dragging')
  e.preventDefault()
})
window.addEventListener('mousemove', (e) => {
  if (!scheduleDrag?.active) return
  const dx = e.clientX - scheduleDrag.startX
  const dy = e.clientY - scheduleDrag.startY
  if (Math.abs(dx) + Math.abs(dy) > 4)
    scheduleDrag.moved = true
  scheduleScroll.scrollLeft = scheduleDrag.scrollLeft - dx
  scheduleScroll.scrollTop = scheduleDrag.scrollTop - dy
})
window.addEventListener('mouseup', () => {
  if (!scheduleDrag?.active) return
  scheduleDrag = null
  scheduleScroll.classList.remove('dragging')
})

// ─── Channel / WebSocket ───────────────────────────────────────────────────────
function selectChannel(video: string) {
  setViewMode('watch')
  if (selectedChannel === video) return
  selectedChannel = video
  commentLogQueue = []
  commentLogFlushScheduled = false
  commentList.innerHTML = ''
  ws?.close()
  watchWs?.close()
  watchWs = null
  clearWatchKeepSeatTimer()
  vposBaseTime = null
  pendingOwnTexts.clear()
  overlay?.dispose()
  overlayArea.innerHTML = ''
  overlay = new CommentOverlay(overlayArea)
  overlay.opacity        = savedOpacity
  overlay.fontScale      = savedFontScale
  overlay.scrollDuration = savedScrollDuration
  overlay.scrollRange    = savedScrollRange
  overlay.maxScrollComments = savedScrollLimit
  wsState = 'connecting'
  reconnectAt = 0
  updateStageInfo()
  updateProgramBanner()
  updatePostArea()
  renderChannelList(true)
  connectWs(video)
  connectWatchWs(video)
}

function connectWs(video: string) {
  wsState = 'connecting'
  updateStageInfo()
  const source = findSourceByKey(video)
  ws = new WebSocket(toWebSocketUrl(source?.source.commentUrl ?? `/comment/${video}`), 'msg.nicovideo.jp#json')
  ws.onopen = () => {
    wsState = 'connected'
    reconnectAt = 0
    updateStageInfo()
    setStatus(`接続中: ${video}`)
  }
  ws.onmessage = (e) => {
    try {
      const obj = JSON.parse(e.data)
      if (obj.chat) handleChat(obj.chat as Chat)
    } catch { /* ignore */ }
  }
  ws.onclose = (e) => {
    if (selectedChannel !== video) return
    wsState = 'disconnected'
    reconnectAt = Date.now() + 5000
    updateStageInfo()
    setStatus(`切断 (${e.code}) — 5秒後に再接続`)
    setTimeout(() => { if (selectedChannel === video) connectWs(video) }, 5000)
  }
  ws.onerror = () => {
    wsState = 'disconnected'
    updateStageInfo()
    setStatus('接続エラー')
  }
}

function connectWatchWs(video: string) {
  watchWs?.close()
  watchWs = null
  clearWatchKeepSeatTimer()
  vposBaseTime = null
  pendingOwnTexts.clear()
  const accountlessPost = isSelectedAccountlessPostSource()
  if (!selectedChannel || (!userCookie && !accountlessPost)) return
  const source = findSourceByKey(video)
  watchWs = new WebSocket(toWebSocketUrl(source?.source.watchUrl ?? `/watch/${video}`))
  watchWs.onopen = () => {
    const data: { room: { commentable: boolean }; cookie?: string } = { room: { commentable: true } }
    if (userCookie) data.cookie = userCookie
    watchWs!.send(JSON.stringify({
      data
    }))
  }
  watchWs.onmessage = (e) => {
    try {
      const obj = JSON.parse(e.data)
      if (obj.type === 'seat')
        startWatchKeepSeat(obj.data?.keepIntervalSec)
      if (obj.type === 'room' && obj.data?.vposBaseTime)
        vposBaseTime = new Date(obj.data.vposBaseTime).getTime()
      if (obj.type === 'postCommentResult') setStatus('投稿完了')
      if (obj.type === 'error')
        setStatus('投稿失敗: ' + (obj.data?.code ?? '不明'))
    } catch { /* ignore */ }
  }
  watchWs.onclose = () => {
    clearWatchKeepSeatTimer()
    if (selectedChannel === video)
      setTimeout(() => { if (selectedChannel === video) connectWatchWs(video) }, 10000)
  }
}

function clearWatchKeepSeatTimer() {
  if (watchKeepSeatTimer === null) return
  window.clearInterval(watchKeepSeatTimer)
  watchKeepSeatTimer = null
}

function startWatchKeepSeat(keepIntervalSec: unknown) {
  const sec = typeof keepIntervalSec === 'number' && Number.isFinite(keepIntervalSec)
    ? Math.max(5, Math.min(300, keepIntervalSec))
    : 30
  clearWatchKeepSeatTimer()
  watchKeepSeatTimer = window.setInterval(() => {
    if (!watchWs || watchWs.readyState !== WebSocket.OPEN) return
    watchWs.send(JSON.stringify({ type: 'keepSeat' }))
  }, sec * 1000)
}

// ─── Comments ─────────────────────────────────────────────────────────────────
function handleChat(chat: Chat) {
  if (ngSet.has(chat.user_id)) return
  const postedAt = pendingOwnTexts.get(chat.content)
  const isOwn = postedAt !== undefined && Date.now() - postedAt < 15000
  if (isOwn) pendingOwnTexts.delete(chat.content)
  overlay?.add(chat, isOwn)
  if (COMMENT_LOG_ENABLED)
    enqueueCommentLog(chat)
}

function enqueueCommentLog(chat: Chat) {
  commentLogQueue.push(chat)
  if (commentLogFlushScheduled) return
  commentLogFlushScheduled = true
  requestAnimationFrame(flushCommentLog)
}

function flushCommentLog() {
  commentLogFlushScheduled = false
  if (commentLogQueue.length === 0) return

  const wrap = commentList.parentElement!
  const shouldStickToBottom = wrap.scrollHeight - wrap.scrollTop - wrap.clientHeight < 60
  const fragment = document.createDocumentFragment()
  const chats = commentLogQueue.splice(0, COMMENT_LOG_FLUSH_LIMIT)

  for (const chat of chats)
    fragment.append(createCommentLogEntry(chat))

  commentList.appendChild(fragment)
  while (commentList.children.length > COMMENT_LOG_MAX_ITEMS)
    commentList.removeChild(commentList.firstChild!)

  if (shouldStickToBottom)
    wrap.scrollTop = wrap.scrollHeight

  if (commentLogQueue.length > 0) {
    commentLogFlushScheduled = true
    requestAnimationFrame(flushCommentLog)
  }
}

function createCommentLogEntry(chat: Chat) {
  const li = document.createElement('li')
  li.className = 'log-entry'
  li.dataset.uid = chat.user_id
  const time = new Date(chat.date * 1000).toLocaleTimeString('ja-JP',
    { hour: '2-digit', minute: '2-digit', second: '2-digit' })
  const timeEl = document.createElement('span')
  timeEl.className = 'log-time'
  timeEl.textContent = time
  const textEl = document.createElement('span')
  textEl.className = 'log-text'
  textEl.textContent = chat.content
  const ngBtn = document.createElement('button')
  ngBtn.className = 'ng-btn'
  ngBtn.textContent = 'NG'
  ngBtn.title = 'NG: ' + chat.user_id
  ngBtn.addEventListener('click', (e) => { e.stopPropagation(); addNg(chat.user_id) })
  li.append(timeEl, textEl, ngBtn)
  return li
}

// ─── Post comment ───────────────────────────────────────────────────────────
function updatePostArea() {
  postArea.hidden = !(selectedChannel && (userCookie || isSelectedAccountlessPostSource()))
}

function toWebSocketUrl(pathOrUrl: string) {
  if (pathOrUrl.startsWith('ws://') || pathOrUrl.startsWith('wss://'))
    return pathOrUrl
  const base = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}`
  return new URL(pathOrUrl, base).toString()
}

function calcVpos(): number {
  if (!vposBaseTime) return 0
  return Math.max(0, Math.round((Date.now() - vposBaseTime) / 10))
}

const POST_COLORS = [
  { command: 'white', label: '白', swatch: '#f5f5f5' },
  { command: 'red', label: '赤', swatch: '#f44336' },
  { command: 'pink', label: '桃', swatch: '#ff80ab' },
  { command: 'orange', label: '橙', swatch: '#ff9800' },
  { command: 'yellow', label: '黄', swatch: '#ffeb3b' },
  { command: 'green', label: '緑', swatch: '#4caf50' },
  { command: 'cyan', label: '水', swatch: '#00bcd4' },
  { command: 'blue', label: '青', swatch: '#2196f3' },
  { command: 'purple', label: '紫', swatch: '#9c27b0' },
  { command: 'black', label: '黒', swatch: '#111' },
  { command: 'niconicowhite', label: 'ニ白', swatch: '#fff' },
  { command: 'maroon', label: '濃赤', swatch: '#800000' },
  { command: 'darkblue', label: '濃青', swatch: '#0b2d7a' },
  { command: 'olive', label: 'オリーブ', swatch: '#808000' },
  { command: 'teal', label: '青緑', swatch: '#008080' },
  { command: 'navy', label: '紺', swatch: '#001f54' },
]
const POST_POSITIONS = [
  { command: 'naka', label: 'なし' },
  { command: 'ue', label: '上' },
  { command: 'shita', label: '下' },
]
const POST_SIZES = [
  { command: 'medium', label: '中' },
  { command: 'small', label: '小' },
  { command: 'big', label: '大' },
]
const POST_COLOR_COMMANDS = new Set(POST_COLORS.map(c => c.command))
const POST_POSITION_COMMANDS = new Set(POST_POSITIONS.map(c => c.command))
const POST_SIZE_COMMANDS = new Set(POST_SIZES.map(c => c.command))
const POST_KNOWN_COMMANDS = new Set([
  ...POST_COLOR_COMMANDS,
  ...POST_POSITION_COMMANDS,
  ...POST_SIZE_COMMANDS,
  'defont',
])

function parseCommandState(mail: string) {
  let color = 'white'
  let position = 'naka'
  let size = 'medium'
  let font = 'defont'
  const extras: string[] = []
  for (const raw of mail.toLowerCase().split(/\s+/).filter(Boolean)) {
    if (raw.startsWith('#') && /^#[0-9a-f]{6}$/.test(raw)) { color = raw; continue }
    if (POST_COLOR_COMMANDS.has(raw)) { color = raw; continue }
    if (POST_POSITION_COMMANDS.has(raw)) { position = raw; continue }
    if (POST_SIZE_COMMANDS.has(raw)) { size = raw; continue }
    if (raw === 'defont') { font = raw; continue }
    if (!POST_KNOWN_COMMANDS.has(raw)) extras.push(raw)
  }
  return { color, position, size, font, extras }
}

function buildPostMail(command: { color: string; position: string; size: string; font: string; extras: string[] }) {
  return [
    command.color === 'white' ? '' : command.color,
    command.position === 'naka' ? '' : command.position,
    command.size === 'medium' ? '' : command.size,
    command.font === 'defont' ? '' : command.font,
    ...command.extras,
  ]
    .filter(Boolean)
    .join(' ')
}

function compactPostMail(mail: string) {
  return buildPostMail(parseCommandState(mail))
}

function setCommandPart(kind: 'color' | 'position' | 'size', value: string) {
  const state = parseCommandState(postMail.value)
  state[kind] = value
  postMail.value = buildPostMail(state)
  savePostMail()
  updateCommandPaletteActive()
  postText.focus()
}

function savePostMail() {
  savedPostMail = compactPostMail(postMail.value)
  postMail.value = savedPostMail
  localStorage.setItem(LS_POST_MAIL, savedPostMail)
  updateCommandToggleState()
}

function resetPostMail() {
  postMail.value = ''
  savePostMail()
  updateCommandPaletteActive()
  postText.focus()
}

function showCommandPalette(show = true) {
  cmdPalette.hidden = !show
}

function updateCommandPaletteActive() {
  const state = parseCommandState(postMail.value)
  cmdPalette.querySelectorAll<HTMLButtonElement>('[data-command]').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.command === state.color ||
      btn.dataset.command === state.position ||
      btn.dataset.command === state.size)
  })
  updateCommandToggleState()
}

function swatchForColor(command: string) {
  if (command.startsWith('#') && /^#[0-9a-f]{6}$/.test(command))
    return command
  return POST_COLORS.find(color => color.command === command)?.swatch ?? '#f5f5f5'
}

function effectiveCommandText() {
  return compactPostMail(postMail.value) || 'デフォルト'
}

function updateCommandToggleState() {
  const state = parseCommandState(postMail.value)
  const posMark = state.position === 'ue' ? '▲' : state.position === 'shita' ? '▼' : '▶'
  const custom = state.color !== 'white' || state.position !== 'naka' ||
    state.size !== 'medium' || state.extras.length > 0
  cmdToggle.classList.toggle('cmd-custom', custom)
  cmdToggle.classList.toggle('cmd-small', state.size === 'small')
  cmdToggle.classList.toggle('cmd-big', state.size === 'big')
  cmdToggle.style.setProperty('--cmd-color', swatchForColor(state.color))
  cmdToggle.textContent = posMark
  cmdToggle.title = effectiveCommandText()
}

function buildCommandPalette() {
  for (const color of POST_COLORS) {
    const btn = document.createElement('button')
    btn.className = 'cmd-color'
    btn.type = 'button'
    btn.dataset.command = color.command
    btn.title = color.command
    btn.style.setProperty('--swatch', color.swatch)
    btn.textContent = color.label
    btn.addEventListener('click', () => setCommandPart('color', color.command))
    cmdColors.appendChild(btn)
  }
  for (const pos of POST_POSITIONS) {
    const btn = document.createElement('button')
    btn.type = 'button'
    btn.dataset.command = pos.command
    btn.textContent = pos.label
    btn.title = pos.command
    btn.addEventListener('click', () => setCommandPart('position', pos.command))
    cmdPositions.appendChild(btn)
  }
  for (const size of POST_SIZES) {
    const btn = document.createElement('button')
    btn.type = 'button'
    btn.dataset.command = size.command
    btn.textContent = size.label
    btn.title = size.command
    btn.addEventListener('click', () => setCommandPart('size', size.command))
    cmdSizes.appendChild(btn)
  }
  updateCommandPaletteActive()
}

function parsePostMail(mail: string) {
  let color = 'white', size = 'medium', position = 'naka'
  for (const p of mail.toLowerCase().split(/\s+/).filter(Boolean)) {
    if (p.startsWith('#') && /^#[0-9a-f]{6}$/.test(p)) { color = p; continue }
    if (POST_COLOR_COMMANDS.has(p)) { color = p; continue }
    if (p === 'big' || p === 'small') { size = p; continue }
    if (p === 'ue' || p === 'shita')  { position = p; continue }
  }
  return { color, size, position }
}

function postComment() {
  if (!watchWs || watchWs.readyState !== WebSocket.OPEN) {
    setStatus('投稿失敗: 視聴セッション未接続'); return
  }
  const text = postText.value.trim()
  if (!text) return
  pendingOwnTexts.set(text, Date.now())  // 送信前に登録
  const m = parsePostMail(postMail.value)
  const isAnon = postAnonBtn.classList.contains('anon-on')
  const data: {
    text: string
    vpos: number
    isAnonymous: boolean
    color?: string
    size?: string
    position?: string
    font?: string
  } = { text, vpos: calcVpos(), isAnonymous: isAnon }
  if (m.color !== 'white') data.color = m.color
  if (m.size !== 'medium') data.size = m.size
  if (m.position !== 'naka') data.position = m.position
  watchWs.send(JSON.stringify({
    type: 'postComment',
    data,
  }))
  postText.value = ''
}

postAnonBtn.addEventListener('click', () => postAnonBtn.classList.toggle('anon-on'))
postBtn.addEventListener('click', postComment)
postText.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.isComposing) postComment() })
cmdToggle.addEventListener('click', (e) => {
  e.stopPropagation()
  showCommandPalette(cmdPalette.hasAttribute('hidden'))
  updateCommandPaletteActive()
})
postMail.addEventListener('focus', () => {
  showCommandPalette(true)
  updateCommandPaletteActive()
})
postMail.addEventListener('input', () => {
  savePostMail()
  updateCommandPaletteActive()
})
postMail.addEventListener('blur', () => savePostMail())
cmdReset.addEventListener('click', resetPostMail)
document.addEventListener('pointerdown', (e) => {
  if (postArea.contains(e.target as Node)) return
  showCommandPalette(false)
})
postMail.value = compactPostMail(savedPostMail)
savePostMail()
buildCommandPalette()

// ─── Stage info ──────────────────────────────────────────────────────────────
function formatJst(utcStr: string | null): string {
  if (!utcStr) return '時刻不明'
  const d = new Date(new Date(utcStr).getTime() + 9 * 3600000)
  const mm = String(d.getUTCMonth() + 1).padStart(2, '0')
  const dd = String(d.getUTCDate()).padStart(2, '0')
  const hh = String(d.getUTCHours()).padStart(2, '0')
  const mn = String(d.getUTCMinutes()).padStart(2, '0')
  return `${mm}/${dd} ${hh}:${mn}`
}

function formatCountdown(ms: number): string {
  if (ms <= 0) return '間もなく開始'
  const h = Math.floor(ms / 3600000)
  const m = Math.floor((ms % 3600000) / 60000)
  const s = Math.floor((ms % 60000) / 1000)
  if (h > 0) return `あと${h}時間${m}分`
  if (m > 0) return `あと${m}分${s}秒`
  return `あと${s}秒`
}

function findSelectedSource() {
  if (!selectedChannel) return null
  const found = findSourceByKey(selectedChannel)
  return found
}

function findSourceByKey(key: string) {
  for (const channel of channels) {
    const source = channel.sources.find(s => s.key === key)
    if (source) return { channel, source }
  }
  return null
}

function isSelectedAccountlessPostSource() {
  const selected = findSelectedSource()
  return !!selected?.source.commentable && !selected.source.requiresAuth
}

function updateProgramBanner() {
  const selected = findSelectedSource()
  const program = selected?.channel.program
  if (!selectedChannel || !program?.title) {
    programBanner.className = ''
    programBanner.textContent = ''
    overlayArea.classList.remove('with-program-banner')
    return
  }

  programBanner.className = `visible ${programGenreClass(program)}`
  programBanner.textContent = program.title
  programBanner.title = program.genreName ? `${program.genreName}: ${program.title}` : program.title
  overlayArea.classList.add('with-program-banner')
}

function updateStageInfo() {
  if (!selectedChannel) {
    stageInfo.className = ''
    stageInfo.innerHTML = '<div class="si-hint">← チャンネルを選択</div>'
    return
  }

  const selected = findSelectedSource()
  const ch = selected?.channel ?? null
  const source = selected?.source ?? null

  if (wsState === 'connected') {
    if (!(source?.sourceType === 'unofficial' && (source.isReserved || source.status === 'retryWaiting'))) {
      stageInfo.className = 'si-hidden'
      stageInfo.innerHTML = ''
      return
    }
  }

  const typeStr   = source?.label ?? '-'
  const typeClass = source?.sourceType === 'official' ? 'si-official'
    : source?.sourceType === 'refuge' || source?.sourceType === 'local' ? 'si-refuge'
    : source?.sourceType === 'unofficial' ? 'si-unofficial' : 'si-unofficial'
  const typeBadge = ch ? `<span class="si-badge ${typeClass}">${typeStr}</span>` : ''
  const nameHtml  = `<div class="si-name">${typeBadge}${ch?.name ?? selectedChannel}</div>`

  let statusHtml = ''
  if (wsState === 'connected') {
    statusHtml = source?.sourceType === 'unofficial' && source.status === 'retryWaiting'
      ? '<div class="si-status si-connecting">配信情報の取得に失敗しました</div>'
      : source?.sourceType === 'unofficial' && source.isReserved
      ? '<div class="si-status si-connecting">配信開始まで待機中</div>'
      : '<div class="si-status si-connecting">接続済み</div>'
  } else if (wsState === 'connecting') {
    statusHtml = '<div class="si-status si-connecting">接続中…</div>'
  } else {
    const remain = Math.max(0, Math.ceil((reconnectAt - Date.now()) / 1000))
    statusHtml = remain > 0
      ? `<div class="si-status si-disconnected">切断 — ${remain}秒後に再接続</div>`
      : '<div class="si-status si-disconnected">切断</div>'
  }

  let extraHtml = ''
  if (source?.sourceType === 'unofficial' && source.status === 'retryWaiting') {
    extraHtml = '<div class="si-scheduled" id="si-scheduled-line">◇ 次の検索まで待機中</div>'
  } else if (source?.sourceType === 'unofficial' && source.isReserved && source.scheduledStartUtc) {
    const diff = new Date(source.scheduledStartUtc).getTime() - Date.now()
    extraHtml = `<div class="si-scheduled" id="si-scheduled-line">◇ ${formatJst(source.scheduledStartUtc)} JST 配信予定 (${formatCountdown(diff)})</div>`
  }

  stageInfo.className = 'si-panel'
  stageInfo.innerHTML = nameHtml + statusHtml + extraHtml
  const scheduledLine = document.getElementById('si-scheduled-line')
  if (scheduledLine && isNicovideoWatchTarget(source?.currentTarget ?? null)) {
    scheduledLine.append(' ')
    appendNicovideoLink(scheduledLine, source!.currentTarget!)
  }
}

function setStatus(msg: string) { statusBar.textContent = msg }

// ─── Init ─────────────────────────────────────────────────────────────────────
applyChannelProgramGenreColorSetting()
renderSidebarChannelOptions()
renderScheduleGenreOptions()
renderScheduleChannelOptions()
fetchStatus()
connectChannelsStream()
setInterval(fetchStatus, 5000)
setInterval(updateStageInfo, 1000)
setInterval(refreshScheduleClock, 60000)
updateStageInfo()
updateLoginUi()
