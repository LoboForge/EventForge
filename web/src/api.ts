const OPS_KEY_STORAGE = 'eventforge_ops_key'

export function getOpsKey(): string {
  return sessionStorage.getItem(OPS_KEY_STORAGE) ?? ''
}

export function setOpsKey(key: string) {
  sessionStorage.setItem(OPS_KEY_STORAGE, key.trim())
}

export async function opsFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  const key = getOpsKey()
  const headers = new Headers(init.headers)
  if (key) headers.set('X-EventForge-Ops-Key', key)
  headers.set('Accept', 'application/json')
  if (init.body && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }
  const resp = await fetch(path, { ...init, headers })
  if (resp.status === 401) throw new Error('Unauthorized — check ops API key')
  if (!resp.ok) {
    const body = await resp.text()
    throw new Error(body || `${resp.status} ${resp.statusText}`)
  }
  return resp.json() as Promise<T>
}

/** Returns null on 503/404 instead of throwing — for optional endpoints when Vast is off. */
export async function opsFetchMaybe<T>(path: string, init: RequestInit = {}): Promise<T | null> {
  const key = getOpsKey()
  const headers = new Headers(init.headers)
  if (key) headers.set('X-EventForge-Ops-Key', key)
  headers.set('Accept', 'application/json')
  if (init.body && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }
  const resp = await fetch(path, { ...init, headers })
  if (resp.status === 401) throw new Error('Unauthorized — check ops API key')
  if (resp.status === 503 || resp.status === 404) return null
  if (!resp.ok) {
    const body = await resp.text()
    throw new Error(body || `${resp.status} ${resp.statusText}`)
  }
  return resp.json() as Promise<T>
}

export type WorkerModels = {
  checkpoints: string[]
  unets: string[]
  clips: string[]
  vaes: string[]
  loras: string[]
}

export type WorkerRow = {
  workerId: string
  nodeUuid: string
  hostname: string
  gpuName: string
  vramTotalMb: number
  vramFreeMb: number
  diskFreeMb: number
  state: string
  activeJobId?: string | null
  currentJobUuid?: string | null
  transport: string
  fleetMode: string
  comfyOk: boolean
  capability: string
  tier: string
  queueAccessOk?: boolean | null
  queueAccessError?: string | null
  lastSeenAt: string
  checkInStale: boolean
  capabilities: string[]
  claimReadyCapabilities: string[]
  knownLoras: string[]
  models: WorkerModels
  jobsClaimed: number
  jobsCompleted: number
  jobsFailed: number
  jobsTimedOut: number
  jobsReleased: number
  contributing?: boolean
  badges: string[]
  quarantined?: boolean
  quarantineReason?: string | null
  quarantinedAt?: string | null
}

export type Snapshot = {
  generated_at: string
  fleet: {
    workers_total: number
    workers_busy: number
    workers_idle: number
    workers_stale: number
    workers_non_contributing?: number
    workers: Array<Record<string, unknown>>
  }
  queue: {
    jobs_total?: number
    jobs_queued: number
    jobs_in_progress: number
    jobs_failed: number
    jobs_completed?: number
    by_capability: { capability: string; queued: number; in_progress: number; failed: number }[]
    by_tier?: { tier: string; queued: number }[]
  }
  queue_by_app?: {
    app_id: string
    paused: boolean
    queued: number
    in_progress: number
    failed: number
    completed: number
  }[]
  active_jobs: JobRow[]
  recent_failures: JobRow[]
}

export type OpsAppRow = {
  app_id: string
  paused: boolean
  pause_reason?: string | null
  paused_at?: string | null
  jobs_queued: number
  jobs_in_progress: number
  jobs_failed: number
  jobs_completed: number
}

export type MetricsHistoryResponse = {
  samples: Record<string, unknown>[]
}

export type JobRow = {
  job_id: string
  app_id?: string
  capability: string
  tier: string
  status: string
  hostname?: string | null
  worker_id?: string | null
  error?: string | null
  leased_at?: string | null
  leased_until?: string | null
  created_at?: string | null
  completed_at?: string | null
}

export function isGenFleetWorker(w: Pick<WorkerRow, 'hostname'>): boolean {
  const h = (w.hostname || '').toLowerCase()
  return h.startsWith('loboforge-image-')
    || h.startsWith('loboforge-video-')
    || h.startsWith('loboforge-ltx-')
    || h.startsWith('loboforge-ollama-')
}

export function workerFleetRowKey(w: Pick<WorkerRow, 'nodeUuid' | 'hostname' | 'workerId'>): string {
  return w.nodeUuid || w.hostname || w.workerId || 'unknown'
}

function readStr(raw: Record<string, unknown>, ...keys: string[]): string {
  for (const k of keys) {
    const v = raw[k]
    if (typeof v === 'string' && v) return v
  }
  return ''
}

function readNum(raw: Record<string, unknown>, ...keys: string[]): number {
  for (const k of keys) {
    const v = raw[k]
    if (typeof v === 'number' && Number.isFinite(v)) return v
    if (typeof v === 'string' && v) {
      const n = parseFloat(v)
      if (Number.isFinite(n)) return n
    }
  }
  return 0
}

