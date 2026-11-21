import { lazy, Suspense, useCallback, useState, type FormEvent } from 'react'
import { AudioLines, CalendarDays, CirclePlay, Disc3, Wallet } from 'lucide-react'
import type { Period, RoyaltyApi, RoyaltyRow } from './api'
import { amount, bucketLabel, integer, periods, ranked, recentMonths, totals } from './metrics'
import { DataTable, Empty, StateContent, Status, TrackTitle, useResource, type Column, type Resource } from './ui'

const TrendChart = lazy(() => import('./TrendChart'))

const royaltyColumns: Column<RoyaltyRow>[] = [
  { label: 'Трек', cell: row => <TrackTitle title={row.title} /> },
  { label: 'События', numeric: true, cell: row => integer.format(row.events) },
  { label: 'Прослушивания', numeric: true, cell: row => integer.format(row.plays) },
  { label: 'Начислено', numeric: true, cell: row => <strong>{amount(row.cents)}</strong> },
]

export function PeriodPicker({ period, onChange }: { period: Period; onChange: (period: Period) => void }) {
  const [draft, setDraft] = useState(period)
  const [invalid, setInvalid] = useState(false)
  function apply(event: FormEvent) {
    event.preventDefault()
    setInvalid(draft.from > draft.to)
    if (draft.from <= draft.to) onChange(draft)
  }
  return (
    <div className="period-panel">
      <form className="period-form" onSubmit={apply}>
        <div className="period-label"><CalendarDays size={18} />Период</div>
        <label><span>С</span><input type="date" required value={draft.from}
          onChange={event => setDraft({ ...draft, from: event.target.value })} /></label>
        <label><span>По</span><input type="date" required value={draft.to}
          onChange={event => setDraft({ ...draft, to: event.target.value })} /></label>
        <button className="button primary">Применить</button>
      </form>
      <div className="shortcuts" aria-label="Быстрый выбор периода">
        {([[1, 'Месяц'], [3, 'Квартал'], [6, '6 месяцев']] as const).map(([count, label]) => {
          const preset = recentMonths(count)
          return <button key={count} type="button" aria-pressed={period.from === preset.from && period.to === preset.to}
            onClick={() => { setDraft(preset); setInvalid(false); onChange(preset) }}>{label}</button>
        })}
      </div>
      {invalid && <p className="field-error" role="alert">Начало периода должно быть не позже даты окончания.</p>}
    </div>
  )
}

export function Analytics({ api, period, overview }: { api: RoyaltyApi | null; period: Period; overview: boolean }) {
  const load = useCallback((signal: AbortSignal) => api!.royalties(period, signal), [api, period])
  const { state, reload } = useResource(api ? load : null)
  const rows = state.status === 'ready' ? ranked(state.data) : []
  return (
    <>
      {overview && <><Metrics rows={state.status === 'ready' ? rows : null} /><div className="chart-grid">
        <Trend api={api} period={period} summary={state} retrySummary={reload} />
        <div className="panel chart-panel">
          <div className="panel-heading"><div><h2>Топ треков</h2><p>По сумме начислений</p></div><Disc3 size={20} /></div>
          <StateContent state={state} retry={reload}>{() => <TrackRanking rows={rows.slice(0, 5)} />}</StateContent>
        </div>
      </div></>}
      <div className="panel royalty-panel">
        <div className="panel-heading">
          <div><h2>{overview ? 'Начисления по трекам' : 'Роялти по трекам'}</h2><p>Суммы за выбранный период</p></div>
          {overview && <a className="text-link" href="#royalties">Все начисления</a>}
        </div>
        <StateContent state={state} retry={reload}>{() => (
          <DataTable key={`${period.from}/${period.to}`} rows={overview ? rows.slice(0, 5) : rows} rowKey={row => row.trackId}
            columns={royaltyColumns} emptyText="За выбранный период начислений нет" paged={!overview} />
        )}</StateContent>
      </div>
    </>
  )
}

function Metrics({ rows }: { rows: RoyaltyRow[] | null }) {
  const sum = rows ? totals(rows) : null
  const values = [
    { label: 'Начислено роялти', value: sum && amount(sum.cents), info: 'В условных единицах', icon: Wallet, color: 'violet' },
    { label: 'Прослушивания', value: sum && integer.format(sum.plays), info: 'Сумма воспроизведений', icon: CirclePlay, color: 'green' },
    { label: 'События', value: sum && integer.format(sum.events), info: 'Записи об использовании', icon: AudioLines, color: 'blue' },
    { label: 'Треки в отчёте', value: rows && integer.format(rows.length), info: 'С активностью за период', icon: Disc3, color: 'sand' },
  ]
  return (
    <div className="metrics">
      {values.map(({ label, value, info, icon: Icon, color }) => (
        <article className="panel metric" key={label}>
          <div className={`metric-icon ${color}`}><Icon size={20} aria-hidden="true" /></div>
          <span className="metric-label">{label}</span>
          <strong title={value ?? undefined}>{value ?? '—'}</strong>
          <span className="metric-info">{info}</span>
        </article>
      ))}
    </div>
  )
}

function Trend({ api, period, summary, retrySummary }: {
  api: RoyaltyApi | null; period: Period; summary: Resource<RoyaltyRow[]>; retrySummary: () => void
}) {
  const hasRows = summary.status === 'ready' && summary.data.length > 0
  const load = useCallback(async (signal: AbortSignal) => Promise.all(periods(period).map(async interval => {
    const cents = totals(await api!.royalties(interval, signal)).cents
    return { name: bucketLabel(interval), value: Number(cents) / 100, exact: amount(cents) }
  })), [api, period])
  const { state, reload } = useResource(api && hasRows ? load : null)
  return (
    <div className="panel chart-panel">
      <div className="panel-heading"><div><h2>Динамика начислений</h2><p>Роялти по интервалам периода</p></div><span className="chart-key">Роялти</span></div>
      {summary.status !== 'ready' ? <Status state={summary} retry={retrySummary} />
        : hasRows ? <StateContent state={state} retry={reload}>{points => (
          <Suspense fallback={<Status state={{ status: 'loading' }} retry={reload} />}><TrendChart points={points} /></Suspense>
        )}</StateContent> : <Empty>График появится, когда будут начисления за период</Empty>}
    </div>
  )
}

function TrackRanking({ rows }: { rows: RoyaltyRow[] }) {
  if (!rows.length) return <Empty>За выбранный период начислений нет</Empty>
  const max = rows[0].cents
  return (
    <ol className="ranking">
      {rows.map(row => (
        <li key={row.trackId}>
          <div className="rank-label"><TrackTitle title={row.title} /><span>{amount(row.cents)}</span></div>
          <div className="bar-track" aria-hidden="true">
            <div style={{ width: `${max > 0n ? Number(row.cents * 1000n / max) / 10 : 0}%` }} />
          </div>
        </li>
      ))}
    </ol>
  )
}
