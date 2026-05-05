import { Outlet, useParams } from 'react-router-dom';
import { Box, Typography } from '@mui/material';
import ChatBubbleOutlineIcon from '@mui/icons-material/ChatBubbleOutlined';

export default function BotChatLayout() {
  const { conversationId } = useParams<{ botId: string; conversationId?: string }>();

  return (
    <Box sx={{ flex: 1, overflow: 'hidden', display: 'flex', flexDirection: 'column', height: '100%' }}>
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
  );
}

