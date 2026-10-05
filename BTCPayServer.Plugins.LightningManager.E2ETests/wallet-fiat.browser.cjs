// Deterministic browser checks of the shipped wallet assets, without Lightning services.
// Run after building E2ETests: node BTCPayServer.Plugins.LightningManager.E2ETests/wallet-fiat.browser.cjs
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require('./bin/Debug/net10.0/.playwright/package');
const assets = path.resolve(__dirname, '../BTCPayServer.Plugins.LightningManager/Wallet/Assets');
const artifacts = process.env.TESTS_ARTIFACTS_DIR || '/private/tmp/wallet-fiat-browser';
fs.mkdirSync(artifacts, { recursive: true });
const html = `<body class="ln-wallet-page"><div class="ln-wallet" data-wallet-root="/wallet/">
<main class="ln-wallet__home-grid"><section class="ln-wallet__balance" data-balance-url="/wallet/balance">
<strong><span data-balance-amount>100,000</span> <small>sats</small></strong>
<span class="ln-wallet__fiat" data-fiat-sats="100000" hidden></span><p data-balance-error hidden></p></section>
<section class="ln-wallet__card ln-wallet__form-card">
<input id="amountSats" type="number" min="1" step="1"><span class="ln-wallet__fiat" data-fiat-input="amountSats" hidden></span>
<span class="ln-wallet__fiat" data-fiat-sats="0" hidden></span>
<span class="ln-wallet__fiat" data-fiat-sats="0.001" hidden></span>
<span class="ln-wallet__fiat" data-fiat-sats="" hidden></span>
</section></main><p class="ln-wallet__fiat-note" data-fiat-url="/wallet/fiat-rate"></p></div></body>`;
const css = '*{box-sizing:border-box}body{margin:0;--btcpay-body-text:#222;--btcpay-neutral-700:#555;--btcpay-body-bg:#fff;--btcpay-body-border-light:#ddd;--btcpay-brand-primary:#15743c}body.dark{--btcpay-body-text:#eee;--btcpay-neutral-700:#aaa;--btcpay-body-bg:#171717;--btcpay-body-border-light:#444}body{background:var(--btcpay-body-bg);color:var(--btcpay-body-text)}' + fs.readFileSync(path.join(assets, 'wallet.css'), 'utf8');
(async () => {
    const browser = await chromium.launch({ headless: true });
    try {
        for (const [currency, digits, locale] of [['BRL', 2, 'pt-BR'], ['USD', 2, 'en-US'], ['JPY', 0, 'ja-JP']]) {
            const context = await browser.newContext({ locale, serviceWorkers: 'block' });
            const page = await context.newPage();
            let requests = 0;
            let balance = { balanceSats: 200000 };
            let quote = { available: true, rate: 60000, currency, divisibility: digits };
            await page.route('http://wallet.test/**', async route => {
                const url = route.request().url();
                if (url.endsWith('fiat-rate')) { requests++; return route.fulfill({ json: quote }); }
                if (url.endsWith('balance')) return balance === null ? route.fulfill({ status: 503, json: {} }) : route.fulfill({ json: balance });
                return route.fulfill({ contentType: 'text/html', body: html });
            });
            await page.addInitScript(() => { window.walletTimers = []; window.setInterval = fn => { window.walletTimers.push(fn); return window.walletTimers.length; }; });
            await page.goto('http://wallet.test/');
            await page.addStyleTag({ content: css });
            await page.addScriptTag({ path: path.join(assets, 'wallet.js') });
            await page.waitForFunction(() => document.querySelector('[data-fiat-note], [data-fiat-url]').textContent.includes('quote'));
            const format = value => new Intl.NumberFormat(locale, { style: 'currency', currency, currencyDisplay: 'code', minimumFractionDigits: digits, maximumFractionDigits: digits }).format(value);
            const fiat = page.locator('.ln-wallet__balance [data-fiat-sats]');
            const tick = async () => {
                const before = requests;
                await page.evaluate(async () => { for (const tick of window.walletTimers) await tick(); });
                assert.equal(requests, before + 1, 'one quote per cycle, no extra fetch from balance rendering');
            };
            assert.equal(await page.evaluate(() => window.walletTimers.length), 1);
            assert.equal(await fiat.textContent(), '≈ ' + format(60));
            assert.equal(await page.locator('[data-fiat-sats="0"]').textContent(), '≈ ' + format(0));
            assert.equal(await page.locator('[data-fiat-sats="0.001"]').textContent(), '≈ < ' + format(10 ** -digits));
            assert.equal(await page.locator('[data-fiat-sats=""]').isVisible(), false);
            for (const value of ['1', '100000', '', '-1', '1.5']) {
                await page.locator('#amountSats').fill(value);
                assert.equal(await page.locator('[data-fiat-input]').isVisible(), value === '1' || value === '100000');
            }
            assert.equal(requests, 1, 'typing must not fetch quotes');
            await tick();
            assert.equal(await fiat.textContent(), '≈ ' + format(120));
            for (const invalid of [null, { balanceSats: null }, { balanceSats: -1 }, { balanceSats: 'NaN' }]) {
                balance = invalid;
                await tick();
                assert.equal(await fiat.isVisible(), false);
                assert.equal(await fiat.textContent(), '');
                assert.equal(await fiat.getAttribute('data-fiat-sats'), '');
            }
            balance = { balanceSats: 0 };
            await tick();
            assert.equal(await fiat.textContent(), '≈ ' + format(0));
            quote = { available: true, rate: 120000, currency, divisibility: digits };
            balance = { balanceSats: 200000 };
            await page.locator('#amountSats').fill('100000');
            const beforeTyping = requests;
            await tick();
            assert.equal(await fiat.textContent(), '≈ ' + format(240));
            assert.equal(await page.locator('[data-fiat-input]').textContent(), '≈ ' + format(120));
            assert.equal(requests, beforeTyping + 1);
            for (const invalid of [0, -1, 'NaN', null, 'Infinity']) {
                quote = { available: true, rate: invalid, currency, divisibility: digits };
                await tick();
                assert.equal(await page.locator('.ln-wallet__fiat:visible').count(), 0);
                assert.match(await page.locator('[data-fiat-url]').textContent(), /unavailable/);
            }
            quote = { available: true, rate: 60000, currency, divisibility: digits };
            await tick();
            assert.equal(await fiat.textContent(), '≈ ' + format(120));
            assert.equal(await page.locator('[data-fiat-input]').textContent(), '≈ ' + format(60));
            assert.equal(await page.locator('[data-fiat-sats=""]').isVisible(), false);
            for (const theme of ['light', 'dark']) for (const width of [320, 390, 1280]) {
                await page.setViewportSize({ width, height: 844 });
                await page.evaluate(theme => document.body.classList.toggle('dark', theme === 'dark'), theme);
                assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
                await page.screenshot({ path: path.join(artifacts, `${currency}-${theme}-${width}.png`), fullPage: true });
            }
            await context.close();
            console.log(`${currency}: formatting, typing, one quote/cycle, changing/failing/recovering quotes, balance recovery, layout PASS`);
        }
        for (const invalid of [0, -1, 'NaN', null, 'Infinity', false, 'timeout']) {
            const page = await browser.newPage({ serviceWorkers: 'block' });
            await page.route('http://wallet.test/**', route => route.request().url().endsWith('fiat-rate')
                ? invalid === 'timeout' ? new Promise(() => {}) : route.fulfill({ json: { available: invalid !== false, rate: invalid, currency: 'USD', divisibility: 2 } })
                : route.fulfill({ contentType: 'text/html', body: html }));
            await page.goto('http://wallet.test/');
            await page.addScriptTag({ path: path.join(assets, 'wallet.js') });
            await page.waitForFunction(() => document.querySelector('[data-fiat-url]').textContent.includes('unavailable'), null, { timeout: 10000 });
            assert.equal(await page.locator('.ln-wallet__fiat:visible').count(), 0);
            await page.close();
        }
        console.log('invalid/missing/timeout rates: hidden estimates PASS');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
