export interface ChannelInfo {
  id: number
  name: string
  video: string        // "jk1" etc.
  bs: boolean
  program?: ProgramInfo | null
  running: boolean
  currentTarget: string | null
  force: number
  viewers: number
  totalComments: number
  lastResNo: number
  sources: ChannelSource[]
}

export interface ChannelSource {
  key: string
  sourceType: 'official' | 'unofficial' | 'refuge' | 'local' | 'unknown'
  label: string
  configured: boolean
  running: boolean
  currentTarget: string | null
  force: number
  viewers: number
  totalComments: number
  lastResNo: number
  isReserved: boolean
  scheduledStartUtc: string | null
  status: string
  statusText: string | null
  commentable: boolean
  requiresAuth: boolean
  watchUrl: string
  commentUrl: string
}

export interface StatusResponse {
  uptimeSec: number
  channels: ChannelInfo[]
}

export interface ProgramInfo {
  title: string
  startAt: string
  endAt: string
  source: string
  genreCode: string | null
  genreName: string | null
  updatedAt: string
  stale: boolean
}

export interface ChannelsStreamStatsChannel {
  id: number
  video: string
  force: number
  viewers: number
  comments: number
  lastResNo: number
}

export interface ChannelsStreamProgramChannel {
  id: number
  video: string
  program: ProgramInfo | null
}

export interface ScheduleProgram {
  title: string
  startAt: string
  endAt: string
  source: string
  genreCode: string | null
  genreName: string | null
}

export interface ScheduleChannel {
  id: number
  video: string
  name: string
  bs: boolean
  programs: ScheduleProgram[]
}

export interface ScheduleResponse {
  date: string
  broadcastStartHour: number
  startAt: string
  endAt: string
  loaded: boolean
  updatedAt: string | null
  channels: ScheduleChannel[]
}

export interface ScheduleDateRange {
  earliestDate: string | null
  latestDate: string | null
}

export type ChannelsStreamMessage =
  | {
      type: 'snapshot'
      updatedAt: string
      statsIntervalSec: number
      channels: Array<ChannelsStreamStatsChannel & {
        name: string
        bs: boolean
        program: ProgramInfo | null
        sources: ChannelSource[]
      }>
    }
  | {
      type: 'stats'
      updatedAt: string
      intervalSec: number
      channels: ChannelsStreamStatsChannel[]
    }
  | {
      type: 'programs'
      updatedAt: string
      channels: ChannelsStreamProgramChannel[]
    }

// {"chat":{...}} から chat 部分
export interface Chat {
  thread: string
  no: number
  vpos: number
  date: number
  date_usec: number
  user_id: string
  premium: number
  anonymity: number
  content: string
  mail?: string        // "red ue big" など
}
