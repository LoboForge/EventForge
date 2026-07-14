import { useCallback, useEffect, useMemo, useState } from 'react'
import { applyPageSeo } from './seo'
import { Link } from './router'
import { BarChart, DonutChart, LineChart, parseMetricsSample } from './charts'
import { formatDateTime, formatDuration, runningDurationMs } from './format'
import { useNow } from './hooks'
import { SortableTable, type SortableColumn } from './SortableTable'
import {
  getOpsKey,
  normalizeWorker,
  opsFetch,
  setOpsKey,
  type JobRow,
  type OpsAppRow,
  type MetricsHistoryResponse,
  type Snapshot,
  type WorkerRow,
} from './api'
import { OpsFleetTab } from './OpsFleetTab'
import { OpsVastTab } from './OpsVastTab'

type Tab = 'overview' | 'fleet' | 'queue' | 'apps' | 'failures' | 'vast'

const TAB_LABELS: Record<Tab, string> = {
  overview: 'Overview',
  fleet: 'Fleet',
  queue: 'Queue',
  apps: 'Consumers',
  failures: 'Failures',
  vast: 'Vast.ai',
}

function isOrphanLease(job: JobRow, workers: WorkerRow[]): boolean {
  const host = job.hostname?.trim()
  if (!host) return false
  const worker = workers.find((w) => w.hostname === host)
  if (!worker) return false
  const active = worker.currentJobUuid || worker.activeJobId
  return !!active && active !== job.job_id
}

function Login({ onLogin }: { onLogin: () => void }) {
  const [key, setKey] = useState(getOpsKey())
  const [err, setErr] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setBusy(true)
    setErr(null)
    setOpsKey(key)
    try {
      await opsFetch('/v1/ops/snapshot')
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
          <h1>EventForge Ops</h1>
          <p className="muted">Fleet monitoring, consumer management, and GPU provisioning.</p>
        </div>
      </div>
      <form onSubmit={(e) => void submit(e)}>
        <label htmlFor="ops-key">Ops API key</label>
        <input id="ops-key" type="password" value={key} onChange={(e) => setKey(e.target.value)} autoComplete="off" />
        {err && <div className="error">{err}</div>}
        <button className="btn" type="submit" disabled={busy || !key.trim()}>{busy ? 'Checking…' : 'Open console'}</button>
      </form>
    </div>
  )
}

function OverviewTab({ snapshot, metrics }: { snapshot: Snapshot | null; metrics: ReturnType<typeof parseMetricsSample>[] }) {
  const q = snapshot?.queue
  const f = snapshot?.fleet
  return (
    <div className="overview-grid">
      <div className="card chart-card">
        <h2>Queue depth</h2>
        <LineChart
          series={[
            { label: 'Queued', color: '#f0a500', values: metrics.map((m) => m.jobsQueued) },
            { label: 'In progress', color: '#3d7fd4', values: metrics.map((m) => m.jobsInProgress) },
          ]}
          height={120}
        />
      </div>
      <div className="card chart-card">
        <h2>Fleet health</h2>
        <LineChart
          series={[
            { label: 'Busy', color: '#4caf82', values: metrics.map((m) => m.workersBusy) },
            { label: 'Stale', color: '#e85d5d', values: metrics.map((m) => m.workersStale) },
            { label: 'Non-contributing', color: '#f0a500', values: metrics.map((m) => m.workersNonContributing) },
          ]}
          height={120}
        />
      </div>
      <div className="card chart-card">
        <h2>Job status</h2>
        <DonutChart
          segments={[
            { label: 'Queued', value: q?.jobs_queued ?? 0, color: '#f0a500' },
            { label: 'Active', value: q?.jobs_in_progress ?? 0, color: '#3d7fd4' },
            { label: 'Completed', value: q?.jobs_completed ?? 0, color: '#4caf82' },
            { label: 'Failed', value: q?.jobs_failed ?? 0, color: '#e85d5d' },
          ]}
        />
      </div>
      <div className="card chart-card wide">
        <h2>Queue by capability</h2>
        <BarChart
          items={(q?.by_capability ?? []).map((row) => ({
            label: row.capability,
            value: row.queued + row.in_progress,
            color: row.failed > 0 ? '#e85d5d' : '#3d7fd4',
          }))}
        />
      </div>
      <div className="card">
        <h2>System summary</h2>
        <dl className="summary-dl">
          <dt>Workers online</dt><dd>{f?.workers_total ?? 0}</dd>
          <dt>Contributing</dt><dd className="ok-text">{(f?.workers_total ?? 0) - (f?.workers_non_contributing ?? 0)}</dd>
          <dt>Non-contributing</dt><dd className={f?.workers_non_contributing ? 'warn-text' : ''}>{f?.workers_non_contributing ?? 0}</dd>
          <dt>Jobs in memory</dt><dd>{q?.jobs_total ?? '—'}</dd>
          <dt>Recent failures</dt><dd>{snapshot?.recent_failures?.length ?? 0}</dd>
          <dt>Consumer apps</dt><dd>{snapshot?.queue_by_app?.length ?? 0}</dd>
        </dl>
      </div>
    </div>
  )
}

