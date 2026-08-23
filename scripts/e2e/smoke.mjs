import { chromium } from 'playwright';

const BASE = 'http://localhost:5001';
const results = [];
const ok = (name) => { results.push(`PASS  ${name}`); };
const fail = (name, err) => { results.push(`FAIL  ${name}: ${err}`); };

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
const consoleErrors = [];
page.on('console', m => { if (m.type() === 'error') consoleErrors.push(m.text().slice(0, 200)); });
page.on('pageerror', e => consoleErrors.push('PAGEERROR: ' + e.message.slice(0, 200)));

try {
  // 1. Login page
  await page.goto(BASE + '/bots', { waitUntil: 'networkidle' });
  await page.waitForURL('**/login', { timeout: 8000 });
  ok('redirect to login when unauthenticated');
  await page.screenshot({ path: 'shot-login.png' });

  // 2. Sign in
  await page.fill('input[autocomplete="username"]', 'admin');
  await page.fill('input[autocomplete="current-password"]', 'admin');
  await page.click('button[type="submit"]');
  await page.waitForURL('**/bots', { timeout: 10000 });
  ok('login → bots page');

  // 3. Bots list shows Mira
  await page.waitForSelector('text=Mira', { timeout: 8000 });
  ok('bots list renders Mira');
  await page.screenshot({ path: 'shot-bots.png' });

  // 4. Open chat (resolve Mira's current id — bots may have been recreated)
  const miraRow = page.locator('.list-row', { hasText: 'Mira' }).first();
  await miraRow.locator('a').first().click();
  await page.waitForURL(/\/bots\/\d+/);
  await page.waitForSelector('.msg', { timeout: 8000 });
  ok('chat page shows history');

  // 5. Send a message, wait for a NEW streaming reply (count must grow)
  const before = await page.locator('.msg').count();
  await page.fill('.chat-input', 'Quick check — is the pottery class still on Tuesdays?');
  await page.click('button:has-text("Send")');
  await page.waitForFunction((n) => {
    const all = document.querySelectorAll('.msg');
    if (all.length <= n + 1) return false;
    const last = all[all.length - 1];
    const bubble = last.querySelector('.msg-bubble');
    return last.classList.contains('bot') && !last.querySelector('.typing') && !last.querySelector('.msg-error') && bubble && bubble.textContent.trim().length > 5;
  }, before, { timeout: 90000 });
  ok('streaming reply rendered (new message)');
  await page.waitForTimeout(400);
  await page.screenshot({ path: 'shot-chat.png' });

  // 6. Presets page
  await page.click('.sidebar-btn:has-text("Presets")');
  await page.waitForURL('**/presets');
  await page.waitForSelector('text=Qwen local', { timeout: 8000 });
  ok('presets page renders');
  await page.screenshot({ path: 'shot-presets.png' });

  // 7. Admin page
  await page.click('.sidebar-btn:has-text("Admin")');
  await page.waitForURL('**/admin');
  await page.waitForSelector('table.data', { timeout: 8000 });
  ok('admin page renders');
  await page.screenshot({ path: 'shot-admin.png' });

  // 8. Theme toggle cycle
  const themeBtn = page.locator('.sidebar-btn[title^="Theme:"]');
  await themeBtn.click();
  await page.waitForTimeout(200);
  const theme2 = await page.locator('.sidebar-btn[title^="Theme:"]').first().getAttribute('title');
  if (theme2 && !theme2.includes('Theme: system')) ok('theme toggle cycles (now ' + theme2 + ')');
  else fail('theme toggle', theme2);
  await page.screenshot({ path: 'shot-theme.png' });

} catch (e) {
  fail('flow', e.message?.slice(0, 300));
  await page.screenshot({ path: 'shot-error.png' }).catch(() => {});
}

console.log('\n── RESULTS ──');
results.forEach(r => console.log(r));
if (consoleErrors.length) {
  console.log('\n── CONSOLE ERRORS ──');
  consoleErrors.slice(0, 10).forEach(e => console.log(e));
} else {
  console.log('\nno console errors');
}
await browser.close();
process.exit(results.some(r => r.startsWith('FAIL')) ? 1 : 0);
