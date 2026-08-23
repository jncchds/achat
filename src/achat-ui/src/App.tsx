import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import AppLayout from './layouts/AppLayout'
import Login from './pages/Login'
import Bots from './pages/Bots'
import BotMemories from './pages/BotMemories'
import BotSettings from './pages/BotSettings'
import Chat from './pages/Chat'
import Presets from './pages/Presets'
import Admin from './pages/Admin'
import Interactions from './pages/Interactions'
import Access from './pages/Access'
import { AuthProvider, useAuth } from './hooks/useAuth'

function Protected() {
  const { user, loading } = useAuth()
  if (loading) return <div className="app-layout"><main className="app-main"><p className="hint">Loading…</p></main></div>
  if (!user) return <Navigate to="/login" replace />
  return (
    <Routes>
      <Route element={<AppLayout />}>
        <Route path="/bots" element={<Bots />} />
        <Route path="/bots/:botId" element={<Chat />} />
        <Route path="/bots/:botId/memories" element={<BotMemories />} />
        <Route path="/bots/:botId/settings" element={<BotSettings />} />
        <Route path="/presets" element={<Presets />} />
        <Route path="/access" element={<Access />} />
        <Route path="/admin" element={<Admin />} />
        <Route path="/admin/interactions" element={<Interactions />} />
        <Route path="*" element={<Navigate to="/bots" replace />} />
      </Route>
    </Routes>
  )
}

export default function App() {
  return (
    <BrowserRouter>
      <AuthProvider>
        <Routes>
          <Route path="/login" element={<Login />} />
          <Route path="/*" element={<Protected />} />
        </Routes>
      </AuthProvider>
    </BrowserRouter>
  )
}
