import { useMemo, useState } from 'react'
import { formatDateTime } from './format'
import { SortableTable, type SortableColumn } from './SortableTable'
import {
  badgeLabel,
  formatDiskGb,
  formatVram,
  isGenFleetWorker,
  opsFetch,
  type WorkerRow,
  workerFleetRowKey,
} from './api'

type FleetFilter = 'all' | 'gen' | 'busy' | 'stale' | 'issues'

function WorkerBadges({ badges }: { badges: string[] }) {
  if (!badges.length) return <span className="badge idle">contributing</span>
  return (
    <div className="badge-row">
      {badges.map((b) => (
        <span key={b} className={'badge contrib ' + b.replace(/[^a-z0-9-]/gi, '')} title={b}>
          {badgeLabel(b)}
        </span>
      ))}
    </div>
  )
}

function TagList({ items, empty = '—' }: { items: string[]; empty?: string }) {
  if (!items.length) return <span className="muted">{empty}</span>
  return (
    <div className="tag-list">
      {items.map((t) => <span key={t} className="tag">{t}</span>)}
    </div>
  )
}

function ModelList({ title, items }: { title: string; items: string[] }) {
  if (!items.length) return null
  return (
    <div className="detail-block">
      <h4>{title} ({items.length})</h4>
      <ul className="model-list">
        {items.map((m) => <li key={m}><code>{m}</code></li>)}
      </ul>
    </div>
  )
}

function WorkerDetail({ w, onQuarantine, onUnquarantine, onRemove, busy }: {
  w: WorkerRow
  onQuarantine: (id: string) => void
  onUnquarantine: (id: string) => void
  onRemove: (id: string) => void
  busy: string | null
}) {
  return (
    <div className="worker-detail">
      <div className="detail-grid">
        <div className="detail-block">
          <h4>Identity</h4>
          <dl className="detail-dl">
            <dt>Node UUID</dt><dd><code>{w.nodeUuid || '—'}</code></dd>
            <dt>Worker auth id</dt><dd><code>{w.workerId || '—'}</code></dd>
            <dt>Fleet mode</dt><dd>{w.fleetMode || '—'}</dd>
            <dt>Transport</dt><dd>{w.transport}</dd>
            <dt>ComfyUI</dt><dd>{w.comfyOk ? 'OK' : <span className="error">down</span>}</dd>
            <dt>Quarantine</dt>
            <dd>
              {w.quarantined ? (
                <>
                  <span className="badge contrib quarantined">quarantined</span>
                  {w.quarantineReason && <span className="muted small"> {w.quarantineReason}</span>}
                  {' '}
                  <button
                    className="btn secondary small"
                    disabled={busy === `unq:${workerFleetRowKey(w)}`}
                    onClick={() => onUnquarantine(w.hostname || w.nodeUuid || w.workerId)}
                  >
                    Unquarantine
                  </button>
                </>
              ) : (
                <button
                  className="btn warn small"
                  disabled={busy === `q:${workerFleetRowKey(w)}`}
                  onClick={() => onQuarantine(w.hostname || w.nodeUuid || w.workerId)}
                >
                  Quarantine box
                </button>
              )}
            </dd>
            {w.checkInStale && (
              <>
                <dt>Ghost row</dt>
                <dd>
                  <button
                    className="btn warn small"
                    disabled={busy === `rm:${workerFleetRowKey(w)}`}
                    onClick={() => onRemove(w.hostname || w.nodeUuid || w.workerId)}
                    title="Delete this fleet row. A live box re-appears on its next check-in."
                  >
                    Remove ghost row
                  </button>
                </dd>
              </>
            )}
          </dl>
        </div>
        <div className="detail-block">
          <h4>Job stats (this task)</h4>
          <dl className="detail-dl">
            <dt>Claimed</dt><dd>{w.jobsClaimed}</dd>
            <dt>Completed</dt><dd className="ok-text">{w.jobsCompleted}</dd>
            <dt>Failed</dt><dd className={w.jobsFailed > 0 ? 'error' : ''}>{w.jobsFailed}</dd>
            <dt>Timed out</dt><dd>{w.jobsTimedOut}</dd>
            <dt>Released</dt><dd>{w.jobsReleased}</dd>
            <dt>Active job</dt><dd><code>{w.activeJobId || w.currentJobUuid || '—'}</code></dd>
          </dl>
        </div>
        <div className="detail-block">
          <h4>Capabilities</h4>
          <p className="muted detail-label">Forge queue</p>
          <TagList items={w.capabilities} empty="none" />
          <p className="muted detail-label">Claim-ready</p>
          <TagList items={w.claimReadyCapabilities} empty="none" />
          {w.capability && <p className="muted">Last claim: <code>{w.capability}</code> ({w.tier})</p>}
        </div>
      </div>
      <div className="detail-grid">
        <ModelList title="UNETs / diffusion" items={w.models.unets} />
        <ModelList title="Checkpoints" items={w.models.checkpoints} />
        <ModelList title="Text encoders / CLIP" items={w.models.clips} />
        <ModelList title="VAEs" items={w.models.vaes} />
        <ModelList title="LoRAs on disk (Comfy)" items={w.models.loras} />
        <div className="detail-block">
          <h4>Known LoRAs ({w.knownLoras.length})</h4>
          {w.knownLoras.length === 0 ? <p className="muted">None reported yet.</p> : (
            <ul className="model-list compact">
              {w.knownLoras.map((l) => <li key={l}><code>{l}</code></li>)}
            </ul>
          )}
        </div>
      </div>
    </div>
  )
}

