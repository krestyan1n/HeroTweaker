import React, { useEffect, useMemo, useRef, useState } from 'react'
import {
  Activity, AppWindow, ArrowDownRight, ArrowRight, ArrowUpRight, AudioWaveform,
  ChevronDown, ChevronRight, CircleHelp, Command, Copy, Cpu, Disc3, Download, ExternalLink, Gauge, HardDrive,
  Layers3, MemoryStick, MonitorCog, Network, PackageCheck, PackageOpen, PanelLeftClose,
  PanelLeftOpen, RefreshCw, Search, Settings2, ShieldCheck, Sparkles, Store, Trash2,
  Wrench, X
} from 'lucide-react'

const navGroups = [
  { label: 'СИСТЕМА', items: [
    { id: 'Информация', name: 'Сводка', icon: Layers3 },
    { id: 'Производительность', name: 'Производительность', icon: Gauge },
    { id: 'Приватность', name: 'Приватность', icon: ShieldCheck },
    { id: 'Службы', name: 'Службы Windows', icon: Activity },
    { id: 'Интерфейс', name: 'Интерфейс Windows', icon: Settings2 },
  ]},
  { label: 'ОБОРУДОВАНИЕ', items: [
    { id: 'Диск', name: 'Накопители', icon: HardDrive },
    { id: 'AMD GPU', name: 'AMD GPU', icon: MonitorCog },
    { id: 'NVIDIA GPU', name: 'NVIDIA GPU', icon: MonitorCog },
    { id: 'Сеть & Пинг', name: 'Сеть', icon: Network },
  ]},
  { label: 'ОБСЛУЖИВАНИЕ', items: [
    { id: 'Приложения', name: 'Приложения', icon: AppWindow },
    { id: 'Автозагрузка', name: 'Автозагрузка', icon: Activity },
    { id: 'Драйверы', name: 'Драйверы', icon: MonitorCog },
    { id: 'Обновления', name: 'Обновления', icon: ArrowDownRight },
  ]},
  { label: 'ИНСТРУМЕНТЫ', items: [
    { id: 'Бенчмарк', name: 'Бенчмарк', icon: AudioWaveform },
    { id: 'Developer', name: 'Среда разработки', icon: Command },
    { id: 'Настройки', name: 'Настройки', icon: Settings2 },
  ]},
]

const fallback = {
  version: '0.9.2', selectedCategory: 'Информация', health: null, healthText: 'Собираю данные системы',
  cpu: null, ram: null, ramDetails: '—', cpuName: 'Определяется…', osName: 'Windows', osBuild: '',
  threads: 0, totalRam: 'RAM —', osArchitecture: '—',
  systemManufacturer: 'Производитель не определён', systemModel: 'Модель не определена',
  motherboard: 'Материнская плата не определена', biosVersion: 'BIOS не определён',
  gpuName: 'Проверка адаптера…', gpuVendor: '', gpuType: 'Тип не определён',
  gpuMemory: 'VRAM не определена', gpuDriverVersion: 'Драйвер не определён', systemDrive: null,
  drives: [], activeTweaks: 0, disabledServices: 0,
  cpuHistory: [], ramHistory: [],
  status: 'Подключение к системному ядру', systemReady: false,
}

function send(kind, payload = {}) {
  const host = window.chrome?.webview
  if (host) host.postMessage({ type: kind, ...payload })
}

function useDesktopBridge() {
  const [state, setState] = useState(fallback)
  useEffect(() => {
    const host = window.chrome?.webview
    if (!host) {
      setState(s => ({ ...s, systemReady: true, status: 'Режим предпросмотра — нет соединения с Windows' }))
      return
    }
    const onMessage = e => {
      if (e.data?.type === 'snapshot') setState(s => ({ ...s, ...e.data.payload }))
      if (e.data?.type === 'patch') setState(s => ({ ...s, ...e.data.payload }))
      if (e.data?.type === 'toast') setState(s => ({ ...s, toast: e.data.message }))
    }
    host.addEventListener('message', onMessage)
    send('ready')
    // Full snapshots include disk trees, installed apps and audit history.
    // Poll less often; explicit actions and navigation still request an immediate snapshot.
    const timer = window.setInterval(() => send('snapshot'), 3000)
    return () => { host.removeEventListener('message', onMessage); window.clearInterval(timer) }
  }, [])
  return state
}

function useAmbientField() {
  const ref = useRef(null)
  useEffect(() => {
    const el = ref.current
    if (!el) return
    let frame = 0
    let point = { x: 0.72, y: 0.24 }
    let target = point
    const move = e => {
      const r = el.getBoundingClientRect()
      target = { x: (e.clientX - r.left) / r.width, y: (e.clientY - r.top) / r.height }
      if (!frame) frame = requestAnimationFrame(draw)
    }
    function draw() {
      frame = 0
      point = { x: point.x + (target.x - point.x) * 0.09, y: point.y + (target.y - point.y) * 0.09 }
      el.style.setProperty('--px', `${point.x * 100}%`)
      el.style.setProperty('--py', `${point.y * 100}%`)
      if (Math.abs(point.x - target.x) + Math.abs(point.y - target.y) > 0.002) frame = requestAnimationFrame(draw)
    }
    el.addEventListener('pointermove', move, { passive: true })
    return () => { el.removeEventListener('pointermove', move); if (frame) cancelAnimationFrame(frame) }
  }, [])
  return ref
}

function WindowButton({ title, children, action, danger }) {
  return <button className={`window-btn ${danger ? 'window-close' : ''}`} title={title} onClick={() => send('window', { action })}>{children}</button>
}

