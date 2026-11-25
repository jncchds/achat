// Real-model check of the turn envelope (§4.7): drives a long conversation over SignalR and
// measures, from the §4.4 interaction log, how often the model returns a valid envelope, how the
// invalid ones look, what mood/memories/traits did, and whether stored facts are recalled.
//
//   ACHAT_BASE=http://localhost:5002 PRESET=qwen node scripts/e2e/real-envelope.mjs [phase...]
//   phases: setup chat mood traits stop memory recall (default: all)
// Needs a throwaway instance on 5002 and a real OpenAI-compatible endpoint (AGENTS.md).
// Results are written to $OUT (default ./real-envelope-out.json) for the report.
import fs from 'node:fs'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'

const BASE = process.env.ACHAT_BASE || 'http://localhost:5002'
const ENDPOINT = process.env.ENDPOINT || 'https://openrouter.ai/api/v1'
const GEN = process.env.GEN || 'google/gemma-4-31b-it:free'
const EMB = process.env.EMB || 'nvidia/nemotron-3.5-embed-1b:free'
// The key is read from this env file (a line OPENROUTER_API_KEY=...) and only ever sent to the preset API.
const KEYFILE = process.env.KEYFILE
const API_KEY = KEYFILE ? (fs.readFileSync(KEYFILE, 'utf8').match(/^OPENROUTER_API_KEY=(.+)$/m) || [])[1]?.trim() : undefined
const OUT = process.env.OUT || 'real-envelope-out.json'
const phases = process.argv.slice(2).length ? process.argv.slice(2) : ['setup', 'chat', 'mood', 'traits', 'stop']
const sleep = ms => new Promise(r => setTimeout(r, ms))
const state = fs.existsSync(OUT) ? JSON.parse(fs.readFileSync(OUT, 'utf8')) : {}
const save = () => fs.writeFileSync(OUT, JSON.stringify(state, null, 2))

async function api(path, opts = {}) {
  const res = await fetch(BASE + path, {
    ...opts,
    headers: { 'Content-Type': 'application/json', ...(opts.token ? { Authorization: `Bearer ${opts.token}` } : {}) },
  })
  const text = await res.text()
  if (!res.ok) throw new Error(`${path} → ${res.status}: ${text.slice(0, 300)}`)
  return text ? JSON.parse(text) : null
}

const token = (await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username: 'admin', password: 'admin' }) })).token

let conn
async function connect() {
  conn = new HubConnectionBuilder().withUrl(BASE + '/hubs/chat', { accessTokenFactory: () => token })
    .withAutomaticReconnect().configureLogging(LogLevel.Error).build()
  await conn.start()
}
await connect()

// One turn: returns { deltas, reasoning, completed|failed, ms }. If stopAfter is set, cancels after
// that many streamed characters.
// Free hosted models are rate limited: pace turns and, on a failed turn that looks like a 429 / upstream
// hiccup, back off and use "try again" (the same path a user would take).
async function turn(botId, message, opts = {}) {
  await sleep(Number(process.env.PACE_MS || 4000))
  let r = await turnOnce(botId, message, opts)
  for (let i = 0; i < 6 && r.ev?.type === 'failed' && /429|rate|temporarily|overloaded|timed out|502|503|provider/i.test(r.ev.message ?? ''); i++) {
    r.retries = (r.retries ?? 0) + 1
    console.log(`  (turn failed: ${(r.ev.message ?? '').slice(0, 80).replace(/\s+/g, ' ')} — retry ${i + 1})`)
    await sleep(15000 * (i + 1))
    const t = await turnOnce(botId, null, { ...opts, retry: true })
    t.retries = r.retries; r = t
  }
  return r
}

async function turnOnce(botId, message, { stopAfter, retry } = {}) {
  const r = { deltas: '', reasoning: '', ev: null, firstMs: null }
  const t0 = Date.now()
  let done = false, stopped = false
  const on = (n, f) => conn.on(n, f)
  conn.off('chat.delta'); conn.off('chat.reasoning'); conn.off('chat.completed'); conn.off('chat.failed'); conn.off('chat.stopped')
  on('chat.delta', m => {
    if (m.botId && m.botId !== botId) return
    r.firstMs ??= Date.now() - t0
    r.deltas += m.delta
    if (stopAfter && !stopped && r.deltas.length >= stopAfter) { stopped = true; conn.invoke('StopAsync', botId).catch(e => r.stopErr = String(e)) }
  })
  on('chat.reasoning', m => { r.reasoning += m.delta })
  on('chat.completed', m => { r.ev = { type: 'completed', ...m }; done = true })
  on('chat.failed', m => { r.ev = { type: 'failed', ...m }; done = true })
  on('chat.stopped', m => { r.ev = { type: 'stopped', ...m }; done = true })
  if (retry) await conn.invoke('RetryLastAsync', botId); else await conn.invoke('SendAsync', botId, message)
  while (!done && Date.now() - t0 < 300000) await sleep(150)
  r.ms = Date.now() - t0
  return r
}

