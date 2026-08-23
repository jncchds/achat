// AChat mock LLM — a minimal OpenAI-compatible endpoint for offline E2E runs.
//
// Endpoints: /v1/models, /v1/chat/completions (SSE + JSON), /v1/embeddings
//
// Purpose-aware replies (inspected via the system message):
//   • evolution requests  → a fixed evolved-traits document
//   • extraction requests → a fixed JSON array of memory candidates
//   • summary requests    → a fixed summary sentence
//   • anything else (chat)→ an echo-style reply
//
// Every request is appended to a JSONL log (MOCK_LOG path, default
// ./mock-requests.jsonl) so E2E scripts can assert the prompt structure —
// e.g. that the evolution request carries a user message (Qwen3's template
// rejects system-only requests) and that the persona block is ordered
// core → evolved → directives.
//
// Usage:  node mockllm.mjs [port]     (default 18999)

import http from 'node:http'
import fs from 'node:fs'
import { fileURLToPath } from 'node:url'

const PORT = parseInt(process.argv[2] || '18999', 10)
const LOG_PATH = process.env.MOCK_LOG || fileURLToPath(new URL('./mock-requests.jsonl', import.meta.url))

const EVOLVED_DOC = 'You two share a running joke about the flat heating, which you call Steve. ' +
  'The tone has settled into brisk, good-humoured banter; Sam likes when you name the problem first ' +
  'and the fix second. You have grown to be the one Sam tells things to before anyone else.'

const EXTRACTION_JSON = JSON.stringify([
  { content: 'The user calls the flat heating unit Steve.', kind: 'fact', importance: 3 },
  { content: 'The user prefers the problem named before the fix.', kind: 'preference', importance: 2 },
])

const SUMMARY_TEXT = 'Sam has been dealing with a broken flat heating (which they call Steve) and ' +
  'has settled into a brisk, good-humoured way of talking with the friend.'

function classify(messages) {
  const system = (messages.find(m => m.role === 'system')?.content || '').slice(0, 400).toLowerCase()
  if (system.includes('evolved-traits')) return 'evolution'
  if (system.includes('extract durable memories')) return 'extraction'
  if (system.includes('rolling summary') || system.includes('running summary')) return 'summary'
  return 'chat'
}

// Deterministic pseudo-embedding: 768 dims seeded from the text, so equal texts
// collide (dedup works) and distinct texts differ (ranking is not degenerate).
function embed(text) {
  const v = new Array(768).fill(0)
  let h = 2166136261
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i)
    h = Math.imul(h, 16777619)
  }
  let s = h >>> 0
  for (let i = 0; i < 768; i++) {
    s = (Math.imul(s, 1103515245) + 12345) >>> 0
    v[i] = (s / 4294967295) * 2 - 1
  }
  return v
}

const server = http.createServer((req, res) => {
  let body = ''
  req.on('data', c => body += c)
  req.on('end', () => {
    let parsed = null
    try { parsed = JSON.parse(body) } catch { parsed = {} }

    fs.appendFile(LOG_PATH, JSON.stringify({
      at: new Date().toISOString(),
      url: req.url,
      model: parsed.model || null,
      stream: !!parsed.stream,
      messages: (parsed.messages || []).map(m => ({ role: m.role, chars: (m.content || '').length, head: (m.content || '').slice(0, 2000) })),
    }) + '\n', () => {})

    if (req.url === '/v1/models') {
      res.writeHead(200, { 'content-type': 'application/json' })
      return res.end(JSON.stringify({ data: [{ id: 'mock-model' }, { id: 'mock-model-small' }] }))
    }

    if (req.url === '/v1/embeddings') {
      const inputs = Array.isArray(parsed.input) ? parsed.input : [parsed.input]
      res.writeHead(200, { 'content-type': 'application/json' })
      return res.end(JSON.stringify({
        object: 'list',
        data: inputs.map((t, i) => ({ object: 'embedding', index: i, embedding: embed(String(t)) })),
        model: parsed.model || 'mock-embed',
        usage: { prompt_tokens: inputs.length, total_tokens: inputs.length },
      }))
    }

    if (req.url === '/v1/chat/completions') {
      const kind = classify(parsed.messages || [])
      let reply
      if (kind === 'evolution') reply = EVOLVED_DOC
      else if (kind === 'extraction') reply = EXTRACTION_JSON
      else if (kind === 'summary') reply = SUMMARY_TEXT
      else {
        const last = (parsed.messages || []).slice(-1)[0]
        reply = `[mock:${kind}] You said: ${(last?.content || '').slice(0, 120)}`
      }

      if (!parsed.stream) {
        res.writeHead(200, { 'content-type': 'application/json' })
        return res.end(JSON.stringify({
          choices: [{ message: { role: 'assistant', content: reply }, finish_reason: 'stop' }],
          usage: { prompt_tokens: 40, completion_tokens: 20, total_tokens: 60 },
        }))
      }

      res.writeHead(200, { 'content-type': 'text/event-stream' })
      const words = reply.split(' ')
      let i = 0
      const timer = setInterval(() => {
        if (i < words.length) {
          const delta = (i === 0 ? '' : ' ') + words[i++]
          res.write(`data: ${JSON.stringify({ choices: [{ delta: { content: delta }, index: 0 }] })}\n\n`)
        } else {
          res.write(`data: ${JSON.stringify({ choices: [{ delta: {}, finish_reason: 'stop' }], usage: { prompt_tokens: 40, completion_tokens: 20, total_tokens: 60 } })}\n\n`)
          res.write('data: [DONE]\n\n')
          res.end()
          clearInterval(timer)
        }
      }, 15)
      return
    }

    res.writeHead(404)
    res.end('not found')
  })
})

server.listen(PORT, () => console.log(`mock llm on ${PORT} (log: ${LOG_PATH})`))
