import { useState, useEffect, useRef } from 'react';
import { useParams } from 'react-router-dom';
import {
  Box, Button, Typography, TextField, Paper, CircularProgress, Divider, Alert
} from '@mui/material';
import SendIcon from '@mui/icons-material/Send';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { conversationsApi, type MessageDto } from '../api/conversations';

export default function ChatPage() {
  const { conversationId } = useParams<{ conversationId: string }>();
  const qc = useQueryClient();
  const bottomRef = useRef<HTMLDivElement>(null);

  const { data: conv } = useQuery({ queryKey: ['conversation', conversationId], queryFn: () => conversationsApi.get(conversationId!) });
  const { data: initialMessages = [], isLoading } = useQuery({
    queryKey: ['messages', conversationId],
    queryFn: () => conversationsApi.getMessages(conversationId!)
  });

  const [messages, setMessages] = useState<MessageDto[]>([]);
  const [input, setInput] = useState('');
  const [streaming, setStreaming] = useState(false);
  const [error, setError] = useState('');
  const streamingRef = useRef(false);
  // Track IDs already rendered so external events don't duplicate
  const shownIds = useRef<Set<string>>(new Set());

  useEffect(() => {
    shownIds.current = new Set(initialMessages.map(m => m.id));
    setMessages(initialMessages);
  }, [initialMessages]);

  useEffect(() => { bottomRef.current?.scrollIntoView({ behavior: 'smooth' }); }, [messages]);

  // Subscribe to live events from other tabs / Telegram
  useEffect(() => {
    if (!conversationId) return;
    const token = localStorage.getItem('token');
    const abortCtrl = new AbortController();

    (async () => {
      try {
        const response = await fetch(`/api/conversations/${conversationId}/events`, {
          headers: { Authorization: `Bearer ${token}` },
          signal: abortCtrl.signal,
        });
        if (!response.ok || !response.body) return;

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });
          const lines = buffer.split('\n');
          buffer = lines.pop() ?? '';
          for (const line of lines) {
            if (!line.startsWith('data: ')) continue;
            const payload = line.slice(6).trim();
            if (!payload || payload.startsWith(':')) continue;
            try {
              const msg: MessageDto = JSON.parse(payload);
              if (shownIds.current.has(msg.id)) continue;
              // When we're streaming our own response, ignore events for that session;
              // the post-stream query invalidation will give us canonical state.
              if (streamingRef.current) continue;
              shownIds.current.add(msg.id);
              setMessages(prev => [...prev, msg]);
            } catch { /* malformed */ }
          }
        }
      } catch { /* aborted or connection error */ }
    })();

    return () => abortCtrl.abort();
  }, [conversationId]);

  const sendMessage = async () => {
    if (!input.trim() || streaming) return;
    const content = input.trim();
    setInput('');
    setError('');

    const userMsg: MessageDto = { id: crypto.randomUUID(), role: 'user', content, createdAt: new Date().toISOString() };
    const assistantMsg: MessageDto = { id: crypto.randomUUID(), role: 'assistant', content: '', createdAt: new Date().toISOString() };
    setMessages(prev => [...prev, userMsg, assistantMsg]);
    setStreaming(true);
    streamingRef.current = true;

    try {
      const token = localStorage.getItem('token');
      const response = await fetch(`/api/conversations/${conversationId}/chat`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
        body: JSON.stringify({ content }),
      });

      if (!response.ok) { setError('Failed to send message'); streamingRef.current = false; setStreaming(false); return; }

      const reader = response.body!.getReader();
      const decoder = new TextDecoder();
      let buffer = '';
      let accumulated = '';

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n');
        buffer = lines.pop() ?? '';
        for (const line of lines) {
          if (line.startsWith('data: ')) {
            const payload = line.slice(6);
            if (payload === '[DONE]') break;
            accumulated += payload;
            setMessages(prev => prev.map((m, i) => i === prev.length - 1 ? { ...m, content: accumulated } : m));
          }
        }
      }
      qc.invalidateQueries({ queryKey: ['messages', conversationId] });
    } catch (e) {
      setError('Connection error');
    } finally {
      streamingRef.current = false;
      setStreaming(false);
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendMessage(); }
  };

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <Box sx={{ px: 2, py: 1, borderBottom: 1, borderColor: 'divider', display: 'flex', alignItems: 'center', gap: 2 }}>
        <Typography sx={{ fontWeight: 700 }}>{conv?.title || 'Chat'}</Typography>
      </Box>

      <Box sx={{ flex: 1, overflow: 'auto', p: 2 }}>
        {isLoading && <CircularProgress />}
        {error && <Alert severity="error" sx={{ mb: 1 }}>{error}</Alert>}
        {messages.filter(m => m.role !== 'system').map(m => (
          <Box key={m.id} sx={{ display: 'flex', justifyContent: m.role === 'user' ? 'flex-end' : 'flex-start', mb: 1.5 }}>
            <Paper elevation={1} sx={{
              p: 1.5, maxWidth: '75%', bgcolor: m.role === 'user' ? 'primary.main' : 'grey.100',
              color: m.role === 'user' ? 'primary.contrastText' : 'text.primary',
              borderRadius: m.role === 'user' ? '18px 18px 4px 18px' : '18px 18px 18px 4px',
              whiteSpace: 'pre-wrap', wordBreak: 'break-word'
            }}>
              {m.content || (streaming && m.role === 'assistant' ? <CircularProgress size={16} /> : '')}
            </Paper>
          </Box>
        ))}
        <div ref={bottomRef} />
      </Box>

      <Divider />
      <Box sx={{ p: 2, display: 'flex', gap: 1 }}>
        <TextField
          fullWidth multiline maxRows={4} size="small"
          placeholder="Type a message… (Enter to send, Shift+Enter for newline)"
          value={input} onChange={e => setInput(e.target.value)} onKeyDown={handleKeyDown}
          disabled={streaming}
        />
        <Button variant="contained" onClick={sendMessage} disabled={streaming || !input.trim()} endIcon={<SendIcon />}>
          Send
        </Button>
      </Box>
    </Box>
  );
}
