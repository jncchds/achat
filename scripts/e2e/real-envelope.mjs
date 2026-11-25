// Real-model check of the turn envelope (§4.7): drives a long conversation over SignalR and
// measures, from the §4.4 interaction log, how often the model returns a valid envelope, how the
// invalid ones look, what mood/memories/traits did, and whether stored facts are recalled.
//
//   ACHAT_BASE=http://localhost:5002 PRESET=qwen node scripts/e2e/real-envelope.mjs [phase...]
//   phases: setup chat recall mood traits stop (default: all)
// Needs a throwaway instance on 5002 and a real OpenAI-compatible endpoint (AGENTS.md).
// Results are written to $OUT (default ./real-envelope-out.json) for the report.
import fs from 'node:fs'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'

const BASE = process.env.ACHAT_BASE || 'http://localhost:5002'
const ENDPOINT = process.env.ENDPOINT || 'http://192.168.2.33:1234/v1'
const GEN = process.env.GEN || 'qwen/qwen3.8-27b'
const EMB = process.env.EMB || 'text-embedding-nomic-embed-text-v1.5'
const OUT = process.env.OUT || 'real-envelope-out.json'
const phases = process.argv.slice(2).length ? process.argv.slice(2) : ['setup', 'chat', 'recall', 'mood', 'traits', 'stop']
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
async function turn(botId, message, { stopAfter } = {}) {
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
  await conn.invoke('SendAsync', botId, message)
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
  let preset = (await api('/api/presets', { token })).find(p => p.name === 'Qwen local')
  if (!preset) preset = await api('/api/presets', { method: 'POST', token, body: JSON.stringify({
    name: 'Qwen local', provider: 'OpenAICompatible', baseEndpoint: ENDPOINT,
    generationModel: GEN, utilityModel: GEN, embeddingModel: EMB, temperature: 0.7 }) })
  const bot = await api('/api/bots', { method: 'POST', token, body: JSON.stringify({
    name: 'Mira', presetId: preset.id,
    corePersona: 'A warm, observant friend with a dry sense of humour. She pays attention to the people in your life and remembers what matters to you.',
    speechStyle: 'Casual, short messages, a little wry. Asks one question at a time.',
    greeting: 'Hey, good to see you.' }) })
  state.presetId = preset.id; state.botId = bot.id
  save(); console.log('preset', preset.id, 'bot', bot.id)
}
const botId = state.botId

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
if (phases.includes('recall')) {
  const out = []
  for (const [q, needle] of [['Quick check — what is my cat called?', /pixel/i], ['Where is my sister getting married?', /lisbon/i], ['What instrument am I learning?', /cello/i]]) {
    const r = await turn(botId, q)
    const ok = needle.test(r.ev?.content ?? '')
    check(ok, 'recall: ' + q, (r.ev?.content ?? '').slice(0, 90) + ' recalled=' + JSON.stringify(r.ev?.recalledMemoryIds ?? r.ev?.recalled))
    out.push({ q, ok, reply: r.ev?.content, ev: r.ev })
  }
  state.recall = out
  state.memories = await api(`/api/memories?botId=${botId}`, { token })
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

await conn.stop()
