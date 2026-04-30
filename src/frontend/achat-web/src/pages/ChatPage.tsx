import { useState, useEffect, useRef, type FormEvent } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import * as signalR from '@microsoft/signalr';
import { botsApi } from '../api/bots';
import { useAuth } from '../contexts/AuthContext';
import { HUB_URL } from '../api/client';

interface ChatMsg {
  role: 'user' | 'assistant';
  content: string;
  id?: string;
  streaming?: boolean;
}

export function ChatPage() {
  const { id: botId } = useParams<{ id: string }>();
  const { token } = useAuth();
  const [messages, setMessages] = useState<ChatMsg[]>([]);
  const [input, setInput] = useState('');
  const [connected, setConnected] = useState(false);
  const [sending, setSending] = useState(false);
  const connRef = useRef<signalR.HubConnection | null>(null);
  const bottomRef = useRef<HTMLDivElement | null>(null);

  const { data: bot } = useQuery({
    queryKey: ['bot', botId],
    queryFn: () => botsApi.get(botId!, token!),
    enabled: !!botId,
  });

  useEffect(() => {
    const conn = new signalR.HubConnectionBuilder()
      .withUrl(HUB_URL, { accessTokenFactory: () => token ?? '' })
      .withAutomaticReconnect()
      .build();

    conn.on('ReceiveToken', (chunk: string) => {
      setMessages(prev => {
        const last = prev[prev.length - 1];
        if (last?.streaming) {
          return [...prev.slice(0, -1), { ...last, content: last.content + chunk }];
        }
        return [...prev, { role: 'assistant', content: chunk, streaming: true }];
      });
    });

    conn.on('ReceiveMessageComplete', (messageId: string) => {
      setMessages(prev => {
        const last = prev[prev.length - 1];
        return last?.streaming
          ? [...prev.slice(0, -1), { ...last, streaming: false, id: messageId }]
          : prev;
      });
      setSending(false);
    });

    conn.on('Error', (msg: string) => {
      setMessages(prev => [...prev, { role: 'assistant', content: `⚠️ ${msg}` }]);
      setSending(false);
    });

    conn.onclose(() => setConnected(false));
    conn.onreconnected(() => setConnected(true));
    conn.start().then(() => setConnected(true)).catch(console.error);

    connRef.current = conn;
    return () => { conn.stop(); };
  }, [token]);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  const handleSend = async (e: FormEvent) => {
    e.preventDefault();
    const text = input.trim();
    if (!text || !connected || sending || !botId) return;
    setMessages(prev => [...prev, { role: 'user', content: text }]);
    setInput('');
    setSending(true);
    try {
      await connRef.current?.invoke('SendMessage', botId, text);
    } catch {
      setMessages(prev => [...prev, { role: 'assistant', content: '⚠️ Failed to send.' }]);
      setSending(false);
    }
  };

  return (
    <div className="chat-page">
      <div className="chat-header">
        <h2>{bot?.name ?? 'Chat'}</h2>
        <span className={`status-dot ${connected ? 'online' : 'offline'}`} title={connected ? 'Connected' : 'Disconnected'} />
      </div>

      <div className="chat-messages">
        {messages.length === 0 && (
          <div className="chat-empty">Send a message to start the conversation.</div>
        )}
        {messages.map((msg, i) => (
          <div key={msg.id ?? i} className={`message message-${msg.role}`}>
            <div className="message-bubble">
              {msg.content}
              {msg.streaming && <span className="cursor">▊</span>}
            </div>
          </div>
        ))}
        <div ref={bottomRef} />
      </div>

      <form className="chat-input-area" onSubmit={handleSend}>
        <input
          value={input}
          onChange={e => setInput(e.target.value)}
          placeholder={connected ? 'Type a message…' : 'Connecting…'}
          disabled={!connected || sending}
          autoFocus
        />
        <button type="submit" className="btn btn-primary" disabled={!connected || sending || !input.trim()}>
          Send
        </button>
      </form>
    </div>
  );
}