const capabilityColumns: SortableColumn<NonNullable<Snapshot['queue']['by_capability']>[number]>[] = [
  {
    id: 'capability',
    header: 'Capability',
    sortValue: (row) => row.capability,
    render: (row) => <code>{row.capability}</code>,
  },
  {
    id: 'queued',
    header: 'Queued',
    sortValue: (row) => row.queued,
    render: (row) => row.queued,
    className: 'num-cell',
  },
  {
    id: 'in_progress',
    header: 'In progress',
    sortValue: (row) => row.in_progress,
    render: (row) => row.in_progress,
    className: 'num-cell',
  },
  {
    id: 'failed',
    header: 'Failed',
    sortValue: (row) => row.failed,
    render: (row) => row.failed,
    className: 'num-cell',
  },
]

function QueueTab({
  snapshot,
  activeJobs,
  workers,
  onCancel,
  busyId,
}: {
  snapshot: Snapshot | null
  activeJobs: JobRow[]
  workers: WorkerRow[]
  onCancel: (jobId: string) => void
  busyId: string | null
}) {
  const q = snapshot?.queue
  const now = useNow(true)
  const jobs = activeJobs.length ? activeJobs : (snapshot?.active_jobs ?? [])

  const columns = useMemo((): SortableColumn<JobRow>[] => [
    {
      id: 'job',
      header: 'Job',
      sortValue: (row) => row.job_id,
      render: (row) => <code title={row.job_id}>{row.job_id.slice(0, 8)}</code>,
    },
    {
      id: 'app',
      header: 'App',
      sortValue: (row) => row.app_id ?? '',
      render: (row) => <code title={row.app_id}>{row.app_id ?? '—'}</code>,
    },
    {
      id: 'capability',
      header: 'Capability',
      sortValue: (row) => row.capability,
      render: (row) => row.capability,
    },
    {
      id: 'tier',
      header: 'Tier',
      sortValue: (row) => row.tier,
      render: (row) => row.tier,
    },
    {
      id: 'worker',
      header: 'Worker',
      sortValue: (row) => row.hostname ?? row.worker_id ?? '',
      render: (row) => row.hostname ?? row.worker_id ?? '—',
    },
    {
      id: 'picked_up',
      header: 'Picked up',
      sortValue: (row) => row.leased_at ?? row.created_at ?? '',
      render: (row) => (
        <span className="muted" title={row.leased_at ?? row.created_at ?? undefined}>
          {formatDateTime(row.leased_at ?? row.created_at)}
        </span>
      ),
    },
    {
      id: 'running',
      header: 'Running',
      sortValue: (row) => runningDurationMs(row.leased_at, now) ?? -1,
      render: (row) => {
        const ms = runningDurationMs(row.leased_at, now)
        const orphan = isOrphanLease(row, workers)
        return (
          <span className={orphan ? 'warn-text' : ''} title={orphan ? 'Worker is busy on a different job' : undefined}>
            {ms != null ? formatDuration(ms) : '—'}
            {orphan && <span className="badge stale orphan-badge">orphan</span>}
          </span>
        )
      },
      className: 'num-cell',
    },
    {
      id: 'lease_until',
      header: 'Lease until',
      sortValue: (row) => row.leased_until ?? '',
      render: (row) => <span className="muted">{formatDateTime(row.leased_until)}</span>,
    },
    {
      id: 'actions',
      header: '',
      sortable: false,
      render: (row) => (
        <button className="btn secondary small" disabled={busyId === row.job_id} onClick={() => onCancel(row.job_id)}>
          {busyId === row.job_id ? '…' : 'Cancel'}
        </button>
      ),
      className: 'actions-cell',
    },
  ], [busyId, now, onCancel, workers])

  return (
    <>
      <div className="stats compact-stats">
        <div className="stat"><div className="label">Queued</div><div className="value">{q?.jobs_queued ?? 0}</div></div>
        <div className="stat"><div className="label">In progress</div><div className="value">{q?.jobs_in_progress ?? 0}</div></div>
        <div className="stat"><div className="label">Completed</div><div className="value ok-text">{q?.jobs_completed ?? 0}</div></div>
        <div className="stat"><div className="label">Failed</div><div className="value">{q?.jobs_failed ?? 0}</div></div>
      </div>

      <div className="card queue-active-card">
        <div className="card-head">
          <div>
            <h2>Jobs in progress</h2>
            <p className="muted card-sub">
              {jobs.length} leased job{jobs.length === 1 ? '' : 's'} · sorted by runtime · orphan = worker moved on without completing
            </p>
          </div>
        </div>
        <SortableTable
          className="queue-active-table"
          rows={jobs}
          rowKey={(row) => row.job_id}
          columns={columns}
          defaultSort={{ id: 'running', dir: 'desc' }}
          rowClassName={(row) => (isOrphanLease(row, workers) ? 'orphan-row' : undefined)}
          emptyMessage="No jobs in progress."
        />
      </div>

      <div className="card">
        <h2>Queue depth by capability</h2>
        <SortableTable
          className="queue-cap-table"
          rows={q?.by_capability ?? []}
          rowKey={(row) => row.capability}
          defaultSort={{ id: 'queued', dir: 'desc' }}
          columns={capabilityColumns}
          emptyMessage="No queued work."
        />
      </div>
    </>
  )
}