async function interactions(botId, take = 500) {
  const list = await api(`/api/interactions?botId=${botId}&purpose=Chat&take=${take}`, { token })
  return list.reverse() // oldest first
}
async function detail(id) { return api(`/api/interactions/${id}`, { token }) }

// classify a raw model response
function classify(raw) {
  if (raw == null) return { kind: 'none' }
  let s = raw.trim()
  let fenced = false
  if (s.startsWith('```')) { fenced = true; s = s.replace(/^```[a-z]*\n?/i, '').replace(/```\s*$/, '').trim() }
  try {
    const o = JSON.parse(s.replace(/,(\s*[}\]])/g, '$1'))
    if (o && typeof o.reply === 'string') {
      const keys = Object.keys(o)
      return { kind: 'valid', fenced, replyFirst: keys[0] === 'reply', keys }
    }
    return { kind: 'json-no-reply', fenced }
  } catch (e) {
    return { kind: s.startsWith('{') ? 'broken-json' : 'plain', fenced }
  }
}

const check = (ok, label, extra = '') => console.log((ok ? '✓ ' : '✗ ') + label + (extra ? ' — ' + extra : ''))

// ── setup ────────────────────────────────────────────────────────────────────
if (phases.includes('setup')) {
  // One preset (chat + utility + embedding) for both bots. NB: a single-model local server (LM Studio)
  // swaps models on every embed/chat switch (~4 min each), so use a hosted endpoint for this script.
  const mk = async (name, embeddingModel, timeoutMs) => {
    const found = (await api('/api/presets', { token })).find(p => p.name === name)
    return found ?? api('/api/presets', { method: 'POST', token, body: JSON.stringify({
      name, provider: 'OpenAICompatible', baseEndpoint: ENDPOINT, apiKey: API_KEY, generationModel: GEN, utilityModel: GEN,
      embeddingModel, temperature: 0.7, maxTokens: 3000, timeoutMs }) })
  }
  const bot = async (name, presetId) => api('/api/bots', { method: 'POST', token, body: JSON.stringify({
    name, presetId,
    corePersona: 'A warm, observant friend with a dry sense of humour. She pays attention to the people in your life and remembers what matters to you.',
    speechStyle: 'Casual, short messages, a little wry. Asks one question at a time.',
    greeting: 'Hey, good to see you.' }) })
  const preset = await mk('OpenRouter free', EMB, 180000)
  state.botId = (await bot('Mira', preset.id)).id
  state.memBotId = (await bot('Remy', preset.id)).id
  save(); console.log('bots', state.botId, state.memBotId)
}
const botId = state.botId
const memBotId = state.memBotId

