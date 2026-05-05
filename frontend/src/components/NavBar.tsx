import {
  Box, Divider, Drawer, IconButton, List, ListItem,
  ListItemButton, ListItemIcon, ListItemText, Tooltip, Typography,
} from '@mui/material';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import SmartToyIcon from '@mui/icons-material/SmartToy';
import TuneIcon from '@mui/icons-material/Tune';
import BarChartIcon from '@mui/icons-material/BarChart';
import PeopleIcon from '@mui/icons-material/People';
import LogoutIcon from '@mui/icons-material/Logout';
import MenuIcon from '@mui/icons-material/Menu';
import ChevronLeftIcon from '@mui/icons-material/ChevronLeft';
import { useAuth } from '../store/AuthContext';

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

  const handleLogout = () => { logout(); navigate('/login'); };

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
        },
      }}
    >
      <Box sx={{ display: 'flex', alignItems: 'center', minHeight: 56, px: 1 }}>
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

      <List dense>
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

      <Box sx={{ flexGrow: 1 }} />
      <Divider />

      <List dense>
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
