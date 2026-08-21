import { useState } from 'react'
import { useTheme, type Theme } from '../hooks/useTheme'

interface SidebarProps {
  children: React.ReactNode
  bottomChildren?: React.ReactNode
}

/**
 * App sidebar shell (§9.1, §9.1a): pinned top block (wordmark + version + stage),
 * scrolling middle (contextual nav + recent bots), pinned bottom (user / theme / sign out).
 */
export default function Sidebar({ children, bottomChildren }: SidebarProps) {
  const [collapsed, setCollapsed] = useState(() => localStorage.getItem('sidebarCollapsed') === 'true')
  const { theme, setTheme } = useTheme()

  const toggle = () => {
    const next = !collapsed
    setCollapsed(next)
    localStorage.setItem('sidebarCollapsed', String(next))
  }

  const nextTheme: Theme = theme === 'light' ? 'dark' : theme === 'dark' ? 'system' : 'light'
  const themeIcon = theme === 'light' ? '☀' : theme === 'dark' ? '🌙' : '⬡'
  const themeLabel = `Theme: ${theme}`

  return (
    <aside className={`app-sidebar ${collapsed ? 'collapsed' : 'expanded'}`}>
      <div className="sidebar-fixed-top">
        <button
          className="sidebar-btn sidebar-toggle-btn"
          onClick={toggle}
          title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
        >
          <span className="s-icon">☰</span>
          {!collapsed && (
            <>
              <span className="s-label">AChat</span>
              <span className="version-pill" title="version">v{__APP_VERSION__}</span>
              <span className="beta-badge">ALPHA</span>
            </>
          )}
        </button>
      </div>

      <div className="sidebar-scroll">
        {children}
      </div>

      <div className="sidebar-fixed-bottom">
        {bottomChildren}
        <button className="sidebar-btn" onClick={() => setTheme(nextTheme)} title={themeLabel}>
          <span className="s-icon">{themeIcon}</span>
          <span className="s-label">{themeLabel}</span>
        </button>
      </div>
    </aside>
  )
}

interface SidebarBtnProps {
  icon: string
  label: string
  onClick?: () => void
  active?: boolean
  disabled?: boolean
  title?: string
  className?: string
}

export function SidebarBtn({ icon, label, onClick, active, disabled, title, className = '' }: SidebarBtnProps) {
  return (
    <button
      className={`sidebar-btn${active ? ' active' : ''}${className ? ' ' + className : ''}`}
      onClick={onClick}
      disabled={disabled}
      title={title ?? label}
    >
      <span className="s-icon">{icon}</span>
      <span className="s-label">{label}</span>
    </button>
  )
}

export function SidebarDivider() {
  return <div className="sidebar-divider" />
}

export function SidebarSection({ title }: { title: string }) {
  return <div className="sidebar-section-title"><span className="s-label">{title}</span></div>
}
