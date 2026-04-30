import { createContext, useContext, useState, useCallback, type ReactNode } from 'react';

interface AuthState {
  token: string | null;
  userId: string | null;
  isAdmin: boolean;
}

interface AuthContextValue extends AuthState {
  login: (token: string, isAdmin: boolean) => void;
  logout: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

function parseUserId(token: string): string | null {
  try {
    const payload = JSON.parse(atob(token.split('.')[1]));
    return payload.sub ?? payload.nameid ?? null;
  } catch {
    return null;
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<AuthState>(() => {
    const token = localStorage.getItem('achat_token');
    const isAdmin = localStorage.getItem('achat_is_admin') === 'true';
    return { token, userId: token ? parseUserId(token) : null, isAdmin };
  });

  const login = useCallback((token: string, isAdmin: boolean) => {
    localStorage.setItem('achat_token', token);
    localStorage.setItem('achat_is_admin', String(isAdmin));
    setState({ token, userId: parseUserId(token), isAdmin });
  }, []);

  const logout = useCallback(() => {
    localStorage.removeItem('achat_token');
    localStorage.removeItem('achat_is_admin');
    setState({ token: null, userId: null, isAdmin: false });
  }, []);

  return (
    <AuthContext.Provider value={{ ...state, login, logout }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
