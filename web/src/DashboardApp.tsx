import { useCallback, useEffect, useMemo, useState } from 'react'
import { applyPageSeo } from './seo'
import { Link } from './router'
import { BarChart, DonutChart, LineChart } from './charts'
import { formatDateTime, formatDuration, runningDurationMs } from './format'
import { useNow } from './hooks'
import { SortableTable, type SortableColumn } from './SortableTable'
import {
  consumerFetch,
  getApiKey,
  setApiKey,
  type ConsumerJob,
  type DashboardStats,
  type MeResponse,
} from './consumerApi'

const STATUS_COLORS: Record<string, string> = {
  queued: '#f0a500',
  leased: '#3d7fd4',
  streaming: '#7eb8ff',
  completed: '#4caf82',
  failed: '#e85d5d',
}

function Login({ onLogin }: { onLogin: () => void }) {
  const [key, setKey] = useState(getApiKey())
  const [err, setErr] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setBusy(true)
    setErr(null)
    setApiKey(key)
    try {
      await consumerFetch<MeResponse>('/v1/me')
      onLogin()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="app-shell login card ops-login">
      <div className="ops-login-brand">
        <span className="ops-logo-mark">EF</span>
        <div>
          <h1>EventForge Dashboard</h1>
          <p className="muted">Monitor jobs, queue depth, and usage for your integration.</p>
        </div>
      </div>
      <form onSubmit={(e) => void submit(e)}>
        <label htmlFor="api-key">App API key</label>
        <input id="api-key" type="password" value={key} onChange={(e) => setKey(e.target.value)} autoComplete="off" />
        {err && <div className="error">{err}</div>}
        <button className="btn" type="submit" disabled={busy || !key.trim()}>{busy ? 'Checking…' : 'Open dashboard'}</button>
      </form>
    </div>
  )
}

function JobStatusBadge({ status }: { status: string }) {
  const s = status.toLowerCase()
  return <span className={`badge job-status ${s}`}>{status}</span>
}

function JobsTable({
  jobs,
  onCancel,
  busyId,
}: {
  jobs: ConsumerJob[]
  onCancel: (id: string, inFlight: boolean) => void
  busyId: string | null
}) {
  const now = useNow(jobs.some((j) => j.status === 'leased' || j.status === 'streaming'))

  const columns = useMemo((): SortableColumn<ConsumerJob>[] => [
    {
      id: 'job',
      header: 'Job',
      sortValue: (j) => j.job_id,
      render: (j) => <code title={j.job_id}>{j.job_id.slice(0, 8)}</code>,
    },
    {
      id: 'capability',
      header: 'Capability',
      sortValue: (j) => j.capability,
      render: (j) => j.capability,
    },
    {
      id: 'tier',
      header: 'Tier',
      sortValue: (j) => j.tier,
      render: (j) => j.tier,
    },
    {
      id: 'status',
      header: 'Status',
      sortValue: (j) => j.status,
      render: (j) => <JobStatusBadge status={j.status} />,
    },
    {
      id: 'worker',
      header: 'Worker',
      sortValue: (j) => j.hostname ?? j.worker_id ?? '',
      render: (j) => <span className="muted">{j.hostname ?? j.worker_id ?? '—'}</span>,
    },
    {
      id: 'created',
      header: 'Created',
      sortValue: (j) => j.created_at,
      render: (j) => <span className="muted">{formatDateTime(j.created_at)}</span>,
    },
    {
      id: 'running',
      header: 'Running',
      sortValue: (j) => runningDurationMs(j.leased_at, now) ?? -1,
      render: (j) => {
        const ms = runningDurationMs(j.leased_at, now)
        return ms != null ? formatDuration(ms) : '—'
      },
      className: 'num-cell',
    },
    {
      id: 'actions',
      header: '',
      sortable: false,
      render: (j) => {
        const cancellable = j.status === 'queued' || j.status === 'leased' || j.status === 'streaming'
        return (
          <>
            {cancellable && (
              <button
                className="btn secondary small"
                disabled={busyId === j.job_id}
                onClick={() => onCancel(j.job_id, j.status !== 'queued')}
              >
                {busyId === j.job_id ? '…' : 'Cancel'}
              </button>
            )}
            {j.error && <span className="error inline">{j.error}</span>}
          </>
        )
      },
      className: 'actions-cell',
    },
  ], [busyId, now, onCancel])

  return (
    <SortableTable
      rows={jobs}
      rowKey={(j) => j.job_id}
      columns={columns}
      defaultSort={{ id: 'created', dir: 'desc' }}
      emptyMessage="No jobs yet."
    />
  )
}

