import { apiFetch } from './client';

export interface Conversation {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  lastMessageAt: string;
  messageCount: number;
}

export interface ConversationMessage {
  id: string;
  conversationId: string;
  role: 'user' | 'assistant';
  content: string;
  createdAt: string;
}

export const conversationsApi = {
  list: (botId: string, token: string) =>
    apiFetch<Conversation[]>(`/api/bots/${botId}/conversations`, {}, token),

  create: (botId: string, token: string) =>
    apiFetch<Conversation>(
      `/api/bots/${botId}/conversations`,
      { method: 'POST', body: JSON.stringify({}) },
      token,
    ),

  messages: (botId: string, conversationId: string, token: string) =>
    apiFetch<ConversationMessage[]>(
      `/api/bots/${botId}/conversations/${conversationId}/messages`,
      {},
      token,
    ),
};