function AppsTab({ apps, onRefresh }: { apps: OpsAppRow[]; onRefresh: () => void }) {
  const [busy, setBusy] = useState<string | null>(null)
  const [msg, setMsg] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)

  async function pause(appId: string) {
    const reason = prompt('Pause reason (e.g. generations_exhausted):', 'generations_exhausted')
    if (reason === null) return
    setBusy(`pause:${appId}`)
    setErr(null)
    try {
      await opsFetch(`/v1/ops/apps/${encodeURIComponent(appId)}/pause`, { method: 'POST', body: JSON.stringify({ reason }) })
      setMsg(`Paused ${appId}`)
      onRefresh()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setBusy(null)
    }
  }

  async function unpause(appId: string) {
    setBusy(`unpause:${appId}`)
    setErr(null)
    try {
      await opsFetch(`/v1/ops/apps/${encodeURIComponent(appId)}/unpause`, { method: 'POST' })
      setMsg(`Unpaused ${appId}`)
      onRefresh()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setBusy(null)
    }
  }

  async function purgeQueued(appId: string) {
    if (!confirm(`Purge all queued jobs for ${appId}? This also deletes S3 job artifacts (inputs/outputs) for those jobs.`)) return
    setBusy(`purge:${appId}`)
    setErr(null)
    try {
      const r = await opsFetch<{ removed: number }>('/v1/ops/jobs/purge-queued', {
        method: 'POST',
        body: JSON.stringify({ app_id: appId, include_in_flight: false, delete_s3: true }),
      })
      setMsg(`Removed ${r.removed} queued job(s) for ${appId} (S3 artifacts deleted)`)
      onRefresh()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setBusy(null)
    }
  }

  return (
    <div className="card">
      {msg && <div className="success">{msg}</div>}
      {err && <div className="error">{err}</div>}
      <div className="card-head">
        <div>
          <h2>Consumer apps</h2>
          <p className="muted card-sub">Pause enqueue when a customer is out of generations. Purge queued work per app.</p>
        </div>
      </div>
      <SortableTable
        rows={apps}
        rowKey={(a) => a.app_id}
        defaultSort={{ id: 'queued', dir: 'desc' }}
        columns={[
          {
            id: 'app_id',
            header: 'App ID',
            sortValue: (a) => a.app_id,
            render: (a) => <code>{a.app_id}</code>,
          },
          {
            id: 'status',
            header: 'Status',
            sortValue: (a) => (a.paused ? 1 : 0),
            render: (a) => (
              <>
                {a.paused ? <span className="badge paused">paused</span> : <span className="badge idle">active</span>}
                {a.pause_reason && <span className="muted small"> {a.pause_reason}</span>}
              </>
            ),
          },
          {
            id: 'jobs_queued',
            header: 'Queued',
            sortValue: (a) => a.jobs_queued,
            render: (a) => a.jobs_queued,
            className: 'num-cell',
          },
          {
            id: 'jobs_in_progress',
            header: 'In progress',
            sortValue: (a) => a.jobs_in_progress,
            render: (a) => a.jobs_in_progress,
            className: 'num-cell',
          },
          {
            id: 'jobs_completed',
            header: 'Completed',
            sortValue: (a) => a.jobs_completed,
            render: (a) => <span className="ok-text">{a.jobs_completed}</span>,
            className: 'num-cell',
          },
          {
            id: 'jobs_failed',
            header: 'Failed',
            sortValue: (a) => a.jobs_failed,
            render: (a) => a.jobs_failed,
            className: 'num-cell',
          },
          {
            id: 'actions',
            header: 'Actions',
            sortable: false,
            render: (a) => (
              <div className="actions-cell">
                {a.paused
                  ? <button className="btn secondary small" disabled={busy === `unpause:${a.app_id}`} onClick={() => void unpause(a.app_id)}>Unpause</button>
                  : <button className="btn warn small" disabled={busy === `pause:${a.app_id}`} onClick={() => void pause(a.app_id)}>Pause</button>}
                <button className="btn secondary small" disabled={busy === `purge:${a.app_id}` || a.jobs_queued === 0} onClick={() => void purgeQueued(a.app_id)}>Purge queued</button>
              </div>
            ),
            className: 'actions-cell',
          },
        ]}
        emptyMessage="No consumer apps with jobs in memory yet."
      />
    </div>
  )
}

