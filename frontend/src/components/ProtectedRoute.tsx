import { Navigate, Outlet } from 'react-router-dom';
import { useAuth } from '../store/AuthContext';
import { Box, CircularProgress } from '@mui/material';

export function ProtectedRoute({ requireAdmin = false }: { requireAdmin?: boolean }) {
  const { user, isLoading, isAdmin } = useAuth();
  if (isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', mt: 8 }}><CircularProgress /></Box>;
  if (!user) return <Navigate to="/login" replace />;
  if (requireAdmin && !isAdmin) return <Navigate to="/bots" replace />;
  return <Outlet />;
}
