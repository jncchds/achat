import { useMemo, useState } from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ThemeProvider, createTheme, CssBaseline, Box } from '@mui/material';
import { AuthProvider } from './store/AuthContext';
import { ThemeContextProvider, useThemeContext } from './store/ThemeContext';
import { ProtectedRoute } from './components/ProtectedRoute';
import { NavBar } from './components/NavBar';
import LoginPage from './pages/LoginPage';
import BotsPage from './pages/BotsPage';
import BotSettingsPage from './pages/BotSettingsPage';
import BotEvolutionPage from './pages/BotEvolutionPage';
import BotAccessPage from './pages/BotAccessPage';
import BotChatLayout from './pages/BotChatLayout';
import ChatPage from './pages/ChatPage';
import PresetsPage from './pages/PresetsPage';
import LlmUsagePage from './pages/LlmUsagePage';
import BotUsagePage from './pages/BotUsagePage';
import AdminUsersPage from './pages/AdminUsersPage';

const queryClient = new QueryClient({ defaultOptions: { queries: { retry: 1, staleTime: 30_000 } } });

function AppLayout() {
  const [sidebarOpen, setSidebarOpen] = useState(true);

  return (
    <Box sx={{ display: 'flex', height: '100vh' }}>
      <NavBar open={sidebarOpen} onToggle={() => setSidebarOpen(o => !o)} />
      <Box component="main" sx={{ flexGrow: 1, overflow: 'auto', display: 'flex', flexDirection: 'column' }}>
        <Routes>
          <Route path="/bots" element={<BotsPage />} />
          <Route path="/bots/:botId/settings" element={<BotSettingsPage />} />
          <Route path="/bots/:botId/evolution" element={<BotEvolutionPage />} />
          <Route path="/bots/:botId/access" element={<BotAccessPage />} />
          <Route path="/bots/:botId/usage" element={<BotUsagePage />} />
          <Route path="/bots/:botId/chat" element={<BotChatLayout />}>
            <Route path=":conversationId" element={<ChatPage />} />
          </Route>
          <Route path="/presets" element={<PresetsPage />} />
          <Route path="/llm-usage" element={<LlmUsagePage />} />
          <Route path="/admin/users" element={<ProtectedRoute requireAdmin />}>
            <Route index element={<AdminUsersPage />} />
          </Route>
          <Route path="*" element={<Navigate to="/bots" replace />} />
        </Routes>
      </Box>
    </Box>
  );
}

function ThemedApp() {
  const { resolvedMode } = useThemeContext();
  const theme = useMemo(() => createTheme({ palette: { mode: resolvedMode } }), [resolvedMode]);

  return (
    <ThemeProvider theme={theme}>
      <CssBaseline />
      <AuthProvider>
        <BrowserRouter>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route element={<ProtectedRoute />}>
              <Route path="/*" element={<AppLayout />} />
            </Route>
          </Routes>
        </BrowserRouter>
      </AuthProvider>
    </ThemeProvider>
  );
}

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <ThemeContextProvider>
        <ThemedApp />
      </ThemeContextProvider>
    </QueryClientProvider>
  );
}
