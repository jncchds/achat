import { apiFetch } from './client';

export interface Preset {
  id: string;
  name: string;
  provider: number;
  baseUrl: string | null;
  modelName: string;
  embeddingModel: string | null;
  parametersJson: string | null;
  hasApiKey: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface CreatePresetRequest {
  name: string;
  provider: number;
  apiKey?: string;
  baseUrl?: string;
  modelName: string;
  embeddingModel?: string;
  parametersJson?: string;
}

export interface UpdatePresetRequest {
  name?: string;
  apiKey?: string;
  baseUrl?: string;
  modelName?: string;
  embeddingModel?: string;
  parametersJson?: string;
}

export const presetsApi = {
  list: (token: string) => apiFetch<Preset[]>('/api/presets', {}, token),

  get: (id: string, token: string) => apiFetch<Preset>(`/api/presets/${id}`, {}, token),

  create: (req: CreatePresetRequest, token: string) =>
    apiFetch<Preset>('/api/presets', { method: 'POST', body: JSON.stringify(req) }, token),

  update: (id: string, req: UpdatePresetRequest, token: string) =>
    apiFetch<Preset>(`/api/presets/${id}`, { method: 'PUT', body: JSON.stringify(req) }, token),

  delete: (id: string, token: string) =>
    apiFetch<void>(`/api/presets/${id}`, { method: 'DELETE' }, token),
};
