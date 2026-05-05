import api from './client';

export interface UserDto {
  id: string; username: string; email: string; role: string;
  telegramId: number | null; isActive: boolean; createdAt: string;
}
export interface CreateUserRequest { username: string; email: string; password: string; role?: string; }
export interface UpdateUserRequest {
  username?: string; email?: string; password?: string; role?: string;
  isActive?: boolean; telegramId?: number; clearTelegramId?: boolean;
}

export const adminApi = {
  getUsers: () => api.get<UserDto[]>('/admin/users').then(r => r.data),
  getUser: (id: string) => api.get<UserDto>(`/admin/users/${id}`).then(r => r.data),
  createUser: (data: CreateUserRequest) => api.post<UserDto>('/admin/users', data).then(r => r.data),
  updateUser: (id: string, data: UpdateUserRequest) => api.put<UserDto>(`/admin/users/${id}`, data).then(r => r.data),
  deleteUser: (id: string) => api.delete(`/admin/users/${id}`),
};
