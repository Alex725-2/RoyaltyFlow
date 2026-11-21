import { useEffect, useState, type ReactNode } from 'react'
import { CircleAlert, Database, LoaderCircle, LockKeyhole } from 'lucide-react'
import { ApiError } from './api'

export type Resource<T> =
  | { status: 'idle' | 'loading' }
  | { status: 'ready'; data: T }
  | { status: 'error'; error: Error }

export function useResource<T>(load: ((signal: AbortSignal) => Promise<T>) | null) {
  const [attempt, setAttempt] = useState(0)
  const [snapshot, setSnapshot] = useState<{
    load: typeof load; attempt: number; value: Resource<T>
  } | null>(null)
  useEffect(() => {
    if (!load) return
    const controller = new AbortController()
    load(controller.signal).then(
      data => {
        if (!controller.signal.aborted) setSnapshot({ load, attempt, value: { status: 'ready', data } })
      },
      error => {
        if (!controller.signal.aborted) {
          const failure = error instanceof Error ? error : new Error('Request failed.')
          setSnapshot({ load, attempt, value: { status: 'error', error: failure } })
        }
      },
    )
    return () => controller.abort()
  }, [load, attempt])
  const state: Resource<T> = !load ? { status: 'idle' }
    : snapshot?.load === load && snapshot.attempt === attempt ? snapshot.value : { status: 'loading' }
  return { state, reload: () => setAttempt(value => value + 1) }
}

export function errorMessage(error: unknown) {
  return error instanceof ApiError ? error.message : 'Не удалось загрузить данные. Проверьте соединение и повторите запрос.'
}

export function Empty({ children, locked = false }: { children: ReactNode; locked?: boolean }) {
  const Icon = locked ? LockKeyhole : Database
  return <div className="empty"><Icon size={21} aria-hidden="true" /><span>{children}</span></div>
}

export function Status<T>({ state, retry }: { state: Resource<T>; retry: () => void }) {
  if (state.status === 'idle') return <Empty locked>Войдите, чтобы увидеть данные</Empty>
  if (state.status === 'loading') {
    return <div className="empty" role="status"><LoaderCircle className="spinner" size={21} />Загрузка данных…</div>
  }
  if (state.status === 'error') return (
    <div className="empty error" role="alert">
      <CircleAlert size={21} /><span>{errorMessage(state.error)}</span>
      <button className="button secondary" onClick={retry}>Повторить</button>
    </div>
  )
  return null
}

export function StateContent<T>({ state, retry, children }: {
  state: Resource<T>; retry: () => void; children: (value: T) => ReactNode
}) {
  return state.status === 'ready' ? children(state.data) : <Status state={state} retry={retry} />
}

export interface Column<T> { label: string; numeric?: boolean; cell: (row: T) => ReactNode }

export function DataTable<T>({ rows, columns, rowKey, emptyText, paged = true }: {
  rows: readonly T[]; columns: Column<T>[]; rowKey: (row: T) => string; emptyText: string; paged?: boolean
}) {
  const [position, setPosition] = useState(0)
  const pageSize = 10
  const last = Math.max(0, Math.ceil(rows.length / pageSize) - 1)
  const page = Math.min(position, last)
  if (!rows.length) return <Empty>{emptyText}</Empty>
  const visible = paged ? rows.slice(page * pageSize, (page + 1) * pageSize) : rows
  return (
    <>
      <div className="table-scroll" tabIndex={0} role="region" aria-label="Таблица данных">
        <table>
          <thead><tr>{columns.map(col => <th scope="col" key={col.label} className={col.numeric ? 'number' : undefined}>{col.label}</th>)}</tr></thead>
          <tbody>{visible.map(row => (
            <tr key={rowKey(row)}>{columns.map(col => <td key={col.label} className={col.numeric ? 'number' : undefined}>{col.cell(row)}</td>)}</tr>
          ))}</tbody>
        </table>
      </div>
      {paged && rows.length > pageSize && (
        <div className="table-footer">
          <span>{page * pageSize + 1}–{page * pageSize + visible.length} из {rows.length}</span>
          <div>
            <button className="button secondary" disabled={page === 0} onClick={() => setPosition(page - 1)}>Назад</button>
            <button className="button secondary" disabled={page === last} onClick={() => setPosition(page + 1)}>Далее</button>
          </div>
        </div>
      )}
    </>
  )
}

export function TrackTitle({ title }: { title: string }) {
  const demo = title.endsWith(' [DEMO]')
  return <span className="track-title"><strong>{demo ? title.slice(0, -7) : title}</strong>{demo && <span className="tag">Демо</span>}</span>
}
