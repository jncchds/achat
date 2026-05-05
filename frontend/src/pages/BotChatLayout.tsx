import { Outlet, useParams, Link, useNavigate } from 'react-router-dom';
import {
  Box, Typography, List, ListItem, ListItemButton, ListItemText,
  Divider, IconButton, Button, Alert, CircularProgress,
} from '@mui/material';
import DeleteIcon from '@mui/icons-material/Delete';
import AddIcon from '@mui/icons-material/Add';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { conversationsApi } from '../api/conversations';

export default function BotChatLayout() {
  const { botId, conversationId } = useParams<{ botId: string; conversationId?: string }>();
  const navigate = useNavigate();
  const qc = useQueryClient();

  const { data: convs = [], isLoading } = useQuery({
    queryKey: ['conversations', botId],
    queryFn: () => conversationsApi.getAll(botId!),
  });

  const createMut = useMutation({
    mutationFn: () => conversationsApi.create(botId!),
    onSuccess: conv => {
      qc.invalidateQueries({ queryKey: ['conversations', botId] });
      navigate(`/bots/${botId}/chat/${conv.id}`);
    },
  });

  const deleteMut = useMutation({
    mutationFn: (id: string) => conversationsApi.delete(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['conversations', botId] }),
  });

  if (conversationId) {
    return (
      <Box sx={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column', height: '100%' }}>
        <Outlet />
      </Box>
    );
  }

  return (
    <Box sx={{ p: 3 }}>
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
        <Typography variant="h5" sx={{ fontWeight: 700 }}>Conversations</Typography>
        <Button variant="contained" startIcon={<AddIcon />} onClick={() => createMut.mutate()} disabled={createMut.isPending}>
          New Chat
        </Button>
      </Box>

      {isLoading && <CircularProgress />}
      {!isLoading && convs.length === 0 && (
        <Alert severity="info">No conversations yet. Start a new chat!</Alert>
      )}

      <List>
        {convs.map(c => (
          <Box key={c.id}>
            <ListItem disablePadding secondaryAction={
              <IconButton edge="end" size="small" color="error" onClick={() => deleteMut.mutate(c.id)}>
                <DeleteIcon />
              </IconButton>
            }>
              <ListItemButton component={Link} to={`/bots/${botId}/chat/${c.id}`}>
                <ListItemText
                  primary={c.title || 'Untitled'}
                  secondary={new Date(c.updatedAt).toLocaleString()}
                />
              </ListItemButton>
            </ListItem>
            <Divider />
          </Box>
        ))}
      </List>
    </Box>
  );
}


