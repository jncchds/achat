import { useCallback, useEffect, useRef, useState } from 'react'
import { useParams } from 'react-router-dom'
import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'
import { apiError, getBot, getMessages, getToken, type Bot, type Message } from '../api'
import Avatar from '../components/Avatar'
import { useAuth } from '../hooks/useAuth'

interface UiMsg {
  key: string
  role: 'User' | 'Assistant'
  content: string
  reasoning: string
  complete: boolean
  error?: string
  streaming?: boolean
  at: string
}

function toUiMsg(m: Message): UiMsg {
  return {
    key: `m${m.id}`,
    role: m.role === 'User' ? 'User' : 'Assistant',
    content: m.content,
    reasoning: m.reasoning ?? '',
    complete: m.isComplete,
    at: m.createdAt,
  }
}

export default function Chat() {
  const { botId } = useParams()
  const id = parseInt(botId ?? '0', 10)
  const { user } = useAuth()
  const [bot, setBot] = useState<Bot | null>(null)
  const [msgs, setMsgs] = useState<UiMsg[]>([])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [loadError, setLoadError] = useState('')
  const scrollRef = useRef<HTMLDivElement>(null)
  const connRef = useRef<HubConnection | null>(null)
  const streamingKey = useRef<string | null>(null)

  // ── Load bot + history ───────────────────────────────────────────────────────
  useEffect(() => {
    let live = true
    getBot(id).then(b => {
      if (live) setBot(b)
      // Greeting: if the timeline is empty and the bot has a greeting, show it.
      return getMessages(id).then(ms => {
        if (!live) return
        if (ms.length === 0 && b.greeting) {
          setMsgs([{ key: 'greeting', role: 'Assistant', content: b.greeting, reasoning: '', complete: true, at: new Date().toISOString() }])
          return
        }
        setMsgs(ms.map(toUiMsg))
      })
    }).catch(e => { if (live) setLoadError(apiError(e)) })
    return () => { live = false }
  }, [id])

  // ── SignalR connection (one per page lifetime) ──────────────────────────────
  useEffect(() => {
    const conn = new HubConnectionBuilder()
      .withUrl('/hubs/chat', { accessTokenFactory: () => getToken() || '' })
      .configureLogging(LogLevel.Warning)
      .withAutomaticReconnect()
      .build()

    conn.on('chat.delta', (m: { delta: string }) => {
      patchStreaming(d => ({ ...d, content: d.content + m.delta }))
    })
    conn.on('chat.reasoning', (m: { delta: string }) => {
      patchStreaming(d => ({ ...d, reasoning: d.reasoning + m.delta }))
    })
    conn.on('chat.completed', () => {
      patchStreaming(d => ({ ...d, streaming: false, complete: true }))
      streamingKey.current = null
      setBusy(false)
      // Refresh from the server so ids / partials are canonical.
      getMessages(id).then(ms => setMsgs(ms.map(toUiMsg))).catch(() => {})
    })
    conn.on('chat.failed', (m: { message: string }) => {
      patchStreaming(d => ({ ...d, streaming: false, complete: false, error: m.message }))
      streamingKey.current = null
      setBusy(false)
      getMessages(id).then(ms => {
        const server = ms.map(toUiMsg) as UiMsg[]
        // Keep the client-side error note if the server has no newer assistant turn.
        setMsgs(prev => {
          const failed = prev.find(p => p.error)
          if (failed && (server.length === 0 || server[server.length - 1].role !== 'Assistant' || !server[server.length - 1].content)) {
            return [...server, { ...failed, streaming: false }]
          }
          return server.length ? server : prev
        })
      }).catch(() => {})
    })

    conn.start().catch(() => { /* surface on send */ })
    connRef.current = conn
    return () => { conn.stop().catch(() => {}) }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  const patchStreaming = (fn: (m: UiMsg) => UiMsg) => {
    setMsgs(prev => {
      const key = streamingKey.current
      if (!key) return prev
      return prev.map(m => m.key === key ? fn(m) : m)
    })
  }

  // ── Auto-scroll ──────────────────────────────────────────────────────────────
  useEffect(() => {
    const el = scrollRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [msgs])

  // ── Send ─────────────────────────────────────────────────────────────────────
  const send = useCallback(async () => {
    const text = input.trim()
    if (!text || busy) return
    const conn = connRef.current
    if (!conn) { setLoadError('Chat transport not ready.'); return }
    if (conn.state !== 'Connected') {
      try { await conn.start() } catch (e) { setLoadError(apiError(e)); return }
    }
    setInput('')
    setBusy(true)
    const userKey = `u${Date.now()}`
    const assistantKey = `a${Date.now()}`
    streamingKey.current = assistantKey
    const assistantMsg: UiMsg = { key: assistantKey, role: 'Assistant', content: '', reasoning: '', complete: false, streaming: true, at: new Date().toISOString() }
    setMsgs(prev => [
      ...prev,
      { key: userKey, role: 'User', content: text, reasoning: '', complete: true, at: new Date().toISOString() },
      assistantMsg,
    ])
    try {
      await conn.invoke('SendAsync', id, text)
    } catch (e) {
      patchStreaming(d => ({ ...d, streaming: false, complete: false, error: (e as Error).message }))
      setBusy(false)
      streamingKey.current = null
    }
  }, [input, busy, id])

  const stop = useCallback(async () => {
    try { await connRef.current?.invoke('StopAsync') } catch { /* ignore */ }
  }, [])

  if (loadError && !bot) {
    return <div className="chat-wrap"><p className="error-msg">{loadError}</p></div>
  }
  if (!bot) {
    return <div className="chat-wrap"><p className="hint">Loading…</p></div>
  }

  return (
    <div className="chat-wrap">
      <div className="chat-scroll" ref={scrollRef}>
        {msgs.map(m => (
          <div key={m.key} className={`msg ${m.role === 'User' ? 'user' : 'bot'}`}>
            <div className="msg-head">
              {m.role === 'Assistant'
                ? <><Avatar name={bot.name} url={bot.avatarUrl} size="sm" /> {bot.name}</>
                : <>{user?.displayName ?? 'you'} · {new Date(m.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</>}
            </div>
            <div className="msg-bubble">
              {m.role === 'Assistant' && m.streaming && m.content === '' && m.reasoning === '' && (
                <span className="typing"><i /><i /><i /></span>
              )}
              {m.content}{m.streaming && m.content !== '' && <span className="cursor" />}
            </div>
            {m.reasoning !== '' && (
              <details className="msg-reason">
                <summary>thinking{m.reasoning.length > 200 ? ` (${m.reasoning.length})` : ''}</summary>
                <div className="msg-reason-body">{m.reasoning}</div>
              </details>
            )}
            {!m.complete && m.error && <span className="msg-error">{m.error}</span>}
            {!m.complete && !m.error && m.content !== '' && <span className="msg-partial">— stopped</span>}
          </div>
        ))}
      </div>

      <div className="chat-input-bar">
        <textarea
          className="chat-input"
          rows={1}
          value={input}
          placeholder={`Message ${bot.name}…`}
          onChange={e => setInput(e.target.value)}
          onKeyDown={e => {
            if (e.key === 'Enter' && !e.shiftKey) {
              e.preventDefault()
              send()
            }
          }}
        />
        {busy
          ? <button className="btn-danger chat-send" onClick={stop}>Stop</button>
          : <button className="chat-send" onClick={send} disabled={!input.trim()}>Send</button>}
      </div>
    </div>
  )
}
