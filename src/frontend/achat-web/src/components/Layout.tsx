import { useState } from 'react';
import { NavLink, Outlet, useMatch, useNavigate } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';

const TOP_NAV = [
  { icon: '🤖', label: 'Bots', path: '/bots' },
  { icon: '🔧', label: 'Presets', path: '/presets' },
  { icon: '👤', label: 'Profile', path: '/profile' },
];

const BOT_NAV = [
  { icon: '💬', label: 'Chat', segment: 'chat' },
  { icon: '⚙️', label: 'Settings', segment: 'settings' },
  { icon: '🧠', label: 'Persona', segment: 'persona' },
  { icon: '📬', label: 'Access Requests', segment: 'access-requests' },
  { icon: '📋', label: 'Access List', segment: 'access-list' },
];

export function Layout() {
  const [collapsed, setCollapsed] = useState(false);
  const { logout } = useAuth();
  const navigate = useNavigate();
  const botMatch = useMatch('/bots/:id/*');
  const botId = botMatch?.params.id;

  const handleLogout = () => {
    logout();
    navigate('/login');
  };

  return (
    <div className="app">
      <aside className={`sidebar${collapsed ? ' collapsed' : ''}`}>
        <div className="sidebar-header">
          <button
            className="toggle-btn"
            onClick={() => setCollapsed(c => !c)}
            title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          >
            {collapsed ? '▶' : '◀'}
          </button>
          {!collapsed && <span className="brand">AChat</span>}
        </div>

        <nav className="sidebar-nav">
          {TOP_NAV.map(item => (
            <NavLink
              key={item.path}
              to={item.path}
              className={({ isActive }) => `nav-item${isActive ? ' active' : ''}`}
              title={collapsed ? item.label : undefined}
            >
              <span className="nav-icon">{item.icon}</span>
              {!collapsed && <span className="nav-label">{item.label}</span>}
            </NavLink>
          ))}

          {/* Bot sub-nav — hidden when collapsed, visible only on bot routes */}
          {!collapsed && botId && (
            <>
              <div className="nav-divider" />
              <div className="nav-section-title">Current Bot</div>
              {BOT_NAV.map(item => (
                <NavLink
                  key={item.segment}
                  to={`/bots/${botId}/${item.segment}`}
                  className={({ isActive }) => `nav-item nav-item-sub${isActive ? ' active' : ''}`}
                >
                  <span className="nav-icon">{item.icon}</span>
                  <span className="nav-label">{item.label}</span>
                </NavLink>
              ))}
            </>
          )}
        </nav>

        <div className="sidebar-footer">
          <button
            className="nav-item logout-btn"
            onClick={handleLogout}
            title={collapsed ? 'Logout' : undefined}
          >
            <span className="nav-icon">🚪</span>
            {!collapsed && <span className="nav-label">Logout</span>}
          </button>
        </div>
      </aside>

      <main className="main">
        <Outlet />
      </main>
    </div>
  );
}
