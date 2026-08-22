import axios from 'axios'

// ── DTOs (mirror AChat.Api/Dtos) ──────────────────────────────────────────────

export type Provider = 'Ollama' | 'OpenAI' | 'OpenAICompatible' | 'GoogleAIStudio'
export type Role = 'User' | 'Assistant' | 'System'

export interface User {
  id: number
  username: string
  displayName: string
  isAdmin: boolean
  timezone: string | null
  createdAt: string
  lastLoginAt: string | null
}

export interface Preset {
  id: number
  name: string
  provider: Provider
  baseEndpoint: string
  hasApiKey: boolean
  maskedApiKey: string | null
  generationModel: string
  utilityModel: string | null
  embeddingModel: string | null
  temperature: number | null
  maxTokens: number | null
  timeoutMs: number | null
  createdAt: string
  updatedAt: string
}

export interface PresetInput {
  name: string
  provider: Provider
  baseEndpoint: string
  apiKey?: string | null
  clearApiKey?: boolean
  generationModel: string
  utilityModel?: string | null
  embeddingModel?: string | null
  temperature?: number | null
  maxTokens?: number | null
  timeoutMs?: number | null
}

export interface Bot {
  id: number
  name: string
  avatarUrl: string | null
  corePersona: string
  speechStyle: string | null
  greeting: string | null
  presetId: number
  presetName: string
  provider: Provider
  generationModel: string
  createdAt: string
  updatedAt: string
  lastMessageAt: string | null
  lastMessagePreview: string | null
}

export interface BotInput {
  name: string
  avatarUrl?: string | null
  corePersona: string
  speechStyle?: string | null
  greeting?: string | null
  presetId: number
}

export interface Message {
  id: number
  role: Role
  content: string
  reasoning: string | null
  isComplete: boolean
  createdAt: string
}

export interface Interaction {
  id: number
  purpose: string
  botId: number | null
  botName: string | null
  userId: number
  username: string
  model: string
  endpoint: string
  promptTokens: number | null
  completionTokens: number | null
  totalTokens: number | null
  finishReason: string | null
  failed: boolean
  httpStatus: number | null
  createdAt: string
}

export interface InteractionDetail extends Omit<Interaction, 'purpose'> {
  purpose: string
  provider: Provider
  presetId: number
  requestBody: string
  reasoning: string | null
  responseContent: string | null
  failureReason: string | null
}

export interface Memory {
  id: number
  content: string
  kind: 'Fact' | 'Preference' | 'Event' | 'Relationship'
  importance: number
  isPinned: boolean
  source: 'Extraction' | 'UserEdited' | 'BotRemember'
  sourceMessageId: number | null
  supersededByMemoryId: number | null
  isDeleted: boolean
  createdAt: string
  lastRecalledAt: string | null
  recallCount: number
}

// ── Memories (§5.6) ───────────────────────────────────────────────────────────
export const listMemories = (botId: number, search?: string) =>
  http.get<Memory[]>('/api/memories', { params: { botId, search: search || undefined } }).then(r => r.data)
export const updateMemory = (id: number, body: { content?: string; kind?: string; importance?: number }) =>
  http.put<Memory>(`/api/memories/${id}`, body).then(r => r.data)
export const pinMemory = (id: number, pinned: boolean) =>
  http.put<Memory>(`/api/memories/${id}/pin`, { pinned }).then(r => r.data)
export const forgetMemory = (id: number) => http.delete(`/api/memories/${id}`)
export const forgetAllMemories = (botId: number) =>
  http.post(`/api/bots/${botId}/memories/forget-all`)

export interface ModelInfo { id: string; name: string | null }
export interface PresetTestResult { ok: boolean; message: string }

// ── Axios instance + JWT ──────────────────────────────────────────────────────

const TOKEN_KEY = 'achat_token'
const USER_KEY = 'achat_user'

export const getToken = (): string | null => localStorage.getItem(TOKEN_KEY)
export const setToken = (t: string | null) =>
  t ? localStorage.setItem(TOKEN_KEY, t) : localStorage.removeItem(TOKEN_KEY)
export const getStoredUser = (): User | null => {
  const raw = localStorage.getItem(USER_KEY)
  return raw ? (JSON.parse(raw) as User) : null
}
export const setStoredUser = (u: User | null) =>
  u ? localStorage.setItem(USER_KEY, JSON.stringify(u)) : localStorage.removeItem(USER_KEY)

