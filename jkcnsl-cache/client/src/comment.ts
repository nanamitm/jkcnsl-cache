export interface CommentStyle {
  color: string
  position: 'naka' | 'ue' | 'shita'
  size: 'medium' | 'big' | 'small'
}

const COLOR_MAP: Record<string, string> = {
  red: '#ff0000', pink: '#ff8080', orange: '#ffc000', yellow: '#ffff00',
  green: '#00ff00', cyan: '#00ffff', blue: '#0000ff', purple: '#c000ff',
  black: '#000000', white: '#ffffff', niconicowhite: '#cccc99',
  maroon: '#800000', darkblue: '#000080', green2: '#008000',
  olive: '#808000', teal: '#008080', navy: '#000080',
}

export function parseMail(mail: string | undefined): CommentStyle {
  const parts = (mail ?? '').toLowerCase().split(/\s+/)
  let color = '#ffffff'
  let position: CommentStyle['position'] = 'naka'
  let size: CommentStyle['size'] = 'medium'

  for (const p of parts) {
    if (p.startsWith('#') && /^#[0-9a-f]{6}$/.test(p)) { color = p; continue }
    if (COLOR_MAP[p]) { color = COLOR_MAP[p]; continue }
    if (p === 'ue') { position = 'ue'; continue }
    if (p === 'shita') { position = 'shita'; continue }
    if (p === 'big') { size = 'big'; continue }
    if (p === 'small') { size = 'small'; continue }
  }
  return { color, position, size }
}

export class FixedCommentSlots {
  private slots: (string | null)[]
  constructor(max = 8) { this.slots = new Array(max).fill(null) }

  allocate(): number {
    const i = this.slots.indexOf(null)
    if (i >= 0) { this.slots[i] = 'used'; return i }
    this.slots[0] = 'used'; return 0
  }

  free(i: number) { this.slots[i] = null }
}

export function fontSizePx(size: CommentStyle['size'], baseH: number): number {
  const base = Math.max(baseH / 22, 14)
  if (size === 'big')   return Math.round(base * 1.5)
  if (size === 'small') return Math.round(base * 0.75)
  return Math.round(base)
}
