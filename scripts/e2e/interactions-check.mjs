import { chromium } from 'playwright'

const browser = await chromium.launch()
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
const errors = []
page.on('pageerror', e => errors.push(String(e)))

const BASE = 'http://localhost:5001'
await page.goto(BASE + '/login', { waitUntil: 'networkidle' })
await page.fill('input[autocomplete="username"]', 'admin')
await page.fill('input[autocomplete="current-password"]', 'admin')
await page.click('button[type="submit"]')
await page.waitForURL('**/bots')

// navigate to interactions
await page.goto(BASE + '/admin/interactions', { waitUntil: 'networkidle' })
await page.waitForSelector('.data tbody tr')
const rowCount = await page.locator('.data tbody tr').count()
console.log('interaction rows:', rowCount)

if (rowCount === 0) {
  console.log('FAIL: no interactions recorded')
  process.exit(1)
}

// expand first row (chat purpose)
await page.locator('.data tbody tr.clickable').first().click()
await page.waitForSelector('.code')
const detailText = await page.locator('td[colSpan]').first().innerText()
console.log('detail has request:', detailText.includes('Request (as sent)'))
console.log('detail has response:', detailText.includes('Response') || detailText.includes('Failure'))
console.log('detail has messages json:', detailText.includes('"messages"'))

// failed-only filter
await page.goto(BASE + '/admin/interactions', { waitUntil: 'networkidle' })
await page.waitForSelector('.data tbody tr')
await page.check('input[type="checkbox"]')
await page.waitForTimeout(500)
const failedRows = await page.locator('.data tbody tr.clickable').count()
console.log('failed-only rows:', failedRows)

await page.screenshot({ path: 'shot-interactions.png' })
console.log('pageerrors:', errors.length ? errors : 'none')
await browser.close()
console.log('DONE')
