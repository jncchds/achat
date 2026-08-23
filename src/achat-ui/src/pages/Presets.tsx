import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import ModelInput from '../components/ModelInput'
import {
  apiError, createPreset, deletePreset, listBots, listPresets, loadModels, testPreset,
  updatePreset, type ModelInfo, type Preset, type PresetInput, type Provider,
} from '../api'

const PROVIDERS: Provider[] = ['OpenAICompatible', 'Ollama', 'OpenAI', 'GoogleAIStudio']

// Blank endpoint → the provider's default (shown as a placeholder hint).
const DEFAULT_ENDPOINTS: Record<Provider, string> = {
  OpenAICompatible: 'https://api.openai.com/v1',
  Ollama: 'http://host.docker.internal:11434/v1',
  OpenAI: 'https://api.openai.com/v1',
  GoogleAIStudio: 'https://generativelanguage.googleapis.com/v1beta/openai',
}

function PresetForm({ initial, onSaved, onCancel }: {
  initial?: Preset
  onSaved: () => void
  onCancel: () => void
}) {
  const [name, setName] = useState(initial?.name ?? '')
  const [provider, setProvider] = useState<Provider>(initial?.provider ?? 'OpenAICompatible')
  const [endpoint, setEndpoint] = useState(initial?.baseEndpoint ?? '')
  const [apiKey, setApiKey] = useState('')
  const [genModel, setGenModel] = useState(initial?.generationModel ?? '')
  const [utilModel, setUtilModel] = useState(initial?.utilityModel ?? '')
  const [embedModel, setEmbedModel] = useState(initial?.embeddingModel ?? '')
  const [temperature, setTemperature] = useState(initial?.temperature?.toString() ?? '')
  const [maxTokens, setMaxTokens] = useState(initial?.maxTokens?.toString() ?? '')
  const [models, setModels] = useState<ModelInfo[]>([])
  const [modelsBusy, setModelsBusy] = useState(false)
  const [modelsError, setModelsError] = useState('')
  const [testResult, setTestResult] = useState<{ ok: boolean; message: string } | null>(null)
  const [testBusy, setTestBusy] = useState(false)
  const [lockedBots, setLockedBots] = useState<string[]>([])
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  // The loaded catalogue feeds the suggestions in all three model fields.
  const modelIds = models.map(m => m.id)

  // §5.7 — which bots lock the embedding model.
  const refreshLock = useCallback((presetId?: number) => {
    if (presetId === undefined) { setLockedBots([]); return }
    listBots().then(bots => setLockedBots(bots.filter(b => b.presetId === presetId).map(b => b.name))).catch(() => {})
  }, [])
  useEffect(() => { refreshLock(initial?.id) }, [initial?.id, refreshLock])

  const draft = () => ({
    provider,
    baseEndpoint: endpoint.trim(),
    apiKey: apiKey.trim() || null,
    generationModel: genModel.trim(),
  })

  const doLoadModels = async () => {
    setModelsBusy(true)
    setModelsError('')
    try {
      const m = await loadModels(draft())
      setModels(m)
    } catch (e) {
      setModelsError(apiError(e))
    } finally {
      setModelsBusy(false)
    }
  }

  const test = async () => {
    setTestBusy(true)
    setTestResult(null)
    try {
      setTestResult(await testPreset(draft()))
    } catch (e) {
      setTestResult({ ok: false, message: apiError(e) })
    } finally {
      setTestBusy(false)
    }
  }

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!genModel.trim()) {
      setError('A generation model is required. Load the model list or type a model id.')
      return
    }
    setBusy(true)
    setError('')
    const payload: PresetInput = {
      name: name.trim(),
      provider,
      baseEndpoint: endpoint.trim(),
      apiKey: apiKey.trim() || null,
      generationModel: genModel.trim(),
      utilityModel: utilModel.trim() || null,
      embeddingModel: embedModel.trim() || null,
      temperature: temperature === '' ? null : parseFloat(temperature),
      maxTokens: maxTokens === '' ? null : parseInt(maxTokens, 10),
    }
    try {
      if (initial) await updatePreset(initial.id, payload)
      else await createPreset(payload)
      onSaved()
    } catch (err) {
      setError(apiError(err))
      setBusy(false)
    }
  }

  return (
    <form onSubmit={submit} className="card" style={{ marginBottom: '1rem' }}>
      <h2 style={{ marginBottom: '1rem' }}>{initial ? `Edit ${initial.name}` : 'New preset'}</h2>
      <div className="form-grid">
        <label>Name
          <input value={name} onChange={e => setName(e.target.value)} required />
        </label>
        <label>Provider
          <select value={provider} onChange={e => setProvider(e.target.value as Provider)}>
            {PROVIDERS.map(p => (
              <option key={p} value={p}>{p}</option>
            ))}
          </select>
        </label>
        <label>Base endpoint <span className="hint">(blank = provider default)</span>
          <input value={endpoint} onChange={e => setEndpoint(e.target.value)} placeholder={DEFAULT_ENDPOINTS[provider] || 'http://host.docker.internal:11434/v1'} />
        </label>
        <label>
          API key {initial?.hasApiKey && <span className="hint">(current: {initial.maskedApiKey} — blank leaves it unchanged)</span>}
          <input type="password" value={apiKey} onChange={e => setApiKey(e.target.value)} placeholder={initial?.hasApiKey ? '••• unchanged •••' : 'sk-…'} autoComplete="off" />
        </label>
        <label>
          Model list <span className="hint">(suggestions for the three model fields below — any model id can still be typed)</span>
          <div style={{ display: 'flex', gap: '0.4rem', alignItems: 'center', flexWrap: 'wrap' }}>
            <button type="button" className="btn-ghost" onClick={doLoadModels} disabled={modelsBusy}>
              {modelsBusy ? 'Loading…' : models.length > 0 ? `Reload (${models.length} loaded)` : 'Load models'}
            </button>
            {models.length > 0 && <span className="hint">{models.length} models from the endpoint above</span>}
          </div>
          {modelsError && <span className="hint" style={{ color: 'var(--danger)', display: 'block' }}>{modelsError}</span>}
        </label>
        <label>Generation model <span className="hint">(the bot's voice)</span>
          <ModelInput
            value={genModel} onChange={setGenModel} suggestions={modelIds}
            placeholder="e.g. qwen/qwen3.8-27b" ariaLabel="Generation model" />
        </label>
        <label>Utility model <span className="hint">(background chores; blank = generation model)</span>
          <ModelInput
            value={utilModel} onChange={setUtilModel} suggestions={modelIds}
            placeholder={genModel || 'defaults to generation model'} ariaLabel="Utility model" />
        </label>
        <label>
          Embedding model {lockedBots.length > 0 && <span className="pill danger" style={{ marginLeft: '0.4rem' }}>locked</span>}
          <ModelInput
            value={embedModel} onChange={setEmbedModel} suggestions={modelIds}
            disabled={lockedBots.length > 0}
            placeholder="e.g. nomic-embed-text" ariaLabel="Embedding model" />
          {lockedBots.length > 0 && <span className="hint">Used by: {lockedBots.join(', ')} — detach or delete those bots to change it (§5.7).</span>}
        </label>
        <label>Temperature <span className="hint">(0–2, blank = provider default)</span>
          <input value={temperature} onChange={e => setTemperature(e.target.value)} placeholder="e.g. 0.8" inputMode="decimal" />
        </label>
        <label>Max output tokens <span className="hint">(blank = provider default)</span>
          <input value={maxTokens} onChange={e => setMaxTokens(e.target.value)} placeholder="e.g. 1024" inputMode="numeric" />
        </label>
      </div>

      <div className="form-actions">
        <button type="submit" disabled={busy}>{busy ? 'Saving…' : initial ? 'Save' : 'Create preset'}</button>
        <button type="button" className="btn-ghost" onClick={test} disabled={testBusy}>
          {testBusy ? 'Testing…' : 'Test connection'}
        </button>
        {testResult && <span className={`field-result ${testResult.ok ? 'ok' : 'err'}`}>{testResult.message}</span>}
      </div>
      {error && <p className="error-msg" style={{ marginTop: '0.75rem' }}>{error}</p>}
      <div className="form-actions">
        <button type="button" className="btn-ghost" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  )
}

