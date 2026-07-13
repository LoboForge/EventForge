import { useCallback, useEffect, useMemo, useState } from 'react'
import { opsFetch, opsFetchMaybe } from './api'
import { SortableTable, type SortableColumn } from './SortableTable'
import {
  formatReliability,
  formatUsd,
  formatVramGb,
  normalizeVastAccount,
  normalizeVastInstances,
  normalizeVastOffers,
  VAST_MODES,
  type VastAccount,
  type VastInstance,
  type VastModeId,
  type VastOffer,
} from './vast'

type VastStatus = {
  configured: boolean
  event_forge_url?: string
}

type SearchQuery = {
  minGpuRamGb: number
  maxDollarsPerHr: number
  minReliability: number
  verifiedOnly: boolean
  gpuNameContains: string
  sortBy: string
  limit: number
}

const DEFAULT_SEARCH: SearchQuery = {
  minGpuRamGb: 16,
  maxDollarsPerHr: 0.75,
  minReliability: 0.95,
  verifiedOnly: true,
  gpuNameContains: '',
  sortBy: 'bang',
  limit: 30,
}

export function OpsVastTab() {
  const [mode, setMode] = useState<VastModeId>('image')
  const [loosen, setLoosen] = useState(false)
  const [offer, setOffer] = useState<VastOffer | null>(null)
  const [searchResults, setSearchResults] = useState<VastOffer[]>([])
  const [instances, setInstances] = useState<VastInstance[]>([])
  const [account, setAccount] = useState<VastAccount | null>(null)
  const [status, setStatus] = useState<VastStatus | null>(null)
  const [search, setSearch] = useState<SearchQuery>(DEFAULT_SEARCH)
  const [provisionCmd, setProvisionCmd] = useState<string | null>(null)
  const [selectedInstance, setSelectedInstance] = useState<number | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const [msg, setMsg] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    setErr(null)
    try {
      const st = await opsFetch<VastStatus>('/v1/ops/vast/status')
      setStatus(st)
      if (!st.configured) {
        setOffer(null)
        setInstances([])
        setAccount(null)
        setSearchResults([])
        return
      }
      const modeCfg = VAST_MODES.find((m) => m.id === mode) ?? VAST_MODES[0]
      const [rec, inst, acct] = await Promise.all([
        opsFetch<{ mode: string; offer?: Record<string, unknown> | null }>(
          `/v1/ops/vast/recommend?mode=${encodeURIComponent(mode)}&loosen=${loosen}`,
        ),
        opsFetch<unknown[]>('/v1/ops/vast/instances/live'),
        opsFetchMaybe<Record<string, unknown>>('/v1/ops/vast/account'),
      ])
      setOffer(rec.offer ? normalizeVastOffers([rec.offer])[0] : null)
      setInstances(normalizeVastInstances(inst))
      setAccount(acct ? normalizeVastAccount(acct) : null)
      setSearch((s) => ({ ...s, minGpuRamGb: mode === 'video' || mode === 'all' || mode === 'ltx-native' || mode === 'wan-native' ? 24 : 16 }))
      void modeCfg
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    }
  }, [mode, loosen])

  useEffect(() => { void load() }, [load])

  async function runSearch() {
    setBusy(true)
    setErr(null)
    try {
      const rows = await opsFetch<unknown[]>('/v1/ops/vast/search', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ...search, mode }),
      })
      setSearchResults(normalizeVastOffers(rows))
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setBusy(false)
    }
  }

  async function rent(offerId: number, rentMode = mode) {
    const modeCfg = VAST_MODES.find((m) => m.id === rentMode) ?? VAST_MODES[0]
    setBusy(true)
    setErr(null)
    setMsg(null)
    try {
      const res = await opsFetch<{ ok: boolean; instanceId?: number; error?: string }>('/v1/ops/vast/rent', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ offerId, mode: rentMode, diskGb: modeCfg.disk }),
      })
      setMsg(res.instanceId ? `Rented instance #${res.instanceId}` : 'Rent submitted')
      await load()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setBusy(false)
    }
  }

  async function instanceAction(path: string, label: string) {
    setBusy(true)
    setErr(null)
    try {
      await opsFetch(path, { method: 'POST' })
      setMsg(label)
      await load()
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    } finally {
      setBusy(false)
    }
  }

  async function loadProvisionCommand(instanceId: number) {
    setSelectedInstance(instanceId)
    setProvisionCmd(null)
    try {
      const res = await opsFetch<{ command?: string }>(
        `/v1/ops/vast/provision-command?instanceId=${instanceId}&mode=${encodeURIComponent(mode)}`,
      )
      setProvisionCmd(res.command ?? null)
    } catch (ex) {
      setErr(ex instanceof Error ? ex.message : String(ex))
    }
  }

  const offerColumns = useMemo((): SortableColumn<VastOffer>[] => [
    {
      id: 'gpu',
      header: 'GPU',
      sortValue: (o) => o.gpuName,
      render: (o) => <span className="vast-gpu">{o.gpuName}</span>,
    },
    {
      id: 'vram',
      header: 'VRAM',
      sortValue: (o) => o.gpuRamMb,
      render: (o) => formatVramGb(o.gpuRamMb),
    },
    {
      id: 'price',
      header: '$/hr',
      sortValue: (o) => o.dphTotal,
      render: (o) => <span className="vast-price">${formatUsd(o.dphTotal)}</span>,
      className: 'num-cell',
    },
    {
      id: 'rel',
      header: 'Rel',
      sortValue: (o) => o.reliability,
      render: (o) => formatReliability(o.reliability),
    },
    {
      id: 'location',
      header: 'Location',
      sortValue: (o) => o.geolocation,
      render: (o) => <span className="muted">{o.geolocation}</span>,
    },
    {
      id: 'actions',
      header: '',
      sortable: false,
      render: (o) => <button className="btn" disabled={busy} onClick={() => void rent(o.id)}>Rent</button>,
      className: 'actions-cell',
    },
  ], [busy, rent])

  const instanceColumns = useMemo((): SortableColumn<VastInstance>[] => [
    { id: 'id', header: 'ID', sortValue: (i) => i.id, render: (i) => i.id, className: 'num-cell' },
    {
      id: 'status',
      header: 'Status',
      sortValue: (i) => i.actualStatus,
      render: (i) => <span className="badge idle">{i.actualStatus}</span>,
    },
    { id: 'gpu', header: 'GPU', sortValue: (i) => i.gpuName, render: (i) => i.gpuName },
    {
      id: 'price',
      header: '$/hr',
      sortValue: (i) => i.dphTotal,
      render: (i) => `$${formatUsd(i.dphTotal)}`,
      className: 'num-cell',
    },
    { id: 'label', header: 'Label', sortValue: (i) => i.label, render: (i) => i.label || '—' },
    { id: 'ip', header: 'IP', sortValue: (i) => i.publicIp, render: (i) => i.publicIp || '—' },
    {
      id: 'ssh',
      header: 'SSH',
      sortValue: (i) => i.sshHost ?? '',
      render: (i) => <span className="muted">{i.sshHost ? `${i.sshHost}:${i.sshPort}` : '—'}</span>,
    },
    {
      id: 'actions',
      header: '',
      sortable: false,
      render: (i) => (
        <div className="row actions-cell">
          <button className="btn secondary" disabled={busy}
            onClick={() => void loadProvisionCommand(i.id)}>Provision cmd</button>
          <button className="btn secondary" disabled={busy}
            onClick={() => void instanceAction(`/v1/ops/vast/stop/${i.id}`, `Stopped #${i.id}`)}>Stop</button>
          <button className="btn secondary" disabled={busy}
            onClick={() => void instanceAction(`/v1/ops/vast/start/${i.id}`, `Started #${i.id}`)}>Start</button>
          <button className="btn warn" disabled={busy}
            onClick={() => void instanceAction(`/v1/ops/vast/terminate/${i.id}`, `Terminated #${i.id}`)}>Terminate</button>
        </div>
      ),
      className: 'actions-cell',
    },
  ], [busy, instanceAction, loadProvisionCommand])

  if (!status?.configured) {
    return (
      <div className="vast-page">
        {err && <div className="error">{err}</div>}
        <div className="card vast-warn">
          <h2>Vast.ai not configured</h2>
          <p className="muted">
            Set <code>VastAi:ApiKey</code> in <code>APP_SECRETS_JSON</code> (root or <code>EventForge:VastAi:ApiKey</code>).
            EventForge reads the same secret as LoboForge — no separate Vast key needed if LoboForge already had one.
          </p>
          <button className="btn secondary" onClick={() => void load()}>Retry</button>
        </div>
      </div>
    )
  }

  return (
    <div className="vast-page">
      {err && <div className="error">{err}</div>}
      {msg && <div className="success">{msg}</div>}

      <div className="card row vast-status-bar">
        <span className="badge idle">Vast.ai configured</span>
        {account && (
          <span className="muted">
            Credit <strong className="ok-text">${formatUsd(account.credit, 2)}</strong>
            {account.email ? ` · ${account.email}` : ''}
          </span>
        )}
        {status.event_forge_url && <span className="muted">Bootstrap URL: <code>{status.event_forge_url}</code></span>}
        <button className="btn secondary" onClick={() => void load()} disabled={busy}>Refresh</button>
      </div>

      <div className="card vast-recommend">
        <h2>Provision new machine</h2>
        <p className="muted card-sub">Pick a mode, rent the recommended offer, or search and rent from results below.</p>
        <div className="vast-mode-pick">
          {VAST_MODES.map((m) => (
            <button
              key={m.id}
              type="button"
              className={'vast-mode-btn' + (mode === m.id ? ' selected' : '')}
              onClick={() => setMode(m.id)}
              disabled={busy}
            >
              <span className="rec-name">{m.label}</span>
              <span className="rec-sub">{m.sub}</span>
            </button>
          ))}
          <label className="row muted" style={{ marginLeft: 'auto' }}>
            <input type="checkbox" checked={loosen} onChange={(e) => setLoosen(e.target.checked)} />
            Loosen filters (cheaper / lower reliability)
          </label>
        </div>
        {offer ? (
          <div className="vast-recommend-card">
            <div className="vast-recommend-gpu">{offer.gpuName} ×{offer.numGpus}</div>
            <div className="vast-recommend-stats row">
              <span><strong>${formatUsd(offer.dphTotal)}</strong>/hr</span>
              <span>{formatVramGb(offer.gpuRamMb)} VRAM</span>
              <span>{formatReliability(offer.reliability)} reliable</span>
              <span>{offer.geolocation || '—'}</span>
              <span>Offer #{offer.id}</span>
            </div>
            <div className="row">
              <button className="btn" onClick={() => void rent(offer.id)} disabled={busy}>Rent recommended</button>
              {offer.offerUrl && (
                <a className="btn secondary" href={offer.offerUrl} target="_blank" rel="noopener noreferrer">Open on Vast.ai</a>
              )}
            </div>
          </div>
        ) : (
          <p className="muted">No offer found for mode <code>{mode}</code>. Try loosen filters or search below.</p>
        )}
      </div>

      <div className="card">
        <h2>Search offers</h2>
        <div className="vast-search row">
          <label className="vast-field">Min VRAM (GB)
            <input type="number" min={8} max={96} value={search.minGpuRamGb}
              onChange={(e) => setSearch((s) => ({ ...s, minGpuRamGb: parseInt(e.target.value, 10) || 16 }))} />
          </label>
          <label className="vast-field">Max $/hr
            <input type="number" step="0.01" min={0.05} max={5} value={search.maxDollarsPerHr}
              onChange={(e) => setSearch((s) => ({ ...s, maxDollarsPerHr: parseFloat(e.target.value) || 0.75 }))} />
          </label>
          <label className="vast-field">Min reliability
            <input type="number" step="0.01" min={0.5} max={1} value={search.minReliability}
              onChange={(e) => setSearch((s) => ({ ...s, minReliability: parseFloat(e.target.value) || 0.95 }))} />
          </label>
          <label className="vast-field">GPU contains
            <input type="text" placeholder="3090, A100…" value={search.gpuNameContains}
              onChange={(e) => setSearch((s) => ({ ...s, gpuNameContains: e.target.value }))} />
          </label>
          <label className="vast-field">Sort
            <select value={search.sortBy} onChange={(e) => setSearch((s) => ({ ...s, sortBy: e.target.value }))}>
              <option value="bang">Best bang/buck</option>
              <option value="cheap">Cheapest</option>
              <option value="fast">Fastest</option>
            </select>
          </label>
          <label className="vast-field vast-field-toggle">
            <input type="checkbox" checked={search.verifiedOnly}
              onChange={(e) => setSearch((s) => ({ ...s, verifiedOnly: e.target.checked }))} />
            Verified only
          </label>
          <button className="btn" onClick={() => void runSearch()} disabled={busy}>Search</button>
        </div>
        {searchResults.length > 0 && (
          <SortableTable
            className="vast-table"
            rows={searchResults}
            rowKey={(o) => String(o.id)}
            columns={offerColumns}
            defaultSort={{ id: 'price', dir: 'asc' }}
          />
        )}
      </div>

      <div className="card">
        <h2>Live Vast instances ({instances.length})</h2>
        {instances.length === 0 ? (
          <p className="muted">No running instances on your Vast account.</p>
        ) : (
          <SortableTable
            rows={instances}
            rowKey={(i) => String(i.id)}
            columns={instanceColumns}
            defaultSort={{ id: 'id', dir: 'desc' }}
          />
        )}
        {selectedInstance != null && provisionCmd && (
          <div className="provision-cmd">
            <h3>Manual provision — instance #{selectedInstance} · mode {mode}</h3>
            <pre>{provisionCmd}</pre>
          </div>
        )}
      </div>
    </div>
  )
}