function readBool(raw: Record<string, unknown>, key: string, defaultValue = false): boolean {
  const v = raw[key]
  if (typeof v === 'boolean') return v
  return defaultValue
}

function readStrList(raw: Record<string, unknown>, ...keys: string[]): string[] {
  for (const k of keys) {
    const v = raw[k]
    if (Array.isArray(v)) return v.map(String).filter(Boolean)
  }
  return []
}

export function parseWorkerModels(modelsJson?: string | null): WorkerModels {
  if (!modelsJson) return { checkpoints: [], unets: [], clips: [], vaes: [], loras: [] }
  try {
    const o = JSON.parse(modelsJson) as Record<string, unknown>
    return {
      checkpoints: Array.isArray(o.checkpoints) ? o.checkpoints.map(String) : [],
      unets: Array.isArray(o.unets) ? o.unets.map(String) : [],
      clips: Array.isArray(o.clips) ? o.clips.map(String) : [],
      vaes: Array.isArray(o.vaes) ? o.vaes.map(String) : [],
      loras: Array.isArray(o.loras) ? o.loras.map(String) : [],
    }
  } catch {
    return { checkpoints: [], unets: [], clips: [], vaes: [], loras: [] }
  }
}

export function normalizeWorker(raw: Record<string, unknown>): WorkerRow {
  const modelsJson = readStr(raw, 'modelsJson', 'models_json') || null
  const models = parseWorkerModels(modelsJson)
  const knownLoras = readStrList(raw, 'knownLoras', 'known_loras')
  return {
    workerId: readStr(raw, 'workerId', 'worker_id'),
    nodeUuid: readStr(raw, 'nodeUuid', 'node_uuid'),
    hostname: readStr(raw, 'hostname') || readStr(raw, 'workerId', 'worker_id'),
    gpuName: readStr(raw, 'gpuName', 'gpu_name'),
    vramTotalMb: readNum(raw, 'vramTotalMb', 'vram_total_mb'),
    vramFreeMb: readNum(raw, 'vramFreeMb', 'vram_free_mb'),
    diskFreeMb: readNum(raw, 'diskFreeMb', 'disk_free_mb'),
    state: readStr(raw, 'state') || 'idle',
    activeJobId: readStr(raw, 'activeJobId', 'active_job_id') || null,
    currentJobUuid: readStr(raw, 'currentJobUuid', 'current_job_uuid') || null,
    transport: readStr(raw, 'transport') || 'eventforge',
    fleetMode: readStr(raw, 'fleetMode', 'fleet_mode'),
    comfyOk: readBool(raw, 'comfyOk', true) && readBool(raw, 'comfy_ok', true),
    capability: readStr(raw, 'capability'),
    tier: readStr(raw, 'tier'),
    queueAccessOk: typeof raw.queueAccessOk === 'boolean' ? raw.queueAccessOk
      : typeof raw.queue_access_ok === 'boolean' ? raw.queue_access_ok as boolean : null,
    queueAccessError: readStr(raw, 'queueAccessError', 'queue_access_error') || null,
    lastSeenAt: readStr(raw, 'lastSeenAt', 'last_seen_at'),
    checkInStale: readBool(raw, 'checkInStale') || readBool(raw, 'check_in_stale'),
    capabilities: readStrList(raw, 'capabilities', 'forge_queue_capabilities'),
    claimReadyCapabilities: readStrList(raw, 'claimReadyCapabilities', 'claim_ready_capabilities'),
    knownLoras: knownLoras.length > 0 ? knownLoras : models.loras,
    models,
    jobsClaimed: readNum(raw, 'jobsClaimed', 'jobs_claimed'),
    jobsCompleted: readNum(raw, 'jobsCompleted', 'jobs_completed'),
    jobsFailed: readNum(raw, 'jobsFailed', 'jobs_failed'),
    jobsTimedOut: readNum(raw, 'jobsTimedOut', 'jobs_timed_out'),
    jobsReleased: readNum(raw, 'jobsReleased', 'jobs_released'),
    contributing: typeof raw.contributing === 'boolean' ? raw.contributing : true,
    badges: readStrList(raw, 'badges'),
    quarantined: readBool(raw, 'quarantined') || readBool(raw, 'Quarantined'),
    quarantineReason: readStr(raw, 'quarantineReason', 'quarantine_reason') || null,
    quarantinedAt: readStr(raw, 'quarantinedAt', 'quarantined_at') || null,
  }
}

const BADGE_LABELS: Record<string, string> = {
  quarantined: 'Quarantined',
  stale: 'Stale',
  'queue-blocked': 'Queue blocked',
  'comfy-down': 'Comfy down',
  'wan-not-ready': 'Wan not ready',
  'no-claim-ready': 'No claim-ready',
  'idle-no-jobs': 'Idle (no jobs)',
  'busy-no-job-id': 'Busy w/o job',
  'disk-low': 'Disk low',
}

export function badgeLabel(badge: string): string {
  return BADGE_LABELS[badge] ?? badge
}

export function formatDiskGb(mb: number): string {
  if (mb <= 0) return '—'
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${mb} MB`
}

export function formatVram(mb: number): string {
  if (mb <= 0) return '—'
  return `${Math.round(mb / 1024)} GB`
}