export default function Presets() {
  const [presets, setPresets] = useState<Preset[]>([])
  const [editing, setEditing] = useState<Preset | 'new' | null>(null)
  const [error, setError] = useState('')

  const load = useCallback(() => {
    listPresets().then(setPresets).catch(e => setError(apiError(e)))
  }, [])
  useEffect(() => { load() }, [load])

  const remove = async (p: Preset) => {
    if (!confirm(`Delete preset ${p.name}?`)) return
    try {
      await deletePreset(p.id)
      load()
    } catch (e) { setError(apiError(e)) }
  }

  return (
    <div>
      <div className="page-header">
        <h2>Presets</h2>
        <button className="btn-primary" onClick={() => setEditing('new')}>+ New Preset</button>
      </div>
      <p className="hint" style={{ marginTop: '-1rem', marginBottom: '1.25rem' }}>
        How to reach a model: endpoint + key + which model does which job. API keys are encrypted at rest and never returned to the browser.
      </p>
      {error && <p className="error-msg" style={{ marginBottom: '1rem' }}>{error}</p>}

      {editing !== null && (
        <PresetForm
          initial={editing === 'new' ? undefined : editing}
          onSaved={() => { setEditing(null); load() }}
          onCancel={() => setEditing(null)}
        />
      )}

      {presets.length === 0 && editing === null ? (
        <div className="card empty">
          <p>No presets yet. <Link to="/presets">Create one</Link> to point AChat at a model.</p>
        </div>
      ) : (
        <div className="list-card">
          {presets.map(p => (
            <div key={p.id} className="list-row">
              <div className="list-row-main">
                <div className="list-row-title">{p.name}</div>
                <div className="list-row-meta">
                  {p.provider} · {p.baseEndpoint || '(default endpoint)'} · <b>{p.generationModel}</b>
                  {p.utilityModel ? ` · util: ${p.utilityModel}` : ''}
                  {p.embeddingModel ? ` · embed: ${p.embeddingModel}` : ''}
                  {p.hasApiKey ? ' · key set' : ' · no key'}
                </div>
              </div>
              <div className="list-row-actions">
                <button className="btn-ghost btn-sm" onClick={() => setEditing(p)}>Edit</button>
                <button className="btn-danger btn-sm" onClick={() => remove(p)}>Delete</button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
