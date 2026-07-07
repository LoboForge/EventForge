export type ChartPoint = { x: number; y: number }

export type MetricsSample = {
  atUtc: string
  jobsQueued: number
  jobsInProgress: number
  jobsFailed: number
  workersTotal: number
  workersBusy: number
  workersStale: number
  workersNonContributing: number
}

export function parseMetricsSample(raw: Record<string, unknown>): MetricsSample {
  const readNum = (k: string) => {
    const v = raw[k]
    return typeof v === 'number' && Number.isFinite(v) ? v : 0
  }
  const at = typeof raw.atUtc === 'string' ? raw.atUtc
    : typeof raw.at_utc === 'string' ? raw.at_utc : ''
  return {
    atUtc: at,
    jobsQueued: readNum('jobsQueued') || readNum('jobs_queued'),
    jobsInProgress: readNum('jobsInProgress') || readNum('jobs_in_progress'),
    jobsFailed: readNum('jobsFailed') || readNum('jobs_failed'),
    workersTotal: readNum('workersTotal') || readNum('workers_total'),
    workersBusy: readNum('workersBusy') || readNum('workers_busy'),
    workersStale: readNum('workersStale') || readNum('workers_stale'),
    workersNonContributing: readNum('workersNonContributing') || readNum('workers_non_contributing'),
  }
}

function buildPoints(values: number[], width: number, height: number, pad = 4): ChartPoint[] {
  if (values.length === 0) return []
  const max = Math.max(...values, 1)
  const step = values.length <= 1 ? 0 : (width - pad * 2) / (values.length - 1)
  return values.map((v, i) => ({
    x: pad + i * step,
    y: height - pad - (v / max) * (height - pad * 2),
  }))
}

function pointsToPath(points: ChartPoint[]): string {
  if (!points.length) return ''
  return points.map((p, i) => `${i === 0 ? 'M' : 'L'}${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' ')
}

type LineChartProps = {
  series: { label: string; color: string; values: number[] }[]
  width?: number
  height?: number
  className?: string
}

export function LineChart({ series, width = 320, height = 100, className }: LineChartProps) {
  const allEmpty = series.every((s) => s.values.length === 0)
  if (allEmpty) {
    return <div className={'chart-empty muted' + (className ? ` ${className}` : '')}>No data yet</div>
  }
  const max = Math.max(...series.flatMap((s) => s.values), 1)
  return (
    <div className={'line-chart' + (className ? ` ${className}` : '')}>
      <svg viewBox={`0 0 ${width} ${height}`} width="100%" height={height} preserveAspectRatio="none">
        <line x1="0" y1={height - 1} x2={width} y2={height - 1} stroke="#1f2a38" strokeWidth="1" />
        {series.map((s) => {
          const pts = buildPoints(s.values, width, height)
          return (
            <g key={s.label}>
              <path d={pointsToPath(pts)} fill="none" stroke={s.color} strokeWidth="2" vectorEffect="non-scaling-stroke" />
            </g>
          )
        })}
        <text x={width - 4} y={12} textAnchor="end" fill="#6b7f9a" fontSize="10">{max}</text>
      </svg>
      <div className="chart-legend">
        {series.map((s) => (
          <span key={s.label} className="chart-legend-item">
            <span className="chart-swatch" style={{ background: s.color }} />
            {s.label}
          </span>
        ))}
      </div>
    </div>
  )
}

type BarChartProps = {
  items: { label: string; value: number; color?: string }[]
  height?: number
  className?: string
}

export function BarChart({ items, height = 120, className }: BarChartProps) {
  const max = Math.max(...items.map((i) => i.value), 1)
  return (
    <div className={'bar-chart' + (className ? ` ${className}` : '')}>
      {items.map((item) => (
        <div key={item.label} className="bar-row">
          <span className="bar-label muted">{item.label}</span>
          <div className="bar-track">
            <div
              className="bar-fill"
              style={{
                width: `${Math.max(2, (item.value / max) * 100)}%`,
                background: item.color ?? '#3d7fd4',
              }}
            />
          </div>
          <span className="bar-value">{item.value}</span>
        </div>
      ))}
      <div style={{ height }} />
    </div>
  )
}

type DonutProps = {
  segments: { label: string; value: number; color: string }[]
  size?: number
  className?: string
}

export function DonutChart({ segments, size = 120, className }: DonutProps) {
  const total = segments.reduce((s, x) => s + x.value, 0)
  if (total === 0) {
    return <div className={'chart-empty muted' + (className ? ` ${className}` : '')}>No data</div>
  }
  const r = size / 2 - 8
  const cx = size / 2
  const cy = size / 2
  let angle = -Math.PI / 2
  const arcs = segments.filter((s) => s.value > 0).map((seg) => {
    const slice = (seg.value / total) * Math.PI * 2
    const x1 = cx + r * Math.cos(angle)
    const y1 = cy + r * Math.sin(angle)
    angle += slice
    const x2 = cx + r * Math.cos(angle)
    const y2 = cy + r * Math.sin(angle)
    const large = slice > Math.PI ? 1 : 0
    const d = `M ${cx} ${cy} L ${x1} ${y1} A ${r} ${r} 0 ${large} 1 ${x2} ${y2} Z`
    return { ...seg, d }
  })
  return (
    <div className={'donut-chart' + (className ? ` ${className}` : '')}>
      <svg width={size} height={size}>
        {arcs.map((a) => <path key={a.label} d={a.d} fill={a.color} opacity={0.9} />)}
        <circle cx={cx} cy={cy} r={r * 0.55} fill="#0f141c" />
        <text x={cx} y={cy} textAnchor="middle" dominantBaseline="middle" fill="#e8edf5" fontSize="14" fontWeight="700">
          {total}
        </text>
      </svg>
      <div className="chart-legend vertical">
        {segments.map((s) => (
          <span key={s.label} className="chart-legend-item">
            <span className="chart-swatch" style={{ background: s.color }} />
            {s.label} ({s.value})
          </span>
        ))}
      </div>
    </div>
  )
}
