import api from './client';

export interface LoginRequest { username: string; password: string; }
export interface LoginResponse { token: string; username: string; role: string; userId: string; }
export interface MeResponse { id: string; username: string; email: string; role: string; telegramId: number | null; }
export interface UpdateProfileRequest { username?: string; email?: string; currentPassword?: string; newPassword?: string; }

export const authApi = {
  login: (data: LoginRequest) => api.post<LoginResponse>('/auth/login', data).then(r => r.data),
  me: () => api.get<MeResponse>('/auth/me').then(r => r.data),
  updateProfile: (data: UpdateProfileRequest) => api.put('/auth/me', data),
};
