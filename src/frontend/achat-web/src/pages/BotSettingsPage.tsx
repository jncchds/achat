import { useState, useEffect, type FormEvent } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { botsApi, type CreateBotRequest, type UpdateBotRequest } from '../api/bots';
import { presetsApi } from '../api/presets';
import { useAuth } from '../contexts/AuthContext';

export function BotSettingsPage() {
  const { id } = useParams<{ id: string }>();
  const isNew = !id;
  const navigate = useNavigate();
  const { token } = useAuth();

  const [name, setName] = useState('');
  const [age, setAge] = useState('');
  const [gender, setGender] = useState('');
  const [characterDescription, setCharacterDescription] = useState('');
  const [llmPresetId, setLlmPresetId] = useState('');
  const [embeddingPresetId, setEmbeddingPresetId] = useState('');
  const [telegramToken, setTelegramToken] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const { data: bot } = useQuery({
    queryKey: ['bot', id],
    queryFn: () => botsApi.get(id!, token!),
    enabled: !isNew,
  });

  const { data: presets } = useQuery({
    queryKey: ['presets'],
    queryFn: () => presetsApi.list(token!),
  });

  useEffect(() => {
    if (bot) {
      setName(bot.name);
      setAge(bot.age?.toString() ?? '');
      setGender(bot.gender ?? '');
      setCharacterDescription(bot.characterDescription);
      setLlmPresetId(bot.llmProviderPresetId ?? '');
      setEmbeddingPresetId(bot.embeddingPresetId ?? '');
    }
  }, [bot]);

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);
    try {
      if (isNew) {
        const req: CreateBotRequest = {
          name,
          age: age ? parseInt(age) : undefined,
          gender: gender || undefined,
          characterDescription,
          llmProviderPresetId: llmPresetId || undefined,
          embeddingPresetId: embeddingPresetId || undefined,
          telegramBotToken: telegramToken || undefined,
        };
        const created = await botsApi.create(req, token!);
        navigate(`/bots/${created.id}/chat`);
      } else {
        const req: UpdateBotRequest = {
          name,
          age: age ? parseInt(age) : undefined,
          gender: gender || undefined,
          characterDescription,
          llmProviderPresetId: llmPresetId || undefined,
          embeddingPresetId: embeddingPresetId || undefined,
          telegramBotToken: telegramToken || undefined,
        };
        await botsApi.update(id!, req, token!);
        navigate(`/bots/${id}/chat`);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Save failed');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="page">
      <div className="page-header">
        <h1>{isNew ? 'New Bot' : `Settings — ${bot?.name ?? '…'}`}</h1>
      </div>

      {error && <div className="alert alert-error">{error}</div>}

      <div className="card form-card">
        <form onSubmit={handleSubmit} className="form">
          <div className="form-row">
            <div className="form-group">
              <label>Name *</label>
              <input value={name} onChange={e => setName(e.target.value)} required autoFocus />
            </div>
            <div className="form-group form-group-sm">
              <label>Age</label>
              <input type="number" value={age} onChange={e => setAge(e.target.value)} min={1} max={999} />
            </div>
            <div className="form-group form-group-sm">
              <label>Gender</label>
              <input value={gender} onChange={e => setGender(e.target.value)} placeholder="e.g. female" />
            </div>
          </div>

          <div className="form-group">
            <label>Character Description *</label>
            <textarea
              value={characterDescription}
              onChange={e => setCharacterDescription(e.target.value)}
              required
              rows={5}
              placeholder="Describe the bot's personality, background, and traits…"
            />
          </div>

          <div className="form-row">
            <div className="form-group">
              <label>LLM Preset</label>
              <select value={llmPresetId} onChange={e => setLlmPresetId(e.target.value)}>
                <option value="">— none —</option>
                {presets?.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
              </select>
            </div>
            <div className="form-group">
              <label>Embedding Preset</label>
              <select value={embeddingPresetId} onChange={e => setEmbeddingPresetId(e.target.value)}>
                <option value="">— none —</option>
                {presets?.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
              </select>
            </div>
          </div>

          <div className="form-group">
            <label>
              Telegram Bot Token
              {!isNew && bot?.hasTelegramToken && <span className="badge badge-tg" style={{ marginLeft: 8 }}>set</span>}
            </label>
            <input
              type="password"
              value={telegramToken}
              onChange={e => setTelegramToken(e.target.value)}
              placeholder={!isNew && bot?.hasTelegramToken ? '(enter new token to replace)' : ''}
            />
          </div>

          <div className="form-actions">
            <button type="button" className="btn btn-secondary" onClick={() => navigate(-1)}>
              Cancel
            </button>
            <button type="submit" className="btn btn-primary" disabled={loading}>
              {loading ? 'Saving…' : isNew ? 'Create Bot' : 'Save Changes'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
