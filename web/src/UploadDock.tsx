import { useCallback, useEffect, useRef, useState } from 'react'

type UploadStatus = 'queued' | 'uploading' | 'completing' | 'ready' | 'failed'
type DockTab = 'queue' | 'uploads' | 'logs'

type UploadItem = {
  id: string
  fileName: string
  size: number
  status: UploadStatus
  progress: number
  error?: string
  assetId?: string
  createdAt: string
}

type UploadLog = {
  id: string
  at: string
  message: string
  tone: 'info' | 'success' | 'error'
}

type BeginUpload = {
  asset_id: string
  upload: { method: string; url: string; headers?: Record<string, string> }
}

const QUEUE_STORAGE = 'eventforge_lora_upload_queue_v1'
const LOG_STORAGE = 'eventforge_lora_upload_logs_v1'
const APP_KEY_STORAGE = 'eventforge_lora_upload_app_key'

function readStored<T>(key: string, fallback: T): T {
  try {
    const value = localStorage.getItem(key)
    return value ? JSON.parse(value) as T : fallback
  } catch {
    return fallback
  }
}

function formatBytes(bytes: number) {
  if (!bytes) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB']
  const power = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1)
  return `${(bytes / 1024 ** power).toFixed(power ? 1 : 0)} ${units[power]}`
}

function labelFor(status: UploadStatus) {
  return status === 'ready' ? 'Ready' : status === 'completing' ? 'Finalizing' : status[0].toUpperCase() + status.slice(1)
}

