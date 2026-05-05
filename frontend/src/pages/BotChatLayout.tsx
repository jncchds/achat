import { Link, Outlet, useNavigate, useParams } from 'react-router-dom';
import {
  Box, CircularProgress, Divider, IconButton, List, ListItem,
  ListItemButton, ListItemText, Tooltip, Typography,
} from '@mui/material';
import AddIcon from '@mui/icons-material/Add';
import DeleteIcon from '@mui/icons-material/Delete';
import ChatBubbleOutlineIcon from '@mui/icons-material/ChatBubbleOutline';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { conversationsApi } from '../api/conversations';

const PANEL_WIDTH = 260;

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
    onSuccess: (_, id) => {
      qc.invalidateQueries({ queryKey: ['conversations', botId] });
      if (conversationId === id) navigate(`/bots/${botId}/chat`);
    },
  });

  return (
    <Box sx={{ display: 'flex', height: '100%' }}>
      {/* Left panel */}
      <Box sx={{
        width: PANEL_WIDTH, flexShrink: 0,
        borderRight: 1, borderColor: 'divider',
        display: 'flex', flexDirection: 'column', overflow: 'hidden',
      }}>
        <Box sx={{
          px: 1.5, py: 1,
          display: 'flex', alignItems: 'center', justifyContent: 'space-between',
          borderBottom: 1, borderColor: 'divider',
        }}>
          <Typography variant="subtitle2" sx={{ fontWeight: 700 }}>Conversations</Typography>
          <Tooltip title="New chat">
            <span>
              <IconButton size="small" onClick={() => createMut.mutate()} disabled={createMut.isPending}>
                <AddIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>
        </Box>

        <Box sx={{ flex: 1, overflowY: 'auto' }}>
          {isLoading && <CircularProgress size={20} sx={{ m: 2 }} />}
          {convs.length === 0 && !isLoading && (
            <Typography variant="body2" color="text.disabled" sx={{ p: 2, textAlign: 'center' }}>
              No conversations yet
            </Typography>
          )}
          <List dense disablePadding>
            {convs.map(c => (
              <Box key={c.id}>
                <ListItem
                  disablePadding
                  secondaryAction={
                    <IconButton
                      edge="end" size="small" color="error"
                      onClick={e => { e.preventDefault(); deleteMut.mutate(c.id); }}
                    >
                      <DeleteIcon fontSize="small" />
                    </IconButton>
                  }
                >
                  <ListItemButton
                    component={Link}
                    to={`/bots/${botId}/chat/${c.id}`}
                    selected={conversationId === c.id}
                    sx={{ pr: 5 }}
                  >
                    <ListItemText
                      primary={c.title || 'Untitled'}
                      secondary={new Date(c.updatedAt).toLocaleDateString()}
                      slotProps={{ primary: { noWrap: true, variant: 'body2' } }}
                    />
                  </ListItemButton>
                </ListItem>
                <Divider />
              </Box>
            ))}
          </List>
        </Box>
      </Box>

      {/* Right panel */}
      <Box sx={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
        {conversationId
          ? <Outlet />
          : (
            <Box sx={{
              display: 'flex', flexDirection: 'column',
              alignItems: 'center', justifyContent: 'center',
              height: '100%', gap: 2, color: 'text.disabled',
            }}>
              <ChatBubbleOutlineIcon sx={{ fontSize: 64 }} />
              <Typography>Select a conversation or start a new one</Typography>
            </Box>
          )}
      </Box>
    </Box>
  );
}
