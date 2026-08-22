import { useCallback, useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import {
  apiError, forgetAllMemories, forgetMemory, listMemories, pinMemory, updateMemory,
  type Memory,
} from '../api'

const KIND_LABEL: Record<string, string> = {
  Fact: 'fact', Preference: 'preference', Event: 'event', Relationship: 'relationship',
}

export default function BotMemories() {
  const { botId } = useParams()
  const bot = parseInt(botId!, 10)
  const [memories, setMemories] = useState<Memory[]>([])
  const [search, setSearch] = useState('')
  const [error, setError] = useState('')
  const [editingId, setEditingId] = useState<number | null>(null)
  const [editContent, setEditContent] = useState('')
  const [busy, setBusy] = useState(false)

  const load = useCallback(() => {
    listMemories(bot, search || undefined)
      .then(setMemories)
      .catch(e => setError(apiError(e)))
  }, [bot, search])

  useEffect(() => { load() }, [load])

  const startEdit = (m: Memory) => { setEditingId(m.id); setEditContent(m.content) }
  const cancelEdit = () => setEditingId(null)

  const saveEdit = async (m: Memory) => {
    setBusy(true); setError('')
    try {
      await updateMemory(m.id, { content: editContent })
      await load()
      setEditingId(null)
    } catch (e) { setError(apiError(e)) } finally { setBusy(false) }
  }

  const togglePin = async (m: Memory) => {
    setBusy(true)
    try {
      await pinMemory(m.id, !m.isPinned)
      await load()
    } catch (e) { setError(apiError(e)) } finally { setBusy(false) }
  }

  const forget = async (m: Memory) => {
    if (!confirm(`Forget this memory?\n\n"${m.content}"`)) return
    setBusy(true)
    try {
      await forgetMemory(m.id)
      await load()
    } catch (e) { setError(apiError(e)) } finally { setBusy(false) }
  }

  const forgetAll = async () => {
    if (!confirm('Forget EVERYTHING this bot remembers about you? (Soft delete — the rows stay in the database, but the bot will never surface them again.)')) return
    setBusy(true); setError('')
    try {
      await forgetAllMemories(bot)
      await load()
    } catch (e) { setError(apiError(e)) } finally { setBusy(false) }
  }

  const active = memories.filter(m => !m.isDeleted)
  const history = memories.filter(m => m.isDeleted || m.supersededByMemoryId)

  return (
    <div>
      <div className="page-header">
        <h2>What your friend remembers</h2>
        <span className="hint">
          Recorded automatically from your conversations (§5.3). You can edit, pin, or forget
          any of them — a human correction is never silently overwritten by extraction (§5.6).
        </span>
        <div className="row" style={{ marginTop: '0.5rem' }}>
          <Link className="btn-ghost btn-sm" to={`/bots/${bot}`}>← Back to chat</Link>
          {active.length > 0 && (
            <button className="btn-danger btn-sm" disabled={busy} onClick={forgetAll}>Forget everything</button>
          )}
        </div>
      </div>
      {error && <p className="error-msg" style={{ marginBottom: '1rem' }}>{error}</p>}

      <div className="card" style={{ marginBottom: '1rem' }}>
        <input
          className="search-input"
          placeholder="Search memories…"
          value={search}
          onChange={e => setSearch(e.target.value)}
        />
      </div>

      {memories.length === 0 && (
        <div className="card empty">
          Nothing recorded yet. Talk to your friend — durable facts and meaningful events are
          picked up in the background after your conversation settles.
        </div>
      )}

      <div className="list">
        {active.map(m => (
          <div key={m.id} className={`card memory-card${m.isPinned ? ' pinned' : ''}`}>
            <div className="memory-head">
              <span className="pill">{KIND_LABEL[m.kind] ?? m.kind}</span>
              <span className="stars" title={`importance ${m.importance}/5`}>
                {'★'.repeat(m.importance)}<span className="dim">{'★'.repeat(5 - m.importance)}</span>
              </span>
              {m.source === 'UserEdited' && <span className="pill accent" title="you edited this">edited</span>}
              {m.isPinned && <span className="pill accent" title="always in the prompt">pinned</span>}
              <span className="hint" style={{ marginLeft: 'auto' }}>
                {new Date(m.createdAt).toLocaleDateString()}
                {m.lastRecalledAt ? ` · recalled ${new Date(m.lastRecalledAt).toLocaleDateString()}` : ''}
                {m.recallCount > 0 ? ` · ×${m.recallCount}` : ''}
              </span>
            </div>

            {editingId === m.id ? (
              <div className="memory-edit">
                <textarea
                  value={editContent}
                  onChange={e => setEditContent(e.target.value)}
                  rows={2}
                  autoFocus
                />
                <div className="row">
                  <button className="btn-primary btn-sm" disabled={busy} onClick={() => void saveEdit(m)}>Save</button>
                  <button className="btn-ghost btn-sm" disabled={busy} onClick={cancelEdit}>Cancel</button>
                </div>
              </div>
            ) : (
              <p className="memory-content">{m.content}</p>
            )}

            <div className="row memory-actions">
              <button className="btn-ghost btn-sm" disabled={busy} onClick={() => void togglePin(m)}>
                {m.isPinned ? 'Unpin' : 'Pin'}
              </button>
              {editingId !== m.id && (
                <button className="btn-ghost btn-sm" disabled={busy} onClick={() => startEdit(m)}>Edit</button>
              )}
              <button className="btn-ghost btn-sm danger-text" disabled={busy} onClick={() => void forget(m)}>Forget</button>
            </div>
          </div>
        ))}
      </div>

      {history.length > 0 && (
        <>
          <h3 style={{ margin: '1.5rem 0 0.75rem' }}>History (forgotten or superseded)</h3>
          <div className="list">
            {history.map(m => (
              <div key={m.id} className="card memory-card faded">
                <div className="memory-head">
                  <span className="pill">{KIND_LABEL[m.kind] ?? m.kind}</span>
                  {m.isDeleted && <span className="pill danger">forgotten</span>}
                  {m.supersededByMemoryId && <span className="pill">superseded</span>}
                  <span className="hint" style={{ marginLeft: 'auto' }}>{new Date(m.createdAt).toLocaleDateString()}</span>
                </div>
                <p className="memory-content">{m.content}</p>
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  )
}
