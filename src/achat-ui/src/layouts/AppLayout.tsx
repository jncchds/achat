import { useEffect, useState } from 'react'
import { NavLink, Outlet } from 'react-router-dom'
import Sidebar, { SidebarDivider, SidebarSection } from '../components/Sidebar'
import Avatar from '../components/Avatar'
import { useAuth } from '../hooks/useAuth'
import { recentBots, type Bot } from '../api'

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
