import { chromium } from 'playwright';

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

await page.goto('http://localhost:5001/login', { waitUntil: 'networkidle' });
await page.evaluate(() => localStorage.setItem('theme', 'dark'));
await page.reload({ waitUntil: 'networkidle' });
await page.goto('http://localhost:5001/login', { waitUntil: 'networkidle' });
await page.evaluate(() => localStorage.removeItem('theme'));
await page.fill('input[autocomplete="username"]', 'admin');
await page.fill('input[autocomplete="current-password"]', 'admin');
await page.click('button[type="submit"]');
await page.waitForURL('**/bots');
await page.waitForSelector('text=Mira');

const dark = await page.evaluate(() => {
  const cs = getComputedStyle(document.documentElement);
  return {
    bg: cs.getPropertyValue('--bg').trim(),
    surface: cs.getPropertyValue('--surface').trim(),
    accent: cs.getPropertyValue('--accent').trim(),
    text: cs.getPropertyValue('--text').trim(),
    sidebarWidth: document.querySelector('.app-sidebar')?.getBoundingClientRect().width,
    bodyBg: getComputedStyle(document.body).backgroundColor,
  };
});
console.log('dark theme:', JSON.stringify(dark));

// cycle theme to light
for (let i = 0; i < 2; i++) { await page.locator('.sidebar-btn[title^="Theme:"]').first().click(); await page.waitForTimeout(150); }
const light = await page.evaluate(() => {
  const cs = getComputedStyle(document.documentElement);
  return {
    theme: document.documentElement.dataset.theme,
    bg: cs.getPropertyValue('--bg').trim(),
    surface: cs.getPropertyValue('--surface').trim(),
    accent: cs.getPropertyValue('--accent').trim(),
    btnColor: getComputedStyle(document.querySelector('.btn-primary') || document.body).backgroundColor,
  };
});
console.log('light theme:', JSON.stringify(light));

// chat page: bubble colors
await page.click('.list-row a[href="/bots/1"]');
await page.waitForSelector('.chat-input');
const chat = await page.evaluate(() => {
  const user = document.querySelector('.msg.user .msg-bubble');
  const bot = document.querySelector('.msg.bot .msg-bubble');
  return {
    userBubble: user ? getComputedStyle(user).backgroundColor : null,
    botBubble: bot ? getComputedStyle(bot).backgroundColor : null,
    msgCount: document.querySelectorAll('.msg').length,
  };
});
console.log('chat:', JSON.stringify(chat));

await browser.close();