function newId() {
  return crypto.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`
}

export function UploadDock() {
  const [queue, setQueueState] = useState<UploadItem[]>(() => readStored<UploadItem[]>(QUEUE_STORAGE, []).map((item) => ({
    ...item,
    status: item.status === 'uploading' || item.status === 'completing' ? 'queued' : item.status,
    progress: item.status === 'ready' ? 100 : 0,
    error: item.status === 'ready' ? item.error : 'Re-attach this file to resume after reload.',
  })))
  const [logs, setLogsState] = useState<UploadLog[]>(() => readStored<UploadLog[]>(LOG_STORAGE, []))
  const [appKey, setAppKey] = useState(() => sessionStorage.getItem(APP_KEY_STORAGE) ?? '')
  const [expanded, setExpanded] = useState(false)
  const [tab, setTab] = useState<DockTab>('queue')
  const files = useRef(new Map<string, File>())
  const queueRef = useRef(queue)
  const pumping = useRef(false)
  const picker = useRef<HTMLInputElement>(null)

  const setQueue = useCallback((next: UploadItem[] | ((items: UploadItem[]) => UploadItem[])) => {
    setQueueState((current) => {
      const value = typeof next === 'function' ? next(current) : next
      queueRef.current = value
      localStorage.setItem(QUEUE_STORAGE, JSON.stringify(value))
      return value
    })
  }, [])

  const appendLog = useCallback((message: string, tone: UploadLog['tone'] = 'info') => {
    setLogsState((current) => {
      const value = [{ id: newId(), at: new Date().toISOString(), message, tone }, ...current].slice(0, 100)
      localStorage.setItem(LOG_STORAGE, JSON.stringify(value))
      return value
    })
  }, [])

  const updateItem = useCallback((id: string, patch: Partial<UploadItem>) => {
    setQueue((items) => items.map((item) => item.id === id ? { ...item, ...patch } : item))
  }, [setQueue])

  const uploadContent = useCallback((url: string, headers: Record<string, string> | undefined, file: File, onProgress: (progress: number) => void) => new Promise<void>((resolve, reject) => {
    const request = new XMLHttpRequest()
    request.open('PUT', url)
    Object.entries(headers ?? {}).forEach(([key, value]) => request.setRequestHeader(key, value))
    const target = new URL(url, location.href)
    if (target.origin === location.origin && appKey.trim()) request.setRequestHeader('Authorization', `Bearer ${appKey.trim()}`)
    request.setRequestHeader('Content-Type', file.type || 'application/octet-stream')
    request.upload.onprogress = (event) => {
      if (event.lengthComputable) onProgress(Math.round((event.loaded / event.total) * 100))
    }
    request.onerror = () => reject(new Error('Network error while uploading file'))
    request.onabort = () => reject(new Error('Upload aborted'))
    request.onload = () => request.status >= 200 && request.status < 300
      ? resolve()
      : reject(new Error(`Upload failed (${request.status} ${request.statusText})`))
    request.send(file)
  }), [appKey])

  const pumpQueue = useCallback(async () => {
    if (pumping.current || !appKey.trim()) return
    pumping.current = true
    try {
      while (true) {
        const item = queueRef.current.find((candidate) => candidate.status === 'queued' && files.current.has(candidate.id))
        if (!item) break
        const file = files.current.get(item.id)!
        updateItem(item.id, { status: 'uploading', progress: 0, error: undefined })
        appendLog(`Started ${item.fileName}`)
        try {
          const begin = await fetch('/v1/assets/loras', {
            method: 'POST',
            headers: { Authorization: `Bearer ${appKey.trim()}`, 'Content-Type': 'application/json', Accept: 'application/json' },
            body: JSON.stringify({ file_name: file.name, bytes: file.size, replace: true }),
          })
          if (!begin.ok) throw new Error((await begin.text()) || `Could not start upload (${begin.status})`)
          const started = await begin.json() as BeginUpload
          await uploadContent(started.upload.url, started.upload.headers, file, (progress) => updateItem(item.id, { progress }))
          updateItem(item.id, { status: 'completing', progress: 100, assetId: started.asset_id })
          const complete = await fetch(`/v1/assets/loras/${encodeURIComponent(started.asset_id)}/complete`, {
            method: 'POST',
            headers: { Authorization: `Bearer ${appKey.trim()}`, 'Content-Type': 'application/json', Accept: 'application/json' },
            body: JSON.stringify({ bytes: file.size }),
          })
          if (!complete.ok) throw new Error((await complete.text()) || `Could not finalize upload (${complete.status})`)
          updateItem(item.id, { status: 'ready', progress: 100 })
          appendLog(`Ready: ${item.fileName}`, 'success')
        } catch (error) {
          const message = error instanceof Error ? error.message : String(error)
          updateItem(item.id, { status: 'failed', error: message })
          appendLog(`Failed: ${item.fileName} — ${message}`, 'error')
        }
      }
    } finally {
      pumping.current = false
    }
  }, [appKey, appendLog, updateItem, uploadContent])

  useEffect(() => { void pumpQueue() }, [queue, pumpQueue])

  const selectFiles = (selected: FileList | null) => {
    if (!selected?.length) return
    const remaining = [...queueRef.current]
    for (const file of Array.from(selected)) {
      const reattach = remaining.find((item) => !files.current.has(item.id) && item.fileName === file.name && item.size === file.size && item.status !== 'ready')
      if (reattach) {
        files.current.set(reattach.id, file)
        const index = remaining.findIndex((item) => item.id === reattach.id)
        remaining[index] = { ...reattach, status: 'queued', progress: 0, error: undefined }
        appendLog(`Re-attached ${file.name}`)
      } else {
        const id = newId()
        files.current.set(id, file)
        remaining.push({ id, fileName: file.name, size: file.size, status: 'queued', progress: 0, createdAt: new Date().toISOString() })
        appendLog(`Queued ${file.name}`)
      }
    }
    setQueue(remaining)
    if (picker.current) picker.current.value = ''
  }

  const retryFailed = () => setQueue((items) => items.map((item) => item.status === 'failed' && files.current.has(item.id)
    ? { ...item, status: 'queued', progress: 0, error: undefined }
    : item))
  const clearCompleted = () => setQueue((items) => {
    for (const item of items) if (item.status === 'ready') files.current.delete(item.id)
    return items.filter((item) => item.status !== 'ready')
  })
  const setKey = (value: string) => {
    setAppKey(value)
    sessionStorage.setItem(APP_KEY_STORAGE, value.trim())
  }

  const active = queue.filter((item) => item.status === 'uploading' || item.status === 'completing')
  const queued = queue.filter((item) => item.status === 'queued')
  const ready = queue.filter((item) => item.status === 'ready')
  const failed = queue.filter((item) => item.status === 'failed')
  const averageProgress = active.length ? Math.round(active.reduce((sum, item) => sum + item.progress, 0) / active.length) : 0
  const summary = active.length ? `${ready.length + 1}/${queue.length} uploading — ${averageProgress}%` : queued.length ? `${queued.length} upload${queued.length === 1 ? '' : 's'} queued` : failed.length ? `${failed.length} upload${failed.length === 1 ? '' : 's'} need attention` : queue.length ? `${ready.length}/${queue.length} uploads ready` : 'LoRA uploads'
  const visible = tab === 'queue' ? queue : tab === 'uploads' ? active : []

  return <aside className={'upload-dock' + (expanded ? ' expanded' : '')} aria-label="LoRA upload queue">
    <button className="upload-dock-bar" onClick={() => setExpanded((value) => !value)} aria-expanded={expanded}>
      <span className="upload-dock-title"><span className="upload-dock-dot" />{summary}</span>
      <span className="muted">{expanded ? 'Collapse' : 'Open'} ▴</span>
    </button>
    {expanded && <div className="upload-dock-body">
      <div className="upload-dock-actions">
        <input ref={picker} className="visually-hidden" id="lora-file-picker" type="file" accept=".safetensors" multiple onChange={(event) => selectFiles(event.target.files)} />
        <label className="btn" htmlFor="lora-file-picker">Add LoRAs</label>
        <button className="btn secondary" onClick={retryFailed} disabled={!failed.some((item) => files.current.has(item.id))}>Retry failed</button>
        <button className="btn secondary" onClick={clearCompleted} disabled={!ready.length}>Clear completed</button>
      </div>
      <div className="upload-key-row">
        <label htmlFor="lora-app-key">Consumer app API key</label>
        <input id="lora-app-key" type="password" value={appKey} onChange={(event) => setKey(event.target.value)} placeholder="Bearer key used to create jobs" autoComplete="off" />
        <span className="muted small">Held only for this browser session. Required by the LoRA asset API.</span>
      </div>
      <nav className="tabs upload-dock-tabs">
        {(['queue', 'uploads', 'logs'] as DockTab[]).map((entry) => <button key={entry} className={'tab' + (tab === entry ? ' active' : '')} onClick={() => setTab(entry)}>{entry === 'queue' ? `Queue (${queue.length})` : entry === 'uploads' ? `Uploads (${active.length})` : `Logs (${logs.length})`}</button>)}
      </nav>
      {!appKey.trim() && <div className="error">Add the consumer app API key before uploads can start. The ops key cannot upload app-scoped LoRAs.</div>}
      {tab === 'logs' ? <div className="upload-log-list">{logs.length ? logs.map((log) => <div className={'upload-log ' + log.tone} key={log.id}><time>{new Date(log.at).toLocaleTimeString()}</time>{log.message}</div>) : <p className="muted">Upload events will appear here.</p>}</div> : <div className="upload-list">{visible.length ? visible.map((item) => <div className="upload-item" key={item.id}><div className="upload-item-head"><strong>{item.fileName}</strong><span className={'badge upload-status ' + item.status}>{labelFor(item.status)}</span></div><div className="upload-item-meta muted">{formatBytes(item.size)} · {item.status === 'queued' && !files.current.has(item.id) ? 'Re-attach file to continue' : `${item.progress}%`}</div><div className="upload-progress"><span style={{ width: `${item.progress}%` }} /></div>{item.error && <p className="upload-item-error">{item.error}</p>}</div>) : <p className="muted">{tab === 'uploads' ? 'No active uploads.' : 'Select LoRA files to build a durable upload queue.'}</p>}</div>}
    </div>}
  </aside>
}
