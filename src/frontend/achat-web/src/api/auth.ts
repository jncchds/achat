import { apiFetch } from './client';

export interface AuthResponse { token: string; }

export const authApi = {
  register: (email: string, password: string) =>
    apiFetch<void>('/api/auth/register', {
      method: 'POST',
      body: JSON.stringify({ email, password }),
    }),

  login: (email: string, password: string) =>
    apiFetch<AuthResponse>('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email, password }),
    }),

  setTelegram: (telegramId: number, token: string) =>
    apiFetch<void>('/api/auth/telegram', {
      method: 'PUT',
      body: JSON.stringify({ telegramId }),
    }, token),
};
