import { useCallback, useEffect, useMemo, useState } from 'react'
import { applyPageSeo } from './seo'
import { Link } from './router'
import { BarChart, DonutChart, LineChart, parseMetricsSample } from './charts'
import {
  getOpsKey,
  normalizeWorker,
  opsFetch,
  setOpsKey,
  type OpsAppRow,
  type MetricsHistoryResponse,
  type Snapshot,
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

function QueueTab({ snapshot, onCancel, busyId }: { snapshot: Snapshot | null; onCancel: (jobId: string) => void; busyId: string | null }) {
  const q = snapshot?.queue
  return (
    <>
      <div className="stats compact-stats">
        <div className="stat"><div className="label">Queued</div><div className="value">{q?.jobs_queued ?? 0}</div></div>
        <div className="stat"><div className="label">In progress</div><div className="value">{q?.jobs_in_progress ?? 0}</div></div>
        <div className="stat"><div className="label">Completed</div><div className="value ok-text">{q?.jobs_completed ?? 0}</div></div>
        <div className="stat"><div className="label">Failed</div><div className="value">{q?.jobs_failed ?? 0}</div></div>
      </div>
      <div className="card">
        <h2>Queue depth by capability</h2>
        <table>
          <thead><tr><th>Capability</th><th>Queued</th><th>In progress</th><th>Failed</th></tr></thead>
          <tbody>
            {(q?.by_capability ?? []).map((row) => (
              <tr key={row.capability}>
                <td><code>{row.capability}</code></td>
                <td>{row.queued}</td>
                <td>{row.in_progress}</td>
                <td>{row.failed}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="card">
        <h2>Active jobs</h2>
        <table>
          <thead><tr><th>Job</th><th>App</th><th>Capability</th><th>Tier</th><th>Worker</th><th>Lease until</th><th></th></tr></thead>
          <tbody>
            {(snapshot?.active_jobs ?? []).map((j) => (
              <tr key={j.job_id}>
                <td><code>{j.job_id.slice(0, 8)}</code></td>
                <td className="muted"><code>{j.app_id?.slice(0, 8) ?? '—'}</code></td>
                <td>{j.capability}</td>
                <td>{j.tier}</td>
                <td>{j.hostname ?? j.worker_id ?? '—'}</td>
                <td className="muted">{j.leased_until ? new Date(j.leased_until).toLocaleTimeString() : '—'}</td>
                <td>
                  <button className="btn secondary small" disabled={busyId === j.job_id} onClick={() => onCancel(j.job_id)}>
                    {busyId === j.job_id ? '…' : 'Cancel'}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
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
    if (!confirm(`Purge all queued jobs for ${appId}?`)) return
    setBusy(`purge:${appId}`)
    setErr(null)
    try {
      const r = await opsFetch<{ removed: number }>('/v1/ops/jobs/purge-queued', {
        method: 'POST',
        body: JSON.stringify({ app_id: appId, include_in_flight: false, delete_s3: false }),
      })
      setMsg(`Removed ${r.removed} queued job(s) for ${appId}`)
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
      <table>
        <thead>
          <tr>
            <th>App ID</th><th>Status</th><th>Queued</th><th>In progress</th><th>Completed</th><th>Failed</th><th>Actions</th>
          </tr>
        </thead>
        <tbody>
          {apps.map((a) => (
            <tr key={a.app_id}>
              <td><code>{a.app_id}</code></td>
              <td>
                {a.paused ? <span className="badge paused">paused</span> : <span className="badge idle">active</span>}
                {a.pause_reason && <span className="muted small"> {a.pause_reason}</span>}
              </td>
              <td>{a.jobs_queued}</td>
              <td>{a.jobs_in_progress}</td>
              <td className="ok-text">{a.jobs_completed}</td>
              <td>{a.jobs_failed}</td>
              <td className="actions-cell">
                {a.paused
                  ? <button className="btn secondary small" disabled={busy === `unpause:${a.app_id}`} onClick={() => void unpause(a.app_id)}>Unpause</button>
                  : <button className="btn warn small" disabled={busy === `pause:${a.app_id}`} onClick={() => void pause(a.app_id)}>Pause</button>}
                <button className="btn secondary small" disabled={busy === `purge:${a.app_id}` || a.jobs_queued === 0} onClick={() => void purgeQueued(a.app_id)}>Purge queued</button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {apps.length === 0 && <p className="muted">No consumer apps with jobs in memory yet.</p>}
    </div>
  )
}

function FailuresTab({ snapshot }: { snapshot: Snapshot | null }) {
  return (
    <div className="card">
      <h2>Recent failures</h2>
      <table>
        <thead><tr><th>Job</th><th>App</th><th>Capability</th><th>Worker</th><th>Error</th><th>When</th></tr></thead>
        <tbody>
          {(snapshot?.recent_failures ?? []).map((j) => (
            <tr key={j.job_id}>
              <td><code>{j.job_id.slice(0, 8)}</code></td>
              <td className="muted"><code>{j.app_id?.slice(0, 8) ?? '—'}</code></td>
              <td>{j.capability}</td>
              <td>{j.hostname ?? j.worker_id ?? '—'}</td>
              <td className="error">{j.error ?? '—'}</td>
              <td className="muted">{j.completed_at ? new Date(j.completed_at).toLocaleString() : '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export default function OpsApp() {
  const [authed, setAuthed] = useState(!!getOpsKey())
  const [tab, setTab] = useState<Tab>('overview')
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null)
  const [metrics, setMetrics] = useState<ReturnType<typeof parseMetricsSample>[]>([])
  const [apps, setApps] = useState<OpsAppRow[]>([])
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
      const [snap, hist, appList] = await Promise.all([
        opsFetch<Snapshot>('/v1/ops/snapshot'),
        opsFetch<MetricsHistoryResponse>('/v1/ops/metrics/history?limit=60'),
        opsFetch<{ apps: OpsAppRow[] }>('/v1/ops/apps'),
      ])
      setSnapshot(snap)
      setMetrics((hist.samples ?? []).map((s) => parseMetricsSample(s)))
      setApps(appList.apps ?? [])
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
      {tab === 'queue' && <QueueTab snapshot={snapshot} onCancel={(id) => void cancelJob(id)} busyId={cancelBusy} />}
      {tab === 'apps' && <AppsTab apps={appRows} onRefresh={() => void refresh()} />}
      {tab === 'failures' && <FailuresTab snapshot={snapshot} />}
      {tab === 'vast' && <OpsVastTab />}
    </div>
  )
}
