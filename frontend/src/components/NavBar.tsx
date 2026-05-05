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
import ArrowBackIcon from '@mui/icons-material/ArrowBack';
import SettingsIcon from '@mui/icons-material/Settings';
import HistoryIcon from '@mui/icons-material/History';
import PersonIcon from '@mui/icons-material/Person';
import LightModeIcon from '@mui/icons-material/LightMode';
import DarkModeIcon from '@mui/icons-material/DarkMode';
import { useAuth } from '../store/AuthContext';
import { useThemeContext } from '../store/ThemeContext';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { conversationsApi } from '../api/conversations';
import { botsApi } from '../api/bots';

const EXPANDED_WIDTH = 240;
const COLLAPSED_WIDTH = 56;

interface NavBarProps {
  open: boolean;
  onToggle: () => void;
}

export function NavBar({ open, onToggle }: NavBarProps) {
  const { user, logout, isAdmin } = useAuth();
  const { resolvedMode, toggleTheme } = useThemeContext();
  const navigate = useNavigate();
  const location = useLocation();
  const qc = useQueryClient();

  const handleLogout = () => { logout(); navigate('/login'); };

  // Route context detection
  const botMatch = matchPath('/bots/:botId/*', location.pathname);
  const botId = botMatch?.params?.botId ?? null;
  const isBotMode = !!botId;

  const convMatch = matchPath('/bots/:botId/chat/:conversationId', location.pathname);
  const activeConversationId = convMatch?.params?.conversationId;

  const { data: bots = [] } = useQuery({
    queryKey: ['bots'],
    queryFn: botsApi.getAll,
    enabled: !isBotMode,
  });

  const { data: bot } = useQuery({
    queryKey: ['bot', botId],
    queryFn: () => botsApi.get(botId!),
    enabled: isBotMode,
  });

  const { data: convs = [], isLoading: convsLoading } = useQuery({
    queryKey: ['conversations', botId],
    queryFn: () => conversationsApi.getAll(botId!),
    enabled: isBotMode,
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
      if (activeConversationId === id) navigate(`/bots/${botId}/chat`);
    },
  });

  const isOwner = bot?.ownerId === user?.id;
  const ThemeIcon = resolvedMode === 'dark' ? LightModeIcon : DarkModeIcon;
  const themeLabel = resolvedMode === 'dark' ? 'Light mode' : 'Dark mode';
  const width = open ? EXPANDED_WIDTH : COLLAPSED_WIDTH;

  const drawerSx = {
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
  } as const;

  // ─── Bottom actions (shared) ──────────────────────────────────────────────
  const bottomActions = open ? (
    <List dense sx={{ flexShrink: 0 }}>
      <ListItem disablePadding>
        <ListItemButton onClick={toggleTheme} sx={{ px: 1.5 }}>
          <ListItemIcon sx={{ minWidth: 36 }}><ThemeIcon /></ListItemIcon>
          <ListItemText primary={themeLabel} />
        </ListItemButton>
      </ListItem>
      <ListItem disablePadding>
        <ListItemButton onClick={handleLogout} sx={{ px: 1.5 }}>
          <ListItemIcon sx={{ minWidth: 36 }}><LogoutIcon /></ListItemIcon>
          <ListItemText primary={user?.username ?? ''} secondary="Logout" slotProps={{ primary: { noWrap: true } }} />
        </ListItemButton>
      </ListItem>
    </List>
  ) : (
    <List dense sx={{ flexShrink: 0 }}>
      <Tooltip title={themeLabel} placement="right">
        <ListItem disablePadding>
          <ListItemButton onClick={toggleTheme} sx={{ px: 1.5, justifyContent: 'center' }}>
            <ListItemIcon sx={{ minWidth: 0 }}><ThemeIcon /></ListItemIcon>
          </ListItemButton>
        </ListItem>
      </Tooltip>
      <Tooltip title={`${user?.username} · Logout`} placement="right">
        <ListItem disablePadding>
          <ListItemButton onClick={handleLogout} sx={{ px: 1.5, justifyContent: 'center' }}>
            <ListItemIcon sx={{ minWidth: 0 }}><LogoutIcon /></ListItemIcon>
          </ListItemButton>
        </ListItem>
      </Tooltip>
    </List>
  );

  // ─── MAIN MODE ────────────────────────────────────────────────────────────
  if (!isBotMode) {
    const mainNav = [
      { label: 'Bots', icon: <SmartToyIcon />, path: '/bots', exact: true },
      { label: 'Presets', icon: <TuneIcon />, path: '/presets', exact: false },
      { label: 'Usage', icon: <BarChartIcon />, path: '/llm-usage', exact: false },
      ...(isAdmin ? [{ label: 'Users', icon: <PeopleIcon />, path: '/admin/users', exact: false }] : []),
    ];

    return (
      <Drawer variant="permanent" sx={drawerSx}>
        <Box sx={{ display: 'flex', alignItems: 'center', minHeight: 56, px: 1, flexShrink: 0 }}>
          {open && <Typography variant="h6" sx={{ flexGrow: 1, ml: 0.5, whiteSpace: 'nowrap', fontWeight: 700 }}>AChat</Typography>}
          <IconButton onClick={onToggle} size="small">{open ? <ChevronLeftIcon /> : <MenuIcon />}</IconButton>
        </Box>

        <Divider />

        {open ? (
          <>
            <List dense sx={{ flexShrink: 0 }}>
              {mainNav.map(item => (
                <ListItem key={item.path} disablePadding>
                  <ListItemButton component={Link} to={item.path}
                    selected={item.exact ? location.pathname === item.path : location.pathname.startsWith(item.path)}
                    sx={{ px: 1.5 }}>
                    <ListItemIcon sx={{ minWidth: 36 }}>{item.icon}</ListItemIcon>
                    <ListItemText primary={item.label} />
                  </ListItemButton>
                </ListItem>
              ))}
            </List>

            <Divider />

            <Box sx={{ px: 1.5, py: 1, flexShrink: 0 }}>
              <Typography variant="caption" sx={{ fontWeight: 700, color: 'text.secondary', textTransform: 'uppercase', letterSpacing: 0.8 }}>
                Bots
              </Typography>
            </Box>

            <Box sx={{ flex: 1, overflowY: 'auto' }}>
              <List dense disablePadding>
                {bots.map(b => (
                  <ListItem key={b.id} disablePadding>
                    <ListItemButton component={Link} to={`/bots/${b.id}/chat`} sx={{ px: 1.5 }}>
                      <ListItemIcon sx={{ minWidth: 36 }}><SmartToyIcon fontSize="small" /></ListItemIcon>
                      <ListItemText primary={b.name} slotProps={{ primary: { noWrap: true, variant: 'body2' } }} />
                    </ListItemButton>
                  </ListItem>
                ))}
              </List>
            </Box>
          </>
        ) : (
          <>
            <List dense sx={{ flexShrink: 0 }}>
              {mainNav.map(item => (
                <Tooltip key={item.path} title={item.label} placement="right">
                  <ListItem disablePadding>
                    <ListItemButton component={Link} to={item.path}
                      selected={item.exact ? location.pathname === item.path : location.pathname.startsWith(item.path)}
                      sx={{ px: 1.5, justifyContent: 'center' }}>
                      <ListItemIcon sx={{ minWidth: 0 }}>{item.icon}</ListItemIcon>
                    </ListItemButton>
                  </ListItem>
                </Tooltip>
              ))}
            </List>
            <Box sx={{ flexGrow: 1 }} />
          </>
        )}

        <Divider />
        {bottomActions}
      </Drawer>
    );
  }

  // ─── BOT MODE ─────────────────────────────────────────────────────────────
  const botNav = [
    { label: 'Back', icon: <ArrowBackIcon />, path: '/bots', exact: true },
    { label: 'Conversations', icon: <ChatBubbleOutlineIcon />, path: `/bots/${botId}/chat`, exact: false },
    { label: 'Settings', icon: <SettingsIcon />, path: `/bots/${botId}/settings`, exact: true },
    { label: 'Usage', icon: <BarChartIcon />, path: `/bots/${botId}/usage`, exact: true },
    { label: 'History', icon: <HistoryIcon />, path: `/bots/${botId}/evolution`, exact: true },
    ...(isOwner ? [{ label: 'Access', icon: <PersonIcon />, path: `/bots/${botId}/access`, exact: true }] : []),
  ];

  return (
    <Drawer variant="permanent" sx={drawerSx}>
      <Box sx={{ display: 'flex', alignItems: 'center', minHeight: 56, px: 1, flexShrink: 0 }}>
        {open && (
          <Typography variant="subtitle1" sx={{ flexGrow: 1, ml: 0.5, whiteSpace: 'nowrap', fontWeight: 700 }}>
            {bot?.name ?? '…'}
          </Typography>
        )}
        <IconButton onClick={onToggle} size="small">{open ? <ChevronLeftIcon /> : <MenuIcon />}</IconButton>
      </Box>

      <Divider />

      {open ? (
        <>
          <List dense sx={{ flexShrink: 0 }}>
            {botNav.map(item => (
              <ListItem key={item.path} disablePadding>
                <ListItemButton component={Link} to={item.path}
                  selected={item.exact ? location.pathname === item.path : location.pathname.startsWith(item.path)}
                  sx={{ px: 1.5 }}>
                  <ListItemIcon sx={{ minWidth: 36 }}>{item.icon}</ListItemIcon>
                  <ListItemText primary={item.label} />
                </ListItemButton>
              </ListItem>
            ))}
          </List>

          <Divider />

          <Box sx={{ px: 1.5, py: 1, display: 'flex', alignItems: 'center', justifyContent: 'space-between', flexShrink: 0 }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 700, whiteSpace: 'nowrap' }}>Conversations</Typography>
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
            {convs.length === 0 && !convsLoading && (
              <Typography variant="body2" color="text.disabled" sx={{ p: 2, textAlign: 'center' }}>
                No conversations yet
              </Typography>
            )}
            <List dense disablePadding>
              {convs.map(c => (
                <Box key={c.id}>
                  <ListItem disablePadding secondaryAction={
                    <IconButton edge="end" size="small" color="error"
                      onClick={e => { e.preventDefault(); deleteMut.mutate(c.id); }}>
                      <DeleteIcon fontSize="small" />
                    </IconButton>
                  }>
                    <ListItemButton component={Link} to={`/bots/${botId}/chat/${c.id}`}
                      selected={activeConversationId === c.id} sx={{ pr: 5 }}>
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
        </>
      ) : (
        <>
          <List dense sx={{ flexShrink: 0 }}>
            {botNav.map(item => (
              <Tooltip key={item.path} title={item.label} placement="right">
                <ListItem disablePadding>
                  <ListItemButton component={Link} to={item.path}
                    selected={item.exact ? location.pathname === item.path : location.pathname.startsWith(item.path)}
                    sx={{ px: 1.5, justifyContent: 'center' }}>
                    <ListItemIcon sx={{ minWidth: 0 }}>{item.icon}</ListItemIcon>
                  </ListItemButton>
                </ListItem>
              </Tooltip>
            ))}
          </List>

          <Divider />

          <Tooltip title="New chat" placement="right">
            <ListItem disablePadding>
              <ListItemButton onClick={() => createMut.mutate()} disabled={createMut.isPending}
                sx={{ px: 1.5, justifyContent: 'center' }}>
                <ListItemIcon sx={{ minWidth: 0 }}><AddIcon fontSize="small" /></ListItemIcon>
              </ListItemButton>
            </ListItem>
          </Tooltip>

          <Box sx={{ flex: 1, overflowY: 'auto', overflowX: 'hidden' }}>
            <List dense disablePadding>
              {convs.map(c => (
                <Tooltip key={c.id} title={c.title || 'Untitled'} placement="right">
                  <ListItem disablePadding>
                    <ListItemButton component={Link} to={`/bots/${botId}/chat/${c.id}`}
                      selected={activeConversationId === c.id} sx={{ px: 1.5, justifyContent: 'center' }}>
                      <ListItemIcon sx={{ minWidth: 0 }}><ChatBubbleOutlineIcon fontSize="small" /></ListItemIcon>
                    </ListItemButton>
                  </ListItem>
                </Tooltip>
              ))}
            </List>
          </Box>
        </>
      )}

      <Divider />
      {bottomActions}
    </Drawer>
  );
}



