// Execute the shipped JS with a minimal DOM/fetch adapter when browsers are unavailable.
// This checks data flow and formatting; it does not verify browser layout or authorization.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const script = fs.readFileSync(path.resolve(__dirname, '../BTCPayServer.Plugins.LightningManager/Wallet/Assets/wallet.js'), 'utf8');
const element = dataset => ({
    dataset, _hidden: true, _textContent: '', textWrites: 0, hiddenWrites: 0, listeners: {},
    get hidden() { return this._hidden; },
    set hidden(value) { this.hiddenWrites++; this._hidden = value; },
    get textContent() { return this._textContent; },
    set textContent(value) { this.textWrites++; this._textContent = value; },
    addEventListener(name, fn) { this.listeners[name] = fn; }
});
async function fixture(rateData, locale = 'en-US', deferRate = false, withBalance = true, withHistory = false) {
    const value = element({ fiatSats: '100000' });
    const zero = element({ fiatSats: '0' });
    const tiny = element({ fiatSats: '0.001' });
    const unknown = element({ fiatSats: '' });
    const input = element({}); input.value = ''; input.validity = { valid: true };
    const estimate = element({ fiatInput: 'amountSats' });
    const note = element({ fiatUrl: '/wallet/fiat-rate' });
    const balance = element({ balanceUrl: '/wallet/balance' });
    const strong = element({}); const amount = element({}); const error = element({});
    balance.querySelector = selector => ({ strong, '[data-balance-amount]': amount, '[data-balance-error]': error, '[data-fiat-sats]': value })[selector];
    let currentBalance = { balanceSats: 200000 }, requests = 0, release;
    let deferred;
    const defer = () => { deferred = new Promise(resolve => { release = () => { deferred = null; resolve(); }; }); };
    if (deferRate) defer();
    const intervals = [], timeouts = [];
    const dom = {
        hidden: false,
        getElementById: () => input,
        querySelector: selector => ({ '[data-wallet-root]': element({ walletRoot: '/wallet/' }), '[data-fiat-url]': note,
            '[data-balance-url]': withBalance ? balance : null,
            '.ln-wallet__operation, [data-history-url]': withHistory ? element({}) : null })[selector] || null,
        querySelectorAll: selector => selector.includes('fiat') ? [value, zero, tiny, unknown, estimate] : []
    };
    const fetch = async (url, options) => {
        if (url.endsWith('fiat-rate')) {
            requests++;
            assert.equal(options.cache, 'no-store');
            assert.equal(options.credentials, 'same-origin');
            if (deferred) await Promise.race([deferred, new Promise((resolve, reject) =>
                options.signal.addEventListener('abort', () => reject(new Error('timeout')), { once: true }))]);
            if (rateData instanceof Error) throw rateData;
            return { ok: true, redirected: false, json: async () => rateData };
        }
        if (currentBalance instanceof Error) throw currentBalance;
        return { ok: true, redirected: false, json: async () => currentBalance };
    };
    vm.runInNewContext(script, { document: dom, navigator: { language: locale }, window: {}, Intl, AbortController, fetch,
        setTimeout: fn => { timeouts.push(fn); return timeouts.length - 1; }, clearTimeout: id => { timeouts[id] = null; },
        setInterval: (fn, ms) => { assert.equal(ms, 30000); intervals.push(fn); return intervals.length; } });
    const flush = async () => { for (let i = 0; i < 10; i++) await Promise.resolve(); };
    await flush();
    assert.equal(intervals.length, 1, 'one refresh timer per page');
    return { value, zero, tiny, unknown, input, estimate, note, strong, dom, requests: () => requests,
        flush, defer, release: () => release(), rate: data => { rateData = data; },
        timeout: () => timeouts.filter(Boolean).forEach(fn => fn()),
        async tick(data = currentBalance) { currentBalance = data; for (const tick of intervals) await tick(); await flush(); } };
}
let suiteComplete = false;
const watchdog = setTimeout(() => {
    if (!suiteComplete) {
        console.error('FAIL: runtime suite did not complete within 5 seconds');
        process.exit(1);
    }
}, 5000);
(async () => {
    for (const [currency, divisibility, locale] of [['BRL', 2, 'pt-BR'], ['USD', 2, 'en-US'], ['JPY', 0, 'ja-JP']]) {
        const f = await fixture({ available: true, currency, divisibility, rate: 60000 }, locale);
        const format = value => new Intl.NumberFormat(locale, { style: 'currency', currency, currencyDisplay: 'code', minimumFractionDigits: divisibility, maximumFractionDigits: divisibility }).format(value);
        assert.equal(f.value.textContent, '≈ ' + format(60));
        assert.equal(f.zero.textContent, '≈ ' + format(0));
        assert.equal(f.tiny.textContent, '≈ < ' + format(10 ** -divisibility));
        assert.equal(f.unknown.hidden, true);
        f.input.value = '100000'; f.input.listeners.input();
        assert.equal(f.estimate.textContent, '≈ ' + format(60));
        for (const value of ['', '-1', 'NaN', 'Infinity']) {
            f.input.value = value; f.input.listeners.input(); assert.equal(f.estimate.hidden, true);
        }
        assert.equal(f.requests(), 1, 'typing must not fetch');
        await f.tick({ balanceSats: 200000 });
        assert.equal(f.value.textContent, '≈ ' + format(120));
        for (const data of [new Error('offline'), { balanceSats: null }, { balanceSats: -1 }, { balanceSats: NaN }, { balanceSats: Infinity }, { balanceSats: '200000' }]) {
            await f.tick(data);
            assert.equal(f.value.hidden, true); assert.equal(f.value.textContent, ''); assert.equal(f.value.dataset.fiatSats, '');
            assert.equal(f.strong.hidden, true);
        }
        await f.tick({ balanceSats: 0 }); assert.equal(f.value.textContent, '≈ ' + format(0));
        assert.equal(f.requests(), 9, 'one initial quote plus one per refresh cycle');
        console.log(`${currency}: precision, tiny/zero/unknown, typing, balance failure/recovery, one quote/cycle PASS`);
    }
    for (const rate of [NaN, Infinity, 0, -1, null, '60000']) {
        const f = await fixture({ available: true, currency: 'USD', divisibility: 2, rate });
        assert.equal(f.value.hidden, true); assert.equal(f.value.textContent, '');
        assert.match(f.note.textContent, /unavailable/);
    }
    for (const data of [{ available: false }, new Error('timeout'), { available: true, rate: 60000, currency: 'BTC', divisibility: -1 }]) {
        const f = await fixture(data); assert.equal(f.value.hidden, true);
    }
    const race = await fixture({ available: true, currency: 'USD', divisibility: 2, rate: 60000 }, 'en-US', true);
    await race.tick(new Error('offline')); race.release(); await race.flush();
    assert.equal(race.value.hidden, true); assert.equal(race.value.textContent, '');
    assert.equal(race.value.dataset.fiatSats, '');
    console.log('invalid initial rates and late quote after balance failure PASS');
    for (const withBalance of [true, false]) {
        const f = await fixture({ available: true, currency: 'USD', divisibility: 2, rate: 60000 }, 'en-US', false, withBalance, withBalance);
        assert.equal(f.note.textContent.includes('History'), withBalance);
        f.input.value = '100000'; f.input.listeners.input();
        const slots = [f.note, f.value, f.zero, f.tiny, f.unknown, f.estimate];
        const writes = slots.map(slot => [slot.textWrites, slot.hiddenWrites]);
        await f.tick({ balanceSats: 100000 });
        await f.tick();
        assert.deepEqual(slots.map(slot => [slot.textWrites, slot.hiddenWrites]), writes,
            'unchanged quote/balance must not rewrite fiat text or visibility, including history note');
        f.rate({ available: true, currency: 'USD', divisibility: 2, rate: 80000 });
        await f.tick({ balanceSats: 200000 });
        assert.equal(f.estimate.textContent, '≈ USD 80.00');
        assert.equal(f.value.textContent, withBalance ? '≈ USD 160.00' : '≈ USD 80.00');
        assert.ok(f.estimate.textWrites > writes[5][0], 'changed quote updates fiat text');
        assert.equal(f.estimate.hiddenWrites, writes[5][1], 'valid refresh preserves visibility');
        assert.equal(f.note.textWrites, writes[0][0], 'same currency preserves note');
        const noteWrites = f.note.textWrites;
        f.rate(new Error('offline')); await f.tick();
        assert.match(f.note.textContent, /unavailable/);
        assert.equal(f.note.textWrites, noteWrites + 1, 'failure announces status once');
        const failedWrites = slots.map(slot => [slot.textWrites, slot.hiddenWrites]);
        await f.tick();
        assert.deepEqual(slots.map(slot => [slot.textWrites, slot.hiddenWrites]), failedWrites,
            'repeated failure does not repeat announcements or hide mutations');
        f.rate({ available: true, currency: 'USD', divisibility: 2, rate: 80000 });
        await f.tick();
        assert.equal(f.note.textWrites, noteWrites + 2, 'recovery announces restored status');
        assert.match(f.note.textContent, /current USD quote/);
        assert.equal(f.estimate.hidden, false);
        assert.equal(f.estimate.textContent, '≈ USD 80.00');
        f.rate({ available: true, currency: 'JPY', divisibility: 0, rate: 100000 });
        await f.tick({ balanceSats: 300000 });
        assert.equal(f.value.textContent, withBalance ? '≈ JPY 300' : '≈ JPY 100');
        assert.equal(f.estimate.textContent, '≈ JPY 100');
        f.rate({ available: true, currency: 'JPY', divisibility: 2, rate: 200000 });
        await f.tick(); assert.equal(f.estimate.textContent, '≈ JPY 200.00');
        for (const invalid of [0, -1, NaN, Infinity, null, '60000', new Error('offline')]) {
            f.rate(invalid instanceof Error ? invalid : { available: true, currency: 'USD', divisibility: 2, rate: invalid });
            const before = f.requests(); await f.tick(); assert.equal(f.requests(), before + 1);
            for (const slot of [f.value, f.zero, f.tiny, f.unknown, f.estimate]) {
                assert.equal(slot.hidden, true); assert.equal(slot.textContent, '');
            }
            assert.match(f.note.textContent, /unavailable/);
            f.input.listeners.input(); assert.equal(f.requests(), before + 1);
        }
        f.rate({ available: true, currency: 'BRL', divisibility: 2, rate: 80000 });
        await f.tick(); assert.equal(f.estimate.textContent, '≈ BRL 80.00'); assert.equal(f.unknown.hidden, true);
        f.dom.hidden = true; const before = f.requests(); await f.tick(); assert.equal(f.requests(), before);
        f.dom.hidden = false; f.defer();
        const pending = f.tick(new Error('offline')); await f.flush();
        await f.tick(); assert.equal(f.requests(), before + 1, 'inflight quote must not duplicate');
        f.release(); await pending;
        if (withBalance) { assert.equal(f.value.hidden, true); assert.equal(f.value.dataset.fiatSats, ''); }
        f.defer(); const timeout = f.tick(); await f.flush(); f.timeout(); await timeout;
        assert.equal(f.estimate.hidden, true); assert.match(f.note.textContent, /unavailable/);
        f.release(); await f.flush(); assert.equal(f.estimate.hidden, true);
        await f.tick({ balanceSats: 400000 });
        assert.equal(f.estimate.textContent, '≈ BRL 80.00');
        if (withBalance) assert.equal(f.value.textContent, '≈ BRL 320.00');
        console.log(`${withBalance ? 'balance' : 'fiat-only'}: changing quote/currency/precision, fresh failures, recovery, hidden/inflight/timeout, unchanged DOM and failure/recovery announcements PASS`);
    }
    suiteComplete = true;
    clearTimeout(watchdog);
    console.log('PASS: runtime suite complete — 6 groups, including DOM mutation and status transition checks');
})().catch(error => { clearTimeout(watchdog); console.error(error); process.exitCode = 1; });
