// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import App from './App'

const artists = [{ id: 'a', name: 'Первый' }, { id: 'b', name: 'Второй' }]
const tracks = [{ id: 't1', artistId: 'a', title: 'Песня A [DEMO]' }, { id: 't2', artistId: 'b', title: 'Песня B' }]
const holder = { id: 'r1', name: 'Вымышленный правообладатель' }
const contract = {
  id: 'c1', trackId: 't1', validFrom: '2026-01-01', validTo: null,
  shares: [{ rightsHolderId: 'r1', percentage: 60 }],
}

beforeEach(() => {
  vi.stubGlobal('scrollTo', vi.fn())
  window.history.replaceState(null, '', '#tracks')
})
afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function apiResponses() {
  return vi.fn<typeof fetch>(async (input, init) => {
    const url = new URL(String(input), 'https://portfolio.test')
    const header = new Headers(init?.headers).get('Authorization')
    if (header !== `Basic ${btoa('demo:synthetic-password')}`) return new Response(null, { status: 401 })
    const payload: Record<string, unknown> = {
      '/api/artists': artists, '/api/tracks': tracks,
      '/api/contracts': [contract], '/api/rights-holders': [holder], '/api/royalties/summary': [],
    }
    return Response.json({ page: Number(url.searchParams.get('page')), items: payload[url.pathname], hasNext: false })
  })
}

function signIn(password = 'synthetic-password') {
  fireEvent.change(screen.getByLabelText('Пароль'), { target: { value: password } })
  fireEvent.click(screen.getByRole('button', { name: 'Войти' }))
}

it('prompts for DemoBasic, reports an invalid password and clears session data on sign-out', async () => {
  const requests = apiResponses()
  vi.stubGlobal('fetch', requests)
  const store = vi.spyOn(Storage.prototype, 'setItem')
  render(<App />)
  expect(requests).not.toHaveBeenCalled()
  signIn('invalid-synthetic-password')
  expect((await screen.findByRole('alert')).textContent).toContain('Доступ отклонён')
  signIn()
  await screen.findByText('Песня A')
  expect(screen.queryByLabelText('Пароль')).toBeNull()
  expect(store).not.toHaveBeenCalled()
  fireEvent.click(screen.getByRole('button', { name: 'Выйти' }))
  expect(screen.getByLabelText('Пароль')).toBeTruthy()
  expect(screen.queryByText('Песня A')).toBeNull()
  store.mockRestore()
})

it('filters tracks by artist and resolves track and holder names in contracts', async () => {
  vi.stubGlobal('fetch', apiResponses())
  render(<App />)
  signIn()
  await screen.findByText('Песня A')
  expect(screen.getByText('Песня B')).toBeTruthy()
  fireEvent.change(screen.getByLabelText('Исполнитель'), { target: { value: 'a' } })
  expect(screen.queryByText('Песня B')).toBeNull()
  fireEvent.change(screen.getByLabelText('Поиск по каталогу'), { target: { value: 'ненайденный трек' } })
  expect(screen.getByText('По заданным фильтрам ничего не найдено')).toBeTruthy()

  fireEvent.click(screen.getByRole('link', { name: 'Договоры' }))
  expect(await screen.findByText(holder.name)).toBeTruthy()
  expect(screen.getByText('60 %')).toBeTruthy()
})

it('applies custom dates to royalties and displays the real empty state', async () => {
  window.history.replaceState(null, '', '#royalties')
  const requests = apiResponses()
  vi.stubGlobal('fetch', requests)
  render(<App />)
  signIn()
  await screen.findByText('За выбранный период начислений нет')
  fireEvent.change(screen.getByLabelText('С', { exact: true }), { target: { value: '2026-04-15' } })
  fireEvent.change(screen.getByLabelText('По', { exact: true }), { target: { value: '2026-05-20' } })
  fireEvent.click(screen.getByRole('button', { name: 'Применить' }))
  await waitFor(() => expect(requests.mock.calls.some(([url]) =>
    String(url).startsWith('/api/royalties/summary?from=2026-04-15&to=2026-05-20'))).toBe(true))
  expect(await screen.findByText('За выбранный период начислений нет')).toBeTruthy()
})

it('shows catalog loading, a service error and a working retry', async () => {
  const responses = apiResponses()
  let failRequest!: (response: Response) => void
  const pending = new Promise<Response>(resolve => { failRequest = resolve })
  let attempts = 0
  vi.stubGlobal('fetch', vi.fn<typeof fetch>((...args) =>
    String(args[0]).startsWith('/api/tracks?') && attempts++ === 0 ? pending : responses(...args),
  ))
  render(<App />)
  signIn()
  expect((await screen.findByRole('status')).textContent).toContain('Загрузка данных')
  failRequest(new Response(null, { status: 503 }))
  expect((await screen.findByRole('alert')).textContent).toContain('API временно недоступен')
  fireEvent.click(screen.getByRole('button', { name: 'Повторить' }))
  expect(await screen.findByText('Песня A')).toBeTruthy()
})
