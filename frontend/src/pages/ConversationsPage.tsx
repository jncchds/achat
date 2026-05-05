import { useParams, Link } from 'react-router-dom';
import {
  Box, Button, Typography, List, ListItem, ListItemButton,
  ListItemText, Divider, IconButton, Alert
} from '@mui/material';
import DeleteIcon from '@mui/icons-material/Delete';
import AddIcon from '@mui/icons-material/Add';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { conversationsApi } from '../api/conversations';

export default function ConversationsPage() {
  const { botId } = useParams<{ botId: string }>();
  const qc = useQueryClient();

  const { data: convs = [], isLoading } = useQuery({
    queryKey: ['conversations', botId],
    queryFn: () => conversationsApi.getAll(botId!)
  });

  const createMut = useMutation({
    mutationFn: () => conversationsApi.create(botId!),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['conversations', botId] })
  });
  const deleteMut = useMutation({
    mutationFn: conversationsApi.delete,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['conversations', botId] })
  });

  return (
    <Box sx={{ p: 3, maxWidth: 600 }}>
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
        <Typography variant="h5" sx={{ fontWeight: 700 }}>Conversations</Typography>
        <Button variant="contained" startIcon={<AddIcon />} onClick={() => createMut.mutate()} disabled={createMut.isPending}>
          New Chat
        </Button>
      </Box>

      {isLoading && <Typography>Loading…</Typography>}
      {convs.length === 0 && !isLoading && <Alert severity="info">No conversations yet. Start a new chat!</Alert>}

      <List>
        {convs.map(c => (
          <Box key={c.id}>
            <ListItem disablePadding secondaryAction={
              <IconButton edge="end" onClick={() => deleteMut.mutate(c.id)} size="small" color="error">
                <DeleteIcon />
              </IconButton>
            }>
              <ListItemButton component={Link} to={`/conversations/${c.id}`}>
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
