import { apiFetch } from './client';

export interface Bot {
  id: string;
  name: string;
  age: number | null;
  gender: string | null;
  characterDescription: string;
  evolvingPersonaPrompt: string;
  personaPushText: string | null;
  personaPushRemainingCycles: number;
  llmProviderPresetId: string | null;
  embeddingPresetId: string | null;
  hasTelegramToken: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface PersonaSnapshot {
  id: string;
  snapshotText: string;
  createdAt: string;
}

export interface CreateBotRequest {
  name: string;
  age?: number;
  gender?: string;
  characterDescription: string;
  llmProviderPresetId?: string;
  embeddingPresetId?: string;
  telegramBotToken?: string;
}

export interface UpdateBotRequest {
  name?: string;
  age?: number;
  gender?: string;
  characterDescription?: string;
  llmProviderPresetId?: string;
  embeddingPresetId?: string;
  telegramBotToken?: string;
}

export const botsApi = {
  list: (token: string) => apiFetch<Bot[]>('/api/bots', {}, token),

  get: (id: string, token: string) => apiFetch<Bot>(`/api/bots/${id}`, {}, token),

  create: (req: CreateBotRequest, token: string) =>
    apiFetch<Bot>('/api/bots', { method: 'POST', body: JSON.stringify(req) }, token),

  update: (id: string, req: UpdateBotRequest, token: string) =>
    apiFetch<Bot>(`/api/bots/${id}`, { method: 'PUT', body: JSON.stringify(req) }, token),

  delete: (id: string, token: string) =>
    apiFetch<void>(`/api/bots/${id}`, { method: 'DELETE' }, token),

  personaHistory: (id: string, token: string) =>
    apiFetch<PersonaSnapshot[]>(`/api/bots/${id}/persona-history`, {}, token),

  personaPush: (id: string, direction: string, token: string) =>
    apiFetch<Bot>(`/api/bots/${id}/persona-push`, { method: 'POST', body: JSON.stringify({ direction }) }, token),

  clearPersonaPush: (id: string, token: string) =>
    apiFetch<void>(`/api/bots/${id}/persona-push`, { method: 'DELETE' }, token),
};