function App() {
  const data = useDesktopBridge()
  const ambientRef = useAmbientField()
  const [active, setActive] = useState('Информация')
  const [collapsed, setCollapsed] = useState(false)
  const [pageDirection, setPageDirection] = useState('right')
  const [query, setQuery] = useState('')
  const [toast, setToast] = useState('')
  const searchRef = useRef(null)
  const previousPage = useRef('Информация')
  const filteredGroups = useMemo(() => navGroups.map(g => ({ ...g, items: g.items.filter(i => `${i.name} ${i.id}`.toLowerCase().includes(query.toLowerCase())) })).filter(g => g.items.length), [query])

  useEffect(() => {
    if (data.selectedCategory && data.selectedCategory !== active) setActive(data.selectedCategory)
  }, [data.selectedCategory])
  useEffect(() => {
    if (previousPage.current !== active) {
      const order = navGroups.flatMap(group => group.items.map(item => item.id))
      setPageDirection(order.indexOf(active) >= order.indexOf(previousPage.current) ? 'right' : 'left')
      previousPage.current = active
    }
  }, [active])
  useEffect(() => {
    const selected = data.settings?.themes?.find(theme => theme.id === data.settings?.selectedThemeId)
    if (!selected) return
    const hex = selected.color || '#38BDF8'
    const rgb = hex.match(/[\da-f]{2}/gi)?.slice(0, 3).map(value => parseInt(value, 16)).join(',') || '56,189,248'
    const root = document.documentElement
    root.style.setProperty('--accent', hex)
    root.style.setProperty('--accent-rgb', rgb)
    root.style.setProperty('--accent-dim', `rgba(${rgb}, .13)`)
    root.style.setProperty('--glow', selected.glowColor || hex)
    root.style.setProperty('--app-canvas', selected.windowBgColor || '#0B0F19')
    root.style.setProperty('--app-wash', selected.sidebarBgColor || '#070A10')
    root.style.setProperty('--surface-solid', selected.cardBgColor || '#111827')
    root.style.setProperty('--surface-hover', selected.cardHoverColor || '#1E293B')
    root.style.setProperty('--border', `${selected.borderColor || '#1E293B'}99`)
  }, [data.settings?.selectedThemeId])
  useEffect(() => { if (!data.toast) return; setToast(data.toast); const t = setTimeout(() => setToast(''), 3200); return () => clearTimeout(t) }, [data.toast])
  useEffect(() => {
    const key = e => { if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); searchRef.current?.focus() } if (e.key === 'Escape') { setQuery(''); searchRef.current?.blur() } }
    window.addEventListener('keydown', key); return () => window.removeEventListener('keydown', key)
  }, [])

  const openSection = id => { setActive(id); setQuery(''); send('navigate', { category: id }) }
  const allGroups = filteredGroups.map(g => g.label === 'ОБОРУДОВАНИЕ' ? { ...g, items: g.items.filter(i => i.id === 'Диск' || i.id === 'Сеть & Пинг' || (i.id === 'AMD GPU' && data.gpuVendor === 'AMD') || (i.id === 'NVIDIA GPU' && data.gpuVendor === 'NVIDIA')) } : g).filter(g => g.items.length)

  return <div className="app-shell" ref={ambientRef}>
    <div className="ambient-wash" aria-hidden="true" />
    <header className="titlebar" onPointerDown={e => { if (e.button === 0 && !e.target.closest('button')) send('windowDrag') }} onDoubleClick={() => send('window', { action: 'maximize' })}>
      <div className="brand-mark"><span>H</span><i /></div>
      <div className="brand-copy"><strong>HeroTweaker</strong><span>SYSTEM WORKBENCH <b>·</b> v{data.version}</span></div>
      <div className="titlebar-spacer" />
      <div className="telemetry-chip"><span className="signal signal-cyan" />CPU <b>{data.cpu == null ? '—' : `${Math.round(data.cpu)}%`}</b></div>
      <div className="telemetry-chip"><span className="signal signal-violet" />RAM <b>{data.ram == null ? '—' : `${Math.round(data.ram)}%`}</b></div>
      <div className="titlebar-actions"><WindowButton title="Свернуть" action="minimize">−</WindowButton><WindowButton title="Развернуть" action="maximize">□</WindowButton><WindowButton title="Закрыть" action="close" danger><X size={15}/></WindowButton></div>
    </header>

    <div className={`work-area ${collapsed ? 'work-area-collapsed' : ''}`}>
      <aside className={`sidebar ${collapsed ? 'sidebar-collapsed' : ''}`}>
        <div className="sidebar-topline"><span>РАБОЧАЯ СРЕДА</span><button className="icon-button" title={collapsed ? 'Развернуть меню' : 'Свернуть меню'} onClick={() => setCollapsed(v => !v)}>{collapsed ? <PanelLeftOpen size={15}/> : <PanelLeftClose size={15}/>}</button></div>
        {!collapsed && <div className="quick-search"><Search size={15}/><input ref={searchRef} value={query} onChange={e => setQuery(e.target.value)} placeholder="Найти раздел"/><kbd>Ctrl K</kbd></div>}
        <nav className="nav-groups">
          {allGroups.map(group => <section className="nav-group" key={group.label}>
            {!collapsed && <div className="group-label">{group.label}</div>}
            {group.items.map(item => { const Icon = item.icon; return <button key={item.id} className={`nav-item ${active === item.id ? 'nav-active' : ''}`} title={collapsed ? item.name : undefined} onClick={() => openSection(item.id)}><Icon size={17} strokeWidth={1.8}/>{!collapsed && <><span>{item.name}</span>{active === item.id && <i className="nav-indicator"/>}</>}</button> })}
          </section>)}
        </nav>
        <div className="sidebar-bottom"><div className="connection-state"><span className={`signal ${data.systemReady ? 'signal-green' : 'signal-cyan'}`}/>{!collapsed && <span>{data.systemReady ? 'СИСТЕМА ПОДКЛЮЧЕНА' : 'СБОР ДАННЫХ'}</span>}</div></div>
      </aside>

      <main className="main-stage">
        <div className="stage-top">
          <div><div className="breadcrumb"><span>HERO / SYSTEM</span><ChevronRight size={13}/><span>{active.toUpperCase()}</span></div><h1>{active === 'Информация' ? 'Обзор системы' : active}</h1><p>{active === 'Информация' ? 'Состояние оборудования и быстрый доступ к обслуживанию' : (data.categorySubtitle || 'Рабочая область системного инструмента')}</p></div>
          <button className="icon-button help-button" title="Справка" onClick={() => send('action', { command: 'help' })}><CircleHelp size={17}/></button>
        </div>

        <div key={active} className={`page-transition page-enter-${pageDirection}`}>
          {active === 'Информация' ? <Overview data={data} openSection={openSection}/>
            : active === 'Диск' ? <DiskModule data={data}/>
              : ['AMD GPU', 'NVIDIA GPU'].includes(active) ? <GpuModule data={data} active={active}/>
                : active === 'Сеть & Пинг' ? <NetworkModule data={data}/>
                  : active === 'Бенчмарк' ? <BenchmarkModule data={data}/>
                    : active === 'Приложения' ? <ApplicationsModule data={data}/>
                      : active === 'Автозагрузка' ? <StartupModule data={data}/>
                        : active === 'Драйверы' ? <DriversModule data={data}/>
                          : active === 'Developer' ? <DeveloperModule data={data}/>
                            : active === 'Настройки' ? <SettingsModule data={data}/>
                              : ['Производительность', 'Приватность', 'Службы', 'Интерфейс', 'Обновления'].includes(active) ? <TweakModule active={active} data={data}/>
                                : <ModuleSurface active={active} data={data} openSection={openSection}/>}
        </div>
        <footer className="statusbar"><span><i className="status-dot"/>{data.status || 'Ожидание'}</span><span>ADMIN SESSION</span><span>LOCAL · {new Date().toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })}</span></footer>
      </main>

      <aside className="inspector">
        <div className="inspector-head"><span>СИСТЕМНАЯ СВОДКА</span><span className="live-label"><i/>LIVE</span></div>
        <div className="inspector-section">
          <span className="micro-label">ОПЕРАЦИОННАЯ СИСТЕМА</span>
          <strong>{data.osName}</strong>
          <small>Сборка {data.osBuild || '—'} · {data.osArchitecture || '—'}</small>
        </div>
        <div className="inspector-section">
          <span className="micro-label">ПРОЦЕССОР</span>
          <strong>{data.cpuName}</strong>
          <small>{data.threads || '—'} логических потоков</small>
        </div>
        <div className="inspector-section">
          <span className="micro-label">ОПЕРАТИВНАЯ ПАМЯТЬ</span>
          <strong>{data.totalRam || 'RAM —'}</strong>
          <small>{data.ramDetails && data.ramDetails !== '—' ? `Используется: ${data.ramDetails}` : 'Текущая загрузка RAM появится после сбора телеметрии'}</small>
        </div>
        <div className="inspector-section">
          <span className="micro-label">СИСТЕМА И ПЛАТА</span>
          <strong>{data.systemManufacturer} {data.systemModel}</strong>
          <small>{data.motherboard}</small>
          <small>BIOS {data.biosVersion}</small>
        </div>
        <div className="inspector-section">
          <span className="micro-label">ГРАФИЧЕСКИЙ АДАПТЕР</span>
          <strong>{data.gpuName}</strong>
          <small>{data.gpuVendor || 'Вендор не определён'} · {data.gpuType}</small>
          <small>VRAM {data.gpuMemory} · драйвер {data.gpuDriverVersion}</small>
        </div>
        <div className="inspector-section">
          <span className="micro-label">СИСТЕМНЫЙ ТОМ</span>
          <strong>{data.systemDrive?.name || data.systemDrive?.rootPath || 'Не определён'}</strong>
          <small>{data.systemDrive ? `${data.systemDrive.free} свободно из ${data.systemDrive.total}` : 'Сведения о системном томе недоступны'}</small>
        </div>
        <div className="inspector-bottom"><ShieldCheck size={15}/><span>Действия выполняются локально</span></div>
      </aside>
    </div>
    {toast && <div className="toast"><Sparkles size={15}/>{toast}<button onClick={() => setToast('')}><X size={14}/></button></div>}
  </div>
}

function Metric({ icon: Icon, label, value, detail, history = [], color = 'blue' }) {
  const points = history.length > 1
    ? history.slice(-35).map((sample, index, values) => `${(index / (values.length - 1)) * 300},${38 - (Math.max(0, Math.min(100, sample)) / 100) * 34}`).join(' ')
    : ''
  const area = points ? `0,40 ${points} 300,40` : ''
  return <div className={`metric metric-${color}`}><div className="metric-head"><span><Icon size={15}/>{label}</span><span className="metric-live">LIVE</span></div><div className="metric-value">{value}<small>{detail}</small></div><svg className="sparkline" viewBox="0 0 300 40" preserveAspectRatio="none" aria-hidden="true">{points && <><polygon points={area} className="spark-area"/><polyline points={points} className="spark-line"/></>}</svg></div>
}

