import { describe, expect, it, vi } from 'vitest'
import { createApi, decimalCents } from './api'
import { amount, periods, recentMonths, totals } from './metrics'

describe('RoyaltyFlow API', () => {
  it('uses same-origin GET with DemoBasic, follows API pagination and keeps numeric precision', async () => {
    const first = '{"items":[{"trackId":"1","title":"Track","events":12,"playCount":90071992547409931,"royaltyAmount":9007199254740993.29}],"page":1,"hasNext":true}'
    const second = '{"items":[{"trackId":"2","title":"Another","events":2,"playCount":3.0,"royaltyAmount":0.02}],"page":2,"hasNext":false}'
    const request = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(new Response(first)).mockResolvedValueOnce(new Response(second))
    const signal = new AbortController().signal
    const rows = await createApi('тестовый пароль', request).royalties({ from: '2026-01-01', to: '2026-01-31' }, signal)
    expect(request).toHaveBeenNthCalledWith(1, '/api/royalties/summary?from=2026-01-01&to=2026-01-31&page=1&pageSize=200',
      expect.objectContaining({
        method: 'GET', redirect: 'error', credentials: 'omit', cache: 'no-store', signal,
        headers: expect.objectContaining({
          Authorization: 'Basic ZGVtbzrRgtC10YHRgtC+0LLRi9C5INC/0LDRgNC+0LvRjA==',
        }),
      }),
    )
    expect(request.mock.calls[1][0]).toContain('page=2')
    expect(rows.map(row => row.cents)).toEqual([900719925474099329n, 2n])
    expect(totals(rows)).toEqual({ events: 14n, plays: 90071992547409934n, cents: 900719925474099331n })
    expect(amount(totals(rows).cents).replace(/\s/g, '')).toBe('9007199254740993,31')
  })

  it('reports protected API failures without using the response body', async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(new Response('private body', { status: 401 }))
    await expect(createApi('synthetic-password', request).signIn()).rejects.toMatchObject({
      status: 401, message: 'Доступ отклонён. Проверьте пароль демо-аккаунта.',
    })
  })

  it('detects a repeated page and repeated summary rows', async () => {
    const request = vi.fn<typeof fetch>().mockImplementation(async () => new Response('{"items":[],"page":1,"hasNext":true}'))
    await expect(createApi('synthetic-password', request).tracks(new AbortController().signal))
      .rejects.toThrow('Не удалось прочитать страницы API.')
    const duplicates = vi.fn<typeof fetch>().mockResolvedValue(
      new Response('{"items":[{"trackId":"1"},{"trackId":"1"}],"page":1,"hasNext":false}'),
    )
    await expect(createApi('synthetic-password', duplicates).royalties(
      { from: '2026-01-01', to: '2026-01-31' }, new AbortController().signal,
    )).rejects.toThrow('повторяющиеся строки')
  })
})

describe('Portfolio metrics', () => {
  it('keeps inclusive chart intervals disjoint at leap day and across months', () => {
    expect(periods({ from: '2024-02-27', to: '2024-03-02' })).toEqual([
      { from: '2024-02-27', to: '2024-02-27' }, { from: '2024-02-28', to: '2024-02-28' },
      { from: '2024-02-29', to: '2024-02-29' }, { from: '2024-03-01', to: '2024-03-01' },
      { from: '2024-03-02', to: '2024-03-02' },
    ])
    const windows = periods({ from: '2026-01-01', to: '2026-06-30' })
    expect(windows).toHaveLength(6)
    expect(windows[0].from).toBe('2026-01-01')
    expect(windows.at(-1)?.to).toBe('2026-06-30')
    for (let index = 1; index < windows.length; index++) {
      expect(Date.parse(windows[index].from) - Date.parse(windows[index - 1].to)).toBe(86_400_000)
    }
  })

  it('presets use completed UTC months and amounts are parsed without rounding', () => {
    expect(recentMonths(6, new Date('2026-11-21T20:00:00Z'))).toEqual({ from: '2026-05-01', to: '2026-10-31' })
    expect(decimalCents('1.50')).toBe(150n)
    expect(() => decimalCents('0.001')).toThrow()
  })
})
