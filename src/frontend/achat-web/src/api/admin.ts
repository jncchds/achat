import { apiFetch } from './client';

export interface AdminUser {
  id: string;
  email: string | null;
  isAdmin: boolean;
  createdAt: string;
}

export interface CreateUserRequest {
  email: string;
  password: string;
}

export const adminApi = {
  listUsers: (token: string) =>
    apiFetch<AdminUser[]>('/api/admin/users', {}, token),

  createUser: (req: CreateUserRequest, token: string) =>
    apiFetch<AdminUser>('/api/admin/users', {
      method: 'POST',
      body: JSON.stringify(req),
    }, token),

  deleteUser: (id: string, token: string) =>
    apiFetch<void>(`/api/admin/users/${id}`, { method: 'DELETE' }, token),
};
