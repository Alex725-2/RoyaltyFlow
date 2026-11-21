import { parse } from 'lossless-json'

export interface Artist { id: string; name: string }
export interface Track { id: string; artistId: string; title: string }
export interface RightsHolder { id: string; name: string }
export interface Contract {
  id: string
  trackId: string
  validFrom: string
  validTo: string | null
  shares: { rightsHolderId: string; percentage: number | { toString(): string } }[]
}
export interface Period { from: string; to: string }
export interface RoyaltyRow {
  trackId: string
  title: string
  events: bigint
  plays: bigint
  cents: bigint
}
interface Page<T> {
  items: T[]
  page: unknown
  hasNext: boolean
}

export class ApiError extends Error {
  constructor(public status: number) {
    super(status === 401 || status === 403
      ? 'Доступ отклонён. Проверьте пароль демо-аккаунта.'
      : status === 503
        ? 'API временно недоступен. Повторите запрос позже.'
        : `Не удалось получить данные (HTTP ${status}).`)
  }
}

export function decimalCents(value: unknown): bigint {
  const [, sign, digits, fraction = ''] = /^(-?)(\d+)(?:\.(\d{1,2}))?$/.exec(String(value)) ?? []
  if (!digits) throw new Error('API вернуло некорректную сумму.')
  return BigInt(digits + fraction.padEnd(2, '0')) * (sign ? -1n : 1n)
}

const integerValue = (value: unknown) => BigInt(String(value).replace(/\.0+$/, ''))

export function createApi(password: string, request: typeof fetch = fetch) {
  const bytes = new TextEncoder().encode(`demo:${password}`)
  const authorization = `Basic ${btoa(String.fromCharCode(...bytes))}`

  async function get<T>(path: string, filters: Record<string, string>, signal?: AbortSignal): Promise<T> {
    const query = new URLSearchParams(filters).toString()
    const response = await request(`/api/${path}?${query}`, {
      method: 'GET', credentials: 'omit', cache: 'no-store', redirect: 'error',
      headers: { Authorization: authorization, Accept: 'application/json' }, signal,
    })
    if (!response.ok) throw new ApiError(response.status)
    return parse(await response.text()) as T
  }

  async function pages<T>(path: string, filters: Record<string, string> = {}, signal?: AbortSignal) {
    const rows: T[] = []
    for (let page = 1; ; page++) {
      const response = await get<Page<T>>(path, { ...filters, page: String(page), pageSize: '200' }, signal)
      if (Number(response.page) !== page || !Array.isArray(response.items) || typeof response.hasNext !== 'boolean') {
        throw new Error('Не удалось прочитать страницы API.')
      }
      for (const item of response.items) rows.push(item)
      if (!response.hasNext) return rows
    }
  }

  return {
    signIn: (signal?: AbortSignal) => get<Page<Artist>>('artists', { page: '1', pageSize: '1' }, signal),
    artists: (signal: AbortSignal) => pages<Artist>('artists', {}, signal),
    tracks: (signal: AbortSignal) => pages<Track>('tracks', {}, signal),
    contracts: (signal: AbortSignal) => pages<Contract>('contracts', {}, signal),
    holders: (signal: AbortSignal) => pages<RightsHolder>('rights-holders', {}, signal),
    async royalties({ from, to }: Period, signal: AbortSignal): Promise<RoyaltyRow[]> {
      const rows = await pages<{
        trackId: string; title: string; events: unknown; playCount: unknown; royaltyAmount: unknown
      }>('royalties/summary', { from, to }, signal)
      if (new Set(rows.map(row => row.trackId)).size !== rows.length) {
        throw new Error('API вернуло повторяющиеся строки аналитики.')
      }
      return rows.map(row => ({
        trackId: row.trackId, title: row.title, events: integerValue(row.events),
        plays: integerValue(row.playCount), cents: decimalCents(row.royaltyAmount),
      }))
    },
  }
}

export type RoyaltyApi = ReturnType<typeof createApi>
