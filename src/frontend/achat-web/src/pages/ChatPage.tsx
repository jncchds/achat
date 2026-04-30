import { useState, useEffect, useRef, useCallback, type FormEvent } from 'react';
import { useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import * as signalR from '@microsoft/signalr';
import { botsApi } from '../api/bots';
import { conversationsApi, type ConversationMessage } from '../api/conversations';
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
  const [activeConversationId, setActiveConversationId] = useState<string | null>(null);
  const [connected, setConnected] = useState(false);
  const [sending, setSending] = useState(false);
  const [creatingConversation, setCreatingConversation] = useState(false);
  const connRef = useRef<signalR.HubConnection | null>(null);
  const bottomRef = useRef<HTMLDivElement | null>(null);
  const queryClient = useQueryClient();

  // Refs so connection event handlers always see the latest values without stale closures
  const sendingRef = useRef(false);
  const activeConversationIdRef = useRef<string | null>(null);

  // Keep refs in sync with state
  useEffect(() => { sendingRef.current = sending; }, [sending]);
  useEffect(() => { activeConversationIdRef.current = activeConversationId; }, [activeConversationId]);

  const { data: bot } = useQuery({
    queryKey: ['bot', botId],
    queryFn: () => botsApi.get(botId!, token!),
    enabled: !!botId,
  });

  const { data: conversations } = useQuery({
    queryKey: ['conversations', botId],
    queryFn: () => conversationsApi.list(botId!, token!),
    enabled: !!botId && !!token,
  });

  const { data: conversationMessages, isLoading: loadingMessages } = useQuery({
    queryKey: ['conversation-messages', botId, activeConversationId],
    queryFn: () => conversationsApi.messages(botId!, activeConversationId!, token!),
    enabled: !!botId && !!token && !!activeConversationId,
  });

  const createConversationMutation = useMutation({
    mutationFn: () => conversationsApi.create(botId!, token!),
    onSuccess: async (created) => {
      setActiveConversationId(created.id);
      await queryClient.invalidateQueries({ queryKey: ['conversations', botId] });
    },
  });

  useEffect(() => {
    if (conversations && conversations.length > 0 && !activeConversationId) {
      setActiveConversationId(conversations[0].id);
    }
  }, [conversations, activeConversationId]);

  useEffect(() => {
    if (!activeConversationId) {
      setMessages([]);
      return;
    }

    if (!sending) {
      const mapped = (conversationMessages ?? []).map((msg: ConversationMessage) => ({
        id: msg.id,
        role: msg.role,
        content: msg.content,
      }));
      setMessages(mapped);
    }
  }, [conversationMessages, activeConversationId, sending]);

  // Build and start the hub connection once per token/botId — NOT per conversation.
  // activeConversationId is read via ref inside handlers to avoid tearing down the socket.
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
      queryClient.invalidateQueries({ queryKey: ['conversations', botId] });
      const convId = activeConversationIdRef.current;
      if (convId) {
        queryClient.invalidateQueries({ queryKey: ['conversation-messages', botId, convId] });
      }
    });

    conn.on('ConversationResolved', (conversationId: string) => {
      setActiveConversationId(conversationId);
    });

    conn.on('BotInitiatedMessageStart', (_botId: string, conversationId: string) => {
      // Switch to the conversation the bot is speaking into, if it's a different one
      setActiveConversationId(conversationId);
    });

    conn.on('Error', (msg: string) => {
      setMessages(prev => [...prev, { role: 'assistant', content: `⚠️ ${msg}` }]);
      setSending(false);
    });

    conn.onclose(() => {
      setConnected(false);
      // Unstick the send state if the connection dropped mid-stream
      if (sendingRef.current) {
        setSending(false);
        setMessages(prev => {
          const last = prev[prev.length - 1];
          const withComplete = last?.streaming
            ? [...prev.slice(0, -1), { ...last, streaming: false }]
            : prev;
          return [...withComplete, { role: 'assistant', content: '⚠️ Disconnected mid-response.' }];
        });
      }
    });

    conn.onreconnected(() => setConnected(true));
    conn.start().then(() => setConnected(true)).catch(console.error);

    connRef.current = conn;
    return () => { conn.stop(); };
  }, [token, queryClient, botId]); // activeConversationId intentionally excluded

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  const handleStop = useCallback(async () => {
    const conn = connRef.current;
    if (!conn) return;

    // Seal the partial message immediately so the user sees what was received
    setMessages(prev => {
      const last = prev[prev.length - 1];
      return last?.streaming
        ? [...prev.slice(0, -1), { ...last, streaming: false }]
        : prev;
    });
    setSending(false);

    // Stopping the connection triggers ConnectionAborted on the server, which cancels the stream.
    // withAutomaticReconnect will then reconnect automatically.
    await conn.stop();
  }, []);

  const handleSend = async (e: FormEvent) => {
    e.preventDefault();
    const text = input.trim();
    if (!text || !connected || sending || !botId) return;

    let conversationId = activeConversationId;
    if (!conversationId) {
      setCreatingConversation(true);
      try {
        const created = await createConversationMutation.mutateAsync();
        conversationId = created.id;
        setActiveConversationId(created.id);
      } finally {
        setCreatingConversation(false);
      }
    }

    setMessages(prev => [...prev, { role: 'user', content: text }]);
    setInput('');
    setSending(true);
    try {
      await connRef.current?.invoke('SendMessage', botId, text, conversationId);
    } catch {
      setMessages(prev => [...prev, { role: 'assistant', content: '⚠️ Failed to send.' }]);
      setSending(false);
    }
  };

  const handleCreateConversation = async () => {
    if (!botId || !token || creatingConversation || sending) return;
    setCreatingConversation(true);
    try {
      const created = await createConversationMutation.mutateAsync();
      setActiveConversationId(created.id);
      setMessages([]);
    } finally {
      setCreatingConversation(false);
    }
  };

  return (
    <div className="chat-page chat-layout">
      <aside className="conversation-sidebar">
        <div className="conversation-sidebar-header">
          <h3>Conversations</h3>
          <button
            type="button"
            className="btn btn-primary btn-sm"
            onClick={handleCreateConversation}
            disabled={creatingConversation || sending}
          >
            {creatingConversation ? 'Creating…' : 'New'}
          </button>
        </div>
        <div className="conversation-list">
          {!conversations || conversations.length === 0 ? (
            <div className="muted">No conversations yet.</div>
          ) : (
            conversations.map(conv => (
              <button
                key={conv.id}
                type="button"
                className={`conversation-item ${activeConversationId === conv.id ? 'active' : ''}`}
                onClick={() => setActiveConversationId(conv.id)}
                disabled={sending}
                title={conv.title}
              >
                <div className="conversation-item-title">{conv.title}</div>
                <div className="conversation-item-meta">
                  {new Date(conv.updatedAt).toLocaleString()}
                </div>
              </button>
            ))
          )}
        </div>
      </aside>

      <div className="chat-main-panel">
        <div className="chat-header">
          <h2>{bot?.name ?? 'Chat'}</h2>
          <span className={`status-dot ${connected ? 'online' : 'offline'}`} title={connected ? 'Connected' : 'Disconnected'} />
        </div>

        <div className="chat-messages">
          {loadingMessages && activeConversationId && (
            <div className="chat-empty">Loading conversation…</div>
          )}
          {!loadingMessages && messages.length === 0 && (
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
            disabled={!connected || sending || creatingConversation}
            autoFocus
          />
          {sending ? (
            <button type="button" className="btn btn-secondary" onClick={handleStop}>
              Stop
            </button>
          ) : (
            <button type="submit" className="btn btn-primary" disabled={!connected || creatingConversation || !input.trim()}>
              Send
            </button>
          )}
        </form>
      </div>
    </div>
  );
}
