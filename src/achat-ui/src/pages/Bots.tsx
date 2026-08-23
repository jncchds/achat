import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { apiError, createBot, deleteBot, listBots, listPresets, updateBot, type Bot, type BotInput, type Preset } from '../api'
import Avatar from '../components/Avatar'

function BotForm({ initial, presets, onSaved, onCancel }: {
  initial?: Bot
  presets: Preset[]
  onSaved: () => void
  onCancel: () => void
}) {
  const [name, setName] = useState(initial?.name ?? '')
  const [avatarUrl, setAvatarUrl] = useState(initial?.avatarUrl ?? '')
  const [corePersona, setCorePersona] = useState(initial?.corePersona ?? '')
  const [speechStyle, setSpeechStyle] = useState(initial?.speechStyle ?? '')
  const [greeting, setGreeting] = useState(initial?.greeting ?? '')
  const [presetId, setPresetId] = useState(initial?.presetId ?? presets[0]?.id ?? 0)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError('')
    const payload: BotInput = {
      name: name.trim(),
      avatarUrl: avatarUrl.trim() || null,
      corePersona: corePersona.trim(),
      speechStyle: speechStyle.trim() || null,
      greeting: greeting.trim() || null,
      presetId,
    }
    try {
      if (initial) await updateBot(initial.id, payload)
      else await createBot(payload)
      onSaved()
    } catch (err) {
      setError(apiError(err))
      setBusy(false)
    }
  }

  return (
    <form onSubmit={submit} className="card" style={{ marginBottom: '1rem' }}>
      <h2 style={{ marginBottom: '1rem' }}>{initial ? `Edit ${initial.name}` : 'New bot'}</h2>
      {presets.length === 0 && (
        <p className="error-msg" style={{ marginBottom: '0.75rem' }}>
          You need a preset before creating a bot. <Link to="/presets">Create one first →</Link>
        </p>
      )}
      <div className="form-grid">
        <label>Name
          <input value={name} onChange={e => setName(e.target.value)} required />
        </label>
        <label>Preset
          <select value={presetId} onChange={e => setPresetId(parseInt(e.target.value))} disabled={presets.length === 0}>
            {presets.map(p => <option key={p.id} value={p.id}>{p.name} — {p.generationModel}</option>)}
          </select>
        </label>
        <label className="span-2">Avatar URL (optional)
          <input value={avatarUrl} onChange={e => setAvatarUrl(e.target.value)} placeholder="https://… or leave blank for initials" />
        </label>
        <label className="span-2">Who this friend is <span className="hint">(core persona — freeform prose)</span>
          <textarea rows={5} value={corePersona} onChange={e => setCorePersona(e.target.value)} required
            placeholder="Background, temperament, how they think and feel, what they care about…" />
        </label>
        <label className="span-2">Speech style hint (optional)
          <input value={speechStyle} onChange={e => setSpeechStyle(e.target.value)} placeholder="Short, warm, a little dry…" />
        </label>
        <label className="span-2">Greeting (optional — sent as their first message)
          <input value={greeting} onChange={e => setGreeting(e.target.value)} placeholder="Hey — you made it." />
        </label>
      </div>
      {error && <p className="error-msg" style={{ marginTop: '0.75rem' }}>{error}</p>}
      <div className="form-actions">
        <button type="submit" disabled={busy || presets.length === 0}>{busy ? 'Saving…' : initial ? 'Save' : 'Create bot'}</button>
        <button type="button" className="btn-ghost" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  )
}

export default function Bots() {
  const [bots, setBots] = useState<Bot[]>([])
  const [presets, setPresets] = useState<Preset[]>([])
  const [editing, setEditing] = useState<Bot | 'new' | null>(null)
  const [error, setError] = useState('')
  const load = useCallback(() => {
    listBots().then(setBots).catch(e => setError(apiError(e)))
    listPresets().then(setPresets).catch(() => {})
  }, [])

  useEffect(() => { load() }, [load])

  const remove = async (b: Bot) => {
    if (!confirm(`Delete ${b.name}? Their conversation history and memories will be removed for you.`)) return
    try {
      await deleteBot(b.id)
      load()
    } catch (e) { setError(apiError(e)) }
  }

  return (
    <div>
      <div className="page-header">
        <h2>Bots</h2>
        <button className="btn-primary" onClick={() => setEditing('new')}>+ New Bot</button>
      </div>
      {error && <p className="error-msg" style={{ marginBottom: '1rem' }}>{error}</p>}

      {editing !== null && (
        <BotForm
          initial={editing === 'new' ? undefined : editing}
          presets={presets}
          onSaved={() => { setEditing(null); load() }}
          onCancel={() => setEditing(null)}
        />
      )}

      {bots.length === 0 && editing === null ? (
        <div className="card empty">
          <p>No friends yet. Create your first bot — give them a name and who they are.</p>
        </div>
      ) : (
        <div className="list-card">
          {bots.map(b => (
            <div key={b.id} className="list-row">
              <Avatar name={b.name} url={b.avatarUrl} size="lg" />
              <div className="list-row-main">
                <div className="list-row-title"><Link to={`/bots/${b.id}`}>{b.name}</Link></div>
                <div className="list-row-meta">
                  {b.presetName} · {b.generationModel}
                  {b.lastMessageAt ? ` · last spoke ${new Date(b.lastMessageAt).toLocaleDateString()}` : ' · not spoken yet'}
                </div>
                {b.lastMessagePreview && <div className="list-row-meta" style={{ fontStyle: 'italic' }}>“{b.lastMessagePreview}”</div>}
              </div>
              <div className="list-row-actions">
                <Link className="btn btn-sm" to={`/bots/${b.id}`}>Chat →</Link>
                <Link className="btn-ghost btn-sm" to={`/bots/${b.id}/settings`}>⚙️ Personality</Link>
                <button className="btn-ghost btn-sm" onClick={() => setEditing(b)}>Edit</button>
                <button className="btn-danger btn-sm" onClick={() => remove(b)}>Delete</button>
              </div>
            </div>
          ))}
        </div>
      )}
      {bots.length > 0 && editing === null && (
        <p className="hint" style={{ marginTop: '1rem' }}>
          Tip: open a bot to talk. Click a row’s <b>Chat →</b> to start.
        </p>
      )}
    </div>
  )
}