export default function DashboardApp() {
  const [authed, setAuthed] = useState(!!getApiKey())
  const [stats, setStats] = useState<DashboardStats | null>(null)
  const [me, setMe] = useState<MeResponse | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [msg, setMsg] = useState<string | null>(null)
  const [busyId, setBusyId] = useState<string | null>(null)
  const [purgeBusy, setPurgeBusy] = useState(false)
  const [statusFilter, setStatusFilter] = useState('')

  useEffect(() => {
    return applyPageSeo({
      title: 'EventForge Dashboard',
      description: 'Customer job queue dashboard for EventForge integrations.',
      canonicalPath: '/dashboard',
      noindex: true,
    })
  }, [])

  const refresh = useCallback(async () => {
    if (!getApiKey()) return
    try {
      const [s, m] = await Promise.all([
        consumerFetch<DashboardStats>('/v1/dashboard/stats'),
        consumerFetch<MeResponse>('/v1/me'),
      ])
      setStats(s)
      setMe(m)
      setErr(null)
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    }
  }, [])

  useEffect(() => {
    if (!authed) return
    void refresh()
    const id = window.setInterval(() => void refresh(), 12000)
    return () => window.clearInterval(id)
  }, [authed, refresh])

  const metrics = useMemo(() => {
    const hist = stats?.metrics_history ?? []
    return hist.map((s) => {
      const raw = s as Record<string, unknown>
      return {
        atUtc: String(raw.at_utc ?? raw.atUtc ?? ''),
        jobsQueued: Number(raw.jobs_queued ?? raw.jobsQueued ?? 0),
        jobsInProgress: Number(raw.jobs_in_progress ?? raw.jobsInProgress ?? 0),
        jobsFailed: Number(raw.jobs_failed ?? raw.jobsFailed ?? 0),
        workersTotal: 0,
        workersBusy: 0,
        workersStale: 0,
        workersNonContributing: 0,
      }
    })
  }, [stats])

  const filteredJobs = useMemo(() => {
    const jobs = stats?.recent_jobs ?? []
    if (!statusFilter) return jobs
    return jobs.filter((j) => j.status.toLowerCase() === statusFilter)
  }, [stats, statusFilter])

  async function cancelQueued() {
    if (!confirm('Cancel all queued jobs for your account?')) return
    setPurgeBusy(true)
    setMsg(null)
    try {
      const r = await consumerFetch<{ cancelled: number }>('/v1/jobs/cancel-queued', { method: 'POST', body: '{}' })
      setMsg(`Cancelled ${r.cancelled} queued job(s).`)
      await refresh()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setPurgeBusy(false)
    }
  }

  async function cancelJob(jobId: string, inFlight: boolean) {
    if (!confirm(inFlight ? 'Cancel this in-flight job?' : 'Remove this queued job?')) return
    setBusyId(jobId)
    setMsg(null)
    try {
      await consumerFetch(`/v1/jobs/${jobId}/cancel`, {
        method: 'POST',
        body: JSON.stringify({ include_in_flight: inFlight }),
      })
      setMsg(`Job ${jobId.slice(0, 8)} cancelled.`)
      await refresh()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setBusyId(null)
    }
  }

  if (!authed) return <Login onLogin={() => setAuthed(true)} />

  const successRate = stats && stats.jobs_last_24h > 0
    ? Math.round((stats.completed_last_24h / stats.jobs_last_24h) * 100)
    : null

  return (
    <div className="app-shell dashboard-app">
      <header className="ops-header dashboard-header">
        <div className="ops-header-left">
          <span className="ops-logo-mark">EF</span>
          <div>
            <h1>EventForge Dashboard</h1>
            <p className="muted card-sub">App <code>{me?.app_id ?? stats?.app_id ?? '—'}</code></p>
          </div>
        </div>
        <div className="row ops-header-actions">
          <Link to="/" className="muted">Public site</Link>
          <Link to="/ops" className="muted">Ops console</Link>
          <button className="btn secondary" onClick={() => void refresh()}>Refresh</button>
          <button className="btn secondary" onClick={() => { sessionStorage.removeItem('eventforge_api_key'); setAuthed(false) }}>Sign out</button>
        </div>
      </header>

      {me?.paused && (
        <div className="error banner">
          <strong>Account paused</strong> — {me.pause_reason ?? 'out of generations'}. New jobs are blocked until your plan is renewed.
        </div>
      )}
      {err && <div className="error">{err}</div>}
      {msg && <div className="success">{msg}</div>}

      <div className="stats dashboard-stats">
        <div className="stat highlight"><div className="label">Queued</div><div className="value">{stats?.jobs_queued ?? 0}</div></div>
        <div className="stat"><div className="label">In progress</div><div className="value">{stats?.jobs_in_progress ?? 0}</div></div>
        <div className="stat"><div className="label">Completed</div><div className="value ok-text">{stats?.jobs_completed ?? 0}</div></div>
        <div className="stat"><div className="label">Failed</div><div className="value">{stats?.jobs_failed ?? 0}</div></div>
        <div className="stat"><div className="label">Last 24h</div><div className="value">{stats?.jobs_last_24h ?? 0}</div></div>
        <div className="stat"><div className="label">24h success</div><div className="value">{successRate != null ? `${successRate}%` : '—'}</div></div>
      </div>

      <div className="dashboard-grid">
        <div className="card chart-card">
          <h2>Queue depth (recent)</h2>
          <LineChart
            series={[
              { label: 'Queued', color: '#f0a500', values: metrics.map((m) => m.jobsQueued) },
              { label: 'In progress', color: '#3d7fd4', values: metrics.map((m) => m.jobsInProgress) },
            ]}
            height={110}
          />
        </div>
        <div className="card chart-card">
          <h2>Job status mix</h2>
          <DonutChart
            segments={[
              { label: 'Queued', value: stats?.jobs_queued ?? 0, color: STATUS_COLORS.queued },
              { label: 'Active', value: stats?.jobs_in_progress ?? 0, color: STATUS_COLORS.leased },
              { label: 'Done', value: stats?.jobs_completed ?? 0, color: STATUS_COLORS.completed },
              { label: 'Failed', value: stats?.jobs_failed ?? 0, color: STATUS_COLORS.failed },
            ]}
          />
        </div>
        <div className="card chart-card wide">
          <h2>By capability</h2>
          <BarChart
            items={(stats?.by_capability ?? []).map((c) => ({
              label: c.capability,
              value: c.queued + c.in_progress,
              color: '#3d7fd4',
            }))}
          />
        </div>
      </div>

      <div className="card">
        <div className="card-head">
          <div>
            <h2>Your jobs</h2>
            <p className="muted card-sub">Recent jobs for this API key. Cancel queued or in-flight work below.</p>
          </div>
          <div className="row">
            <select className="select-input" value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)}>
              <option value="">All statuses</option>
              <option value="queued">Queued</option>
              <option value="leased">Leased</option>
              <option value="streaming">Streaming</option>
              <option value="completed">Completed</option>
              <option value="failed">Failed</option>
            </select>
            <button className="btn warn" disabled={purgeBusy || (stats?.jobs_queued ?? 0) === 0} onClick={() => void cancelQueued()}>
              {purgeBusy ? 'Cancelling…' : 'Cancel all queued'}
            </button>
          </div>
        </div>
        <JobsTable jobs={filteredJobs} onCancel={(id, inflight) => void cancelJob(id, inflight)} busyId={busyId} />
      </div>

      <div className="card muted-foot">
        <p>Integration docs: <a href="/ai-context/eventforge-context.md">eventforge-context.md</a> · API reference: <a href="/docs/QueueIntegration.md">QueueIntegration.md</a></p>
      </div>
    </div>
  )
}