function FailuresTab({ snapshot }: { snapshot: Snapshot | null }) {
  const failures = snapshot?.recent_failures ?? []
  return (
    <div className="card">
      <h2>Recent failures</h2>
      <SortableTable
        rows={failures}
        rowKey={(j) => j.job_id}
        defaultSort={{ id: 'when', dir: 'desc' }}
        columns={[
          {
            id: 'job',
            header: 'Job',
            sortValue: (j) => j.job_id,
            render: (j) => <code title={j.job_id}>{j.job_id.slice(0, 8)}</code>,
          },
          {
            id: 'app',
            header: 'App',
            sortValue: (j) => j.app_id ?? '',
            render: (j) => <code className="muted" title={j.app_id}>{j.app_id?.slice(0, 12) ?? '—'}</code>,
          },
          {
            id: 'capability',
            header: 'Capability',
            sortValue: (j) => j.capability,
            render: (j) => j.capability,
          },
          {
            id: 'worker',
            header: 'Worker',
            sortValue: (j) => j.hostname ?? j.worker_id ?? '',
            render: (j) => j.hostname ?? j.worker_id ?? '—',
          },
          {
            id: 'error',
            header: 'Error',
            sortValue: (j) => j.error ?? '',
            render: (j) => <span className="error">{j.error ?? '—'}</span>,
          },
          {
            id: 'when',
            header: 'When',
            sortValue: (j) => j.completed_at ?? j.created_at ?? '',
            render: (j) => <span className="muted">{formatDateTime(j.completed_at ?? j.created_at)}</span>,
          },
        ]}
        emptyMessage="No recent failures."
      />
    </div>
  )
}