function Overview({ data, openSection }) {
  return <div className="overview-grid">
    <section className="overview-intro panel panel-main">
      <div className="panel-kicker"><span className="live-pulse"/>SYSTEM HEALTH</div>
      <div className="health-row"><div className="health-meter" style={{ '--score': `${data.health ?? 0}%` }}><span>{data.health ?? '—'}</span><small>СОСТОЯНИЕ</small></div><div className="health-summary"><h2>{data.healthText || 'Анализ состояния системы'}</h2><p>{data.activeTweaks ?? 0} активных конфигураций <b>·</b> {data.disabledServices ?? 0} отключённых служб</p><div className="health-actions"><button className="action-primary" onClick={() => send('action', { command: 'analyzeSystem' })}><Activity size={15}/>Пересчитать состояние</button><button className="action-secondary" disabled={data.isGlobalBusy} onClick={() => send('action', { command: 'applyRecommendations' })}><ShieldCheck size={15}/>Применить рекомендации</button></div></div></div>
      <div className="quick-actions"><button onClick={() => send('action', { command: 'createRestorePoint' })}><ShieldCheck size={16}/>Точка восстановления</button><button onClick={() => send('action', { command: 'cleanTemp' })}><Sparkles size={16}/>Очистить временные файлы</button><button onClick={() => send('action', { command: 'cleanTempAndRam' })}><MemoryStick size={16}/>Temp + RAM</button><button onClick={() => openSection('Производительность')}><Wrench size={16}/>Настроить систему</button></div>
    </section>
    <Metric icon={Cpu} label="ПРОЦЕССОР" value={data.cpu == null ? '—' : `${Math.round(data.cpu)}%`} detail={data.cpu == null ? 'сбор телеметрии' : 'общая загрузка'} history={data.cpuHistory} color="blue"/>
    <Metric icon={MemoryStick} label="ОПЕРАТИВНАЯ ПАМЯТЬ" value={data.ram == null ? '—' : `${Math.round(data.ram)}%`} detail={data.ramDetails || 'сбор телеметрии'} history={data.ramHistory} color="violet"/>
    <section className="panel drives-panel"><div className="section-head"><div><div className="panel-kicker">ХРАНЕНИЕ</div><h2>Накопители</h2></div><button className="text-action" onClick={() => openSection('Диск')}>Открыть DiskTree<ArrowRight size={14}/></button></div>{data.drives?.length ? <div className="drive-list">{data.drives.slice(0, 4).map(d => <div className="drive-row" key={d.rootPath}><div className="drive-icon"><Disc3 size={17}/></div><div className="drive-meta"><strong>{d.displayName || d.rootPath}</strong><span>{d.availableFreeSpace} свободно · {d.totalSize}</span><div className="drive-track"><i style={{ width: `${d.usedPercentage}%` }}/></div></div><b>{Math.round(d.usedPercentage)}%</b></div>)}</div> : <div className="empty-inline"><HardDrive size={18}/><span>{data.driveStatus || 'Получаю инвентаризацию томов…'}</span></div>}</section>
    <section className="panel machine-panel"><div className="panel-kicker">АППАРАТНАЯ КОНФИГУРАЦИЯ</div><h2>{data.cpuName}</h2><div className="machine-tags"><span><Cpu size={14}/>{data.threads || '—'} потоков</span><span><MemoryStick size={14}/>{data.totalRam || 'RAM —'}</span></div><div className="hardware-row"><span>GPU</span><strong>{data.gpuName}</strong><button className="subtle-arrow" onClick={() => openSection(data.gpuVendor === 'AMD' ? 'AMD GPU' : data.gpuVendor === 'NVIDIA' ? 'NVIDIA GPU' : 'Драйверы')}><ArrowUpRight size={15}/></button></div></section>
    <section className="panel activity-panel"><div className="section-head"><div><div className="panel-kicker">ОБСЛУЖИВАНИЕ</div><h2>Инструменты</h2></div><span className="quiet-count">4 действия</span></div><div className="tool-grid"><button onClick={() => send('action', { command: 'sfcScan' })}><span><ShieldCheck size={17}/></span><b>SFC Scan</b><small>Проверка файлов Windows</small><ArrowRight size={14}/></button><button onClick={() => send('action', { command: 'dismRepair' })}><span><Wrench size={17}/></span><b>DISM Restore</b><small>Восстановление компонентов</small><ArrowRight size={14}/></button><button onClick={() => openSection('Сеть & Пинг')}><span><Network size={17}/></span><b>Сетевой стек</b><small>Диагностика и DNS</small><ArrowRight size={14}/></button><button onClick={() => openSection('Драйверы')}><span><MonitorCog size={17}/></span><b>Драйверы</b><small>Проверка оборудования</small><ArrowRight size={14}/></button></div></section>
  </div>
}

function SunburstChart({ slices = [], total }) {
  const [hovered, setHovered] = useState(null)
  const points = (angle, radius) => {
    const radians = (angle - 90) * Math.PI / 180
    return [110 + radius * Math.cos(radians), 110 + radius * Math.sin(radians)]
  }
  const arc = (start, sweep, inner, outer) => {
    if (!Number.isFinite(start) || !Number.isFinite(sweep) || sweep <= 0) return ''
    if (sweep >= 359.95) sweep = 359.95
    const a = points(start, outer), b = points(start + sweep, outer)
    const c = points(start + sweep, inner), d = points(start, inner)
    const large = sweep > 180 ? 1 : 0
    return `M ${a[0]} ${a[1]} A ${outer} ${outer} 0 ${large} 1 ${b[0]} ${b[1]} L ${c[0]} ${c[1]} A ${inner} ${inner} 0 ${large} 0 ${d[0]} ${d[1]} Z`
  }
  const inner = slices.filter(item => item.level === 1 && item.sweepAngle > .35)
  const outer = slices.filter(item => item.level === 2 && item.sweepAngle > .35)
  const picked = slices.find(item => item.path === hovered)
  return <div className="sunburst-layout">
    <div className="sunburst-visual">
      <svg viewBox="0 0 220 220" role="img" aria-label="Круговая карта занятого места на диске">
        <circle cx="110" cy="110" r="107" fill="none" stroke="rgba(255,255,255,.07)" strokeWidth="1"/>
        {outer.map((item, i) => <path key={`outer-${i}`} d={arc(item.startAngle, item.sweepAngle, 81, 105)} fill={item.color || '#55b9aa'} stroke="#0d1417" strokeWidth="1.4" className="sunburst-slice" onMouseEnter={() => setHovered(item.path)} onMouseLeave={() => setHovered(null)}><title>{item.name} · {item.size}</title></path>)}
        {inner.map((item, i) => <path key={`inner-${i}`} d={arc(item.startAngle, item.sweepAngle, 52, 78)} fill={item.color || '#55b9aa'} stroke="#0d1417" strokeWidth="1.6" className="sunburst-slice" onMouseEnter={() => setHovered(item.path)} onMouseLeave={() => setHovered(null)}><title>{item.name} · {item.size}</title></path>)}
        <circle cx="110" cy="110" r="48" fill="#11191d" stroke="rgba(151,176,194,.16)"/>
      </svg>
      <div className="sunburst-center"><span>{picked?.name || 'ЗАНЯТО'}</span><b>{picked?.size || total || '—'}</b></div>
    </div>
    <div className="sunburst-legend"><div className="panel-kicker">КРУГОВОЕ РАСПРЕДЕЛЕНИЕ</div><span>Внутреннее кольцо — папки верхнего уровня</span><span>Внешнее кольцо — их крупнейшие подпапки</span>{inner.slice(0, 6).map((item, i) => <div key={`legend-${i}`} className="sunburst-legend-row" onMouseEnter={() => setHovered(item.path)} onMouseLeave={() => setHovered(null)}><i style={{ background: item.color || '#55b9aa' }}/><b>{item.name}</b><small>{item.size}</small></div>)}</div>
  </div>
}

