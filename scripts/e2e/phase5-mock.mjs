// Turn-envelope pipeline check against the mock LLM (no real model needed).
// Verifies, from the mock's request log and the API:
//   • the chat system block is ordered core → evolved traits → directives
//   • the envelope instructions are sent (and offer memories + traits to the creator)
//   • the user's message and a clean reply are what gets stored (no JSON leaks into the chat)
//   • bot memories from the envelope are stored, recalled in the next prompt, and reported
//     in chat.completed
//   • a garbled envelope (here: a plain-text reply) still delivers the reply and has no side effects
//
// Prereqs: mockllm.mjs running on 18999 (MOCK_LOG default); throwaway instance on 5002.
//   ACHAT_BASE=http://localhost:5002 node scripts/e2e/phase5-mock.mjs
import fs from 'node:fs'
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const BASE = process.env.ACHAT_BASE || 'http://localhost:5002'
const sleep = ms => new Promise(r => setTimeout(r, ms))

async function api(path, opts = {}) {
  const res = await fetch(BASE + path, {
    ...opts,
    headers: { 'Content-Type': 'application/json', ...(opts.token ? { Authorization: `Bearer ${opts.token}` } : {}) },
  })
  const text = await res.text()
  if (!res.ok) throw new Error(`${path} → ${res.status}: ${text.slice(0, 300)}`)
  return text ? JSON.parse(text) : null
}

async function chat(token, botId, message) {
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

// ── setup: mock preset + bot with directives and a core persona ──────────────
const token = (await api('/api/auth/login', { method: 'POST', body: JSON.stringify({ username: 'admin', password: 'admin' }) })).token
let preset = (await api('/api/presets', { token })).find(p => p.name === 'Mock')
if (!preset) preset = await api('/api/presets', { method: 'POST', token, body: JSON.stringify({
  name: 'Mock', provider: 'OpenAICompatible', baseEndpoint: 'http://host.docker.internal:18999/v1',
  generationModel: 'mock-model', utilityModel: 'mock-model-small', embeddingModel: 'mock-embed', temperature: 0.7 }) })
const bot = await api('/api/bots', { method: 'POST', token, body: JSON.stringify({
  name: 'EnvelopeSage', presetId: preset.id,
  corePersona: 'A thoughtful friend with a dry sense of humour. Values honesty over comfort.',
  speechStyle: 'Direct, a little wry.' }) })
console.log('mock bot:', bot.id, bot.name)
const directive = 'Call me Sam — never "sir" or any title.'
await api(`/api/bots/${bot.id}/directives`, { method: 'POST', token, body: JSON.stringify({ text: directive }) })

const logPath = process.env.MOCK_LOG || fileURLToPath(new URL('./mock-requests.jsonl', import.meta.url))
const chatRequests = () => fs.readFileSync(logPath, 'utf8').trim().split('\n').map(l => JSON.parse(l))
  .filter(l => l.url === '/v1/chat/completions' && (l.messages || []).some(m => m.role === 'system' && m.head.includes('You are EnvelopeSage')))

try {
  // ── 1. a trait + a memory in one envelope ──────────────────────────────────
  const trait = 'jokes about the heating, which they call Steve'
  const r1 = await chat(token, bot.id, `The flat heating gave up again. I named it Steve. [[trait:${trait}]] [[remember:The user's flat heating is called Steve.]]`)
  check(!/\{"reply"|"memories"|"traits"/.test(r1.content), 'the stored/streamed reply carries no raw JSON', r1.content.slice(0, 60))
  check(r1.remembered === 1, 'chat.completed reports one remembered memory', `remembered=${r1.remembered}`)
  check(r1.traitChanges?.added?.[0] === trait, 'chat.completed reports the added trait')
  const mems = await api(`/api/memories?botId=${bot.id}`, { token })
  check(mems.length === 1 && mems[0].source === 'BotRemember', 'one BotRemember memory stored', mems.map(m => m.content).join(' | '))

  // ── 2. the next prompt: persona block order, memory recall, envelope instructions ──
  await sleep(500)
  await chat(token, bot.id, 'How is the heating?')
  const last = chatRequests().pop()
  const sys = last.messages.find(m => m.role === 'system').head
  const iCore = sys.indexOf('thoughtful friend'), iTrait = sys.indexOf(trait), iDir = sys.indexOf(directive)
  check(iCore >= 0 && iTrait > iCore && iDir > iTrait, 'system block is ordered core → evolved trait → directive', `${iCore} < ${iTrait} < ${iDir}`)
  check(last.messages.some(m => m.role === 'user'), 'request carries a user message')
  check(/Response format — reply with ONE JSON object/.test(sys), 'envelope instructions are sent')
  check(/\"memories\"/.test(sys) && /\"traits\"/.test(sys), 'the creator is offered memories and traits')
  check(/Steve/.test(sys), 'the remembered fact is in the prompt', 'memory block')

  // ── 3. a garbled envelope never loses the reply, and applies nothing ──────
  // The mock answers plain text unless a marker is present, i.e. a non-envelope reply.
  const before = (await api('/api/memories?botId=' + bot.id, { token })).length
  const r3 = await chat(token, bot.id, 'just chatting, no markers')
  check(typeof r3.content === 'string' && r3.content.includes('You said'), 'a non-envelope reply is delivered as is', r3.content.slice(0, 60))
  check((await api('/api/memories?botId=' + bot.id, { token })).length === before, 'and writes no memories')
  check((await api(`/api/bots/${bot.id}/persona/traits`, { token })).length === 1, 'and changes no traits')

  // ── 4. history shows clean text ───────────────────────────────────────────
  const msgs = await api(`/api/bots/${bot.id}/messages`, { token })
  check(!msgs.some(m => /^\s*\{"reply"/.test(m.content)), 'no stored message holds raw envelope JSON')
} finally {
  await api(`/api/bots/${bot.id}`, { method: 'DELETE', token }).catch(() => {})
}

console.log(failures === 0 ? '\nENVELOPE (mock) E2E: PASS' : `\nENVELOPE (mock) E2E: FAIL (${failures})`)
process.exit(failures === 0 ? 0 : 1)