export default function OpsApp() {
  const [authed, setAuthed] = useState(!!getOpsKey())
  const [tab, setTab] = useState<Tab>('overview')
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null)
  const [metrics, setMetrics] = useState<ReturnType<typeof parseMetricsSample>[]>([])
  const [apps, setApps] = useState<OpsAppRow[]>([])
  const [activeJobs, setActiveJobs] = useState<JobRow[]>([])
  const [err, setErr] = useState<string | null>(null)
  const [cancelBusy, setCancelBusy] = useState<string | null>(null)

  useEffect(() => {
    return applyPageSeo({
      title: 'EventForge Ops',
      description: 'Authenticated fleet, queue, and Vast.ai operations console for EventForge.',
      canonicalPath: '/ops',
      noindex: true,
    })
  }, [])

  const refresh = useCallback(async () => {
    if (!getOpsKey()) return
    try {
      const [snap, hist, appList, active] = await Promise.all([
        opsFetch<Snapshot>('/v1/ops/snapshot'),
        opsFetch<MetricsHistoryResponse>('/v1/ops/metrics/history?limit=60'),
        opsFetch<{ apps: OpsAppRow[] }>('/v1/ops/apps'),
        opsFetch<{ jobs: JobRow[] }>('/v1/ops/jobs/active'),
      ])
      setSnapshot(snap)
      setMetrics((hist.samples ?? []).map((s) => parseMetricsSample(s)))
      setApps(appList.apps ?? [])
      setActiveJobs(active.jobs ?? [])
      setErr(null)
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    }
  }, [])

  useEffect(() => {
    if (!authed) return
    void refresh()
    const id = window.setInterval(() => void refresh(), 15000)
    return () => window.clearInterval(id)
  }, [authed, refresh])

  useEffect(() => {
    if (!authed) return
    const key = getOpsKey()
    const proto = location.protocol === 'https:' ? 'wss' : 'ws'
    const ws = new WebSocket(`${proto}://${location.host}/v1/ops/ws?token=${encodeURIComponent(key)}`)
    ws.onmessage = (ev) => {
      try {
        const msg = JSON.parse(String(ev.data)) as { type?: string; snapshot?: Snapshot }
        if (msg.type === 'ops.fleet.snapshot' && msg.snapshot) setSnapshot(msg.snapshot)
        else if (msg.type?.startsWith('ops.job.')) void refresh()
      } catch { /* ignore */ }
    }
    return () => ws.close()
  }, [authed, refresh])

  const workers = useMemo(() => (snapshot?.fleet.workers ?? []).map((w) => normalizeWorker(w)), [snapshot])

  async function cancelJob(jobId: string) {
    if (!confirm(`Cancel job ${jobId.slice(0, 8)}?`)) return
    setCancelBusy(jobId)
    try {
      await opsFetch(`/v1/ops/jobs/${jobId}/cancel`, { method: 'POST', body: JSON.stringify({ include_in_flight: true }) })
      await refresh()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setCancelBusy(null)
    }
  }

  if (!authed) return <Login onLogin={() => setAuthed(true)} />

  const nonContrib = snapshot?.fleet.workers_non_contributing ?? workers.filter((w) => w.badges.length > 0).length
  const appRows = apps.length ? apps : (snapshot?.queue_by_app ?? []).map((a) => ({
    app_id: a.app_id, paused: a.paused, jobs_queued: a.queued, jobs_in_progress: a.in_progress,
    jobs_failed: a.failed, jobs_completed: a.completed,
  }))

  return (
    <div className="app-shell ops-app">
      <header className="ops-header">
        <div className="ops-header-left">
          <span className="ops-logo-mark">EF</span>
          <div>
            <h1>EventForge Ops</h1>
            <p className="muted card-sub">Production GPU fleet · queue · consumers</p>
          </div>
        </div>
        <div className="row ops-header-actions">
          <Link to="/" className="muted">Public site</Link>
          <Link to="/dashboard" className="muted">Customer dashboard</Link>
          <span className="ops-live-dot" title="Live updates" />
          <span className="muted ops-updated">Updated {snapshot?.generated_at ? new Date(snapshot.generated_at).toLocaleTimeString() : '—'}</span>
          <button className="btn secondary" onClick={() => void refresh()}>Refresh</button>
          <button className="btn secondary" onClick={() => { sessionStorage.removeItem('eventforge_ops_key'); setAuthed(false) }}>Sign out</button>
        </div>
      </header>
      {err && <div className="error">{err}</div>}
      <div className="stats ops-kpi">
        <div className="stat kpi"><div className="label">Workers</div><div className="value">{snapshot?.fleet.workers_total ?? 0}</div></div>
        <div className="stat kpi"><div className="label">Busy</div><div className="value">{snapshot?.fleet.workers_busy ?? 0}</div></div>
        <div className="stat kpi"><div className="label">Stale</div><div className="value">{snapshot?.fleet.workers_stale ?? 0}</div></div>
        <div className="stat kpi warn"><div className="label">Non-contributing</div><div className="value">{nonContrib}</div></div>
        <div className="stat kpi"><div className="label">Queued</div><div className="value">{snapshot?.queue.jobs_queued ?? 0}</div></div>
        <div className="stat kpi"><div className="label">In progress</div><div className="value">{snapshot?.queue.jobs_in_progress ?? 0}</div></div>
        <div className="stat kpi"><div className="label">Failed</div><div className="value">{snapshot?.queue.jobs_failed ?? 0}</div></div>
        <div className="stat kpi"><div className="label">Apps</div><div className="value">{appRows.length}</div></div>
      </div>
      <nav className="tabs ops-tabs">
        {(['overview', 'fleet', 'queue', 'apps', 'failures', 'vast'] as Tab[]).map((t) => (
          <button key={t} className={'tab' + (tab === t ? ' active' : '')} onClick={() => setTab(t)}>{TAB_LABELS[t]}</button>
        ))}
      </nav>
      {tab === 'overview' && <OverviewTab snapshot={snapshot} metrics={metrics} />}
      {tab === 'fleet' && <OpsFleetTab workers={workers} />}
      {tab === 'queue' && (
        <QueueTab
          snapshot={snapshot}
          activeJobs={activeJobs}
          workers={workers}
          onCancel={(id) => void cancelJob(id)}
          busyId={cancelBusy}
        />
      )}
      {tab === 'apps' && <AppsTab apps={appRows} onRefresh={() => void refresh()} />}
      {tab === 'failures' && <FailuresTab snapshot={snapshot} />}
      {tab === 'vast' && <OpsVastTab />}
    </div>
  )
}
