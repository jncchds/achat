import { useState, type FormEvent } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { botsApi } from '../api/bots';
import { useAuth } from '../contexts/AuthContext';

export function PersonaPage() {
  const { id: botId } = useParams<{ id: string }>();
  const { token } = useAuth();
  const queryClient = useQueryClient();

  const [pushDirection, setPushDirection] = useState('');
  const [pushError, setPushError] = useState('');

  const { data: bot } = useQuery({
    queryKey: ['bot', botId],
    queryFn: () => botsApi.get(botId!, token!),
    enabled: !!botId,
  });

  const { data: snapshots, isLoading } = useQuery({
    queryKey: ['persona', botId],
    queryFn: () => botsApi.personaHistory(botId!, token!),
    enabled: !!botId,
  });

  const pushMutation = useMutation({
    mutationFn: (direction: string) => botsApi.personaPush(botId!, direction, token!),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['bot', botId] });
      setPushDirection('');
      setPushError('');
    },
    onError: (err: Error) => setPushError(err.message),
  });

  const clearPushMutation = useMutation({
    mutationFn: () => botsApi.clearPersonaPush(botId!, token!),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['bot', botId] }),
  });

  const handlePushSubmit = (e: FormEvent) => {
    e.preventDefault();
    if (!pushDirection.trim()) return;
    pushMutation.mutate(pushDirection.trim());
  };

  return (
    <div className="page">
      <div className="page-header">
        <h1>Persona — {bot?.name ?? '…'}</h1>
      </div>

      <div className="card form-card">
        <h3>Current Persona</h3>
        <p className="persona-text" style={{ marginTop: 12 }}>{bot?.evolvingPersonaPrompt ?? '…'}</p>
      </div>

      <div className="card form-card">
        <h3>Persona Push</h3>
        <p className="muted" style={{ marginBottom: 12, fontSize: '0.875rem' }}>
          Nudge the bot's personality in a direction without resetting it. The push fades over a few evolution cycles.
        </p>

        {bot?.personaPushText && (
          <div className="alert" style={{ marginBottom: 12, background: 'var(--card-bg)', border: '1px solid var(--border)' }}>
            <strong>Active push</strong> ({bot.personaPushRemainingCycles} cycle{bot.personaPushRemainingCycles !== 1 ? 's' : ''} remaining):{' '}
            <em>"{bot.personaPushText}"</em>
            <button
              className="btn btn-secondary btn-sm"
              style={{ marginLeft: 12 }}
              onClick={() => clearPushMutation.mutate()}
              disabled={clearPushMutation.isPending}
            >
              Clear
            </button>
          </div>
        )}

        {pushError && <div className="alert alert-error" style={{ marginBottom: 8 }}>{pushError}</div>}

        <form onSubmit={handlePushSubmit} className="form" style={{ display: 'flex', gap: 8, alignItems: 'flex-end' }}>
          <div className="form-group" style={{ flex: 1, marginBottom: 0 }}>
            <label>Direction</label>
            <input
              value={pushDirection}
              onChange={e => setPushDirection(e.target.value)}
              placeholder="e.g. become more empathetic and curious"
            />
          </div>
          <button type="submit" className="btn btn-primary" disabled={pushMutation.isPending || !pushDirection.trim()}>
            {pushMutation.isPending ? 'Applying…' : 'Push'}
          </button>
        </form>
      </div>

      <h2 className="section-title">Evolution History</h2>

      {isLoading && <p className="muted">Loading…</p>}
      {snapshots?.length === 0 && <div className="empty-state"><p>No persona changes recorded yet.</p></div>}

      <div className="timeline">
        {snapshots?.map(s => (
          <div key={s.id} className="timeline-item">
            <div className="timeline-date">{new Date(s.createdAt).toLocaleString()}</div>
            <div className="card">
              <p className="persona-text">{s.snapshotText}</p>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
