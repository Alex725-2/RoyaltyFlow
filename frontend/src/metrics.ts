import type { Period, RoyaltyRow } from './api'

const day = 86_400_000
export const integer = new Intl.NumberFormat('ru-RU')
const shortDate = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short', timeZone: 'UTC' })
const wholeDate = new Intl.DateTimeFormat('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric', timeZone: 'UTC' })
const iso = (date: Date) => date.toISOString().slice(0, 10)

export function recentMonths(count: number, now = new Date()): Period {
  const year = now.getUTCFullYear(), month = now.getUTCMonth()
  return { from: iso(new Date(Date.UTC(year, month - count, 1))), to: iso(new Date(Date.UTC(year, month, 0))) }
}

export function periods({ from, to }: Period): Period[] {
  const start = Date.parse(from)
  const days = (Date.parse(to) - start) / day + 1
  const buckets = Math.min(days, 6)
  return Array.from({ length: buckets }, (_, index) => ({
    from: iso(new Date(start + Math.floor(index * days / buckets) * day)),
    to: iso(new Date(start + (Math.floor((index + 1) * days / buckets) - 1) * day)),
  }))
}

export const formatDate = (value: string) => wholeDate.format(new Date(value))
export const bucketLabel = ({ from, to }: Period) => from === to
  ? shortDate.format(new Date(from))
  : `${shortDate.format(new Date(from))} – ${shortDate.format(new Date(to))}`

export function totals(rows: readonly RoyaltyRow[]) {
  return rows.reduce((sum, row) => ({
    cents: sum.cents + row.cents, plays: sum.plays + row.plays, events: sum.events + row.events,
  }), { cents: 0n, plays: 0n, events: 0n })
}

export function amount(cents: bigint) {
  const absolute = cents < 0n ? -cents : cents
  const sign = cents < 0n ? '−' : ''
  return `${sign}${integer.format(absolute / 100n)},${String(absolute % 100n).padStart(2, '0')}`
}

export function ranked(rows: readonly RoyaltyRow[]) {
  return [...rows].sort((a, b) => a.cents === b.cents ? a.title.localeCompare(b.title, 'ru') : a.cents > b.cents ? -1 : 1)
}
