import type { Chat } from './types'
import { parseMail, fontSizePx, FixedCommentSlots, type CommentStyle } from './comment'

const FIXED_DURATION_MS = 3000
const MAX_SCROLL_SPEED_PX_PER_SEC = 640

type ActiveComment = {
  content: string
  style: CommentStyle
  fontSize: number
  width: number
  y: number
  start: number
  duration: number
  isOwn: boolean
  slot?: number
}

class TrackManager {
  private tracks: number[]
  private trackH: number

  constructor(count: number, trackH: number) {
    this.tracks = new Array(count).fill(0)
    this.trackH = trackH
  }

  allocate(tw: number, cw: number, now: number, durationMs: number): number {
    const idx = this.tracks.reduce((best, t, i) => t < this.tracks[best] ? i : best, 0)
    const clearMs = durationMs * ((cw + tw) / (cw + 0.01))
    this.tracks[idx] = now + clearMs
    return idx * this.trackH
  }

  resize(count: number, h: number) {
    this.trackH = h
    this.tracks = new Array(count).fill(0)
  }
}

export class CommentOverlay {
  scrollDuration = 2000
  maxScrollComments = 320

  private _fontScale = 1.0
  get fontScale() { return this._fontScale }
  set fontScale(v: number) { this._fontScale = v; this.resize() }

  private _scrollRange = 0.7
  get scrollRange() { return this._scrollRange }
  set scrollRange(v: number) {
    this._scrollRange = Math.min(1, Math.max(0.5, v))
    this.resize()
  }

  private _opacity = 1.0
  get opacity() { return this._opacity }
  set opacity(v: number) {
    this._opacity = v
    this.container.style.opacity = v.toFixed(2)
  }

  private container: HTMLElement
  private canvas: HTMLCanvasElement
  private ctx: CanvasRenderingContext2D
  private trackMgr!: TrackManager
  private ueSlots = new FixedCommentSlots()
  private shitaSlots = new FixedCommentSlots()
  private ro!: ResizeObserver
  private active: ActiveComment[] = []
  private rafId: number | null = null

  constructor(container: HTMLElement) {
    this.container = container
    this.canvas = document.createElement('canvas')
    this.canvas.style.cssText = 'position:absolute;top:0;left:0;width:100%;height:100%;pointer-events:none;'
    container.appendChild(this.canvas)
    this.ctx = this.canvas.getContext('2d')!
    this.resize()
    this.ro = new ResizeObserver(() => this.resize())
    this.ro.observe(container)
  }

  dispose() {
    this.ro.disconnect()
    if (this.rafId !== null) {
      cancelAnimationFrame(this.rafId)
      this.rafId = null
    }
    this.active = []
  }

  private resize() {
    const { clientWidth: w, clientHeight: h } = this.container
    this.canvas.width = w || 640
    this.canvas.height = h || 360
    const baseFont = Math.round(fontSizePx('medium', this.canvas.height) * this._fontScale)
    const trackH = Math.round(baseFont * 1.2)
    const scrollHeight = Math.max(trackH, Math.floor(this.canvas.height * this._scrollRange))
    const count = Math.max(1, Math.floor(scrollHeight / trackH))
    if (!this.trackMgr) this.trackMgr = new TrackManager(count, trackH)
    else this.trackMgr.resize(count, trackH)
  }

  add(chat: Chat, isOwn = false) {
    const style = parseMail(chat.mail)
    const cw = this.canvas.width
    const ch = this.canvas.height
    const fontSize = Math.round(fontSizePx(style.size, ch) * this._fontScale)
    this.ctx.font = `bold ${fontSize}px sans-serif`
    const width = this.ctx.measureText(chat.content).width
    const now = performance.now()
    const lineH = Math.round(fontSize * 1.2)

    const scrollDuration = Math.max(
      this.scrollDuration,
      ((cw + width + 20) / MAX_SCROLL_SPEED_PX_PER_SEC) * 1000)

    const item: ActiveComment = {
      content: chat.content,
      style,
      fontSize,
      width,
      y: 0,
      start: now,
      duration: style.position === 'naka' ? scrollDuration : FIXED_DURATION_MS,
      isOwn,
    }

    if (style.position === 'naka') {
      item.y = this.trackMgr.allocate(width, cw, now, scrollDuration)
    } else if (style.position === 'ue') {
      const slot = this.ueSlots.allocate()
      item.slot = slot
      item.y = slot * lineH
    } else {
      const slot = this.shitaSlots.allocate()
      item.slot = slot
      item.y = Math.max(0, ch - ((slot + 1) * lineH))
    }

    this.active.push(item)
    if (style.position === 'naka')
      this.trimScrollComments()
    this.ensureLoop()
  }

  private trimScrollComments() {
    let overflow = this.active.filter(item => item.style.position === 'naka').length - this.maxScrollComments
    if (overflow <= 0) return

    this.active = this.active.filter(item => {
      if (overflow <= 0 || item.style.position !== 'naka')
        return true
      overflow--
      return false
    })
  }

  private ensureLoop() {
    if (this.rafId !== null) return
    this.rafId = requestAnimationFrame((now) => this.draw(now))
  }

  private draw(now: number) {
    this.rafId = null
    const cw = this.canvas.width
    const ch = this.canvas.height
    this.ctx.clearRect(0, 0, cw, ch)

    const next: ActiveComment[] = []
    for (const item of this.active) {
      const elapsed = now - item.start
      if (elapsed >= item.duration) {
        this.freeFixedSlot(item)
        continue
      }

      next.push(item)
      const x = this.commentX(item, elapsed, cw)
      this.drawText(item, x, item.y)
    }
    this.active = next

    if (this.active.length > 0)
      this.rafId = requestAnimationFrame((nextNow) => this.draw(nextNow))
  }

  private commentX(item: ActiveComment, elapsed: number, cw: number) {
    if (item.style.position !== 'naka')
      return Math.round((cw - item.width) / 2)
    const progress = elapsed / item.duration
    return cw - progress * (cw + item.width + 20)
  }

  private drawText(item: ActiveComment, x: number, y: number) {
    this.ctx.font = `bold ${item.fontSize}px sans-serif`
    this.ctx.textBaseline = 'top'
    this.ctx.lineJoin = 'round'

    if (item.isOwn) {
      this.ctx.lineWidth = Math.max(5, Math.round(item.fontSize * 0.18))
      this.ctx.strokeStyle = '#ffd54f'
      this.ctx.strokeText(item.content, x, y)
    }

    this.ctx.lineWidth = Math.max(3, Math.round(item.fontSize * 0.11))
    this.ctx.strokeStyle = '#000'
    this.ctx.strokeText(item.content, x, y)
    this.ctx.fillStyle = item.style.color
    this.ctx.fillText(item.content, x, y)
  }

  private freeFixedSlot(item: ActiveComment) {
    if (item.slot === undefined) return
    if (item.style.position === 'ue') this.ueSlots.free(item.slot)
    if (item.style.position === 'shita') this.shitaSlots.free(item.slot)
  }
}
