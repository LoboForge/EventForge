import { useCallback, useEffect, useState } from 'react'
import { opsFetch } from './api'
import { formatDateTime } from './format'

type CapacityRequest = {
  request_id: string
  email: string
  company?: string | null
  name?: string | null
  models: string[]
  estimated_jobs: number
  notes?: string | null
  preferred_payment: string
  status: string
  payment_method?: string | null
  payment_instructions?: Record<string, unknown> | null
  rejection_reason?: string | null
  created_at: string
}

type InstructionsResponse = {
  payment_instructions: Record<string, unknown>
}

function copyInstructions(value: Record<string, unknown> | null | undefined) {
  if (!value) return
  void navigator.clipboard.writeText(
    Object.entries(value)
      .filter(([, v]) => v !== null && v !== '')
      .map(([k, v]) => `${k.replaceAll('_', ' ')}: ${String(v)}`)
      .join('\n'),
  )
}

export function OpsCapacityTab() {
  const [rows, setRows] = useState<CapacityRequest[]>([])
  const [status, setStatus] = useState('')
  const [busy, setBusy] = useState('')
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')

  const load = useCallback(async () => {
    setError('')
    try {
      const query = status ? `?status=${encodeURIComponent(status)}` : ''
      const result = await opsFetch<{ requests: CapacityRequest[] }>(`/v1/ops/capacity-requests${query}`)
      setRows(result.requests ?? [])
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }, [status])

  useEffect(() => { void load() }, [load])

  async function instructions(row: CapacityRequest, method: 'paypal' | 'wire' | 'monero') {
    const amountRaw = prompt('Optional USD amount hint (required for automatic PayPal invoice):', '')
    if (amountRaw === null) return
    const invoiceUrl = method === 'paypal'
      ? prompt('Paste an existing PayPal invoice URL, or leave blank to create one when credentials and amount are configured:', '')
      : undefined
    if (method === 'paypal' && invoiceUrl === null) return
    setBusy(`${row.request_id}:instructions`); setError(''); setMessage('')
    try {
      const amount = amountRaw.trim() ? Number(amountRaw) : undefined
      const result = await opsFetch<InstructionsResponse>(
        `/v1/ops/capacity-requests/${row.request_id}/payment-instructions`,
        {
          method: 'POST',
          body: JSON.stringify({
            method,
            amount_hint_usd: amount,
            invoice_url: invoiceUrl?.trim() || undefined,
          }),
        },
      )
      copyInstructions(result.payment_instructions)
      setMessage(`${method} instructions saved and copied for ${row.email}.`)
      await load()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy('')
    }
  }

  async function approve(row: CapacityRequest) {
    const raw = prompt('Credits to grant (0 is allowed for capacity-only activation):', '0')
    if (raw === null) return
    const credits = Number(raw)
    if (!Number.isSafeInteger(credits) || credits < 0) {
      setError('Credits must be a non-negative whole number.')
      return
    }
    if (!confirm(`Mark ${row.email} paid and activate an API key?`)) return
    setBusy(`${row.request_id}:approve`); setError(''); setMessage('')
    try {
      const result = await opsFetch<{ api_key: string; account_created: boolean }>(
        `/v1/ops/capacity-requests/${row.request_id}/approve`,
        { method: 'POST', body: JSON.stringify({ credits, api_key_email: false }) },
      )
      await navigator.clipboard.writeText(result.api_key)
      setMessage(`Approved ${row.email}. API key copied; send it manually through a secure channel.`)
      await load()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy('')
    }
  }

  async function reject(row: CapacityRequest) {
    const reason = prompt(`Reason for rejecting ${row.email}:`, '')
    if (!reason?.trim()) return
    setBusy(`${row.request_id}:reject`); setError(''); setMessage('')
    try {
      await opsFetch(`/v1/ops/capacity-requests/${row.request_id}/reject`, {
        method: 'POST',
        body: JSON.stringify({ reason: reason.trim() }),
      })
      setMessage(`Rejected ${row.email}.`)
      await load()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy('')
    }
  }

  return (
    <div className="card">
      <div className="card-head">
        <div>
          <h2>Capacity requests</h2>
          <p className="muted card-sub">Prepare offline payment instructions, then activate access only after funds clear.</p>
        </div>
        <div className="row">
          <select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">All statuses</option>
            <option value="received">Received</option>
            <option value="payment_pending">Payment pending</option>
            <option value="approved">Approved</option>
            <option value="rejected">Rejected</option>
          </select>
          <button className="btn secondary small" onClick={() => void load()}>Reload</button>
        </div>
      </div>
      {message && <div className="success">{message}</div>}
      {error && <div className="error">{error}</div>}
      <div className="table-wrap">
        <table>
          <thead><tr><th>Requester</th><th>Models</th><th>Jobs</th><th>Payment</th><th>Status</th><th>Created</th><th>Actions</th></tr></thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.request_id}>
                <td><strong>{row.email}</strong><div className="muted small">{row.name || row.company || row.request_id.slice(0, 10)}</div></td>
                <td><span title={row.models.join(', ')}>{row.models.join(', ')}</span>{row.notes && <div className="muted small" title={row.notes}>{row.notes.slice(0, 80)}</div>}</td>
                <td className="num-cell">{row.estimated_jobs.toLocaleString()}</td>
                <td>{row.payment_method || row.preferred_payment}{row.payment_instructions && <button className="btn secondary small" onClick={() => copyInstructions(row.payment_instructions)}>Copy</button>}</td>
                <td><span className="badge idle">{row.status}</span>{row.rejection_reason && <div className="muted small">{row.rejection_reason}</div>}</td>
                <td className="muted">{formatDateTime(row.created_at)}</td>
                <td className="actions-cell">
                  {row.status !== 'approved' && row.status !== 'rejected' && <>
                    <button className="btn secondary small" disabled={Boolean(busy)} onClick={() => void instructions(row, 'paypal')}>PayPal</button>
                    <button className="btn secondary small" disabled={Boolean(busy)} onClick={() => void instructions(row, 'wire')}>Wire</button>
                    <button className="btn secondary small" disabled={Boolean(busy)} onClick={() => void instructions(row, 'monero')}>XMR</button>
                    <button className="btn small" disabled={Boolean(busy)} onClick={() => void approve(row)}>Approve</button>
                    <button className="btn warn small" disabled={Boolean(busy)} onClick={() => void reject(row)}>Reject</button>
                  </>}
                </td>
              </tr>
            ))}
            {rows.length === 0 && <tr><td colSpan={7} className="muted">No capacity requests match this filter.</td></tr>}
          </tbody>
        </table>
      </div>
    </div>
  )
}
