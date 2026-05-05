import api from './client';

export type ProviderType = 'Ollama' | 'OpenAI' | 'GoogleAI';

export interface PresetDto {
  id: string; name: string; providerType: ProviderType; providerUrl: string;
  hasApiToken: boolean; generationModel: string; embeddingModel: string | null;
  createdAt: string; updatedAt: string;
}
export interface CreatePresetRequest {
  name: string; providerType: ProviderType; providerUrl: string;
  apiToken?: string; generationModel: string; embeddingModel?: string;
}
export interface UpdatePresetRequest {
  name?: string; providerType?: ProviderType; providerUrl?: string;
  apiToken?: string; generationModel?: string; embeddingModel?: string;
}

export const presetsApi = {
  getAll: () => api.get<PresetDto[]>('/presets').then(r => r.data),
  get: (id: string) => api.get<PresetDto>(`/presets/${id}`).then(r => r.data),
  create: (data: CreatePresetRequest) => api.post<PresetDto>('/presets', data).then(r => r.data),
  update: (id: string, data: UpdatePresetRequest) => api.put<PresetDto>(`/presets/${id}`, data).then(r => r.data),
  delete: (id: string) => api.delete(`/presets/${id}`),
  getModels: (id: string) => api.get<string[]>(`/presets/${id}/models`).then(r => r.data),
};
