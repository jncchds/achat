// Phase 4 E2E: memory extraction → retrieval → viewer → embedding lock.
// Runs against a throwaway instance with fast extraction triggers.
const BASE = process.env.ACHAT_BASE || 'http://localhost:5002'
const sleep = ms => new Promise(r => setTimeout(r, ms))

async function api(path, opts = {}) {
  const res = await fetch(BASE + path, {
    ...opts,
    headers: { 'Content-Type': 'application/json', ...(opts.headers || {}), ...(opts.token ? { Authorization: `Bearer ${opts.token}` } : {}) },
  })
  const text = await res.text()
  if (!res.ok) throw new Error(`${path} → ${res.status}: ${text.slice(0, 300)}`)
  return text ? JSON.parse(text) : null
}

async function chat(token, botId, message) {
  const { spawn } = await import('child_process')
  const { fileURLToPath } = await import('node:url')
  const sigtest = fileURLToPath(new URL('./sigtest.mjs', import.meta.url))
  const out = await new Promise(resolve => {
    const p = spawn('node', [sigtest, token, String(botId), message],
      { stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, ACHAT_BASE: BASE } })
    let data = ''
    p.stdout.on('data', d => data += d)
    p.stderr.on('data', d => data += d)
    p.on('close', () => resolve(data))
  })
  const m = out.match(/COMPLETED (\{.*\})/)
  if (!m) throw new Error('no COMPLETED event:\n' + out.slice(-800))
  return JSON.parse(m[1])
}

let failures = 0
const check = (ok, label, extra = '') => {
  console.log((ok ? '✓' : '✗') + ' ' + label + (extra ? ' — ' + extra : ''))
  if (!ok) failures++
}

// ── setup ────────────────────────────────────────────────────────────────────
const login = await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username: 'admin', password: 'admin', timezone: 'Europe/Moscow' }) })
const token = login.token

const botName = process.env.PHASE4_BOT || 'Memo'
const bots = await api('/api/bots', { token })
let bot = bots.find(b => b.name === botName)
if (!bot) {
  bot = await api('/api/bots', {
    method: 'POST', token,
    body: JSON.stringify({
      name: botName, presetId: 1,
      corePersona: 'A warm, attentive friend who remembers details.',
      speechStyle: 'Casual, kind.',
      greeting: 'Hey! How are you?',
    }),
  })
}
console.log('bot:', bot.id, bot.name)

// ── plant durable facts ──────────────────────────────────────────────────────
const turns = [
  'Quick one: my dog is named Biscuit. He is a golden retriever.',
  'Oh, and I really love oat milk — I put it in everything.',
  'Last thing: I start a new job on Monday at a bookstore.',
  'Anyway — how is your day going?',
  'Tell me something fun that happened to you recently.',
  'Ha, that is nice. Anyway, back to my week… it has been a lot.',
  'I keep meaning to clean the kitchen. It is a disaster.',
  'Also the heating in my flat is making a weird noise.',
  'Anyway, enough about my apartment. What are you up to?',
  'I should get to bed. Goodnight!'
]
for (const t of turns) {
  await chat(token, bot.id, t)
  await sleep(2000)
}

// ── wait for the extraction worker to cover the planted facts ────────────────
let mems = []
for (let i = 0; i < 40; i++) {
  await sleep(5000)
  mems = await api(`/api/memories?botId=${bot.id}`, { token }) || []
  if (mems.some(m => /biscuit/i.test(m.content)) && mems.some(m => /oat/i.test(m.content))) break
}
check(mems.length >= 1, `extraction produced memories (${mems.length})`, mems.map(m => m.content.slice(0, 40)).join(' | ').slice(0, 160))

// ── retrieval: ask about a planted fact in a later turn ──────────────────────
await sleep(3000)
const recall = await chat(token, bot.id, 'Right — what is my dog called, and what milk do I like?')
console.log('── recall answer ──', recall.content.slice(0, 160))
const recallOk = /biscuit/i.test(recall.content) && /oat/i.test(recall.content)
check(recallOk, 'bot recalled planted facts (dog + milk)')

// ── the turn must have recalled memories (interaction log) ───────────────────
const inter = await api('/api/interactions?take=1', { token })
const last = (inter.find(x => x.purpose === 'Chat')) || inter[0]
check(last && true, `latest chat interaction #${last?.id}`)
// RecalledMemoryIds lives on the detail endpoint
const detail = await api(`/api/interactions/${last.id}`, { token })
const recalledIds = detail.recalledMemoryIds ? JSON.parse(detail.recalledMemoryIds) : []
check(recalledIds.length > 0, `interaction recorded recalled memory ids: [${recalledIds}]`)

// ── viewer API: edit, pin, forget ───────────────────────────────────────────
const dogMem = mems.find(m => /biscuit/i.test(m.content))
if (dogMem) {
  const edited = await api(`/api/memories/${dogMem.id}`, { method: 'PUT', token, body: JSON.stringify({ content: dogMem.content.trim() + ' (and he likes squeaky toys.)' }) })
  check(edited.source === 'UserEdited', 'edited memory is flagged UserEdited')
  const pinned = await api(`/api/memories/${dogMem.id}/pin`, { method: 'PUT', token, body: JSON.stringify({ pinned: true }) })
  check(pinned.isPinned === true, 'memory pinned')
  const milkMem = mems.find(m => /oat/i.test(m.content))
  if (milkMem) {
    await api(`/api/memories/${milkMem.id}`, { method: 'DELETE', token })
    const after = await api(`/api/memories?botId=${bot.id}`, { token })
    const still = after.find(m => m.id === milkMem.id)
    check(still?.isDeleted === true, 'forgotten memory soft-deleted (still inspectable)')
  }
}

// ── embedding model lock (§5.7): changing it while a bot uses the preset ─────
let lockOk = false, lockMsg = ''
try {
  await api('/api/presets/1', { method: 'PUT', token, body: JSON.stringify({
    name: 'Qwen local', provider: 'OpenAICompatible', baseEndpoint: 'http://192.168.2.33:1234/v1',
    generationModel: 'qwen/qwen3.8-27b', utilityModel: null,
    embeddingModel: 'some-other-model', temperature: 0.7,
  })})
} catch (e) {
  lockOk = /409/.test(String(e.message))
  lockMsg = String(e.message).slice(0, 120)
}
check(lockOk, 'embedding model change blocked with 409 (§5.7)', lockMsg)

console.log(failures === 0 ? '\nPHASE 4 E2E: PASS' : `\nPHASE 4 E2E: FAIL (${failures})`)
process.exit(failures === 0 ? 0 : 1)
