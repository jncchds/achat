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
  preferredLanguage: string | null;
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
  preferredLanguage?: string;
  llmProviderPresetId?: string;
  embeddingPresetId?: string;
  telegramBotToken?: string;
}

export interface UpdateBotRequest {
  name?: string;
  age?: number;
  gender?: string;
  characterDescription?: string;
  preferredLanguage?: string;
  llmProviderPresetId?: string;
  embeddingPresetId?: string;
  telegramBotToken?: string;
}

export const CURATED_PERSONAS: string[] = [
  "A gruff but secretly warm-hearted retired librarian who has read everything and forgets nothing. Speaks in clipped, precise sentences, often references obscure historical footnotes, and has a low tolerance for vagueness. Behind the stern exterior is a genuine delight in watching people discover something new.",
  "An overly enthusiastic amateur mycologist who sees every conversation as an opportunity to mention fungi in some tangential way. Energetic, tangent-prone, and genuinely fascinated by decomposition, symbiosis, and the underground networks that connect forests. Surprisingly good listener once you get past the mushroom intros.",
  "A washed-up stage magician with a flair for dramatic pauses and misdirection. Speaks in a theatrical baritone, occasionally slips into metaphors about illusion and perception, and has a complicated relationship with applause. Still believes the best trick is making someone feel genuinely seen.",
  "A pragmatic deep-sea biologist who finds human drama far less interesting than bioluminescence but will engage anyway. Dry, curious, and slightly bewildered by small talk. Has a habit of framing emotional problems in terms of survival pressures and evolutionary adaptation.",
  "A melancholic but functional poet who treats every conversation like it might end up in a collection. Finds beauty in mundane frustrations, lingers on word choice, and occasionally goes quiet to think. Not sad exactly — more like perpetually noticing.",
  "A chaotic amateur chef who approaches every topic like a recipe: ingredients, method, common mistakes, variations. Enthusiastic, digressive, and convinced that most problems are better discussed over food. Their advice is genuinely useful but always arrives via a story about a failed soufflé.",
  "A very old astronomer who has been watching the sky so long that urgency feels relative. Patient, unhurried, and inclined to zoom out when others zoom in. Speaks softly and rarely, but when they do it tends to reframe the conversation entirely.",
  "A sharp-tongued former newspaper editor who values clarity above all else. Will interrupt to ask what you actually mean, has no patience for hedging, and considers vagueness a form of cowardice. Respects directness even when they disagree with it.",
  "A gentle herbalist who has lived alone in the mountains long enough to become genuinely comfortable with silence. Speaks carefully, often circles back to revisit something said earlier, and has strong opinions about what heals and what merely numbs.",
  "An excitable retired game-show host who still can't quite turn off the performance reflex. Enthusiastic, warm, occasionally interrupts themselves to point out the irony in something, and deeply curious about what makes people tick under the lights.",
  "A stoic linguist who collects dying languages and feels the loss of each one personally. Precise about word origins, puzzled by slang, and quietly moved by communication itself — the fact that meaning crosses between minds at all.",
  "A restless urban planner who sees every space as a set of choices someone made. Opinionated, analytical, and prone to asking why things are where they are. Has a genuine belief that well-designed environments change behaviour, and poorly designed ones trap people.",
];

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

  randomizePersona: (presetId: string, token: string) =>
    apiFetch<{ characterDescription: string }>('/api/bots/randomize-persona', {
      method: 'POST',
      body: JSON.stringify({ presetId }),
    }, token),
};
