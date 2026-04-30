import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuthProvider } from './contexts/AuthContext';
import { Layout } from './components/Layout';
import { ProtectedRoute } from './components/ProtectedRoute';
import { LoginPage } from './pages/LoginPage';
import { RegisterPage } from './pages/RegisterPage';
import { BotsPage } from './pages/BotsPage';
import { BotSettingsPage } from './pages/BotSettingsPage';
import { ChatPage } from './pages/ChatPage';
import { PresetsPage } from './pages/PresetsPage';
import { PersonaPage } from './pages/PersonaPage';
import { AccessRequestsPage } from './pages/AccessRequestsPage';
import { AccessListPage } from './pages/AccessListPage';
import { ProfilePage } from './pages/ProfilePage';

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, staleTime: 30_000 } },
});

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <BrowserRouter>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route path="/register" element={<RegisterPage />} />
            <Route element={<ProtectedRoute />}>
              <Route element={<Layout />}>
                <Route path="/bots" element={<BotsPage />} />
                <Route path="/bots/new" element={<BotSettingsPage />} />
                <Route path="/bots/:id/settings" element={<BotSettingsPage />} />
                <Route path="/bots/:id/chat" element={<ChatPage />} />
                <Route path="/bots/:id/persona" element={<PersonaPage />} />
                <Route path="/bots/:id/access-requests" element={<AccessRequestsPage />} />
                <Route path="/bots/:id/access-list" element={<AccessListPage />} />
                <Route path="/presets" element={<PresetsPage />} />
                <Route path="/profile" element={<ProfilePage />} />
              </Route>
            </Route>
            <Route path="*" element={<Navigate to="/bots" replace />} />
          </Routes>
        </BrowserRouter>
      </AuthProvider>
    </QueryClientProvider>
  );
}