function DiskModule({ data }) {
  const disk = data.disk || { items: [], breadcrumbs: [] }
  const tab = disk.tab || 0
  return <div className="module-workspace">
    <div className="workspace-toolbar">
      <div className="disk-toolbar-main">
        <div className="segmented-tabs"><button className={tab === 0 ? 'segment-active' : ''} onClick={() => send('diskTab', { index: 0 })}>DiskTree</button><button className={tab === 1 ? 'segment-active' : ''} onClick={() => send('diskTab', { index: 1 })}>Очистка</button></div>
        {tab === 0 ? <div className="drive-picker"><span className="micro-label">ВЫБРАТЬ ТОМ</span><div className="drive-picker-list">{data.drives?.map(d => <button key={d.rootPath} className={`drive-pill ${disk.path?.startsWith(d.rootPath) ? 'drive-pill-active' : ''}`} onClick={() => send('selectDrive', { path: d.rootPath })}><HardDrive size={15}/><span><b>{d.displayName || d.rootPath}</b><small>{d.availableFreeSpace} / {d.totalSize}</small></span></button>)}</div></div> : <div className="cleanup-toolbar-note">Выбери категории и проверь размер перед запуском очистки.</div>}
      </div>
      {tab === 0 ? <div className="disk-toolbar-actions"><button className="action-secondary refresh-action" disabled={!disk.canGoUp || disk.isScanning} onClick={() => send('diskUp')}><ArrowUpRight size={14}/>Вверх</button><button className="action-secondary refresh-action" disabled={disk.isScanning} onClick={() => send('diskRefresh')}><Activity size={14}/>Обновить</button><button className="action-secondary refresh-action" disabled={disk.isScanning} onClick={() => send('moduleAction', { command: 'chooseDiskFolder' })}>Выбрать папку…</button></div> : <button className="action-secondary refresh-action" disabled={disk.cleanup?.isScanning} onClick={() => send('moduleAction', { command: 'scanCleanup' })}><Activity size={14}/>Пересканировать</button>}
    </div>
    {tab === 1 ? <DiskCleanupModule data={data} /> : <>
    <section className="panel disk-browser">
      <div className="disk-browser-head"><div><div className="panel-kicker">DISKTREE / АНАЛИЗ ФАЙЛОВОЙ СИСТЕМЫ</div><h2>{disk.title || 'Выбери накопитель'}</h2></div><div className="disk-total">{disk.total || ''}</div></div>
      <div className="breadcrumbs">{disk.breadcrumbs?.map((crumb, i) => <React.Fragment key={crumb.path}>{i > 0 && <ChevronRight size={12}/>}<button className={i === disk.breadcrumbs.length - 1 ? 'crumb-current' : ''} onClick={() => send('diskNavigate', { path: crumb.path })}>{crumb.name}</button></React.Fragment>)}</div>
      <div className={`disk-explorer-body ${disk.slices?.length ? 'disk-explorer-with-sunburst' : ''}`}>
      {!!disk.slices?.length && <SunburstChart slices={disk.slices} total={disk.total}/>}
      <div className="disk-table-region"><div className="disk-table-head"><span>ИМЯ</span><span>РАЗМЕР</span><span>ДОЛЯ</span></div>
      <div className="disk-table-body">
        {disk.isScanning && <div className="empty-workspace"><span className="loader-ring"/>Сканирую содержимое выбранного расположения…</div>}
        {!disk.isScanning && disk.items?.length === 0 && <div className="empty-workspace"><HardDrive size={19}/>{data.driveStatus || 'Выбери том для анализа'}</div>}
        {!disk.isScanning && disk.items?.map((item, index) => <div key={`${item.path}-${index}`} className="disk-entry" role={item.isFolder ? 'button' : undefined} tabIndex={item.isFolder ? 0 : -1} onClick={() => item.isFolder && send('diskDrill', { path: item.path })} onKeyDown={event => { if (item.isFolder && (event.key === 'Enter' || event.key === ' ')) { event.preventDefault(); send('diskDrill', { path: item.path }) } }}>
          <span className="disk-entry-name">{item.isFolder ? <Layers3 size={15}/> : <Disc3 size={15}/>}<b>{item.name}</b></span><span className="disk-entry-size">{item.size}</span><span className="disk-share"><i><b style={{ width: `${Math.min(100, item.percent || 0)}%` }}/></i><small>{(item.percent || 0).toFixed(1)}%</small></span><span className="disk-entry-actions"><button title="Открыть в Проводнике" onClick={event => { event.stopPropagation(); send('diskAction', { action: 'open', path: item.path }) }}><ExternalLink size={12}/></button><button title="Скопировать путь" onClick={event => { event.stopPropagation(); send('diskAction', { action: 'copy', path: item.path }) }}><Copy size={12}/></button><button className="disk-delete" title="Переместить в Корзину" disabled={item.isSynthetic || data.isGlobalBusy} onClick={event => { event.stopPropagation(); send('diskAction', { action: 'delete', path: item.path }) }}><Trash2 size={12}/></button></span><ChevronRight size={14}/>
        </div>)}
      </div></div>
      </div>
    </section>
    <div className="workspace-note"><ShieldCheck size={14}/>DiskTree отображает фактический размер элементов. Системные папки не изменяются без отдельного действия.</div>
    </>}
  </div>
}

function DiskCleanupModule({ data }) {
  const cleanup = data.disk?.cleanup || { categories: [] }
  const riskNames = { Safe: 'БЕЗОПАСНО', Advanced: 'ТРЕБУЕТ ВНИМАНИЯ', Experimental: 'ЭКСПЕРИМЕНТАЛЬНО' }
  return <div className="disk-clean-workspace">
    <section className="panel cleanup-summary"><div><div className="panel-kicker">АНАЛИЗ КАТЕГОРИЙ</div><h2>{cleanup.isScanning ? 'Сканирую временные данные…' : 'Проверка перед очисткой'}</h2><p>Выбранные категории будут перепроверены перед удалением.</p></div><div className="cleanup-total"><span>ВЫБРАНО К УДАЛЕНИЮ</span><b>{cleanup.total || '—'}</b></div></section>
    <section className="panel cleanup-categories">{cleanup.categories?.map(item => <label className="cleanup-category" key={item.id}><input type="checkbox" checked={item.isSelected} disabled={cleanup.isScanning || item.bytes <= 0} onChange={event => send('cleanupTarget', { id: item.id, value: event.target.checked })}/><div className="cleanup-category-copy"><div><b>{item.title}</b><span className={`cleanup-risk risk-${item.risk?.toLowerCase()}`}>{riskNames[item.risk] || item.risk}</span></div><small>{item.description}</small></div><div className="cleanup-category-size"><b>{cleanup.isScanning ? '…' : item.size}</b><small>{item.files ? `${item.files} файлов` : 'нет данных'}</small></div></label>)}{!cleanup.categories?.length && <div className="empty-workspace"><HardDrive size={18}/>Категории очистки не загружены.</div>}</section>
    <div className="cleanup-footer"><span><ShieldCheck size={14}/>Очистка спросит подтверждение и запишет результат в историю изменений.</span><button className="action-primary" disabled={cleanup.isScanning || !cleanup.categories?.some(item => item.isSelected && item.bytes > 0)} onClick={() => send('moduleAction', { command: 'cleanSelectedCategories' })}><Trash2 size={14}/>Очистить · {cleanup.total || '0 Б'}</button></div>
  </div>
}

function RangeField({ label, value, min, max, step = 1, control, unit = 'МГц', disabled }) {
  const [localValue, setLocalValue] = useState(value)
  useEffect(() => setLocalValue(value), [value])
  const hasRange = Number.isFinite(Number(min)) && Number.isFinite(Number(max)) && Number(max) > Number(min)
  const hasValue = value != null && Number.isFinite(Number(value))
  const isDisabled = disabled || !hasRange || !hasValue
  const commit = () => { if (!isDisabled) send('gpuControl', { control, value: Number(localValue) }) }
  return <label className={`range-field ${isDisabled ? 'range-disabled' : ''}`}><span className="range-title">{label}<b>{hasValue ? `${Math.round(Number(localValue))} ${unit}` : '—'}</b></span><input type="range" min={hasRange ? min : 0} max={hasRange ? max : 0} step={step} value={hasValue ? Number(localValue) : 0} disabled={isDisabled} onChange={e => setLocalValue(Number(e.target.value))} onPointerUp={commit} onKeyUp={commit}/><span className="range-limits">{hasRange ? <><small>{min} {unit}</small><small>{max} {unit}</small></> : <small>Диапазон не сообщает драйвер</small>}</span></label>
}

