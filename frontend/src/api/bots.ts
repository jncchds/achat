import api from './client';

export interface BotDto {
  id: string; ownerId: string; name: string; presetId: string; presetName: string; personality: string;
  hasTelegramToken: boolean; unknownUserReply: string; gender: string | null; language: string | null;
  evolutionIntervalHours: number | null;
  lastEvolvedAt: string | null; createdAt: string; updatedAt: string;
}
export interface CreateBotRequest { name: string; presetId: string; personality: string; telegramToken?: string; gender?: string; language?: string; }
export interface UpdateBotRequest {
  name?: string; presetId?: string; personality?: string; telegramToken?: string;
  unknownUserReply?: string; gender?: string; language?: string; evolutionIntervalHours?: number | null;
}
export interface BotAccessRequestDto {
  id: string; botId: string; requesterId: string; requesterUsername: string; status: string; createdAt: string;
}
export interface BotEvolutionLogDto {
  id: string;
  oldPersonality: string;
  newPersonality: string;
  reasoning: string;
  direction: string | null;
  evolvedAt: string;
}

export const botsApi = {
  getAll: () => api.get<BotDto[]>('/bots').then(r => r.data),
  get: (id: string) => api.get<BotDto>(`/bots/${id}`).then(r => r.data),
  create: (data: CreateBotRequest) => api.post<BotDto>('/bots', data).then(r => r.data),
  update: (id: string, data: UpdateBotRequest) => api.put<BotDto>(`/bots/${id}`, data).then(r => r.data),
  delete: (id: string) => api.delete(`/bots/${id}`),
  replacePersonality: (id: string, personality: string) => api.post(`/bots/${id}/personality`, { personality }),
  nudge: (id: string, direction?: string) => api.post(`/bots/${id}/nudge`, { direction }),
  getAccessRequests: (id: string) => api.get<BotAccessRequestDto[]>(`/bots/${id}/access-requests`).then(r => r.data),
  respondToAccessRequest: (botId: string, requestId: string, approve: boolean) =>
    api.put(`/bots/${botId}/access-requests/${requestId}`, { approve }),
  requestAccess: (id: string) => api.post<BotAccessRequestDto>(`/bots/${id}/request-access`).then(r => r.data),
  getEvolutionHistory: (id: string) => api.get<BotEvolutionLogDto[]>(`/bots/${id}/evolution-history`).then(r => r.data),
};
