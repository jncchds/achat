import { apiFetch } from './client';

export interface AccessRequest {
  id: string;
  botId: string;
  subjectType: string;
  subjectId: string;
  displayName: string | null;
  requestedAt: string;
  status: string;
}

export interface AccessListEntry {
  id: string;
  botId: string;
  subjectType: string;
  subjectId: string;
  status: string;
  addedAt: string;
}

export const accessApi = {
  listRequests: (botId: string, token: string) =>
    apiFetch<AccessRequest[]>(`/api/bots/${botId}/access-requests`, {}, token),

  approve: (botId: string, requestId: string, token: string) =>
    apiFetch<void>(`/api/bots/${botId}/access-requests/${requestId}/approve`, { method: 'POST' }, token),

  deny: (botId: string, requestId: string, token: string) =>
    apiFetch<void>(`/api/bots/${botId}/access-requests/${requestId}/deny`, { method: 'POST' }, token),

  listAccess: (botId: string, token: string) =>
    apiFetch<AccessListEntry[]>(`/api/bots/${botId}/access-list`, {}, token),

  removeAccess: (botId: string, entryId: string, token: string) =>
    apiFetch<void>(`/api/bots/${botId}/access-list/${entryId}`, { method: 'DELETE' }, token),
};