function GpuModule({ data, active }) {
  const gpu = data.gpu || { adapters: [], checks: [] }
  const isAmd = active === 'AMD GPU'
  const adapter = gpu.adapters?.find(item => item.vendor?.toLowerCase().includes(isAmd ? 'amd' : 'nvidia')) || gpu.adapters?.[0]
  return <div className="module-workspace gpu-workspace">
    <section className="gpu-hero panel"><div className="gpu-hero-copy"><div className="panel-kicker"><span className="live-pulse"/>GPU DEVICE / {active.toUpperCase()}</div><h2>{adapter?.name || data.gpuName}</h2><p>{adapter ? `${adapter.vendor} · ${adapter.type} · ${adapter.memory}` : gpu.status || 'Сканирование графического адаптера'}</p></div><div className="gpu-driver"><span className="micro-label">ДРАЙВЕР</span><b>{adapter?.driver || 'Не сообщает драйвер'}</b><small>{adapter?.driverDate || 'Версия доступна после аудита'}</small></div><button className="action-secondary" onClick={() => send('gpuAction', { command: 'refreshAudit' })}><Activity size={14}/>Обновить аудит</button></section>
    <div className="gpu-columns">
      <section className="panel gpu-audit"><div className="section-head"><div><div className="panel-kicker">КОНФИГУРАЦИЯ</div><h2>Проверки адаптера</h2></div><span className="quiet-count">{gpu.checks?.length || 0} параметров</span></div><div className="audit-list">{gpu.checks?.length ? gpu.checks.map((item, index) => <div className="audit-row" key={`${item.title}-${index}`}><i className={`audit-state audit-${item.severity?.toLowerCase()}`}/><div><b>{item.title}</b><small>{item.details}</small></div><strong>{item.value}</strong></div>) : <div className="empty-inline"><Activity size={16}/>{gpu.status || 'Аудит ещё не выполнен'}</div>}</div></section>
      <section className="panel gpu-tuning"><div className="section-head"><div><div className="panel-kicker">РУЧНЫЕ ПАРАМЕТРЫ</div><h2>{isAmd ? 'Частотные лимиты' : 'Вендорные настройки'}</h2></div><button className="text-action" disabled={!isAmd || gpu.tuningBusy} onClick={() => send('gpuAction', { command: 'refreshTuning' })}>{gpu.tuningBusy && isAmd ? <><span className="loader-ring"/>Читаю из драйвера…</> : <>{isAmd ? 'Считать из драйвера' : 'Недоступно'}<ArrowRight size={13}/></>}</button></div>
        {isAmd ? <>
          {!gpu.tuningAvailable && <div className="tuning-empty"><MonitorCog size={17}/><span>{gpu.tuningStatus || 'Нажми «Считать из драйвера», чтобы проверить доступные диапазоны ADLX.'}</span></div>}
          <div className="slider-stack"><RangeField label="Минимальная частота ядра" value={gpu.minFrequency} min={gpu.minFrequencyLimit} max={gpu.maxFrequencyLimit} control="minFrequency" disabled={!gpu.tuningAvailable || gpu.tuningBusy}/><RangeField label="Максимальная частота ядра" value={gpu.maxFrequency} min={gpu.minFrequencyLimit} max={gpu.maxFrequencyLimit} control="maxFrequency" disabled={!gpu.tuningAvailable || gpu.tuningBusy}/>{gpu.powerLimitAvailable && <RangeField label="Лимит мощности" value={gpu.powerLimit} min={gpu.powerLimitMin} max={gpu.powerLimitMax} unit="%" control="powerLimit" disabled={gpu.tuningBusy}/>}{gpu.vramFrequencyAvailable && <RangeField label="Частота видеопамяти" value={gpu.vramFrequency} min={gpu.vramFrequencyMin} max={gpu.vramFrequencyMax} control="vramFrequency" disabled={gpu.tuningBusy}/>}</div>
          <div className="tuning-footer"><span>{gpu.tuningStatus}</span><div><button className="action-secondary" disabled={!gpu.tuningAvailable || gpu.tuningBusy} onClick={() => send('gpuAction', { command: 'reset' })}>Сбросить</button><button className="action-primary" disabled={!gpu.tuningAvailable || gpu.tuningBusy} onClick={() => send('gpuAction', { command: 'applyCore' })}>Применить ядро</button></div></div>
          {gpu.powerLimitAvailable && <button className="text-action apply-extra" onClick={() => send('gpuAction', { command: 'applyPower' })}>Применить лимит мощности</button>}{gpu.vramFrequencyAvailable && <button className="text-action apply-extra" onClick={() => send('gpuAction', { command: 'applyVram' })}>Применить частоту VRAM</button>}
        </> : <div className="tuning-empty"><MonitorCog size={17}/><span>Рычаги NVIDIA появятся только после подключения и проверки поддерживаемого API. Здесь не показываются параметры, которых нет в драйвере.</span></div>}
      </section>
    </div>
    <section className="panel gpu-system-links"><div><div className="panel-kicker">ПАРАМЕТРЫ WINDOWS</div><span>Системные страницы управления графикой</span></div><div><button className="action-secondary" onClick={() => send('gpuSystemSetting', { target: 'graphics' })}>Профиль GPU для приложений</button><button className="action-secondary" onClick={() => send('gpuSystemSetting', { target: 'display' })}>Дополнительные параметры дисплея</button><button className="action-secondary" onClick={() => send('gpuSystemSetting', { target: 'device-manager' })}>Диспетчер устройств</button></div></section>
    <section className="panel gpu-profiles"><div className="section-head"><div><div className="panel-kicker">ПРОФИЛИ WINDOWS</div><h2>Игровые приложения</h2></div><button className="action-secondary" onClick={() => send('gpuAction', { command: 'addProfile' })}>＋ Добавить EXE</button></div><div className="profile-list">{gpu.profiles?.length ? gpu.profiles.map(profile => <div className="profile-row" key={profile.path}><AppWindow size={15}/><div><b>{profile.name}</b><small>{profile.path}</small></div><span>{profile.preference}</span><button className="icon-button profile-remove" title="Удалить профиль" onClick={() => send('gpuProfileRemove', { path: profile.path })}><X size={13}/></button></div>) : <div className="empty-inline">Профили не добавлены. Они назначают игре предпочтительный GPU через параметры Windows.</div>}</div></section>
  </div>
}

function NetworkModule({ data }) {
  const network = data.network || { adapters: [], dnsServers: [] }
  return <div className="module-workspace network-workspace">
    <section className="network-device panel"><div><div className="panel-kicker">ФИЗИЧЕСКИЙ АДАПТЕР</div><h2>{network.selectedAdapterName || 'Адаптер не выбран'}</h2><p>{network.adapters?.find(item => item.id === network.selectedAdapterId)?.description || 'Выбери адаптер с маршрутом по умолчанию'}</p></div><div className="network-device-actions"><label className="adapter-select"><span>Сетевой интерфейс</span><select value={network.selectedAdapterId || ''} onChange={e => send('selectAdapter', { id: e.target.value })}>{network.adapters?.map(adapter => <option key={adapter.id} value={adapter.id}>{adapter.name}{adapter.isDefault ? ' · по умолчанию' : ''}</option>)}</select></label><button className="icon-button" title="Обновить список" onClick={() => send('networkAction', { command: 'refreshAdapters' })}><Activity size={16}/></button></div></section>
    <div className="network-columns"><section className="panel dns-panel"><div className="section-head"><div><div className="panel-kicker">DNS / LATENCY</div><h2>Проверка DNS-серверов</h2></div><button className="action-primary" disabled={network.isBenchmarking} onClick={() => send('networkAction', { command: 'dnsBenchmark' })}><AudioWaveform size={14}/>{network.isBenchmarking ? 'Измеряю…' : 'Измерить'}</button></div><div className="dns-table-head"><span>СЕРВЕР</span><span>АДРЕС</span><span>ОТКЛИК</span><span/></div><div className="dns-list">{network.dnsServers?.map(server => <div className={`dns-row ${server.isCurrent ? 'dns-current' : ''}`} key={server.id}><span className="dns-server"><b>{server.name}</b><small>{server.description}</small></span><span className="dns-address">{server.address}</span><span className={`dns-ping ${server.ping >= 0 && server.ping < 45 ? 'ping-good' : server.ping >= 45 && server.ping < 90 ? 'ping-mid' : server.ping >= 90 ? 'ping-high' : ''}`}>{server.pingText}</span><button className="dns-apply" disabled={server.isCurrent || !network.selectedAdapterId} onClick={() => send('applyDns', { id: server.id })}>{server.isCurrent ? 'ТЕКУЩИЙ' : 'Применить'}</button></div>)}</div></section>
      <section className="panel network-tools"><div className="panel-kicker">АДАПТЕР / СИСТЕМА</div><h2>Сетевые инструменты</h2><div className="network-state"><span>IP-адрес</span><b>{network.adapters?.find(item => item.id === network.selectedAdapterId)?.ip || '—'}</b></div><div className="network-state"><span>DNS</span><b>{network.adapters?.find(item => item.id === network.selectedAdapterId)?.dns || 'Автоматически'}</b></div><div className="nagle-row"><div><b>Низкая задержка TCP</b><small>{network.isNagleEnabled ? 'Nagle отключён' : 'Стандартные параметры Windows'}</small></div><button className={`state-toggle ${network.isNagleEnabled ? 'state-toggle-on' : ''}`} onClick={() => send('networkAction', { command: 'toggleNagle' })}><i/></button></div><button className="network-tool-button" onClick={() => send('networkAction', { command: 'resetDns' })}><span>Сбросить DNS на DHCP</span><ArrowRight size={14}/></button><button className="network-tool-button" onClick={() => send('networkAction', { command: 'flushDns' })}><span>Очистить DNS-кэш</span><ArrowRight size={14}/></button><button className="network-tool-button network-danger" onClick={() => send('networkAction', { command: 'resetWinsock' })}><span>Сбросить Winsock и TCP/IP</span><ArrowRight size={14}/></button></section></div>
    <section className="panel network-rescue"><div className="section-head"><div><div className="panel-kicker">RESCUE KIT / ОФЛАЙН-ДИАГНОСТИКА</div><h2>Восстановление сети</h2></div><span className="quiet-count">Действия выполняются только по нажатию</span></div><div className="rescue-actions"><button className="action-secondary" onClick={() => send('networkAction', { command: 'rescanPnp' })}><RefreshCw size={14}/>Пересканировать PnP</button><button className="action-secondary" onClick={() => send('networkAction', { command: 'installDriverFolder' })}><PackageOpen size={14}/>Установить INF из папки</button><button className="action-secondary" onClick={() => send('networkAction', { command: 'installLoopback' })}><Network size={14}/>Установить Loopback</button><button className="action-secondary" onClick={() => send('networkAction', { command: 'rndisHelp' })}><CircleHelp size={14}/>Мастер USB-модема</button><button className="action-secondary rescue-danger" onClick={() => send('networkAction', { command: 'hardResetStack' })}><Wrench size={14}/>Полный сброс сетевого стека</button></div></section>
  </div>
}

