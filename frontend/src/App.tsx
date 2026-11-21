import { useEffect, useRef, useState, type FormEvent } from 'react'
import {
  AudioLines, ChartNoAxesCombined, CircleUserRound, CodeXml, Disc3, FileSignature,
  LayoutDashboard, LockKeyhole, LogOut, Menu, ShieldCheck, UsersRound, X,
} from 'lucide-react'
import { createApi, type RoyaltyApi } from './api'
import { Analytics, PeriodPicker } from './Analytics'
import { Catalog } from './Catalog'
import { recentMonths } from './metrics'
import { errorMessage } from './ui'

const sections = [
  { id: 'dashboard', label: 'Обзор', title: 'Обзор роялти', icon: LayoutDashboard, text: 'Показатели каталога и начисления за выбранный период.' },
  { id: 'artists', label: 'Исполнители', title: 'Исполнители', icon: UsersRound, text: 'Исполнители каталога и количество связанных треков.' },
  { id: 'tracks', label: 'Треки', title: 'Треки', icon: Disc3, text: 'Музыкальный каталог с поиском и фильтром по исполнителю.' },
  { id: 'royalties', label: 'Роялти', title: 'Отчёт по роялти', icon: ChartNoAxesCombined, text: 'Прослушивания и начисления по трекам за выбранные даты.' },
  { id: 'contracts', label: 'Договоры', title: 'Договоры', icon: FileSignature, text: 'Сроки действия договоров и доли правообладателей.' },
  { id: 'holders', label: 'Правообладатели', title: 'Правообладатели', icon: ShieldCheck, text: 'Правообладатели, зарегистрированные в каталоге.' },
] as const

const currentSection = () => sections.find(item => `#${item.id}` === window.location.hash) ?? sections[0]

export default function App() {
  const [section, setSection] = useState(currentSection)
  const [menu, setMenu] = useState(false)
  const [api, setApi] = useState<RoyaltyApi | null>(null)
  const [period, setPeriod] = useState(() => recentMonths(6))
  useEffect(() => {
    const navigate = () => {
      setSection(currentSection())
      setMenu(false)
      document.getElementById('main-content')?.focus({ preventScroll: true })
      window.scrollTo(0, 0)
    }
    window.addEventListener('hashchange', navigate)
    return () => window.removeEventListener('hashchange', navigate)
  }, [])

  const destination = section.id
  const analytics = destination === 'dashboard' || destination === 'royalties'
  return (
    <div className="app-shell">
      <a className="skip-link" href="#main-content" onClick={event => {
        event.preventDefault()
        document.getElementById('main-content')?.focus()
      }}>К содержимому</a>
      <aside className={`sidebar ${menu ? 'menu-open' : ''}`}>
        <div className="sidebar-top">
          <a className="brand" href="#dashboard" onClick={() => setMenu(false)}>
            <span className="brand-mark"><AudioLines size={23} /></span>
            <span><strong>RoyaltyFlow</strong><small>Аналитика роялти</small></span>
          </a>
          <button className="menu-toggle" type="button" aria-expanded={menu} aria-controls="side-navigation"
            aria-label={menu ? 'Закрыть меню' : 'Открыть меню'} onClick={() => setMenu(!menu)}>
            {menu ? <X size={23} /> : <Menu size={23} />}
          </button>
        </div>
        <nav aria-label="Разделы" id="side-navigation">
          {sections.map(({ id, label, icon: Icon }) => (
            <a key={id} href={`#${id}`} aria-current={destination === id ? 'page' : undefined} onClick={() => setMenu(false)}>
              <Icon size={19} aria-hidden="true" />{label}
            </a>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <div className="sidebar-note"><span className="sidebar-shield"><ShieldCheck size={17} /></span>
            <div><strong>Демонстрационный доступ</strong><span>Только чтение</span></div></div>
          <a className="api-link" href="/swagger/index.html" target="_blank" rel="noreferrer"><CodeXml size={18} />Swagger API</a>
        </div>
      </aside>
      <div className="workspace">
        <header className="topbar">
          <span className="breadcrumb"><span>Рабочее пространство</span><span aria-hidden="true">/</span><strong>{section.label}</strong></span>
          <div className="account">
            <span className="account-icon"><CircleUserRound size={20} /></span>
            <span>{api ? 'demo' : 'Гостевой режим'}</span>
            {api && <button className="icon-button" type="button" aria-label="Выйти" title="Выйти" onClick={() => setApi(null)}><LogOut size={18} /></button>}
          </div>
        </header>
        <main className="content" id="main-content" tabIndex={-1}>
          <div className="page-heading"><div><h1>{section.title}</h1><p>{section.text}</p></div></div>
          {!api && <Login onSuccess={setApi} />}
          {analytics && <PeriodPicker period={period} onChange={setPeriod} />}
          {analytics ? <Analytics api={api} period={period} overview={destination === 'dashboard'} />
            : <Catalog key={destination} api={api} section={destination} />}
          <footer className="footer">Демонстрационный проект. Данные с пометкой «Демо» и связанные начисления вымышлены.</footer>
        </main>
      </div>
    </div>
  )
}

function Login({ onSuccess }: { onSuccess: (api: RoyaltyApi) => void }) {
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const pending = useRef<AbortController | null>(null)
  useEffect(() => () => pending.current?.abort(), [])

  async function signIn(event: FormEvent) {
    event.preventDefault()
    if (pending.current || !password) return
    const controller = new AbortController()
    pending.current = controller
    setError('')
    setBusy(true)
    try {
      const api = createApi(password)
      await api.signIn(controller.signal)
      setPassword('')
      onSuccess(api)
    } catch (failure) {
      if (!controller.signal.aborted) setError(errorMessage(failure))
    } finally {
      pending.current = null
      if (!controller.signal.aborted) setBusy(false)
    }
  }

  return (
    <section className="login-panel" aria-labelledby="login-title">
      <div className="login-copy"><span className="login-icon"><LockKeyhole size={21} /></span>
        <div><h2 id="login-title">Доступ к данным</h2><p>Войдите в демо-аккаунт, чтобы открыть отчёты и каталог.</p></div>
      </div>
      <form className="login-form" onSubmit={signIn}>
        <label><span>Логин</span><input value="demo" readOnly autoComplete="username" /></label>
        <label><span>Пароль</span><input type="password" autoComplete="current-password" placeholder="Введите пароль"
          required maxLength={256} value={password} onChange={event => setPassword(event.target.value)} /></label>
        <button type="submit" className="button primary" disabled={busy}>{busy ? 'Входим…' : 'Войти'}</button>
        {error && <p className="field-error" role="alert">{error}</p>}
      </form>
    </section>
  )
}
