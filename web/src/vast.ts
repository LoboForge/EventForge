export type VastAccount = {
  credit: number
  email: string
  balance: number
}

export type VastOffer = {
  id: number
  machineId: number
  gpuName: string
  numGpus: number
  gpuRamMb: number
  dphTotal: number
  reliability: number
  dlPerf: number
  dlPerfPerDollar: number
  verified: boolean
  cpuCores: number
  cpuRamMb: number
  diskSpace: number
  inetUpMbps?: number
  inetDownMbps?: number
  geolocation: string
  offerUrl: string
}

export type VastInstance = {
  id: number
  machineId: number
  actualStatus: string
  intendedStatus: string
  gpuName: string
  dphTotal: number
  publicIp: string
  sshHost: string
  sshPort: number
  label: string
}

function toNumber(value: unknown): number {
  if (typeof value === 'number') return value
  if (typeof value === 'string') return parseFloat(value)
  return NaN
}

export function formatUsd(value: unknown, digits = 3): string {
  const n = toNumber(value)
  return Number.isFinite(n) ? n.toFixed(digits) : '—'
}

export function formatReliability(value: unknown): string {
  const n = toNumber(value)
  return Number.isFinite(n) ? `${(n * 100).toFixed(0)}%` : '—'
}

export function formatVramGb(gpuRamMb: unknown): string {
  const mb = toNumber(gpuRamMb)
  return Number.isFinite(mb) ? `${(mb / 1024).toFixed(0)} GB` : '—'
}

export function normalizeVastAccount(raw: Record<string, unknown>): VastAccount {
  return {
    credit: toNumber(raw.credit ?? raw.Credit) || 0,
    email: String(raw.email ?? raw.Email ?? ''),
    balance: toNumber(raw.balance ?? raw.Balance) || 0,
  }
}

export function normalizeVastInstance(raw: Record<string, unknown>): VastInstance {
  return {
    id: toNumber(raw.id ?? raw.Id) || 0,
    machineId: toNumber(raw.machineId ?? raw.machine_id) || 0,
    actualStatus: String(raw.actualStatus ?? raw.actual_status ?? ''),
    intendedStatus: String(raw.intendedStatus ?? raw.intended_status ?? ''),
    gpuName: String(raw.gpuName ?? raw.gpu_name ?? 'GPU'),
    dphTotal: toNumber(raw.dphTotal ?? raw.dph_total) || 0,
    publicIp: String(raw.publicIp ?? raw.public_ipaddr ?? raw.public_ip ?? ''),
    sshHost: String(raw.sshHost ?? raw.ssh_host ?? ''),
    sshPort: toNumber(raw.sshPort ?? raw.ssh_port) || 0,
    label: String(raw.label ?? raw.Label ?? ''),
  }
}

export function normalizeVastOffer(raw: Record<string, unknown>): VastOffer {
  const dph = toNumber(raw.dphTotal ?? raw.dph_total) || 0
  const dl = toNumber(raw.dlPerf ?? raw.dlperf) || 0
  const perfPerDollar = toNumber(raw.dlPerfPerDollar ?? raw.dl_perf_per_dollar)
  return {
    id: toNumber(raw.id ?? raw.Id) || 0,
    machineId: toNumber(raw.machineId ?? raw.machine_id) || 0,
    gpuName: String(raw.gpuName ?? raw.gpu_name ?? 'GPU'),
    numGpus: toNumber(raw.numGpus ?? raw.num_gpus) || 1,
    gpuRamMb: toNumber(raw.gpuRamMb ?? raw.gpu_ram) || 0,
    dphTotal: dph,
    reliability: toNumber(raw.reliability ?? raw.reliability2) || 0,
    dlPerf: dl,
    dlPerfPerDollar: Number.isFinite(perfPerDollar) ? perfPerDollar : (dph > 0 ? dl / dph : 0),
    verified: Boolean(raw.verified ?? raw.Verified),
    cpuCores: toNumber(raw.cpuCores ?? raw.cpu_cores) || 0,
    cpuRamMb: toNumber(raw.cpuRamMb ?? raw.cpu_ram) || 0,
    diskSpace: toNumber(raw.diskSpace ?? raw.disk_space) || 0,
    inetUpMbps: toNumber(raw.inetUpMbps ?? raw.inet_up) || undefined,
    inetDownMbps: toNumber(raw.inetDownMbps ?? raw.inet_down) || undefined,
    geolocation: String(raw.geolocation ?? raw.Geolocation ?? ''),
    offerUrl: String(raw.offerUrl ?? raw.offer_url ?? ''),
  }
}

export function normalizeVastInstances(rows: unknown): VastInstance[] {
  if (!Array.isArray(rows)) return []
  return rows.map((r) => normalizeVastInstance(r as Record<string, unknown>))
}

export function normalizeVastOffers(rows: unknown): VastOffer[] {
  if (!Array.isArray(rows)) return []
  return rows.map((r) => normalizeVastOffer(r as Record<string, unknown>))
}

export const VAST_MODES = [
  { id: 'image', label: 'Image', sub: 'Flux / Z-Image', disk: 120 },
  { id: 'video', label: 'Video', sub: 'Wan 2.2', disk: 120 },
  { id: 'ltx-native', label: 'LTX native', sub: 'LTX 2.3', disk: 120 },
  { id: 'all', label: 'All stacks', sub: 'Image + video + LTX', disk: 150 },
] as const

export type VastModeId = (typeof VAST_MODES)[number]['id']
