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
  opsOutputUrl,
  setOpsKey,
  type JobRow,
  type ModerationJobRow,
  type OpsAppRow,
  type OpsJobsResponse,
  type MetricsHistoryResponse,
  type Snapshot,
  type WorkerRow,
} from './api'
import { OpsFleetTab } from './OpsFleetTab'
import { OpsVastTab } from './OpsVastTab'
import { UploadDock } from './UploadDock'

type Tab = 'overview' | 'fleet' | 'queue' | 'apps' | 'moderation' | 'failures' | 'vast'

const TAB_LABELS: Record<Tab, string> = {
  overview: 'Overview',
  fleet: 'Fleet',
  queue: 'Queue',
  apps: 'Consumers',
  moderation: 'Moderation',
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

function PromptText({ prompt }: { prompt?: string | null }) {
  const [open, setOpen] = useState(false)
  if (!prompt) return <span className="muted">—</span>
  const short = prompt.length > 90 ? prompt.slice(0, 90) + '…' : prompt
  const clamped = prompt.length > 90
  return (
    <span
      className="prompt-cell"
      title={clamped && !open ? prompt : undefined}
      style={{ whiteSpace: open ? 'pre-wrap' : 'nowrap', cursor: clamped ? 'pointer' : 'default', display: 'inline-block', maxWidth: open ? 480 : 320, overflow: 'hidden', textOverflow: 'ellipsis', verticalAlign: 'middle' }}
      onClick={() => clamped && setOpen((v) => !v)}
    >
      {open ? prompt : short}
      {clamped && <span className="muted small"> {open ? ' (less)' : ' (more)'}</span>}
    </span>
  )
}

function OutputThumb({ job }: { job: ModerationJobRow }) {
  const [failed, setFailed] = useState(false)
  if (!job.has_output) return <span className="muted small">no output</span>
  const url = opsOutputUrl(job.job_id)
  if (job.output_kind === 'image' && !failed) {
    return (
      <a href={url} target="_blank" rel="noreferrer" title="Open full output">
        <img
          src={url}
          alt=""
          loading="lazy"
          onError={() => setFailed(true)}
          style={{ width: 56, height: 56, objectFit: 'cover', borderRadius: 6, background: '#1b1f27', display: 'block' }}
        />
      </a>
    )
  }
  const label = job.output_kind === 'video' ? '▶ video'
    : job.output_kind === 'audio' ? '♪ audio'
    : job.output_kind === 'text' ? 'text'
    : failed ? 'preview n/a' : 'file'
  return (
    <a href={url} target="_blank" rel="noreferrer" className="badge idle" title="Open output in new tab">{label}</a>
  )
}

type QueueSubTab = 'queued' | 'in_flight' | 'completed'

function QueueTab({
  snapshot,
  activeJobs,
  workers,
  onRefresh,
}: {
  snapshot: Snapshot | null
  activeJobs: JobRow[]
  workers: WorkerRow[]
  onRefresh: () => void
}) {
  const q = snapshot?.queue
  const now = useNow(true)
  const [sub, setSub] = useState<QueueSubTab>('in_flight')
  const [rows, setRows] = useState<ModerationJobRow[]>([])
  const [loading, setLoading] = useState(false)
  const [busyId, setBusyId] = useState<string | null>(null)
  const [msg, setMsg] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)

  const statusParam: Record<QueueSubTab, string> = {
    queued: 'queued',
    in_flight: 'active',
    completed: 'completed',
  }

  const load = useCallback(async () => {
    setLoading(true)
    setErr(null)
    try {
      const r = await opsFetch<OpsJobsResponse>(`/v1/ops/jobs?status=${statusParam[sub]}&limit=200`)
      setRows(r.jobs ?? [])
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setLoading(false)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sub])

  useEffect(() => { void load() }, [load])

  // Keep in-flight view live via the poll/WS-fed activeJobs prop.
  const inFlightRows: ModerationJobRow[] = useMemo(() => {
    if (sub !== 'in_flight') return rows
    const byId = new Map(rows.map((r) => [r.job_id, r]))
    return (activeJobs.length ? activeJobs : (snapshot?.active_jobs ?? [])).map((a) => ({
      ...(byId.get(a.job_id) ?? {}),
      ...a,
    }))
  }, [sub, rows, activeJobs, snapshot])

  async function cancelJob(jobId: string, includeInFlight: boolean) {
    if (!confirm(`Cancel job ${jobId.slice(0, 8)}? Queued work is removed; in-flight work is failed so a late worker result cannot revive it. S3 job artifacts are deleted.`)) return
    setBusyId(jobId)
    setErr(null); setMsg(null)
    try {
      await opsFetch(`/v1/ops/jobs/${jobId}/cancel`, { method: 'POST', body: JSON.stringify({ include_in_flight: includeInFlight, delete_artifacts: true }) })
      setMsg(`Cancelled ${jobId.slice(0, 8)}`)
      await load(); onRefresh()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setBusyId(null)
    }
  }

  async function deleteJob(job: ModerationJobRow) {
    if (!confirm(`Delete completed job ${job.job_id.slice(0, 8)} for ${job.app_id ?? 'app'}?\n\nThis permanently deletes the output image/file from S3 storage and removes it from the event store so the consumer can NO LONGER receive or pick up this result. This cannot be undone.`)) return
    setBusyId(job.job_id)
    setErr(null); setMsg(null)
    try {
      await opsFetch(`/v1/ops/jobs/${job.job_id}`, { method: 'DELETE' })
      setMsg(`Deleted ${job.job_id.slice(0, 8)} and its S3 output`)
      await load(); onRefresh()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setBusyId(null)
    }
  }

  const inFlightColumns = useMemo((): SortableColumn<ModerationJobRow>[] => [
    { id: 'job', header: 'Job', sortValue: (r) => r.job_id, render: (r) => <code title={r.job_id}>{r.job_id.slice(0, 8)}</code> },
    { id: 'app', header: 'App', sortValue: (r) => r.app_id ?? '', render: (r) => <code title={r.app_id}>{r.app_id ?? '—'}</code> },
    { id: 'capability', header: 'Capability', sortValue: (r) => r.capability, render: (r) => r.capability },
    { id: 'prompt', header: 'Prompt', sortable: false, render: (r) => <PromptText prompt={r.prompt} /> },
    { id: 'worker', header: 'Worker', sortValue: (r) => r.hostname ?? r.worker_id ?? '', render: (r) => r.hostname ?? r.worker_id ?? '—' },
    {
      id: 'running', header: 'Running', className: 'num-cell',
      sortValue: (r) => runningDurationMs(r.leased_at, now) ?? -1,
      render: (r) => {
        const ms = runningDurationMs(r.leased_at, now)
        const orphan = isOrphanLease(r, workers)
        return (
          <span className={orphan ? 'warn-text' : ''} title={orphan ? 'Worker is busy on a different job' : undefined}>
            {ms != null ? formatDuration(ms) : '—'}
            {orphan && <span className="badge stale orphan-badge">orphan</span>}
          </span>
        )
      },
    },
    {
      id: 'actions', header: '', sortable: false, className: 'actions-cell',
      render: (r) => (
        <button className="btn secondary small" disabled={busyId === r.job_id} onClick={() => void cancelJob(r.job_id, true)}>
          {busyId === r.job_id ? '…' : 'Cancel'}
        </button>
      ),
    },
  ], [busyId, now, workers])

  const queuedColumns = useMemo((): SortableColumn<ModerationJobRow>[] => [
    { id: 'job', header: 'Job', sortValue: (r) => r.job_id, render: (r) => <code title={r.job_id}>{r.job_id.slice(0, 8)}</code> },
    { id: 'app', header: 'App', sortValue: (r) => r.app_id ?? '', render: (r) => <code title={r.app_id}>{r.app_id ?? '—'}</code> },
    { id: 'capability', header: 'Capability', sortValue: (r) => r.capability, render: (r) => r.capability },
    { id: 'tier', header: 'Tier', sortValue: (r) => r.tier, render: (r) => r.tier },
    { id: 'prompt', header: 'Prompt', sortable: false, render: (r) => <PromptText prompt={r.prompt} /> },
    { id: 'created', header: 'Created', sortValue: (r) => r.created_at ?? '', render: (r) => <span className="muted">{formatDateTime(r.created_at)}</span> },
    {
      id: 'actions', header: '', sortable: false, className: 'actions-cell',
      render: (r) => (
        <button className="btn secondary small" disabled={busyId === r.job_id} onClick={() => void cancelJob(r.job_id, false)}>
          {busyId === r.job_id ? '…' : 'Cancel'}
        </button>
      ),
    },
  ], [busyId])

  const completedColumns = useMemo((): SortableColumn<ModerationJobRow>[] => [
    { id: 'thumb', header: '', sortable: false, render: (r) => <OutputThumb job={r} /> },
    { id: 'job', header: 'Job', sortValue: (r) => r.job_id, render: (r) => <code title={r.job_id}>{r.job_id.slice(0, 8)}</code> },
    { id: 'app', header: 'App', sortValue: (r) => r.app_id ?? '', render: (r) => <code title={r.app_id}>{r.app_id ?? '—'}</code> },
    { id: 'capability', header: 'Capability', sortValue: (r) => r.capability, render: (r) => r.capability },
    { id: 'prompt', header: 'Prompt', sortable: false, render: (r) => <PromptText prompt={r.prompt} /> },
    { id: 'output', header: 'Output', sortValue: (r) => r.output_kind ?? '', render: (r) => <span className="muted small">{r.output_kind ?? '—'}</span> },
    { id: 'completed', header: 'Completed', sortValue: (r) => r.completed_at ?? '', render: (r) => <span className="muted">{formatDateTime(r.completed_at)}</span> },
    {
      id: 'actions', header: '', sortable: false, className: 'actions-cell',
      render: (r) => (
        <button className="btn warn small" disabled={busyId === r.job_id} onClick={() => void deleteJob(r)}>
          {busyId === r.job_id ? '…' : 'Delete'}
        </button>
      ),
    },
  ], [busyId])

  return (
    <>
      <div className="stats compact-stats">
        <div className="stat"><div className="label">Queued</div><div className="value">{q?.jobs_queued ?? 0}</div></div>
        <div className="stat"><div className="label">In progress</div><div className="value">{q?.jobs_in_progress ?? 0}</div></div>
        <div className="stat"><div className="label">Completed</div><div className="value ok-text">{q?.jobs_completed ?? 0}</div></div>
        <div className="stat"><div className="label">Failed</div><div className="value">{q?.jobs_failed ?? 0}</div></div>
      </div>

      <div className="card">
        {msg && <div className="success">{msg}</div>}
        {err && <div className="error">{err}</div>}
        <div className="card-head">
          <div>
            <h2>Jobs</h2>
            <p className="muted card-sub">Inspect queued, in-flight, and completed jobs. Prompts are click-to-expand; completed rows show the output thumbnail.</p>
          </div>
          <div className="row" style={{ gap: '0.4rem', alignItems: 'center' }}>
            <nav className="tabs" style={{ margin: 0 }}>
              {(['queued', 'in_flight', 'completed'] as QueueSubTab[]).map((s) => (
                <button key={s} className={'tab' + (sub === s ? ' active' : '')} onClick={() => setSub(s)}>
                  {s === 'in_flight' ? 'In flight' : s === 'queued' ? 'Queued' : 'Completed'}
                </button>
              ))}
            </nav>
            <button className="btn secondary small" disabled={loading} onClick={() => void load()}>{loading ? '…' : 'Reload'}</button>
          </div>
        </div>

        {sub === 'in_flight' && (
          <SortableTable
            className="queue-active-table"
            rows={inFlightRows}
            rowKey={(r) => r.job_id}
            columns={inFlightColumns}
            defaultSort={{ id: 'running', dir: 'desc' }}
            rowClassName={(r) => (isOrphanLease(r, workers) ? 'orphan-row' : undefined)}
            emptyMessage={loading ? 'Loading…' : 'No jobs in progress.'}
          />
        )}
        {sub === 'queued' && (
          <SortableTable
            rows={rows}
            rowKey={(r) => r.job_id}
            columns={queuedColumns}
            defaultSort={{ id: 'created', dir: 'desc' }}
            emptyMessage={loading ? 'Loading…' : 'No queued jobs.'}
          />
        )}
        {sub === 'completed' && (
          <SortableTable
            rows={rows}
            rowKey={(r) => r.job_id}
            columns={completedColumns}
            defaultSort={{ id: 'completed', dir: 'desc' }}
            emptyMessage={loading ? 'Loading…' : 'No completed jobs in memory.'}
          />
        )}
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

function ModerationTab({ apps, onRefresh }: { apps: OpsAppRow[]; onRefresh: () => void }) {
  const [appId, setAppId] = useState('')
  const [keyword, setKeyword] = useState('')
  const [includeInFlight, setIncludeInFlight] = useState(true)
  const [preview, setPreview] = useState<{ matched: number; ids: string[] } | null>(null)
  const [confirmText, setConfirmText] = useState('')
  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)

  const canPreview = appId.trim().length > 0 && keyword.trim().length > 0

  async function runDryRun() {
    setBusy(true); setErr(null); setMsg(null); setPreview(null)
    try {
      const r = await opsFetch<{ matched: number; job_ids_sample: string[] }>('/v1/ops/jobs/cancel-by-keyword', {
        method: 'POST',
        body: JSON.stringify({ app_id: appId.trim(), keyword: keyword.trim(), include_in_flight: includeInFlight, dry_run: true }),
      })
      setPreview({ matched: r.matched, ids: r.job_ids_sample ?? [] })
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setBusy(false)
    }
  }

  async function runCancel() {
    if (confirmText.trim() !== appId.trim()) {
      setErr(`Type the exact app id (${appId.trim()}) to confirm.`)
      return
    }
    setBusy(true); setErr(null); setMsg(null)
    try {
      const r = await opsFetch<{ cancelled: number; matched: number }>('/v1/ops/jobs/cancel-by-keyword', {
        method: 'POST',
        body: JSON.stringify({ app_id: appId.trim(), keyword: keyword.trim(), include_in_flight: includeInFlight, dry_run: false, delete_s3: true }),
      })
      setMsg(`Cancelled ${r.cancelled} of ${r.matched} matching job(s) for ${appId.trim()}.`)
      setPreview(null); setConfirmText('')
      onRefresh()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="card">
      {msg && <div className="success">{msg}</div>}
      {err && <div className="error">{err}</div>}
      <div className="card-head">
        <div>
          <h2>Manual moderation — cancel by keyword</h2>
          <p className="muted card-sub">
            Cancel queued and in-flight jobs for one consumer whose <strong>prompt text</strong> contains a keyword you type.
            This is a manual, explicit action — no automatic content scanning. It never pauses the app or quarantines workers,
            and only affects the selected consumer. Preview matches first, then confirm.
          </p>
        </div>
      </div>

      <div className="row" style={{ flexWrap: 'wrap', gap: '0.75rem', alignItems: 'flex-end' }}>
        <label style={{ display: 'flex', flexDirection: 'column', gap: '0.25rem' }}>
          <span className="muted small">Consumer / app id</span>
          <input list="moderation-apps" value={appId} onChange={(e) => { setAppId(e.target.value); setPreview(null) }} placeholder="app id" autoComplete="off" />
          <datalist id="moderation-apps">
            {apps.map((a) => <option key={a.app_id} value={a.app_id} />)}
          </datalist>
        </label>
        <label style={{ display: 'flex', flexDirection: 'column', gap: '0.25rem', flex: '1 1 240px' }}>
          <span className="muted small">Keyword (literal, case-insensitive substring of the prompt)</span>
          <input value={keyword} onChange={(e) => { setKeyword(e.target.value); setPreview(null) }} placeholder="keyword to match in prompt" autoComplete="off" />
        </label>
        <label className="muted" style={{ display: 'flex', gap: '0.35rem', alignItems: 'center' }}>
          <input type="checkbox" checked={includeInFlight} onChange={(e) => setIncludeInFlight(e.target.checked)} />
          Include in-flight
        </label>
        <button className="btn secondary" disabled={busy || !canPreview} onClick={() => void runDryRun()}>
          {busy ? '…' : 'Preview matches'}
        </button>
      </div>

      {preview && (
        <div style={{ marginTop: '1rem' }}>
          <p>
            <strong>{preview.matched}</strong> matching job(s) for <code>{appId.trim()}</code> containing “{keyword.trim()}”.
            {preview.ids.length > 0 && <span className="muted small"> Sample: {preview.ids.slice(0, 10).map((i) => i.slice(0, 8)).join(', ')}</span>}
          </p>
          {preview.matched > 0 && (
            <div className="row" style={{ gap: '0.5rem', alignItems: 'center', flexWrap: 'wrap' }}>
              <input
                value={confirmText}
                onChange={(e) => setConfirmText(e.target.value)}
                placeholder={`Type "${appId.trim()}" to confirm`}
                autoComplete="off"
                style={{ minWidth: 220 }}
              />
              <button className="btn warn" disabled={busy || confirmText.trim() !== appId.trim()} onClick={() => void runCancel()}>
                {busy ? 'Cancelling…' : `Cancel ${preview.matched} matching job(s)`}
              </button>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

function AppsTab({ apps, onRefresh }: { apps: OpsAppRow[]; onRefresh: () => void }) {
  const [busy, setBusy] = useState<string | null>(null)
  const [msg, setMsg] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)

  async function pause(appId: string) {
    const reason = prompt('Pause reason (billing only — blocks ALL uploads for this app). For a broken GPU box, quarantine the worker on the Fleet tab instead.\n\nReason:', 'generations_exhausted')
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
          <p className="muted card-sub">Pause blocks all job enqueue (HTTP 402) for billing/quota holds — not for broken workers. Quarantine a box on the Fleet tab instead. Purge queued work per app.</p>
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

function FailuresTab({ snapshot, onRefresh }: { snapshot: Snapshot | null; onRefresh: () => void }) {
  const failures = snapshot?.recent_failures ?? []
  const [busy, setBusy] = useState<number | 'all' | string | null>(null)
  const [msg, setMsg] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [capability, setCapability] = useState('')
  const caps = Array.from(new Set([
    ...failures.map((j) => j.capability).filter(Boolean),
    'wan', 'image', 'ltx', 'ollama',
  ])).sort((a, b) => a.localeCompare(b))

  async function requeue(opts: {
    windowHours?: number | null
    jobId?: string
    capability?: string
  }) {
    const cap = (opts.capability ?? capability).trim()
    const parts: string[] = []
    if (opts.jobId) parts.push(`job ${opts.jobId.slice(0, 8)}…`)
    else if (opts.windowHours == null) parts.push('ALL failed jobs')
    else parts.push(`failed in last ${opts.windowHours}h`)
    if (cap && !opts.jobId) parts.push(`capability=${cap}`)
    const label = parts.join(', ')
    if (!confirm(`Requeue ${label}? They will be moved back onto the queue and retried.`)) return
    setBusy(opts.jobId ?? opts.windowHours ?? 'all')
    setErr(null)
    setMsg(null)
    try {
      const body: Record<string, unknown> = {}
      if (opts.jobId) body.jobId = opts.jobId
      if (opts.windowHours != null) body.failedWithinHours = opts.windowHours
      if (cap) body.capability = cap
      const r = await opsFetch<{ requeued: number }>('/v1/ops/jobs/requeue-failed', {
        method: 'POST',
        body: JSON.stringify(body),
      })
      setMsg(`Requeued ${r.requeued} failed job(s) (${label}).`)
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
        <h2>Recent failures</h2>
        <div className="row" style={{ flexWrap: 'wrap', gap: '0.5rem', alignItems: 'center' }}>
          <label className="muted" style={{ display: 'flex', gap: '0.35rem', alignItems: 'center' }}>
            Capability
            <select
              value={capability}
              onChange={(e) => setCapability(e.target.value)}
              disabled={busy !== null}
            >
              <option value="">all</option>
              {caps.map((c) => (
                <option key={c} value={c}>{c}</option>
              ))}
            </select>
          </label>
          <button className="btn secondary" disabled={busy !== null} onClick={() => void requeue({ windowHours: 1 })}>
            {busy === 1 ? 'Requeuing…' : 'Requeue (1h)'}
          </button>
          <button className="btn secondary" disabled={busy !== null} onClick={() => void requeue({ windowHours: 6 })}>
            {busy === 6 ? 'Requeuing…' : 'Requeue (6h)'}
          </button>
          <button className="btn secondary" disabled={busy !== null} onClick={() => void requeue({ windowHours: null })}>
            {busy === 'all' ? 'Requeuing…' : 'Requeue (All)'}
          </button>
        </div>
      </div>
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
          {
            id: 'actions',
            header: '',
            sortValue: () => '',
            render: (j) => (
              <button
                className="btn secondary small"
                disabled={busy !== null}
                onClick={() => void requeue({ jobId: j.job_id })}
              >
                {busy === j.job_id ? '…' : 'Requeue'}
              </button>
            ),
            className: 'actions-cell',
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
        {(['overview', 'fleet', 'queue', 'apps', 'moderation', 'failures', 'vast'] as Tab[]).map((t) => (
          <button key={t} className={'tab' + (tab === t ? ' active' : '')} onClick={() => setTab(t)}>{TAB_LABELS[t]}</button>
        ))}
      </nav>
      {tab === 'overview' && <OverviewTab snapshot={snapshot} metrics={metrics} />}
      {tab === 'fleet' && <OpsFleetTab workers={workers} onRefresh={refresh} />}
      {tab === 'queue' && (
        <QueueTab
          snapshot={snapshot}
          activeJobs={activeJobs}
          workers={workers}
          onRefresh={() => void refresh()}
        />
      )}
      {tab === 'apps' && <AppsTab apps={appRows} onRefresh={() => void refresh()} />}
      {tab === 'moderation' && <ModerationTab apps={appRows} onRefresh={() => void refresh()} />}
      {tab === 'failures' && <FailuresTab snapshot={snapshot} onRefresh={() => void refresh()} />}
      {tab === 'vast' && <OpsVastTab />}
      <UploadDock />
    </div>
  )
}