// ── chat: ≥ 24 ordinary turns with 3 planted facts and one repeat ────────────
if (phases.includes('chat')) {
  const script = [
    ['hi Mira! how are you today?'],
    ['My cat is called Pixel, she is a grey tabby and a terrible thief of socks.', 'fact'],
    ['what do you think makes a good morning?'],
    ['I just got back from a walk, the leaves are turning where I live.'],
    ['My sister Ann is getting married in June, in Lisbon.', 'fact'],
    ['do you ever get bored of chatting?'],
    ['Tell me something small and interesting.'],
    ['I started learning the cello last month, I am terrible but I love it.', 'fact'],
    ['what is a good snack for a long evening?'],
    ['ha, fair enough.'],
    ['Reminder, my cat Pixel stole another sock today.', 'repeat'],
    ['how do you stay patient when things are slow?'],
    ['I am thinking of making soup tonight.'],
    ['any tips for a lentil soup?'],
    ['nice. what are you up to?'],
    ['I need to pick a book for the train next week. Fiction or not?'],
    ['good call. do you have a favourite season?'],
    ['mine is autumn too, I think.'],
    ['what should I do on a rainy sunday?'],
    ['ok that sounds lovely.'],
    ['random question: tea or coffee?'],
    ['I switched to tea after lunch lately.'],
    ['how would you describe our chats so far?'],
    ['thanks, that is kind of you to say.'],
    ['goodnight for now!'],
  ]
  const rows = []
  for (const [msg, tag] of script) {
    const r = await turn(botId, msg)
    const e = r.ev
    rows.push({ msg, tag, type: e?.type, ms: r.ms, firstMs: r.firstMs, streamed: r.deltas, content: e?.content, mood: e?.mood, moodChanged: e?.moodChanged,
      remembered: e?.remembered, reasoningChars: r.reasoning.length })
    console.log(`· [${e?.type} ${Math.round(r.ms / 1000)}s] ${msg.slice(0, 40)} → ${(e?.content ?? e?.message ?? '').slice(0, 70)}${e?.remembered ? '  (remembered ' + e.remembered + ')' : ''}${e?.moodChanged ? '  MOOD→' + e.mood : ''}`)
  }
  state.chat = rows; save()
  const list = await interactions(botId)
  const cls = []
  for (const it of list.slice(-script.length)) {
    const d = await detail(it.id)
    cls.push({ id: it.id, finish: d.finishReason, notes: d.notes, c: classify(d.responseContent), raw: d.responseContent, reasoningChars: d.reasoning?.length ?? 0,
      completionTokens: d.completionTokens })
  }
  state.chatInteractions = cls; save()
  const count = k => cls.filter(x => x.c.kind === k).length
  console.log('valid', count('valid'), 'plain', count('plain'), 'broken-json', count('broken-json'), 'json-no-reply', count('json-no-reply'),
    'fenced', cls.filter(x => x.c.fenced).length, 'replyFirst', cls.filter(x => x.c.replyFirst).length, 'of', cls.length)
  const leaks = rows.filter(x => /"reply"|^\s*[{`]/.test(x.streamed) || /"reply"|^\s*\{/.test(x.content ?? ''))
  check(leaks.length === 0, 'no JSON artifacts in streamed deltas or stored reply', leaks.length ? leaks.length + ' turns' : '')
  const think = rows.filter(x => /<think>|<\/think>/.test(x.streamed + (x.content ?? '')))
  check(think.length === 0, 'no thinking leaked into reply')
  console.log('turns with reasoning:', cls.filter(x => x.reasoningChars > 0).length)
}

// ── recall far outside the history window ────────────────────────────────────
if (phases.includes('memory')) {
  // needs the stack started with Chat__HistoryTurns=4 so the facts fall out of the window
  const rows = []
  for (const m of [
    'My cat is called Pixel, she is a grey tabby and a terrible thief of socks.',
    'My sister Ann is getting married in June, in Lisbon.',
    'I started learning the cello last month, I am terrible but I love it.',
    'Reminder, my cat Pixel stole another sock today.',
    'what is a good snack for a long evening?',
    'ha, fair enough.',
    'I am thinking of making soup tonight.',
    'nice. what are you up to?',
  ]) {
    const r = await turn(memBotId, m)
    rows.push({ m, reply: r.ev?.content, remembered: r.ev?.remembered, type: r.ev?.type })
    console.log(`· [${r.ev?.type} ${Math.round(r.ms / 1000)}s] ${m.slice(0, 40)} remembered=${r.ev?.remembered}`)
  }
  state.memoryPlant = rows; save()
}

if (phases.includes('recall')) {
  const out = []
  for (const [q, needle] of [['Quick check — what is my cat called?', /pixel/i], ['Where is my sister getting married?', /lisbon/i], ['What instrument am I learning?', /cello/i]]) {
    const r = await turn(memBotId, q)
    const ok = needle.test(r.ev?.content ?? '')
    check(ok, 'recall: ' + q, (r.ev?.content ?? '').slice(0, 90) + ' recalled=' + JSON.stringify(r.ev?.recalledMemoryIds ?? r.ev?.recalled))
    out.push({ q, ok, reply: r.ev?.content, ev: r.ev })
  }
  state.recall = out
  state.memories = await api(`/api/memories?botId=${memBotId}`, { token })
  save()
  console.log('memories stored:', (state.memories.items ?? state.memories).length)
}

// ── mood: an emotionally clear exchange ──────────────────────────────────────
if (phases.includes('mood')) {
  const msgs = [
    'I have some bad news. My grandmother passed away this morning.',
    'She raised me, we were really close. I do not know what to do with myself.',
    'thank you for listening. I think I just need to sit with it.',
    'can we talk about something quiet and ordinary for a bit?',
    'what is the weather like where you would imagine it?',
    'I think I will make tea.',
  ]
  const rows = []
  for (const m of msgs) {
    const r = await turn(botId, m)
    rows.push({ m, mood: r.ev?.mood, changed: r.ev?.moodChanged, reply: r.ev?.content })
    console.log('·', m.slice(0, 40), '→ mood', r.ev?.mood, r.ev?.moodChanged ? '(changed)' : '')
  }
  state.moodRun = rows; save()
}

// ── traits: sustained creator conversation ───────────────────────────────────
if (phases.includes('traits')) {
  const msgs = [
    'You know, you have a way of making me laugh when I am low. I think we have a running joke about socks now.',
    'Honestly I would like you to be a bit more direct with me when I am overthinking things. Tell me straight.',
    'We should call the thing where you tease me about my cello "the squeak report".',
    'That worked, thank you. Being blunt with me really helps.',
    'Do you think our friendship has changed how you talk to me?',
    'I want you to remember that I always like a plain answer first, details after.',
    'ok, quick one: what is 12 times 12?',
    'And what do you think of the squeak report so far?',
  ]
  const rows = []
  for (const m of msgs) {
    const r = await turn(botId, m)
    rows.push({ m, traits: r.ev?.traitChanges, reply: r.ev?.content, ev: r.ev })
    console.log('·', m.slice(0, 50), '→ traits', JSON.stringify(r.ev?.traitChanges ?? null))
  }
  state.traitsRun = rows
  state.traitList = await api(`/api/bots/${botId}/persona/traits`, { token })
  state.versions = await api(`/api/bots/${botId}/persona/versions`, { token })
  save()
  console.log('traits now:', JSON.stringify(state.traitList).slice(0, 500))
}

// ── stop mid-stream and try again ────────────────────────────────────────────
if (phases.includes('stop')) {
  const r = await turn(botId, 'Tell me a long story about a lighthouse keeper, as long as you can.', { stopAfter: 40 })
  console.log('stop ->', r.ev?.type, 'canceled=', r.ev?.canceled, 'chars streamed=', r.deltas.length, 'content=', JSON.stringify((r.ev?.content ?? '').slice(0, 80)))
  state.stop = { ev: r.ev, streamed: r.deltas, err: r.stopErr }
  const r2 = await turn(botId, 'Never mind the story. Just say hi.')
  console.log('after stop ->', r2.ev?.type, (r2.ev?.content ?? '').slice(0, 80))
  state.afterStop = r2.ev
  save()
}

// ── "try again" after a completed turn, and a longer creator conversation for traits ─────────
if (phases.includes('retry')) {
  const before = await api(`/api/bots/${botId}/messages`, { token })
  const r = await turn(botId, null, { retry: true })
  console.log('retry ->', r.ev?.type, (r.ev?.content ?? r.ev?.message ?? '').slice(0, 80))
  state.retry = { type: r.ev?.type, content: r.ev?.content, streamed: r.deltas }
  save()
}

if (phases.includes('traits2')) {
  const msgs = [
    'I have been thinking about how these chats changed me. Before, I never told anyone about the cello. Now I tell you everything first. You are honestly my closest friend lately.',
    'You have changed too, I think. You used to be more guarded, and now you tease me all the time. I love that, please keep it going.',
    'Let us make it official: our running joke is the "squeak report", where you rate my cello practice out of ten, harshly.',
    'Today I practised for an hour. Give me the squeak report.',
    'Ha, harsh but fair. By the way, you have started being blunt with me when I spiral, and it genuinely helps. That is a lasting thing between us now.',
    'Would you say you have become a different kind of friend since we started talking?',
    'What would you tell yourself about how to treat me, in one sentence?',
    'Squeak report again: I played the same scale for twenty minutes.',
    'One more thing: you always ask me one question at a time, and I like that about you.',
    'Do you feel any different about yourself after all this?',
  ]
  const rows = []
  for (const m of msgs) {
    const r = await turn(botId, m)
    rows.push({ m, reply: r.ev?.content, traitChanges: r.ev?.traitChanges })
    console.log('·', m.slice(0, 50), '→ traits', JSON.stringify(r.ev?.traitChanges ?? null))
  }
  state.traits2 = rows
  state.traitList = await api(`/api/bots/${botId}/persona/traits`, { token })
  state.versions = await api(`/api/bots/${botId}/persona/versions`, { token })
  const list = await interactions(botId)
  const cls = []
  for (const it of list.slice(-msgs.length)) { const d = await detail(it.id); cls.push({ notes: d.notes, raw: d.responseContent }) }
  state.traits2Raw = cls
  save()
  console.log('traits now:', JSON.stringify(state.traitList).slice(0, 600))
  console.log('notes:', cls.map(c => c.notes).filter(Boolean))
}

await conn.stop()
