import api from './client';

export interface ConversationDto {
  id: string; botId: string; botName: string; title: string; createdAt: string; updatedAt: string;
}
export interface MessageDto {
  id: string; role: 'user' | 'assistant' | 'system'; content: string; createdAt: string;
}

export const conversationsApi = {
  getAll: (botId: string) => api.get<ConversationDto[]>(`/bots/${botId}/conversations`).then(r => r.data),
  get: (id: string) => api.get<ConversationDto>(`/conversations/${id}`).then(r => r.data),
  create: (botId: string, title?: string) => api.post<ConversationDto>(`/bots/${botId}/conversations`, { title }).then(r => r.data),
  getMessages: (id: string) => api.get<MessageDto[]>(`/conversations/${id}/messages`).then(r => r.data),
  delete: (id: string) => api.delete(`/conversations/${id}`),
};
