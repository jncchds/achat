import { useState, type FormEvent } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { presetsApi, type Preset, type CreatePresetRequest } from '../api/presets';
import { useAuth } from '../contexts/AuthContext';

const PROVIDERS = [
  { value: 0, label: 'Ollama' },
  { value: 1, label: 'OpenAI' },
  { value: 2, label: 'Google AI Studio' },
];

const EMPTY = { name: '', provider: 0, apiKey: '', baseUrl: '', modelName: '', embeddingModel: '', parametersJson: '' };

export function PresetsPage() {
  const { token } = useAuth();
  const qc = useQueryClient();
  const [editing, setEditing] = useState<Preset | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [form, setForm] = useState(EMPTY);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const { data: presets, isLoading } = useQuery({
    queryKey: ['presets'],
    queryFn: () => presetsApi.list(token!),
  });

  const deleteMut = useMutation({
    mutationFn: (id: string) => presetsApi.delete(id, token!),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['presets'] }),
  });

  const openCreate = () => {
    setEditing(null);
    setForm(EMPTY);
    setError('');
    setShowForm(true);
  };

  const openEdit = (p: Preset) => {
    setEditing(p);
    setForm({ name: p.name, provider: p.provider, apiKey: '', baseUrl: p.baseUrl ?? '', modelName: p.modelName, embeddingModel: p.embeddingModel ?? '', parametersJson: p.parametersJson ?? '' });
    setError('');
    setShowForm(true);
  };

  const closeForm = () => { setShowForm(false); setEditing(null); setError(''); };

  const set = (key: keyof typeof EMPTY, value: string | number) =>
    setForm(prev => ({ ...prev, [key]: value }));

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);
    try {
      if (editing) {
        await presetsApi.update(editing.id, {
          name: form.name,
          apiKey: form.apiKey || undefined,
          baseUrl: form.baseUrl || undefined,
          modelName: form.modelName,
          embeddingModel: form.embeddingModel || undefined,
          parametersJson: form.parametersJson || undefined,
        }, token!);
      } else {
        const req: CreatePresetRequest = {
          name: form.name,
          provider: Number(form.provider),
          apiKey: form.apiKey || undefined,
          baseUrl: form.baseUrl || undefined,
          modelName: form.modelName,
          embeddingModel: form.embeddingModel || undefined,
          parametersJson: form.parametersJson || undefined,
        };
        await presetsApi.create(req, token!);
      }
      qc.invalidateQueries({ queryKey: ['presets'] });
      closeForm();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Save failed');
    } finally {
      setLoading(false);
    }
  };

  if (isLoading) return <div className="page"><p className="muted">Loading…</p></div>;

  return (
    <div className="page">
      <div className="page-header">
        <h1>LLM Presets</h1>
        <button className="btn btn-primary" onClick={openCreate}>+ New Preset</button>
      </div>

      {showForm && (
        <div className="card form-card">
          <h3>{editing ? 'Edit Preset' : 'New Preset'}</h3>
          {error && <div className="alert alert-error" style={{ marginTop: 12 }}>{error}</div>}
          <form onSubmit={handleSubmit} className="form" style={{ marginTop: 16 }}>
            <div className="form-row">
              <div className="form-group">
                <label>Name *</label>
                <input value={form.name} onChange={e => set('name', e.target.value)} required autoFocus />
              </div>
              <div className="form-group form-group-sm">
                <label>Provider *</label>
                <select value={form.provider} onChange={e => set('provider', parseInt(e.target.value))} disabled={!!editing}>
                  {PROVIDERS.map(p => <option key={p.value} value={p.value}>{p.label}</option>)}
                </select>
              </div>
            </div>
            <div className="form-row">
              <div className="form-group">
                <label>API Key {editing && <span className="muted">(blank = keep current)</span>}</label>
                <input type="password" value={form.apiKey} onChange={e => set('apiKey', e.target.value)} />
              </div>
              <div className="form-group">
                <label>Base URL</label>
                <input value={form.baseUrl} onChange={e => set('baseUrl', e.target.value)} placeholder="http://localhost:11434" />
              </div>
            </div>
            <div className="form-row">
              <div className="form-group">
                <label>Model Name *</label>
                <input value={form.modelName} onChange={e => set('modelName', e.target.value)} required placeholder="e.g. llama3, gpt-4o" />
              </div>
              <div className="form-group">
                <label>Embedding Model</label>
                <input value={form.embeddingModel} onChange={e => set('embeddingModel', e.target.value)} placeholder="e.g. nomic-embed-text" />
              </div>
            </div>
            <div className="form-group">
              <label>Parameters (JSON)</label>
              <input value={form.parametersJson} onChange={e => set('parametersJson', e.target.value)} placeholder='{"temperature": 0.7}' />
            </div>
            <div className="form-actions">
              <button type="button" className="btn btn-secondary" onClick={closeForm}>Cancel</button>
              <button type="submit" className="btn btn-primary" disabled={loading}>
                {loading ? 'Saving…' : editing ? 'Save Changes' : 'Create'}
              </button>
            </div>
          </form>
        </div>
      )}

      {presets?.length === 0 && !showForm && (
        <div className="empty-state"><p>No presets yet.</p></div>
      )}

      {presets && presets.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr><th>Name</th><th>Provider</th><th>Model</th><th>API Key</th><th></th></tr>
            </thead>
            <tbody>
              {presets.map(p => (
                <tr key={p.id}>
                  <td>{p.name}</td>
                  <td>{PROVIDERS.find(x => x.value === p.provider)?.label ?? p.provider}</td>
                  <td><code>{p.modelName}</code></td>
                  <td>{p.hasApiKey ? '••••••••' : <span className="muted">—</span>}</td>
                  <td>
                    <div className="td-actions">
                      <button className="btn btn-secondary btn-sm" onClick={() => openEdit(p)}>Edit</button>
                      <button
                        className="btn btn-danger btn-sm"
                        onClick={() => { if (confirm(`Delete "${p.name}"?`)) deleteMut.mutate(p.id); }}
                      >Delete</button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
