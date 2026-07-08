import { Fragment, useMemo, useState, type ReactNode } from 'react'
import { compareSortValues } from './format'

export type SortableColumn<T> = {
  id: string
  header: ReactNode
  sortable?: boolean
  sortValue?: (row: T) => string | number | Date | null | undefined
  render: (row: T) => ReactNode
  className?: string
  headerClassName?: string
}

export type SortState = {
  id: string
  dir: 'asc' | 'desc'
}

type SortableTableProps<T> = {
  rows: T[]
  columns: SortableColumn<T>[]
  rowKey: (row: T) => string
  defaultSort?: SortState
  className?: string
  emptyMessage?: ReactNode
  onRowClick?: (row: T) => void
  rowClassName?: (row: T) => string | undefined
  renderRowExtra?: (row: T) => ReactNode
  footer?: ReactNode
}

function sortRows<T>(rows: T[], columns: SortableColumn<T>[], sort: SortState | null): T[] {
  if (!sort) return rows
  const col = columns.find((c) => c.id === sort.id)
  if (!col?.sortValue) return rows
  const sorted = [...rows].sort((a, b) => compareSortValues(col.sortValue!(a), col.sortValue!(b)))
  return sort.dir === 'desc' ? sorted.reverse() : sorted
}

export function SortableTable<T>({
  rows,
  columns,
  rowKey,
  defaultSort,
  className,
  emptyMessage = 'No rows to display.',
  onRowClick,
  rowClassName,
  renderRowExtra,
  footer,
}: SortableTableProps<T>) {
  const [sort, setSort] = useState<SortState | null>(defaultSort ?? null)

  const sortedRows = useMemo(() => sortRows(rows, columns, sort), [rows, columns, sort])

  function toggleSort(col: SortableColumn<T>) {
    if (col.sortable === false || !col.sortValue) return
    setSort((prev) => {
      if (prev?.id !== col.id) return { id: col.id, dir: 'asc' }
      if (prev.dir === 'asc') return { id: col.id, dir: 'desc' }
      return null
    })
  }

  if (!rows.length) {
    return typeof emptyMessage === 'string'
      ? <p className="muted table-empty">{emptyMessage}</p>
      : <>{emptyMessage}</>
  }

  return (
    <div className="table-wrap">
      <table className={className ? `data-table ${className}` : 'data-table'}>
        <thead>
          <tr>
            {columns.map((col) => {
              const active = sort?.id === col.id
              const sortable = col.sortable !== false && !!col.sortValue
              return (
                <th
                  key={col.id}
                  className={[
                    col.headerClassName,
                    sortable ? 'sortable' : '',
                    active ? `sorted-${sort!.dir}` : '',
                  ].filter(Boolean).join(' ')}
                  aria-sort={active ? (sort!.dir === 'asc' ? 'ascending' : 'descending') : undefined}
                  onClick={sortable ? () => toggleSort(col) : undefined}
                >
                  <span className="th-inner">
                    {col.header}
                    {sortable && <span className="sort-indicator" aria-hidden />}
                  </span>
                </th>
              )
            })}
          </tr>
        </thead>
        <tbody>
          {sortedRows.map((row) => {
            const key = rowKey(row)
            const extra = rowClassName?.(row)
            return (
              <Fragment key={key}>
                <tr
                  className={[onRowClick ? 'clickable-row' : '', extra].filter(Boolean).join(' ') || undefined}
                  onClick={onRowClick ? () => onRowClick(row) : undefined}
                >
                  {columns.map((col) => (
                    <td key={col.id} className={col.className}>{col.render(row)}</td>
                  ))}
                </tr>
                {renderRowExtra?.(row)}
              </Fragment>
            )
          })}
        </tbody>
        {footer && <tfoot><tr><td colSpan={columns.length}>{footer}</td></tr></tfoot>}
      </table>
    </div>
  )
}
