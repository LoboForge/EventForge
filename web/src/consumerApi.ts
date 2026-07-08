const API_KEY_STORAGE = 'eventforge_api_key'

export function getApiKey(): string {
  return sessionStorage.getItem(API_KEY_STORAGE) ?? ''
}

export function setApiKey(key: string) {
  sessionStorage.setItem(API_KEY_STORAGE, key.trim())
}

export async function consumerFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  const key = getApiKey()
  const headers = new Headers(init.headers)
  if (key) headers.set('Authorization', `Bearer ${key}`)
  headers.set('Accept', 'application/json')
  if (init.body && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }
  const resp = await fetch(path, { ...init, headers })
  if (resp.status === 401) throw new Error('Unauthorized — check your API key')
  if (resp.status === 402) throw new Error('Account paused — out of generations. Contact support to resume.')
  if (!resp.ok) {
    const body = await resp.text()
    throw new Error(body || `${resp.status} ${resp.statusText}`)
  }
  return resp.json() as Promise<T>
}

export type ConsumerJob = {
  job_id: string
  app_id: string
  capability: string
  tier: string
  kind: string
  status: string
  worker_id?: string | null
  hostname?: string | null
  created_at: string
  leased_at?: string | null
  leased_until?: string | null
  completed_at?: string | null
  error?: string | null
  output_url?: string | null
}

export type DashboardStats = {
  app_id: string
  paused: boolean
  pause_reason?: string | null
  jobs_total: number
  jobs_queued: number
  jobs_in_progress: number
  jobs_completed: number
  jobs_failed: number
  jobs_last_24h: number
  completed_last_24h: number
  failed_last_24h: number
  by_capability: {
    capability: string
    queued: number
    in_progress: number
    completed: number
    failed: number
  }[]
  by_status: { status: string; count: number }[]
  recent_jobs: ConsumerJob[]
  metrics_history: Record<string, unknown>[]
}

export type MeResponse = {
  app_id: string
  paused: boolean
  pause_reason?: string | null
  paused_at?: string | null
}
