import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';

const token = process.argv[2];
const botId = parseInt(process.argv[3] || '1');
const message = process.argv[4] || "Hey Mira, remember: my sister's name is Ann.";

const conn = new HubConnectionBuilder()
  .withUrl((process.env.ACHAT_BASE || 'http://localhost:5001') + '/hubs/chat', { accessTokenFactory: () => token })
  .withAutomaticReconnect()
  .configureLogging(LogLevel.Warning)
  .build();

let done = false;
conn.on('chat.started', m => console.log('STARTED', JSON.stringify(m)));
conn.on('chat.delta', m => { process.stdout.write('Δ ' + m.delta + ' '); });
conn.on('chat.reasoning', m => { process.stdout.write('R ' + m.delta + ' '); });
conn.on('chat.completed', m => { console.log('\nCOMPLETED', JSON.stringify(m)); done = true; });
conn.on('chat.failed', m => { console.log('\nFAILED', JSON.stringify(m)); done = true; });

await conn.start();
await conn.invoke('SendAsync', botId, message);

const t0 = Date.now();
while (!done && Date.now() - t0 < 120000) await new Promise(r => setTimeout(r, 200));
await conn.stop();
process.exit(done ? 0 : 2);
