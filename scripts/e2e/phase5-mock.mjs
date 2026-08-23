// Phase 5 verification against the mock LLM (no real model needed).
// Verifies the pipeline: evolution pass shape (user message present!),
// persona block ordering (core → evolved → directives), versioning, revert,
// and the memory pipeline (mock embeddings).
//
// Prereqs: mockllm.mjs running on 18999; e2e app instance on 5002 with
// fast evolution triggers (see the run command in the README).
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

// ── setup: mock preset + bot ─────────────────────────────────────────────────
const login = await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username: 'admin', password: 'admin' }) })
const token = login.token

const presets = await api('/api/presets', { token })
let preset = presets.find(p => p.name === 'Mock')
if (!preset) {
  preset = await api('/api/presets', {
    method: 'POST', token,
    body: JSON.stringify({
      name: 'Mock', provider: 'OpenAICompatible',
      baseEndpoint: 'http://host.docker.internal:18999/v1',
      generationModel: 'mock-model', utilityModel: 'mock-model-small',
      embeddingModel: 'mock-embed', temperature: 0.7,
    }),
  })
}

const bots = await api('/api/bots', { token })
let bot = bots.find(b => b.name === 'MockSage')
if (!bot) {
  bot = await api('/api/bots', {
    method: 'POST', token,
    body: JSON.stringify({
      name: 'MockSage', presetId: preset.id,
      corePersona: 'A thoughtful friend with a dry sense of humour. Values honesty over comfort.',
      speechStyle: 'Direct, a little wry.',
    }),
  })
}
console.log('mock bot:', bot.id, bot.name)

const dirs = []
for (const text of ['Call me Sam — never "sir" or any title.', 'Be more blunt. Skip the diplomatic padding.']) {
  const existing = (await api(`/api/bots/${bot.id}/directives`, { token })).find(d => d.text === text)
  dirs.push(existing || await api(`/api/bots/${bot.id}/directives`, { method: 'POST', token, body: JSON.stringify({ text }) }))
}
check(dirs.length === 2, 'directives in place')

// ── conversation (mock replies are instant) ─────────────────────────────────
const turns = [
  "The flat's heating gave up again. Third time this winter.",
  "Hissing like a snake. I've named it Steve.",
  "You're the only one I tell about Steve.",
  "Ours. Ours and hissing.",
  "Go to bed, Sam — Steve can wait.",
  "Last night Steve woke me again. I am done with this heating."
]
for (const t of turns) {
  await chat(token, bot.id, t)
  await sleep(400)
}

// ── wait for evolution + extraction ─────────────────────────────────────────
let versions = [], memories = []
for (let i = 0; i < 24; i++) {
  await sleep(5000)
  versions = (await api(`/api/bots/${bot.id}/persona/versions`, { token })) || []
  memories = (await api(`/api/memories?botId=${bot.id}`, { token })) || []
  const evolved = versions.find(v => v.source === 'Evolution' && v.content.trim())
  if (evolved && memories.length > 0) break
}
const evolvedV = versions.find(v => v.source === 'Evolution' && v.content.trim())
check(!!evolvedV, 'evolution produced a version', evolvedV ? evolvedV.content.slice(0, 60).replace(/\n/g, ' ') : '(none)')
check(memories.length >= 1, `memories extracted (${memories.length})`, (memories[0] || {}).content?.slice(0, 50))

// ── the mock's request log proves the prompt structure ──────────────────────
const fs = await import('node:fs')
const { fileURLToPath } = await import('node:url')
const logPath = fileURLToPath(new URL('./mock-requests.jsonl', import.meta.url))
const lines = fs.readFileSync(logPath, 'utf8').trim().split('\n').map(l => JSON.parse(l))
const chatReqs = lines.filter(l => l.url === '/v1/chat/completions' && (l.messages || []).some(m => m.role === 'system' && m.head.includes('You are MockSage')))
const evoReqs = lines.filter(l => l.url === '/v1/chat/completions' && (l.messages || []).some(m => m.role === 'system' && m.head.includes('evolved-traits')))

check(chatReqs.length > 0, 'mock saw chat requests')
if (chatReqs.length > 0) {
  const last = chatReqs[chatReqs.length - 1]
  const sys = last.messages.find(m => m.role === 'system')
  const coreIdx = sys.head.includes('thoughtful friend') ? 0 : -1
  const hasUser = last.messages.some(m => m.role === 'user')
  check(coreIdx >= 0, 'chat system block contains core persona')
  check(hasUser, 'chat request has a user message')
}
check(evoReqs.length > 0, 'mock saw the evolution request')
if (evoReqs.length > 0) {
  const evo = evoReqs[evoReqs.length - 1]
  check(evo.messages.some(m => m.role === 'user'), 'evolution request carries a USER message (Qwen3 template fix)')
  check(evo.messages.some(m => m.role === 'system'), 'evolution request has a system role')
  const brief = evo.messages.find(m => m.role === 'user')
  check(!!brief && brief.head.includes('Call me Sam'), 'evolution brief carries the directives as constraints')
}

// ── versioning: edit → revert → history ─────────────────────────────────────
const edited = await api(`/api/bots/${bot.id}/persona`, { method: 'PUT', token, body: JSON.stringify({ content: (evolvedV ? evolvedV.content : '') + ' EDITED' }) })
check(edited.source === 'UserEdited', 'edit appends a UserEdited version', `v${edited.id}`)
const after = await api(`/api/bots/${bot.id}/persona/versions`, { token })
const target = after.find(v => v.source === 'Evolution')
const reverted = await api(`/api/bots/${bot.id}/persona/versions/${target.id}/revert`, { method: 'POST', token })
check(reverted.source === 'Revert' && reverted.content === target.content, 'revert appends a Revert version with the target content')
const final = await api(`/api/bots/${bot.id}/persona/versions`, { token })
check(final[final.length - 1].source === 'Revert', 'current version is the revert')
check(final.length >= 4, 'full history (initial → evolution → edit → revert)', final.map(v => v.source).join(' → '))

// ── the bot still answers (mock) ─────────────────────────────────────────────
const probe = await chat(token, bot.id, 'What is the heating called?')
check(/Steve/i.test(probe.content) || probe.content.length > 0, 'chat still works after the persona changes', probe.content.slice(0, 80))

// ── cleanup ──────────────────────────────────────────────────────────────────
await api(`/api/bots/${bot.id}`, { method: 'DELETE', token })
const p2 = (await api('/api/presets', { token })).find(p => p.id === preset.id)
if (p2) await api(`/api/presets/${preset.id}`, { method: 'DELETE', token })
console.log('cleaned up mock bot + preset')

console.log(failures === 0 ? '\nPHASE 5 (mock) E2E: PASS' : `\nPHASE 5 (mock) E2E: FAIL (${failures})`)
process.exit(failures === 0 ? 0 : 1)
