import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { botsApi } from '../api/bots';
import { useAuth } from '../contexts/AuthContext';

export function PersonaPage() {
  const { id: botId } = useParams<{ id: string }>();
  const { token } = useAuth();

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

  return (
    <div className="page">
      <div className="page-header">
        <h1>Persona — {bot?.name ?? '…'}</h1>
      </div>

      <div className="card form-card">
        <h3>Current Persona</h3>
        <p className="persona-text" style={{ marginTop: 12 }}>{bot?.evolvingPersonaPrompt ?? '…'}</p>
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
