export function parseIso(iso?: string | null): Date | null {
  if (!iso) return null
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? null : d
}

export function formatDateTime(iso?: string | null): string {
  const d = parseIso(iso)
  if (!d) return '—'
  return d.toLocaleString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
    second: '2-digit',
  })
}

export function formatTime(iso?: string | null): string {
  const d = parseIso(iso)
  if (!d) return '—'
  return d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit', second: '2-digit' })
}

export function formatDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return '—'
  const totalSec = Math.floor(ms / 1000)
  if (totalSec < 60) return `${totalSec}s`
  const min = Math.floor(totalSec / 60)
  const sec = totalSec % 60
  if (min < 60) return sec > 0 ? `${min}m ${sec}s` : `${min}m`
  const hr = Math.floor(min / 60)
  const remMin = min % 60
  if (hr < 48) return remMin > 0 ? `${hr}h ${remMin}m` : `${hr}h`
  const days = Math.floor(hr / 24)
  const remHr = hr % 24
  return remHr > 0 ? `${days}d ${remHr}h` : `${days}d`
}

export function runningDurationMs(leasedAt?: string | null, nowMs = Date.now()): number | null {
  const start = parseIso(leasedAt)
  if (!start) return null
  return Math.max(0, nowMs - start.getTime())
}

export function compareSortValues(a: unknown, b: unknown): number {
  if (a == null && b == null) return 0
  if (a == null) return 1
  if (b == null) return -1
  if (typeof a === 'number' && typeof b === 'number') return a - b
  if (a instanceof Date && b instanceof Date) return a.getTime() - b.getTime()
  return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' })
}