function TweakModule({ active, data }) {
  const tweaks = data.tweaks || []
  return <div className="module-workspace tweak-workspace">
    <div className="tweak-toolbar"><div><div className="panel-kicker">КОНФИГУРАЦИЯ WINDOWS</div><div className="tweak-count">{tweaks.length} параметров в разделе <b>{active}</b></div></div><div className="tweak-tools"><label className="tweak-search"><Search size={14}/><input placeholder="Поиск по параметрам" value={data.searchText || ''} onChange={e => send('tweakSearch', { value: e.target.value })}/></label>{data.hasPendingChanges && <><button className="action-secondary" onClick={() => send('tweakAction', { command: 'discard' })}>Отменить</button><button className="action-primary" disabled={data.isGlobalBusy} onClick={() => send('tweakAction', { command: 'apply' })}>Применить · {data.pendingCount}</button></>}</div></div>
    <div className="tweak-list">{tweaks.length ? tweaks.map(item => <article className={`tweak-row ${item.hasPending ? 'tweak-pending' : ''}`} key={item.id}><div className="tweak-main"><div className="tweak-icon"><Wrench size={15}/></div><div className="tweak-copy"><div className="tweak-title"><h2>{item.name}</h2><span className={`risk-tag risk-${item.risk === 'Опасно' ? 'high' : item.risk === 'Продвинутый' ? 'medium' : 'low'}`}>{item.risk}</span>{item.reboot && item.reboot !== 'Мгновенно' && <span className="reboot-tag">Перезапуск: {item.reboot}</span>}</div><p>{item.description}</p>{item.expanded && <div className="tweak-details">{item.userWhy && <p><b>Зачем:</b> {item.userWhy}</p>}{item.technicalDetails && <p><b>Технически:</b> {item.technicalDetails}</p>}</div>}</div><label className="toggle-control"><input type="checkbox" checked={item.targetState} disabled={data.isGlobalBusy} onChange={e => send('tweakTarget', { id: item.id, value: e.target.checked })}/><i/></label></div><div className="tweak-meta"><span className={item.currentState ? 'state-active' : ''}>{item.hasPending ? 'ИЗМЕНЕНИЕ ОЖИДАЕТ ПРИМЕНЕНИЯ' : item.currentState ? 'ВКЛЮЧЕНО' : 'ВЫКЛЮЧЕНО'}</span><button onClick={() => send('tweakExpand', { id: item.id })}>{item.expanded ? 'Скрыть детали' : 'Технические детали'}<ChevronDown size={12}/></button></div></article>) : <div className="panel empty-inline tweak-empty"><Wrench size={18}/>{data.searchText ? 'Совпадений нет' : `Для раздела «${active}» твики не найдены`}</div>}</div>
    <div className="workspace-note"><ShieldCheck size={14}/>Переключатели сначала формируют список изменений. Системные настройки меняются только после нажатия «Применить».</div>
  </div>
}

function BenchmarkModule({ data }) {
  const benchmark = data.benchmark || {}
  const baseline = benchmark.baseline
  const after = benchmark.after
  const format = (value, digits = 1, unit = '') => value == null ? '—' : `${Number(value).toFixed(digits)}${unit}`
  const metrics = [
    ['Средний FPS', 'AverageFps', 1, ''], ['1% low', 'OnePercentLowFps', 1, ''], ['0.1% low', 'PointOnePercentLowFps', 1, ''], ['P99 кадра', 'P99FrameTimeMs', 2, ' мс'],
    ['CPU игры', 'AverageGameCpuPercent', 1, '%'], ['RAM процесса', 'AverageGameWorkingSetMb', 0, ' МБ'], ['GPU игры', 'AverageGameGpuEnginePercent', 1, '%'], ['Температура GPU', 'AverageGpuTemperatureC', 1, ' °C'],
    ['Потребление GPU', 'AverageGpuPowerWatts', 0, ' Вт'], ['Частота GPU', 'AverageGpuClockMhz', 0, ' МГц'], ['VRAM игры', 'AverageGameDedicatedGpuMemoryMb', 0, ' МБ'], ['CPU системы', 'AverageSystemCpuPercent', 1, '%'],
  ]
  return <div className="module-workspace benchmark-workspace">
    <section className="benchmark-control panel"><div><div className="panel-kicker">PRESENTMON / ЗАПИСЬ КАДРОВ И ТЕЛЕМЕТРИИ</div><h2>Сравнение производительности</h2><p>Сними одинаковую сцену до и после применения настроек. Приложение соберёт FPS, frametime и доступные сенсоры.</p></div><div className="benchmark-controls"><label>Длительность<input type="number" min="15" max="600" step="15" value={benchmark.duration || 60} onChange={e => send('benchmarkDuration', { value: Number(e.target.value) })}/><small>секунд</small></label><button className="action-secondary" disabled={benchmark.isCapturing} onClick={() => send('benchmarkImport', { kind: 'baseline' })}>Импорт ДО</button><button className="action-primary" disabled={benchmark.isCapturing} onClick={() => send('benchmarkCapture', { kind: 'baseline' })}><Activity size={14}/>Замер ДО</button></div></section>
    {benchmark.isCapturing && <div className="capture-state"><span className="loader-ring"/>{benchmark.status}</div>}
    <div className="benchmark-pair">{[["ДО / BASELINE", baseline, 'baseline'], ["ПОСЛЕ / AFTER", after, 'after']].map(([label, result, kind]) => <section className={`panel benchmark-card ${result ? 'benchmark-ready' : ''}`} key={kind}><div className="section-head"><div><div className="panel-kicker">{label}</div><h2>{result?.ExecutableName || 'Замер не сохранён'}</h2></div><button className={kind === 'after' ? 'action-primary' : 'action-secondary'} disabled={benchmark.isCapturing} onClick={() => send('benchmarkCapture', { kind })}>{kind === 'after' ? 'Замер ПОСЛЕ' : 'Перезаписать'}<ArrowRight size={13}/></button></div>{result ? <><div className="benchmark-meta"><span>{result.SourceFileName}</span><span>{Number(result.SampleCount).toLocaleString('ru-RU')} кадров</span></div><div className="benchmark-stats">{metrics.slice(0,4).map(([title,key,digits,unit]) => <div key={key}><small>{title}</small><b>{format(result[key],digits,unit)}</b></div>)}</div><button className="text-action import-action" onClick={() => send('benchmarkImport', { kind })}>Загрузить CSV<ArrowRight size={12}/></button></> : <div className="empty-inline"><AudioWaveform size={17}/>Выбери режим замера. При захвате HeroTweaker попросит указать EXE игры.</div>}</section>)}</div>
    {baseline && after && <section className="panel benchmark-compare"><div className="section-head"><div><div className="panel-kicker">ИЗМЕРЕННЫЕ ДАННЫЕ</div><h2>До / после</h2></div><div className="fps-delta">{benchmark.averageDelta}</div></div><div className="compare-table"><div className="compare-head"><span>МЕТРИКА</span><span>ДО</span><span>ПОСЛЕ</span><span>ИЗМЕНЕНИЕ</span></div>{metrics.map(([title,key,digits,unit]) => { const before = baseline[key], current = after[key]; const delta = before == null || current == null ? null : current - before; return <div className="compare-row" key={key}><span>{title}</span><b>{format(before,digits,unit)}</b><b>{format(current,digits,unit)}</b><small>{delta == null ? 'Недоступно' : `${delta > 0 ? '+' : ''}${format(delta,digits,unit)}`}</small></div> })}</div><p className="sensor-note">Счётчики, недоступные драйверу или Windows, остаются прочерками. В сравнении используются только значения из CSV/телеметрии.</p></section>}
    <div className="benchmark-status"><span className="status-dot"/>{benchmark.status || 'Ожидание первого замера'}</div>
  </div>
}

