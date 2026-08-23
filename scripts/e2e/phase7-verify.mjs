// Phase 7 verification: each NEW provider (OpenAI, Ollama, GoogleAIStudio) runs
// end-to-end against a live OpenAI-compatible endpoint (local Qwen), plus a real bot chat.
// All four families share one OpenAI-shaped implementation; this proves the new
// subclasses route correctly and reach a real server.
const BASE = process.env.BASE || 'http://localhost:5002'
const LIVE_ENDPOINT = 'http://192.168.2.33:1234/v1'
const GEN_MODEL = 'qwen/qwen3.8-27b'
const EMB_MODEL = 'text-embedding-nomic-embed-text-v2-moe'

let token = ''
async function api(path, { method = 'GET', body } = {}) {
  const r = await fetch(BASE + path, {
    method, headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
    body,
  })
  const text = await r.text()
  try { return JSON.parse(text) } catch { return text }
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

// ── login ──
const login = await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username: 'admin', password: 'admin' }) })
token = login.token
if (!token) { console.error('login failed', JSON.stringify(login).slice(0, 200)); process.exit(1) }

const createdPresets = [], createdBots = []
async function cleanup() {
  for (const id of createdBots) await fetch(`${BASE}/api/bots/${id}`, { method: 'DELETE', headers: { authorization: `Bearer ${token}` } }).catch(() => {})
  for (const id of createdPresets) await fetch(`${BASE}/api/presets/${id}`, { method: 'DELETE', headers: { authorization: `Bearer ${token}` } }).catch(() => {})
}

const NEW_PROVIDERS = ['OpenAI', 'Ollama', 'GoogleAIStudio']

for (const provider of NEW_PROVIDERS) {
  // 1) test-connection (load-models) via a draft config — proves provider → live endpoint path
  const lm = await api('/api/presets/load-models', {
    method: 'POST',
    body: JSON.stringify({ baseEndpoint: LIVE_ENDPOINT, apiKey: null, generationModel: GEN_MODEL, provider }),
  })
  const models = Array.isArray(lm) ? lm : (lm.models || [])
  const genListed = models.some(m => m.id === GEN_MODEL)
  check(models.length > 0 && genListed, `load-models via ${provider}`,
    `${models.length} models, genModel ${genListed ? 'listed' : 'NOT listed'}`)

  // 2) create a preset + bot for this provider
  const preset = await api('/api/presets', {
    method: 'POST',
    body: JSON.stringify({
      name: `V7-${provider}`, provider, baseEndpoint: LIVE_ENDPOINT, apiKey: null,
      generationModel: GEN_MODEL, utilityModel: null, embeddingModel: EMB_MODEL,
      temperature: null, topP: null, timeoutMs: 120000,
    }),
  })
  if (!preset?.id) { check(false, `create preset ${provider}`, JSON.stringify(preset).slice(0, 150)); continue }
  createdPresets.push(preset.id)

  const bot = await api('/api/bots', {
    method: 'POST',
    body: JSON.stringify({ name: `V7Bot-${provider}`, presetId: preset.id, corePersona: 'You are a concise test friend. Answer in one short sentence.' }),
  })
  if (!bot?.id) { check(false, `create bot ${provider}`, JSON.stringify(bot).slice(0, 150)); continue }
  createdBots.push(bot.id)

  // 3) real bot chat through the established SignalR helper
  try {
    const c = await chat(token, bot.id, 'Say the single word: penguin.')
    const reply = (c.content || '').trim()
    check(reply.length > 0, `bot chat via ${provider}`, `reply: "${reply.slice(0, 60)}"`)
  } catch (e) {
    check(false, `bot chat via ${provider}`, String(e).slice(0, 150))
  }
}

await cleanup()
console.log(failures === 0 ? '\nALL PASSED' : `\n${failures} FAILED`)
process.exit(failures === 0 ? 0 : 1)
