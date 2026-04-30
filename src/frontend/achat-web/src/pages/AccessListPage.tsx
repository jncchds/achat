import { useParams } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { accessApi } from '../api/access';
import { botsApi } from '../api/bots';
import { useAuth } from '../contexts/AuthContext';

export function AccessListPage() {
  const { id: botId } = useParams<{ id: string }>();
  const { token } = useAuth();
  const qc = useQueryClient();

  const { data: bot } = useQuery({
    queryKey: ['bot', botId],
    queryFn: () => botsApi.get(botId!, token!),
    enabled: !!botId,
  });

  const { data: entries, isLoading } = useQuery({
    queryKey: ['access-list', botId],
    queryFn: () => accessApi.listAccess(botId!, token!),
    enabled: !!botId,
  });

  const deleteMut = useMutation({
    mutationFn: (entryId: string) => accessApi.removeAccess(botId!, entryId, token!),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['access-list', botId] }),
  });

  return (
    <div className="page">
      <div className="page-header">
        <h1>Access List — {bot?.name ?? '…'}</h1>
      </div>

      {isLoading && <p className="muted">Loading…</p>}
      {entries?.length === 0 && <div className="empty-state"><p>Access list is empty.</p></div>}

      {entries && entries.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr><th>Type</th><th>Subject ID</th><th>Status</th><th>Added</th><th></th></tr>
            </thead>
            <tbody>
              {entries.map(e => (
                <tr key={e.id}>
                  <td>{e.subjectType}</td>
                  <td><code>{e.subjectId}</code></td>
                  <td><span className={`badge badge-${e.status.toLowerCase()}`}>{e.status}</span></td>
                  <td>{new Date(e.addedAt).toLocaleString()}</td>
                  <td>
                    <div className="td-actions">
                      <button
                        className="btn btn-danger btn-sm"
                        onClick={() => { if (confirm('Remove this entry?')) deleteMut.mutate(e.id); }}
                      >Remove</button>
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