const http = axios.create({ baseURL: '/' })

http.interceptors.request.use((config) => {
  const t = getToken()
  if (t) config.headers.Authorization = `Bearer ${t}`
  return config
})

http.interceptors.response.use(
  (r) => r,
  (err) => {
    if (err.response?.status === 401) {
      setToken(null)
      setStoredUser(null)
      if (location.pathname !== '/login') location.href = '/login'
    }
    return Promise.reject(err)
  },
)

export function apiError(e: unknown): string {
  const r = (e as { response?: { data?: { message?: string } } })?.response?.data
  return r?.message ?? (e as Error)?.message ?? 'Something went wrong'
}

// ── Auth ──────────────────────────────────────────────────────────────────────

export async function login(username: string, password: string, timezone?: string | null) {
  const { data } = await http.post<{ token: string; user: User }>('/api/auth/login', {
    username,
    password,
    timezone: timezone ?? null,
  })
  setToken(data.token)
  setStoredUser(data.user)
  return data.user
}

/** §5.9 — the user's IANA timezone (manual override; browser capture happens at login). */
export async function setTimezone(timezone: string): Promise<User> {
  const { data } = await http.patch<User>('/api/auth/me/timezone', { timezone })
  setStoredUser(data)
  return data
}

/** The device's IANA timezone, for browser capture at login (§5.9). */
export function browserTimezone(): string | null {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone ?? null } catch { return null }
}

export async function me(): Promise<User> {
  const { data } = await http.get<User>('/api/auth/me')
  return data
}

export async function logout() {
  try { await http.post('/api/auth/logout') } catch { /* token may already be dead */ }
  setToken(null)
  setStoredUser(null)
}

// ── Presets ───────────────────────────────────────────────────────────────────

export const listPresets = () => http.get<Preset[]>('/api/presets').then(r => r.data)
export const getPreset = (id: number) => http.get<Preset>(`/api/presets/${id}`).then(r => r.data)
export const createPreset = (p: PresetInput) => http.post<Preset>('/api/presets', p).then(r => r.data)
export const updatePreset = (id: number, p: PresetInput) => http.put<Preset>(`/api/presets/${id}`, p).then(r => r.data)
export const deletePreset = (id: number) => http.delete(`/api/presets/${id}`)
export const loadModels = (cfg: { provider: Provider; baseEndpoint: string; apiKey?: string | null; generationModel?: string }) =>
  http.post<ModelInfo[]>('/api/presets/load-models', cfg).then(r => r.data)
export const testPreset = (cfg: { provider: Provider; baseEndpoint: string; apiKey?: string | null; generationModel?: string }) =>
  http.post<PresetTestResult>('/api/presets/test', cfg).then(r => r.data)

// ── Bots ──────────────────────────────────────────────────────────────────────

export const listBots = () => http.get<Bot[]>('/api/bots').then(r => r.data)
export const recentBots = (n = 10) => http.get<Bot[]>('/api/bots/recent', { params: { n } }).then(r => r.data)
export const getBot = (id: number) => http.get<Bot>(`/api/bots/${id}`).then(r => r.data)
export const createBot = (b: BotInput) => http.post<Bot>('/api/bots', b).then(r => r.data)
export const updateBot = (id: number, b: BotInput) => http.put<Bot>(`/api/bots/${id}`, b).then(r => r.data)
export const deleteBot = (id: number) => http.delete(`/api/bots/${id}`)
export const getMessages = (id: number) => http.get<Message[]>(`/api/bots/${id}/messages`).then(r => r.data)

// ── Admin ─────────────────────────────────────────────────────────────────────

export const listUsers = () => http.get<User[]>('/api/admin/users').then(r => r.data)

// ── Interactions (§4.4) ───────────────────────────────────────────────────────

export const listInteractions = (params: { botId?: number; userId?: number; failed?: boolean; purpose?: string; take?: number }) =>
  http.get<Interaction[]>('/api/interactions', { params }).then(r => r.data)
export const getInteraction = (id: number) => http.get<InteractionDetail>(`/api/interactions/${id}`).then(r => r.data)

export { http }