function ApplicationsModule({ data }) {
  const apps = data.applications || { catalog: [], installed: [], leftovers: [], tab: 0 }
  const [catalogQuery, setCatalogQuery] = useState('')
  const catalog = (apps.catalog || []).filter(item => `${item.name} ${item.category} ${item.description}`.toLowerCase().includes(catalogQuery.toLowerCase()))
  const tabs = [['Каталог', 0], ['Установленные', 1], ['Остатки', 2]]
  return <div className="module-workspace service-workspace">
    <section className="panel module-toolbar"><div><div className="panel-kicker">ПРИЛОЖЕНИЯ / WINGET И ДЕИНСТАЛЛЯЦИЯ</div><h2>Программы и пакеты</h2></div><div className="segmented-tabs">{tabs.map(([label, index]) => <button key={index} className={apps.tab === index ? 'segment-active' : ''} onClick={() => send('appsTab', { index })}>{label}{index === 1 && apps.selectedUninstallCount > 0 ? ` · ${apps.selectedUninstallCount}` : ''}</button>)}</div></section>
    {apps.tab === 0 && <>
      <div className="module-toolbar plain-toolbar"><div className="module-count">{catalog.length} предложений в проверенном каталоге</div><label className="module-search"><Search size={14}/><input value={catalogQuery} onChange={event => setCatalogQuery(event.target.value)} placeholder="Имя, издатель или категория"/></label></div>
      <div className="catalog-grid">{catalog.map(item => <article className="panel catalog-item" key={item.id}><div className="app-glyph"><PackageOpen size={18}/></div><div className="catalog-copy"><span className="micro-label">{item.category || 'WINGET'}</span><h3>{item.name}</h3><p>{item.description}</p><small>{item.id}</small></div><button className="action-secondary" disabled={item.isInstalling || data.isGlobalBusy} onClick={() => send('moduleAction', { command: 'installCatalogApp', id: item.id })}>{item.isInstalling ? 'Установка…' : <><Download size={13}/>Установить</>}</button></article>)}{!catalog.length && <div className="empty-inline panel">В каталоге нет совпадений по этому запросу.</div>}</div>
    </>}
    {apps.tab === 1 && <>
      <div className="module-toolbar plain-toolbar"><div><div className="module-count">{apps.installed?.length || 0} программ в текущем представлении</div><small className="soft-note">Выбери несколько строк и запусти пакетное удаление одной командой.</small></div><div className="module-actions"><label className="module-search"><Search size={14}/><input value={apps.search || ''} onChange={event => send('appsSearch', { value: event.target.value })} placeholder="Фильтр по установленным"/></label><button className="action-secondary" onClick={() => send('moduleAction', { command: 'refreshApps' })}><RefreshCw size={14}/>Обновить</button><button className="action-primary" disabled={!apps.selectedUninstallCount || data.isGlobalBusy} onClick={() => send('moduleAction', { command: 'uninstallApps' })}><Trash2 size={14}/>Удалить выбранные · {apps.selectedUninstallCount || 0}</button></div></div>
      <section className="panel table-panel"><div className="app-table app-table-head"><span>ПРОГРАММА</span><span>ВЕРСИЯ</span><span>ИЗДАТЕЛЬ</span><span>РАЗМЕР</span><span>ВЫБРАТЬ</span></div><div className="app-table-body">{apps.installed?.map(item => <label className="app-table app-row" key={item.id}><span className="app-name"><b>{item.name}</b><small>{item.removalStatus || item.installDate || 'Приложение Windows'}</small></span><span>{item.version || '—'}</span><span className="ellipsize">{item.publisher || '—'}</span><span>{item.size || '—'}</span><input type="checkbox" checked={item.isSelected} onChange={event => send('appTarget', { kind: 'installed', id: item.id, value: event.target.checked })}/></label>)}{!apps.installed?.length && <div className="empty-workspace"><PackageCheck size={18}/>Нажми «Обновить», чтобы получить список программ.</div>}</div></section>
    </>}
    {apps.tab === 2 && <>
      <div className="module-toolbar plain-toolbar"><div><div className="module-count">Остатки и восстановление системных приложений</div><small className="soft-note">Остатки удаляются по выбранным путям. Восстановление Store и UWP повторно регистрирует системные пакеты.</small></div><div className="module-actions"><button className="action-secondary" disabled={data.isGlobalBusy} onClick={() => send('moduleAction', { command: 'scanLeftovers' })}><Search size={14}/>Найти остатки</button><button className="action-primary" disabled={!apps.selectedLeftoverCount || data.isGlobalBusy} onClick={() => send('moduleAction', { command: 'removeLeftovers' })}><Trash2 size={14}/>Удалить выбранные · {apps.selectedLeftoverCount || 0}</button></div></div>
      <section className="panel leftovers-list">{apps.leftovers?.length ? apps.leftovers.map(item => <label className="leftover-row" key={item.id}><input type="checkbox" checked={item.isSelected} onChange={event => send('appTarget', { kind: 'leftover', id: item.id, value: event.target.checked })}/><div><b>{item.program}</b><small>{item.path}</small></div><span>{item.kind}</span></label>) : <div className="empty-workspace"><PackageCheck size={18}/>{apps.hasLeftovers ? 'Найденные остатки не содержат доступных элементов.' : 'Сканирование не запускалось. После удаления программ проверь остаточные записи.'}</div>}</section>
      <div className="app-repair-actions"><button className="action-secondary" disabled={data.isGlobalBusy} onClick={() => send('moduleAction', { command: 'restoreStore' })}><Store size={14}/>Восстановить Microsoft Store</button><button className="action-secondary" disabled={data.isGlobalBusy} onClick={() => send('moduleAction', { command: 'restoreUwp' })}><PackageCheck size={14}/>Перерегистрировать системные UWP</button></div>
    </>}
  </div>
}

function StartupModule({ data }) {
  const startup = data.startup || { items: [] }
  return <div className="module-workspace service-workspace">
    <section className="panel module-toolbar"><div><div className="panel-kicker">АВТОЗАПУСК WINDOWS</div><h2>{startup.count || 0} записей <span className="inline-subtle">· {startup.highImpactCount || 0} с высоким влиянием</span></h2><p className="soft-note">Состояние меняется для выбранной записи в реестре или папке автозагрузки.</p></div><button className="action-secondary" disabled={startup.isScanning} onClick={() => send('moduleAction', { command: 'scanStartup' })}><RefreshCw size={14}/>{startup.isScanning ? 'Сканирование…' : 'Пересканировать'}</button></section>
    <section className="panel startup-list">{startup.items?.length ? startup.items.map(item => <article className="startup-row" key={item.id}><div className={`startup-led ${item.isEnabled ? 'led-on' : ''}`}/><div className="startup-copy"><div className="startup-title"><b>{item.name}</b><span className={`impact-tag ${item.impact?.includes('ВЫСОКОЕ') ? 'impact-high' : item.impact?.includes('СРЕДНЕЕ') ? 'impact-medium' : ''}`}>{item.impact}</span><small>{item.publisher}</small></div><small className="startup-command">{item.command || item.location}</small></div><label className="toggle-control"><input type="checkbox" checked={item.isEnabled} disabled={startup.isScanning} onChange={event => send('startupTarget', { id: item.id, value: event.target.checked })}/><i/></label></article>) : <div className="empty-workspace"><Activity size={18}/>{startup.isScanning ? 'Собираю записи Run и папок автозагрузки…' : 'Записи автозагрузки не загружены. Запусти пересканирование.'}</div>}</section>
  </div>
}

