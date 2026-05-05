import api from './client';

export interface LlmInteractionDto {
  id: string; botId: string | null; botName: string | null; userId: string; username: string;
  presetId: string | null; presetName: string | null; endpoint: string; modelName: string;
  inputTokens: number; outputTokens: number; metadata: string | null; createdAt: string;
}
export interface LlmUsagePagedResult {
  items: LlmInteractionDto[]; totalCount: number; page: number; pageSize: number;
}

export const usageApi = {
  getMyUsage: (page = 1, pageSize = 20) =>
    api.get<LlmUsagePagedResult>(`/llm-usage?page=${page}&pageSize=${pageSize}`).then(r => r.data),
  getAllUsage: (page = 1, pageSize = 20) =>
    api.get<LlmUsagePagedResult>(`/admin/llm-usage?page=${page}&pageSize=${pageSize}`).then(r => r.data),
};
