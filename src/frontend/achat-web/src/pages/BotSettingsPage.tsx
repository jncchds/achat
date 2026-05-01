import { useState, useEffect, type FormEvent } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { botsApi, CURATED_PERSONAS, type CreateBotRequest, type UpdateBotRequest } from '../api/bots';
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
  const [preferredLanguage, setPreferredLanguage] = useState('');
  const [characterDescription, setCharacterDescription] = useState('');
  const [originalCharacterDescription, setOriginalCharacterDescription] = useState('');
  const [llmPresetId, setLlmPresetId] = useState('');
  const [embeddingPresetId, setEmbeddingPresetId] = useState('');
  const [telegramToken, setTelegramToken] = useState('');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [randomizing, setRandomizing] = useState(false);

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
      setPreferredLanguage(bot.preferredLanguage ?? '');
      setCharacterDescription(bot.characterDescription);
      setOriginalCharacterDescription(bot.characterDescription);
      setLlmPresetId(bot.llmProviderPresetId ?? '');
      setEmbeddingPresetId(bot.embeddingPresetId ?? '');
    }
  }, [bot]);

  const handleRandomize = async () => {
    setRandomizing(true);
    try {
      if (llmPresetId) {
        const result = await botsApi.randomizePersona({
          presetId: llmPresetId,
          age: age ? parseInt(age) : undefined,
          gender: gender.trim() || undefined,
          characterDescription: characterDescription.trim() || undefined,
        }, token!);
        setCharacterDescription(result.characterDescription);
      } else {
        // Curated fallback — pick one different from what's currently shown
        const options = CURATED_PERSONAS.filter(p => p !== characterDescription);
        const pick = options[Math.floor(Math.random() * options.length)];
        setCharacterDescription(pick);
      }
    } catch {
      // silently ignore; user can retry
    } finally {
      setRandomizing(false);
    }
  };

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError('');

    // Warn when CharacterDescription changes on an existing bot — it resets the evolved persona
    if (!isNew && characterDescription !== originalCharacterDescription) {
      const confirmed = window.confirm(
        'Changing the Character Description will reset the evolved persona back to this new description and clear any active persona push. Continue?'
      );
      if (!confirmed) return;
    }

    setLoading(true);
    try {
      if (isNew) {
        const req: CreateBotRequest = {
          name,
          age: age ? parseInt(age) : undefined,
          gender: gender || undefined,
          characterDescription,
          preferredLanguage: preferredLanguage || undefined,
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
          preferredLanguage: preferredLanguage || '',
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
            <div className="form-group form-group-sm">
              <label>Language</label>
              <input
                list="language-suggestions"
                value={preferredLanguage}
                onChange={e => setPreferredLanguage(e.target.value)}
                placeholder="e.g. English"
              />
              <datalist id="language-suggestions">
                {['English','Spanish','French','German','Portuguese','Italian','Russian','Japanese','Korean','Chinese','Arabic','Hindi','Dutch','Polish','Turkish'].map(l => (
                  <option key={l} value={l} />
                ))}
              </datalist>
            </div>
          </div>

          <div className="form-group">
            <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', marginBottom: 4 }}>
              <label style={{ marginBottom: 0 }}>Character Description *</label>
              <button
                type="button"
                className="btn btn-secondary btn-sm"
                onClick={handleRandomize}
                disabled={randomizing}
                title={llmPresetId ? 'Generate a random persona using your LLM preset' : 'Pick a random archetype (select an LLM preset to use AI generation)'}
              >
                {randomizing ? 'Generating…' : characterDescription ? '↺ Randomize again' : '✦ Randomize'}
              </button>
            </div>
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
