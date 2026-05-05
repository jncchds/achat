import {
  Box, Divider, Drawer, IconButton, List, ListItem,
  ListItemButton, ListItemIcon, ListItemText, Tooltip, Typography, CircularProgress,
} from '@mui/material';
import { Link, matchPath, useLocation, useNavigate } from 'react-router-dom';
import SmartToyIcon from '@mui/icons-material/SmartToy';
import TuneIcon from '@mui/icons-material/Tune';
import BarChartIcon from '@mui/icons-material/BarChart';
import PeopleIcon from '@mui/icons-material/People';
import LogoutIcon from '@mui/icons-material/Logout';
import MenuIcon from '@mui/icons-material/Menu';
import ChevronLeftIcon from '@mui/icons-material/ChevronLeft';
import AddIcon from '@mui/icons-material/Add';
import DeleteIcon from '@mui/icons-material/Delete';
import ChatBubbleOutlineIcon from '@mui/icons-material/ChatBubbleOutlined';
import { useAuth } from '../store/AuthContext';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { conversationsApi } from '../api/conversations';

const EXPANDED_WIDTH = 220;
const COLLAPSED_WIDTH = 56;

interface NavBarProps {
  open: boolean;
  onToggle: () => void;
}

export function NavBar({ open, onToggle }: NavBarProps) {
  const { user, logout, isAdmin } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const qc = useQueryClient();

  const handleLogout = () => { logout(); navigate('/login'); };

  // Detect chat routes and extract botId / conversationId
  const chatMatch = matchPath('/bots/:botId/chat/*', location.pathname);
  const chatBotId = chatMatch?.params?.botId;
  const convMatch = matchPath('/bots/:botId/chat/:conversationId', location.pathname);
  const activeConversationId = convMatch?.params?.conversationId;

  const { data: convs = [], isLoading: convsLoading } = useQuery({
    queryKey: ['conversations', chatBotId],
    queryFn: () => conversationsApi.getAll(chatBotId!),
    enabled: !!chatBotId,
  });

  const createMut = useMutation({
    mutationFn: () => conversationsApi.create(chatBotId!),
    onSuccess: conv => {
      qc.invalidateQueries({ queryKey: ['conversations', chatBotId] });
      navigate(`/bots/${chatBotId}/chat/${conv.id}`);
    },
  });

  const deleteMut = useMutation({
    mutationFn: (id: string) => conversationsApi.delete(id),
    onSuccess: (_, id) => {
      qc.invalidateQueries({ queryKey: ['conversations', chatBotId] });
      if (activeConversationId === id) navigate(`/bots/${chatBotId}/chat`);
    },
  });

  const navItems = [
    { label: 'Bots', icon: <SmartToyIcon />, path: '/bots' },
    { label: 'Presets', icon: <TuneIcon />, path: '/presets' },
    { label: 'Usage', icon: <BarChartIcon />, path: '/llm-usage' },
    ...(isAdmin ? [{ label: 'Users', icon: <PeopleIcon />, path: '/admin/users' }] : []),
  ];

  const width = open ? EXPANDED_WIDTH : COLLAPSED_WIDTH;

  return (
    <Drawer
      variant="permanent"
      sx={{
        width,
        flexShrink: 0,
        '& .MuiDrawer-paper': {
          width,
          overflowX: 'hidden',
          transition: 'width 0.2s ease',
          boxSizing: 'border-box',
          display: 'flex',
          flexDirection: 'column',
        },
      }}
    >
      {/* Header */}
      <Box sx={{ display: 'flex', alignItems: 'center', minHeight: 56, px: 1, flexShrink: 0 }}>
        {open && (
          <Typography variant="h6" sx={{ flexGrow: 1, ml: 0.5, whiteSpace: 'nowrap', fontWeight: 700 }}>
            AChat
          </Typography>
        )}
        <IconButton onClick={onToggle} size="small">
          {open ? <ChevronLeftIcon /> : <MenuIcon />}
        </IconButton>
      </Box>

      <Divider />

      {/* Nav items */}
      <List dense sx={{ flexShrink: 0 }}>
        {navItems.map(item => (
          <Tooltip key={item.path} title={open ? '' : item.label} placement="right">
            <ListItem disablePadding>
              <ListItemButton
                component={Link}
                to={item.path}
                selected={location.pathname.startsWith(item.path)}
                sx={{ px: 1.5 }}
              >
                <ListItemIcon sx={{ minWidth: 36 }}>{item.icon}</ListItemIcon>
                {open && <ListItemText primary={item.label} />}
              </ListItemButton>
            </ListItem>
          </Tooltip>
        ))}
      </List>

      {/* Conversations section — only on chat routes */}
      {chatBotId && (
        <>
          <Divider sx={{ flexShrink: 0 }} />
          <Box sx={{
            flexShrink: 0, px: 1.5, py: 1,
            display: 'flex', alignItems: 'center',
            justifyContent: open ? 'space-between' : 'center',
          }}>
            {open && (
              <Typography variant="subtitle2" sx={{ fontWeight: 700, whiteSpace: 'nowrap' }}>
                Conversations
              </Typography>
            )}
            <Tooltip title="New chat" placement="right">
              <span>
                <IconButton size="small" onClick={() => createMut.mutate()} disabled={createMut.isPending}>
                  <AddIcon fontSize="small" />
                </IconButton>
              </span>
            </Tooltip>
          </Box>

          <Box sx={{ flex: 1, overflowY: 'auto', overflowX: 'hidden' }}>
            {convsLoading && <CircularProgress size={20} sx={{ m: 2 }} />}
            {convs.length === 0 && !convsLoading && open && (
              <Typography variant="body2" color="text.disabled" sx={{ p: 2, textAlign: 'center' }}>
                No conversations yet
              </Typography>
            )}
            <List dense disablePadding>
              {convs.map(c => (
                <Box key={c.id}>
                  {open ? (
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
                        to={`/bots/${chatBotId}/chat/${c.id}`}
                        selected={activeConversationId === c.id}
                        sx={{ pr: 5 }}
                      >
                        <ListItemText
                          primary={c.title || 'Untitled'}
                          secondary={new Date(c.updatedAt).toLocaleDateString()}
                          slotProps={{ primary: { noWrap: true, variant: 'body2' } }}
                        />
                      </ListItemButton>
                    </ListItem>
                  ) : (
                    <Tooltip title={c.title || 'Untitled'} placement="right">
                      <ListItem disablePadding>
                        <ListItemButton
                          component={Link}
                          to={`/bots/${chatBotId}/chat/${c.id}`}
                          selected={activeConversationId === c.id}
                          sx={{ px: 1.5, justifyContent: 'center' }}
                        >
                          <ListItemIcon sx={{ minWidth: 0 }}>
                            <ChatBubbleOutlineIcon fontSize="small" />
                          </ListItemIcon>
                        </ListItemButton>
                      </ListItem>
                    </Tooltip>
                  )}
                  <Divider />
                </Box>
              ))}
            </List>
          </Box>
        </>
      )}

      {/* Spacer — only when not in chat mode */}
      {!chatBotId && <Box sx={{ flexGrow: 1 }} />}

      <Divider />

      {/* Logout */}
      <List dense sx={{ flexShrink: 0 }}>
        <Tooltip title={open ? '' : `${user?.username} · Logout`} placement="right">
          <ListItem disablePadding>
            <ListItemButton onClick={handleLogout} sx={{ px: 1.5 }}>
              <ListItemIcon sx={{ minWidth: 36 }}><LogoutIcon /></ListItemIcon>
              {open && (
                <ListItemText
                  primary={user?.username ?? ''}
                  secondary="Logout"
                  slotProps={{ primary: { noWrap: true } }}
                />
              )}
            </ListItemButton>
          </ListItem>
        </Tooltip>
      </List>
    </Drawer>
  );
}



