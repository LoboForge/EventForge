import { useCallback, useEffect, useMemo, useState } from 'react'
import { applyPageSeo } from './seo'
import { Link } from './router'
import { getOpsKey, normalizeWorker, opsFetch, setOpsKey, type Snapshot } from './api'
import { OpsFleetTab } from './OpsFleetTab'
import { OpsVastTab } from './OpsVastTab'

type Tab = 'fleet' | 'queue' | 'failures' | 'vast'

const TAB_LABELS: Record<Tab, string> = {
  fleet: 'Fleet',
  queue: 'Queue',
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
    <div className="app-shell login card">
      <h1>EventForge Ops</h1>
      <p className="muted">Fleet monitoring, queue health, and Vast.ai GPU provisioning.</p>
      <form onSubmit={(e) => void submit(e)}>
        <label htmlFor="ops-key">Ops API key</label>
        <input id="ops-key" type="password" value={key} onChange={(e) => setKey(e.target.value)} autoComplete="off" />
        {err && <div className="error">{err}</div>}
        <button className="btn" type="submit" disabled={busy || !key.trim()}>{busy ? 'Checking…' : 'Open dashboard'}</button>
      </form>
    </div>
  )
}

function QueueTab({ snapshot }: { snapshot: Snapshot | null }) {
  const q = snapshot?.queue
  return (
    <>
      <div className="stats">
        <div className="stat"><div className="label">Queued</div><div className="value">{q?.jobs_queued ?? 0}</div></div>
        <div className="stat"><div className="label">In progress</div><div className="value">{q?.jobs_in_progress ?? 0}</div></div>
        <div className="stat"><div className="label">Failed (memory)</div><div className="value">{q?.jobs_failed ?? 0}</div></div>
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
          <thead><tr><th>Job</th><th>Capability</th><th>Tier</th><th>Worker</th><th>Lease until</th></tr></thead>
          <tbody>
            {(snapshot?.active_jobs ?? []).map((j) => (
              <tr key={j.job_id}>
                <td><code>{j.job_id.slice(0, 8)}</code></td>
                <td>{j.capability}</td>
                <td>{j.tier}</td>
                <td>{j.hostname ?? j.worker_id ?? '—'}</td>
                <td className="muted">{j.leased_until ? new Date(j.leased_until).toLocaleTimeString() : '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  )
}

function FailuresTab({ snapshot }: { snapshot: Snapshot | null }) {
  return (
    <div className="card">
      <h2>Recent failures</h2>
      <table>
        <thead><tr><th>Job</th><th>Capability</th><th>Worker</th><th>Error</th><th>When</th></tr></thead>
        <tbody>
          {(snapshot?.recent_failures ?? []).map((j) => (
            <tr key={j.job_id}>
              <td><code>{j.job_id.slice(0, 8)}</code></td>
              <td>{j.capability}</td>
              <td>{j.hostname ?? j.worker_id ?? '—'}</td>
              <td className="error">{j.error ?? '—'}</td>
              <td className="muted">{j.leased_until ? new Date(j.leased_until).toLocaleTimeString() : '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export default function OpsApp() {
  const [authed, setAuthed] = useState(!!getOpsKey())
  const [tab, setTab] = useState<Tab>('fleet')
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null)
  const [err, setErr] = useState<string | null>(null)

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
      const data = await opsFetch<Snapshot>('/v1/ops/snapshot')
      setSnapshot(data)
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

  const workers = useMemo(
    () => (snapshot?.fleet.workers ?? []).map((w) => normalizeWorker(w)),
    [snapshot],
  )

  if (!authed) return <Login onLogin={() => setAuthed(true)} />

  return (
    <div className="app-shell ops-app">
      <div className="topbar">
        <h1>EventForge Ops</h1>
        <div className="row">
          <Link to="/" className="muted">← Public site</Link>
          <span className="muted">Updated {snapshot?.generated_at ? new Date(snapshot.generated_at).toLocaleTimeString() : '—'}</span>
          <button className="btn secondary" onClick={() => void refresh()}>Refresh</button>
          <button className="btn secondary" onClick={() => { sessionStorage.removeItem('eventforge_ops_key'); setAuthed(false) }}>Sign out</button>
        </div>
      </div>
      {err && <div className="error">{err}</div>}
      <div className="stats">
        <div className="stat"><div className="label">Workers</div><div className="value">{snapshot?.fleet.workers_total ?? 0}</div></div>
        <div className="stat"><div className="label">Busy</div><div className="value">{snapshot?.fleet.workers_busy ?? 0}</div></div>
        <div className="stat"><div className="label">Stale</div><div className="value">{snapshot?.fleet.workers_stale ?? 0}</div></div>
        <div className="stat"><div className="label">Queued jobs</div><div className="value">{snapshot?.queue.jobs_queued ?? 0}</div></div>
        <div className="stat"><div className="label">In progress</div><div className="value">{snapshot?.queue.jobs_in_progress ?? 0}</div></div>
      </div>
      <div className="tabs">
        {(['fleet', 'queue', 'failures', 'vast'] as Tab[]).map((t) => (
          <button key={t} className={'tab' + (tab === t ? ' active' : '')} onClick={() => setTab(t)}>{TAB_LABELS[t]}</button>
        ))}
      </div>
      {tab === 'fleet' && <OpsFleetTab workers={workers} />}
      {tab === 'queue' && <QueueTab snapshot={snapshot} />}
      {tab === 'failures' && <FailuresTab snapshot={snapshot} />}
      {tab === 'vast' && <OpsVastTab />}
    </div>
  )
}