function DriversModule({ data }) {
  const drivers = data.drivers || { logs: [] }
  const commands = [
    ['driverSearch', 'Поиск в Windows Update', Search], ['driverBackup', 'Экспорт драйверов', Download],
    ['driverRestore', 'Импорт из резервной копии', PackageCheck], ['installVCRuntimes', 'Visual C++ Runtime', PackageOpen],
    ['rescanPnp', 'Пересканировать устройства', RefreshCw], ['installDriverFolder', 'Установка из каталога', Store],
  ]
  return <div className="module-workspace service-workspace">
    <section className="panel module-toolbar"><div><div className="panel-kicker">ДРАЙВЕРЫ / УСТРОЙСТВА И ПАКЕТЫ</div><h2>Обслуживание оборудования</h2><p className="soft-note">Обновления поступают через Windows Update. Резервные копии драйверов можно экспортировать и восстановить.</p></div><span className={`device-state ${drivers.isRunning ? 'device-busy' : ''}`}><i/>{drivers.isRunning ? 'ВЫПОЛНЯЕТСЯ' : 'ГОТОВ'}</span></section>
    <div className="driver-command-grid">{commands.map(([command, label, Icon]) => <button className="panel driver-command" key={command} disabled={drivers.isRunning} onClick={() => send('moduleAction', { command })}><span><Icon size={17}/></span><b>{label}</b><ArrowRight size={14}/></button>)}</div>
    {drivers.hasUpdates && <section className="panel update-driver-banner"><div><div className="panel-kicker">ОБНОВЛЕНИЯ НАЙДЕНЫ</div><p>{drivers.status}</p></div><button className="action-primary" disabled={drivers.isRunning} onClick={() => send('moduleAction', { command: 'installDriverUpdates' })}><Download size={14}/>Установить</button></section>}
    <section className="panel log-panel"><div className="section-head"><div><div className="panel-kicker">ЖУРНАЛ ВЫПОЛНЕНИЯ</div><h2>Операции с драйверами</h2></div><span className="quiet-count">{drivers.logs?.length || 0} событий</span></div><div className="module-log">{drivers.logs?.length ? drivers.logs.map((item, index) => <div className={`log-row log-${item.level?.toLowerCase()}`} key={`${item.time}-${index}`}><time>{item.time}</time><b>{item.prefix}</b><span>{item.message}</span></div>) : <div className="empty-workspace">Результаты команд будут отображаться здесь.</div>}</div></section>
  </div>
}

function DeveloperModule({ data }) {
  const dev = data.developer || { components: [], paths: [] }
  return <div className="module-workspace service-workspace">
    <section className="panel module-toolbar"><div><div className="panel-kicker">DEVELOPER DOCTOR / PATH</div><h2>Аудит среды разработки</h2><p className="soft-note">Проверка доступности компиляторов, SDK и записей переменной PATH.</p></div><button className="action-secondary" disabled={dev.isScanning} onClick={() => send('moduleAction', { command: 'scanDeveloper' })}><RefreshCw size={14}/>{dev.isScanning ? 'Сканирование…' : 'Пересканировать'}</button></section>
    <div className="dev-component-grid">{dev.components?.map(item => <article className="panel dev-component" key={item.id}><div className={`dev-status dev-${item.statusCode?.toLowerCase()}`}/><div><span className="micro-label">{item.status}</span><h3>{item.name}</h3><b>{item.version}</b><small>{item.path || item.details}</small></div></article>)}</div>
    <section className="panel dev-inspector"><div className="section-head"><div><div className="panel-kicker">РАЗРЕШЕНИЕ КОМАНДЫ</div><h2>Где находится исполняемый файл?</h2></div><span className="quiet-count">PATH · {dev.paths?.length || 0} записей · {dev.brokenPaths || 0} проблем</span></div><div className="dev-command-form"><label className="module-search"><Command size={14}/><input value={dev.inspectInput || ''} onChange={event => send('inspectInput', { value: event.target.value })} placeholder="Например: python, dotnet, git"/></label><button className="action-primary" disabled={dev.isScanning || !dev.inspectInput} onClick={() => send('moduleAction', { command: 'inspectCommand' })}>Найти в PATH<ArrowRight size={14}/></button></div>
      {dev.inspectResult && <div className={`inspect-result ${dev.inspectResult.found ? 'inspect-found' : 'inspect-missing'}`}><span>{dev.inspectResult.found ? 'НАЙДЕНО' : 'НЕ НАЙДЕНО'}</span><b>{dev.inspectResult.path}</b>{dev.inspectResult.version && <small>{dev.inspectResult.version}</small>}</div>}
      <div className="path-list">{dev.paths?.map((item, index) => <div className="path-row" key={`${item.scope}-${item.path}-${index}`}><span className={`path-state ${!item.exists ? 'path-missing' : item.duplicate ? 'path-duplicate' : ''}`}>{item.status}</span><small>{item.scope}</small><code>{item.path}</code></div>)}{!dev.paths?.length && <div className="empty-workspace">Записи PATH загрузятся после аудита окружения.</div>}</div>
      {dev.brokenPaths > 0 && <div className="dev-clean-action"><span>Найдено проблемных записей: <b>{dev.brokenPaths}</b></span><button className="action-secondary" disabled={dev.isScanning} onClick={() => send('moduleAction', { command: 'cleanBrokenPaths' })}><Trash2 size={14}/>Очистить PATH</button></div>}
    </section>
  </div>
}

function SettingsModule({ data }) {
  const settings = data.settings || { themes: [], history: [] }
  return <div className="module-workspace service-workspace settings-workspace">
    <section className="panel themes-panel"><div className="section-head"><div><div className="panel-kicker">ВНЕШНИЙ ВИД</div><h2>Цветовой профиль</h2></div><span className="quiet-count">Сохраняется в текущем сеансе</span></div><div className="theme-options">{settings.themes?.map(theme => <button key={theme.id} className={`theme-option ${settings.selectedThemeId === theme.id ? 'theme-selected' : ''}`} onClick={() => send('themeSelect', { id: theme.id })}><i style={{ background: theme.color }}/><span><b>{theme.name}</b><small>{theme.description}</small></span>{settings.selectedThemeId === theme.id && <span className="theme-check">АКТИВЕН</span>}</button>)}</div></section>
    <section className="panel updates-panel"><div className="section-head"><div><div className="panel-kicker">SELF UPDATE</div><h2>Обновление HeroTweaker</h2></div><span className="version-chip">v{data.version}</span></div><div className="update-status">{settings.updateStatus || 'Состояние обновлений пока не проверено.'}</div>{settings.isUpdateInstalling && <div className="progress-track"><i style={{ width: `${settings.updateProgress || 0}%` }}/></div>}<div className="module-actions"><button className="action-secondary" disabled={settings.isUpdateChecking || settings.isUpdateInstalling} onClick={() => send('moduleAction', { command: 'checkUpdates' })}><RefreshCw size={14}/>{settings.updateActionText || 'Проверить обновления'}</button>{settings.hasUpdateAvailable && <><button className="action-primary" disabled={!settings.canInstallUpdate} onClick={() => send('moduleAction', { command: 'installUpdate' })}><Download size={14}/>Скачать и установить</button><button className="action-secondary" onClick={() => send('moduleAction', { command: 'openRelease' })}>Открыть релиз</button></>}</div></section>
    <section className="panel maintenance-panel"><div className="section-head"><div><div className="panel-kicker">СОСТОЯНИЕ И ВОССТАНОВЛЕНИЕ</div><h2>Обслуживание</h2></div><span className="quiet-count">DiskTree кэш · {settings.diskCache || '—'}</span></div><div className="maintenance-actions"><button className="action-secondary" onClick={() => send('moduleAction', { command: 'restorePoint' })}><ShieldCheck size={14}/>Создать точку восстановления</button><button className="action-secondary" onClick={() => send('moduleAction', { command: 'cleanDiskCache' })}><Trash2 size={14}/>Очистить кэш DiskTree</button></div></section>
    <section className="panel audit-panel"><div className="section-head"><div><div className="panel-kicker">ЖУРНАЛ ИЗМЕНЕНИЙ</div><h2>Транзакции конфигурации</h2></div><span className="quiet-count">{settings.history?.length || 0} записей</span></div><div className="audit-history">{settings.history?.map(item => <article className="history-row" key={item.id}><div className={`history-state ${item.isRolledBack ? '' : item.isApplied ? 'history-applied' : ''}`}/><div><b>{item.name}</b><small>{item.time} · {item.category} · {item.details}</small></div>{item.isApplied && !item.isRolledBack && <button className="text-action" disabled={data.isGlobalBusy} onClick={() => send('moduleAction', { command: 'rollback', id: item.id })}>Откатить<ArrowRight size={13}/></button>}</article>)}{!settings.history?.length && <div className="empty-workspace">Здесь появятся применённые изменения и доступные операции отката.</div>}</div></section>
  </div>
}

function ModuleSurface({ active, data, openSection }) {
  const icon = navGroups.flatMap(g => g.items).find(i => i.id === active)?.icon || MonitorCog
  const Icon = icon
  return <section className="module-placeholder panel">
    <div className="module-glyph"><Icon size={24}/></div><div className="panel-kicker">РАБОЧАЯ ОБЛАСТЬ</div><h2>{active}</h2><p>Этот раздел пока не подключён. Остальные системные модули остаются доступны из навигации.</p>
    <div className="module-status"><span className="status-dot"/>{data.status || 'Ожидание'}</div>
    <div className="module-actions"><button className="action-primary" onClick={() => openSection('Информация')}>К сводке<ArrowRight size={15}/></button></div>
  </section>
}

export default App;
