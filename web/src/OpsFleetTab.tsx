import { Fragment, useMemo, useState } from 'react'
import {
  formatDiskGb,
  formatVram,
  isGenFleetWorker,
  type WorkerRow,
  workerFleetRowKey,
} from './api'

type FleetFilter = 'all' | 'gen' | 'busy' | 'stale'

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

function WorkerDetail({ w }: { w: WorkerRow }) {
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
        <ModelList title="Checkpoints" items={w.models.checkpoints} />
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

export function OpsFleetTab({ workers }: { workers: WorkerRow[] }) {
  const [filter, setFilter] = useState<FleetFilter>('all')
  const [expanded, setExpanded] = useState<string | null>(null)
  const [search, setSearch] = useState('')

  const visible = useMemo(() => {
    let rows = workers
    if (filter === 'gen') rows = rows.filter(isGenFleetWorker)
    else if (filter === 'busy') rows = rows.filter((w) => w.state === 'busy')
    else if (filter === 'stale') rows = rows.filter((w) => w.checkInStale)
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

  return (
    <div className="card">
      <div className="card-head">
        <div>
          <h2>Connected workers</h2>
          <p className="muted card-sub">
            {workers.length} total · {genCount} gen fleet · click a row for models, LoRAs, and job stats
          </p>
        </div>
        <div className="row">
          <input
            className="search-input"
            placeholder="Search hostname, GPU, capability…"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          <select value={filter} onChange={(e) => setFilter(e.target.value as FleetFilter)}>
            <option value="all">All workers ({workers.length})</option>
            <option value="gen">Gen fleet only ({genCount})</option>
            <option value="busy">Busy only</option>
            <option value="stale">Stale only</option>
          </select>
        </div>
      </div>
      {visible.length === 0 ? (
        <p className="muted">No workers match this filter.</p>
      ) : (
        <div className="table-wrap">
          <table className="fleet-table">
            <thead>
              <tr>
                <th></th>
                <th>Hostname</th>
                <th>GPU</th>
                <th>VRAM</th>
                <th>Disk</th>
                <th>State</th>
                <th>Jobs</th>
                <th>Capabilities</th>
                <th>LoRAs</th>
                <th>Mode</th>
                <th>EF queue</th>
                <th>Last seen</th>
              </tr>
            </thead>
            <tbody>
              {visible.map((w) => {
                const key = workerFleetRowKey(w)
                const open = expanded === key
                return (
                  <Fragment key={key}>
                    <tr
                      className={'fleet-row' + (open ? ' expanded' : '')}
                      onClick={() => setExpanded(open ? null : key)}
                    >
                      <td className="chevron">{open ? '▼' : '▶'}</td>
                      <td><strong>{w.hostname}</strong></td>
                      <td className="muted">{w.gpuName || '—'}</td>
                      <td>{formatVram(w.vramFreeMb)}{w.vramTotalMb > 0 ? ` / ${formatVram(w.vramTotalMb)}` : ''}</td>
                      <td>{formatDiskGb(w.diskFreeMb)}</td>
                      <td>
                        <span className={'badge ' + (w.checkInStale ? 'stale' : w.state === 'busy' ? 'busy' : 'idle')}>
                          {w.checkInStale ? 'stale' : w.state}
                        </span>
                      </td>
                      <td>
                        <span title="claimed / completed / failed">{w.jobsClaimed} / {w.jobsCompleted} / {w.jobsFailed}</span>
                      </td>
                      <td className="cap-cell">
                        <TagList items={w.capabilities.slice(0, 3)} />
                        {w.capabilities.length > 3 && <span className="muted">+{w.capabilities.length - 3}</span>}
                      </td>
                      <td>{w.knownLoras.length || w.models.loras.length}</td>
                      <td className="muted">{w.fleetMode || '—'}</td>
                      <td>
                        {w.queueAccessOk === true ? 'OK'
                          : w.queueAccessOk === false ? <span className="error">{w.queueAccessError ?? 'fail'}</span>
                            : '—'}
                      </td>
                      <td className="muted">{w.lastSeenAt ? new Date(w.lastSeenAt).toLocaleTimeString() : '—'}</td>
                    </tr>
                    {open && (
                      <tr className="fleet-detail-row">
                        <td colSpan={12}><WorkerDetail w={w} /></td>
                      </tr>
                    )}
                  </Fragment>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