export function OpsFleetTab({ workers, onRefresh }: { workers: WorkerRow[]; onRefresh?: () => void }) {
  const [filter, setFilter] = useState<FleetFilter>('all')
  const [expanded, setExpanded] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  const [msg, setMsg] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)

  async function quarantineWorker(id: string) {
    const reason = prompt('Quarantine reason (stops claims for this box only; consumers can still upload):', 'maintenance_worker')
    if (reason === null) return
    setBusy(`q:${id}`)
    setErr(null)
    try {
      await opsFetch(`/v1/ops/workers/${encodeURIComponent(id)}/quarantine`, {
        method: 'POST',
        body: JSON.stringify({ reason, quarantinedBy: 'ops-ui' }),
      })
      setMsg(`Quarantined ${id}`)
      onRefresh?.()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setBusy(null)
    }
  }

  async function unquarantineWorker(id: string) {
    setBusy(`unq:${id}`)
    setErr(null)
    try {
      await opsFetch(`/v1/ops/workers/${encodeURIComponent(id)}/unquarantine`, { method: 'POST' })
      setMsg(`Unquarantined ${id}`)
      onRefresh?.()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setBusy(null)
    }
  }

  async function removeWorker(id: string) {
    if (!confirm(`Remove fleet row "${id}"? A live box re-appears on its next check-in; use this to purge ghost rows whose Vast box no longer exists.`)) return
    setBusy(`rm:${id}`)
    setErr(null)
    try {
      await opsFetch(`/v1/ops/workers/${encodeURIComponent(id)}`, { method: 'DELETE' })
      setMsg(`Removed ${id}`)
      onRefresh?.()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setBusy(null)
    }
  }

  const visible = useMemo(() => {
    let rows = workers
    if (filter === 'gen') rows = rows.filter(isGenFleetWorker)
    else if (filter === 'busy') rows = rows.filter((w) => w.state === 'busy')
    else if (filter === 'stale') rows = rows.filter((w) => w.checkInStale)
    else if (filter === 'issues') rows = rows.filter((w) => w.badges.length > 0)
    const q = search.trim().toLowerCase()
    if (q) {
      rows = rows.filter((w) =>
        w.hostname.toLowerCase().includes(q)
        || w.gpuName.toLowerCase().includes(q)
        || w.capabilities.some((c) => c.toLowerCase().includes(q)),
      )
    }
    return rows
  }, [workers, filter, search])

  const genCount = workers.filter(isGenFleetWorker).length

  const columns = useMemo((): SortableColumn<WorkerRow>[] => [
    {
      id: 'expand',
      header: '',
      sortable: false,
      className: 'chevron',
      render: (w) => {
        const key = workerFleetRowKey(w)
        return expanded === key ? '▼' : '▶'
      },
    },
    {
      id: 'hostname',
      header: 'Hostname',
      sortValue: (w) => w.hostname,
      render: (w) => <strong>{w.hostname}</strong>,
    },
    {
      id: 'gpu',
      header: 'GPU',
      sortValue: (w) => w.gpuName,
      render: (w) => <span className="muted">{w.gpuName || '—'}</span>,
    },
    {
      id: 'vram',
      header: 'VRAM',
      sortValue: (w) => w.vramFreeMb,
      render: (w) => `${formatVram(w.vramFreeMb)}${w.vramTotalMb > 0 ? ` / ${formatVram(w.vramTotalMb)}` : ''}`,
    },
    {
      id: 'disk',
      header: 'Disk',
      sortValue: (w) => w.diskFreeMb,
      render: (w) => formatDiskGb(w.diskFreeMb),
    },
    {
      id: 'state',
      header: 'State',
      sortValue: (w) => (w.checkInStale ? 'stale' : w.state),
      render: (w) => (
        <span className={'badge ' + (w.checkInStale ? 'stale' : w.state === 'busy' ? 'busy' : 'idle')}>
          {w.checkInStale ? 'stale' : w.state}
        </span>
      ),
    },
    {
      id: 'badges',
      header: 'Badges',
      sortValue: (w) => w.badges.length,
      render: (w) => <WorkerBadges badges={w.badges} />,
    },
    {
      id: 'jobs',
      header: 'Jobs',
      sortValue: (w) => w.jobsCompleted,
      render: (w) => <span title="claimed / completed / failed">{w.jobsClaimed} / {w.jobsCompleted} / {w.jobsFailed}</span>,
    },
    {
      id: 'capabilities',
      header: 'Capabilities',
      sortValue: (w) => w.capabilities.join(','),
      className: 'cap-cell',
      render: (w) => (
        <>
          <TagList items={w.capabilities.slice(0, 3)} />
          {w.capabilities.length > 3 && <span className="muted">+{w.capabilities.length - 3}</span>}
        </>
      ),
    },
    {
      id: 'loras',
      header: 'LoRAs',
      sortValue: (w) => w.knownLoras.length || w.models.loras.length,
      render: (w) => w.knownLoras.length || w.models.loras.length,
      className: 'num-cell',
    },
    {
      id: 'mode',
      header: 'Mode',
      sortValue: (w) => w.fleetMode,
      render: (w) => <span className="muted">{w.fleetMode || '—'}</span>,
    },
    {
      id: 'queue',
      header: 'EF queue',
      sortValue: (w) => (w.queueAccessOk === true ? 2 : w.queueAccessOk === false ? 0 : 1),
      render: (w) => (
        w.queueAccessOk === true ? 'OK'
          : w.queueAccessOk === false ? <span className="error">{w.queueAccessError ?? 'fail'}</span>
            : '—'
      ),
    },
    {
      id: 'last_seen',
      header: 'Last seen',
      sortValue: (w) => w.lastSeenAt,
      render: (w) => <span className="muted">{formatDateTime(w.lastSeenAt)}</span>,
    },
  ], [expanded])

  return (
    <div className="card">
      <div className="card-head">
        <div>
          <h2>Connected workers</h2>
          <p className="muted card-sub">
{msg && <div className="success">{msg}</div>}
            {err && <div className="error">{err}</div>}
            {workers.length} total · {genCount} gen fleet · click a row for models, LoRAs, and job stats
            <span className="muted"> · Quarantine a broken box here — do not pause the consumer app</span>
          </p>
        </div>
        <div className="row toolbar">
          <input
            className="search-input"
            placeholder="Search hostname, GPU, capability…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          <select className="select-input" value={filter} onChange={(e) => setFilter(e.target.value as FleetFilter)}>
            <option value="all">All workers ({workers.length})</option>
            <option value="gen">Gen fleet only ({genCount})</option>
            <option value="busy">Busy only</option>
            <option value="stale">Stale only</option>
            <option value="issues">Non-contributing</option>
          </select>
        </div>
      </div>
      <SortableTable
        className="fleet-table"
        rows={visible}
        rowKey={workerFleetRowKey}
        columns={columns}
        defaultSort={{ id: 'hostname', dir: 'asc' }}
        emptyMessage="No workers match this filter."
        onRowClick={(w) => {
          const key = workerFleetRowKey(w)
          setExpanded((prev) => (prev === key ? null : key))
        }}
        rowClassName={(w) => {
          const key = workerFleetRowKey(w)
          const classes = ['fleet-row']
          if (expanded === key) classes.push('expanded')
          if (w.badges.length > 0) classes.push('has-issues')
          return classes.join(' ')
        }}
        renderRowExtra={(w) => {
          const key = workerFleetRowKey(w)
          if (expanded !== key) return null
          return (
            <tr className="fleet-detail-row">
              <td colSpan={columns.length}><WorkerDetail w={w} onQuarantine={(id) => void quarantineWorker(id)} onUnquarantine={(id) => void unquarantineWorker(id)} onRemove={(id) => void removeWorker(id)} busy={busy} /></td>
            </tr>
          )
        }}
      />
    </div>
  )
}
