import React, { createContext, useContext, useState, useEffect, useCallback } from 'react';

type ThemeMode = 'light' | 'dark';

interface ThemeContextValue {
  resolvedMode: ThemeMode;
  toggleTheme: () => void;
}

const ThemeContext = createContext<ThemeContextValue>(null!);

function getSystemMode(): ThemeMode {
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

export function ThemeContextProvider({ children }: { children: React.ReactNode }) {
  const [override, setOverride] = useState<ThemeMode | null>(() => {
    const stored = localStorage.getItem('themeMode');
    return stored === 'light' || stored === 'dark' ? stored : null;
  });

  const [systemMode, setSystemMode] = useState<ThemeMode>(getSystemMode);

  useEffect(() => {
    const mq = window.matchMedia('(prefers-color-scheme: dark)');
    const handler = (e: MediaQueryListEvent) => setSystemMode(e.matches ? 'dark' : 'light');
    mq.addEventListener('change', handler);
    return () => mq.removeEventListener('change', handler);
  }, []);

  const resolvedMode = override ?? systemMode;

  const toggleTheme = useCallback(() => {
    setOverride(prev => {
      const next = (prev ?? systemMode) === 'dark' ? 'light' : 'dark';
      localStorage.setItem('themeMode', next);
      return next;
    });
  }, [systemMode]);

  return (
    <ThemeContext.Provider value={{ resolvedMode, toggleTheme }}>
      {children}
    </ThemeContext.Provider>
  );
}

export function useThemeContext() { return useContext(ThemeContext); }
