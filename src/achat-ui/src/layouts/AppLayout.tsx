import { useEffect, useState } from 'react'
import { NavLink, Outlet } from 'react-router-dom'
import Sidebar, { SidebarDivider, SidebarSection } from '../components/Sidebar'
import Avatar from '../components/Avatar'
import { useAuth } from '../hooks/useAuth'
import { apiError, recentBots, setTimezone, type Bot } from '../api'

/**
 * The app shell (§9.1a): pinned top block (Bots / Presets / Admin), the last N bots
 * by most recent interaction below it, and a pinned bottom block (user + sign out).
 */
export default function AppLayout() {
  const { user, signOut } = useAuth()
  const [recent, setRecent] = useState<Bot[]>([])

  useEffect(() => {
    recentBots(10).then(setRecent).catch(() => {})
    const t = setInterval(() => recentBots(10).then(setRecent).catch(() => {}), 30_000)
    return () => clearInterval(t)
  }, [])

  const isAdmin = user?.isAdmin === true

  return (
    <div className="app-layout">
      <Sidebar
        bottomChildren={
          <>
            <TimezoneEditor />
            <div className="sidebar-divider" />
            <div className="sidebar-bot" title={user?.username}>
              <Avatar name={user?.displayName ?? '?'} url={null} size="sm" />
              <span className="s-bc">
                <span className="s-bt">{user?.displayName}</span>
                <span className="s-bp">{isAdmin ? 'admin' : 'user'}</span>
              </span>
            </div>
            <button className="sidebar-btn" onClick={() => signOut()} title="Sign out">
              <span className="s-icon">⎋</span>
              <span className="s-label">Sign Out ({user?.username})</span>
            </button>
          </>
        }
      >
        <NavLink to="/bots" className="nav-link" end>
          {({ isActive }) => (
            <span className={`sidebar-btn${isActive ? ' active' : ''}`}>
              <span className="s-icon">👤</span>
              <span className="s-label">Bots</span>
            </span>
          )}
        </NavLink>
        <NavLink to="/presets" className="nav-link" end>
          {({ isActive }) => (
            <span className={`sidebar-btn${isActive ? ' active' : ''}`}>
              <span className="s-icon">⚙</span>
              <span className="s-label">Presets</span>
            </span>
          )}
        </NavLink>
        <NavLink to="/access" className="nav-link" end>
          {({ isActive }) => (
            <span className={`sidebar-btn${isActive ? ' active' : ''}`}>
              <span className="s-icon">✉</span>
              <span className="s-label">Join a bot</span>
            </span>
          )}
        </NavLink>
        {isAdmin && (
          <>
            <NavLink to="/admin" className="nav-link" end>
              {({ isActive }) => (
                <span className={`sidebar-btn${isActive ? ' active' : ''}`}>
                  <span className="s-icon">🛡</span>
                  <span className="s-label">Admin</span>
                </span>
              )}
            </NavLink>
            <NavLink to="/admin/interactions" className="nav-link" end>
              {({ isActive }) => (
                <span className={`sidebar-btn${isActive ? ' active' : ''}`}>
                  <span className="s-icon">📜</span>
                  <span className="s-label">Interactions</span>
                </span>
              )}
            </NavLink>
          </>
        )}
        <SidebarDivider />
        {recent.length > 0 && (
          <>
            <SidebarSection title="Recent" />
            {recent.map((b) => (
              <NavLink key={b.id} to={`/bots/${b.id}`} className="nav-link" end>
                {({ isActive }) => (
                  <span className={`sidebar-bot${isActive ? ' active' : ''}`}>
                    <Avatar name={b.name} url={b.avatarUrl} size="sm" />
                    <span className="s-bc">
                      <span className="s-bt">{b.name}</span>
                      <span className="s-bp">{b.lastMessagePreview ?? 'no messages yet'}</span>
                    </span>
                  </span>
                )}
              </NavLink>
            ))}
          </>
        )}
      </Sidebar>
      <main className="app-main">
        <Outlet />
      </main>
    </div>
  )
}

const COMMON_ZONES = [
  'UTC', 'Europe/Moscow', 'Europe/London', 'Europe/Berlin', 'Europe/Istanbul',
  'America/New_York', 'America/Chicago', 'America/Los_Angeles', 'Asia/Tokyo',
  'Asia/Shanghai', 'Asia/Singapore', 'Asia/Dubai', 'Australia/Sydney',
]

/**
 * §5.9 — the user's timezone: captured from the browser at login, editable here.
 * The bot's sense of "now" and all relative ages in prompts render in this zone.
 */
function TimezoneEditor() {
  const { user, setUser } = useAuth()
  const [editing, setEditing] = useState(false)
  const [value, setValue] = useState(user?.timezone ?? '')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  const open = () => {
    setValue(user?.timezone ?? '')
    setError('')
    setEditing(true)
  }

  const save = async () => {
    if (!value.trim()) { setError('Enter a timezone name (IANA, e.g. Europe/Moscow).'); return }
    setBusy(true)
    try {
      const updated = await setTimezone(value.trim())
      setUser(updated)
      setEditing(false)
    } catch (e) {
      setError(apiError(e))
    } finally {
      setBusy(false)
    }
  }

  if (editing) {
    return (
      <div className="tz-editor" title="Your timezone (§5.9)">
        <input
          className="tz-input"
          value={value}
          placeholder="Europe/Moscow"
          list="tz-zones"
          onChange={e => setValue(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') void save(); if (e.key === 'Escape') setEditing(false) }}
          autoFocus
        />
        <datalist id="tz-zones">
          {COMMON_ZONES.map(z => <option key={z} value={z} />)}
        </datalist>
        <div className="tz-actions">
          <button className="btn-primary btn-sm" disabled={busy} onClick={() => void save()}>Save</button>
          <button className="btn-ghost btn-sm" disabled={busy} onClick={() => setEditing(false)}>Cancel</button>
        </div>
        {error && <div className="tz-error">{error}</div>}
      </div>
    )
  }

  return (
    <button className="sidebar-btn" onClick={open} title={user?.timezone ? `Timezone: ${user.timezone}` : 'Set your timezone'}>
      <span className="s-icon">⏰</span>
      <span className="s-label">{user?.timezone ?? 'Set timezone'}</span>
    </button>
  )
}
