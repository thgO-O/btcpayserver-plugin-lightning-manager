'use strict';
(() => {
    const wallet = document.querySelector('[data-wallet-root]');
    if (!wallet) return;
    const root = wallet.dataset.walletRoot;
    let fiatRate = null;
    let fiatFormat = null;
    let fiatDigits = 0;
    function renderFiat(element) {
        const input = element.dataset.fiatInput ? document.getElementById(element.dataset.fiatInput) : null;
        const raw = input ? input.value : element.dataset.fiatSats;
        const sats = typeof raw === 'string' && raw.trim() !== '' ? Number(raw) : NaN;
        const value = sats / 100000000 * fiatRate;
        if (!fiatFormat || !Number.isFinite(sats) || sats < 0 || !Number.isFinite(value) || (input && !input.validity.valid)) {
            if (element.textContent !== '') element.textContent = '';
            if (!element.hidden) element.hidden = true;
            return;
        }
        // A positive sub-cent amount must not appear to be worth exactly zero.
        const unit = 10 ** -fiatDigits;
        const text = value > 0 && value < unit
            ? '≈ < ' + fiatFormat.format(unit) : '≈ ' + fiatFormat.format(value);
        if (element.textContent !== text) element.textContent = text;
        if (element.hidden) element.hidden = false;
    }
    const fiatElements = document.querySelectorAll('[data-fiat-sats], [data-fiat-input]');
    fiatElements.forEach(element => {
        if (element.dataset.fiatInput) document.getElementById(element.dataset.fiatInput)?.addEventListener('input', () => renderFiat(element));
    });
    const fiatNote = document.querySelector('[data-fiat-url]');
    let fiatRunning = false;
    async function refreshFiat() {
        if (!fiatNote || !fiatElements.length || document.hidden || fiatRunning) return;
        fiatRunning = true;
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), 5000);
        try {
            const response = await fetch(fiatNote.dataset.fiatUrl, { headers: { Accept: 'application/json' },
                credentials: 'same-origin', cache: 'no-store', signal: controller.signal });
            if (!response.ok || response.redirected) throw new Error();
            const data = await response.json();
            if (data.available !== true || typeof data.rate !== 'number' || !Number.isFinite(data.rate) || data.rate <= 0 ||
                !Number.isInteger(data.divisibility) || data.divisibility < 0 || data.divisibility > 20 ||
                typeof data.currency !== 'string' || !/^[A-Z]{3}$/.test(data.currency)) throw new Error();
            fiatFormat = new Intl.NumberFormat(navigator.language, { style: 'currency', currency: data.currency,
                currencyDisplay: 'code', minimumFractionDigits: data.divisibility, maximumFractionDigits: data.divisibility });
            fiatRate = data.rate;
            fiatDigits = data.divisibility;
            let note = 'Estimates at current ' + data.currency + ' quote. Payments in sats.';
            if (document.querySelector('.ln-wallet__operation, [data-history-url]'))
                note += ' History uses current, not payment-time quotes.';
            if (fiatNote.textContent !== note) fiatNote.textContent = note;
        } catch {
            fiatRate = null;
            fiatFormat = null;
            const note = 'Fiat estimates unavailable. Payments are in sats.';
            if (fiatNote.textContent !== note) fiatNote.textContent = note;
        } finally {
            clearTimeout(timer);
            fiatElements.forEach(renderFiat);
            fiatRunning = false;
        }
    }
    if (fiatNote && fiatElements.length) refreshFiat();
    else if (fiatNote) fiatNote.hidden = true;
    if ('serviceWorker' in navigator && window.isSecureContext) {
        navigator.serviceWorker.register(root + 'worker.js', { scope: root }).catch(() => {});
    }
    const dates = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });
    document.querySelectorAll('time[data-local-time]').forEach(time => {
        const date = new Date(time.dateTime);
        if (Number.isNaN(date.getTime())) return;
        time.textContent = dates.format(date);
        time.title = date.toISOString().replace('T', ' ');
    });
    const fromBase64 = s => Uint8Array.from(atob(s.replace(/-/g, '+').replace(/_/g, '/')), c => c.charCodeAt(0));
    const toBase64 = data => btoa(String.fromCharCode(...new Uint8Array(data))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    async function post(url, form) {
        const response = await fetch(url, { method: 'POST', body: form, headers: { Accept: 'application/json' }, cache: 'no-store', credentials: 'same-origin' });
        if (response.redirected || !response.headers.get('content-type')?.includes('application/json')) throw new Error('Sign in again, then check the history before retrying.');
        const data = await response.json();
        if (!response.ok) throw new Error(data.error || 'Operation unavailable. Check the history before retrying.');
        return data;
    }
    const paymentForm = document.querySelector('.ln-wallet__confirm');
    if (paymentForm) {
        const button = paymentForm.querySelector('button');
        const message = document.querySelector('.ln-wallet__payment-message');
        button.addEventListener('click', async () => {
            button.disabled = true;
            button.setAttribute('aria-busy', 'true');
            message.textContent = 'Confirm this payment with your passkey.';
            try {
                if (!window.isSecureContext || !navigator.credentials) throw new Error('A secure connection and passkey support are required.');
                const form = new FormData(paymentForm);
                const publicKey = await post(paymentForm.dataset.challengeUrl, form);
                publicKey.challenge = fromBase64(publicKey.challenge);
                if (publicKey.allowCredentials) publicKey.allowCredentials = publicKey.allowCredentials.map(c => ({ ...c, id: fromBase64(c.id) }));
                const credential = await navigator.credentials.get({ publicKey });
                if (!credential) throw new Error('Passkey confirmation was cancelled. No payment was submitted.');
                form.set('assertion', JSON.stringify({
                    id: credential.id, rawId: toBase64(credential.rawId), type: credential.type,
                    response: {
                        authenticatorData: toBase64(credential.response.authenticatorData),
                        clientDataJSON: toBase64(credential.response.clientDataJSON), signature: toBase64(credential.response.signature),
                        userHandle: credential.response.userHandle ? toBase64(credential.response.userHandle) : null
                    }, extensions: credential.getClientExtensionResults()
                }));
                message.textContent = 'Payment submitted. Keep this screen open or check the history.';
                const result = await post(paymentForm.action, form);
                window.location.assign(result.url);
            } catch (error) {
                message.textContent = error.name === 'NotAllowedError' ? 'Passkey confirmation was cancelled. No payment was submitted.' : error.message;
                button.disabled = false;
                button.removeAttribute('aria-busy');
            }
        });
    }
    const scan = document.querySelector('.ln-wallet__scan');
    if (scan && typeof initCameraScanningApp === 'function') {
        initCameraScanningApp('Scan a Lightning invoice', value => {
            document.querySelector('[name=bolt11]').value = value.trim().replace(/^lightning:/i, '');
        }, 'wallet-scan-modal', true);
        scan.addEventListener('click', () => bootstrap.Modal.getOrCreateInstance(document.getElementById('wallet-scan-modal')).show());
    }
    const status = document.querySelector('[data-status-url]');
    if (status && status.dataset.final !== 'true') {
        let running = false;
        const timer = setInterval(async () => {
            if (document.hidden || running) return;
            running = true;
            try {
                const response = await fetch(status.dataset.statusUrl, { headers: { Accept: 'application/json' }, cache: 'no-store' });
                if (!response.ok || response.redirected) throw new Error();
                const data = await response.json();
                const settled = data.state === 'Settled' && status.dataset.state !== data.state;
                status.textContent = data.state;
                status.dataset.state = data.state;
                const failure = document.querySelector('[data-payment-failure]');
                if (failure) {
                    failure.hidden = data.state !== 'Failed';
                    failure.querySelector('[data-failure-message]').textContent = data.failureMessage || '';
                }
                if (data.final || settled) { clearInterval(timer); location.reload(); }
                document.querySelector('.ln-wallet__status-error').textContent = '';
            } catch {
                document.querySelector('.ln-wallet__status-error').textContent = 'Status unavailable. Check again when connected; do not assume the payment failed.';
            } finally { running = false; }
        }, 3000);
    }
    const balance = document.querySelector('[data-balance-url]');
    const history = document.querySelector('[data-history-url]');
    if (balance || history) {
        let running = false;
        setInterval(async () => {
            if (document.hidden) return;
            const fiatRefresh = refreshFiat();
            if (running) return fiatRefresh;
            running = true;
            try {
                const response = await fetch(balance ? balance.dataset.balanceUrl : history.dataset.historyUrl,
                    { headers: { Accept: 'application/json' }, cache: 'no-store' });
                if (!response.ok || response.redirected) throw new Error();
                const data = await response.json();
                if (balance) {
                    if (typeof data.balanceSats !== 'number' || !Number.isFinite(data.balanceSats) || data.balanceSats < 0) throw new Error();
                    balance.querySelector('[data-balance-amount]').textContent = new Intl.NumberFormat('en-US', { maximumFractionDigits: 3 }).format(data.balanceSats);
                    const fiat = balance.querySelector('[data-fiat-sats]');
                    if (fiat) { fiat.dataset.fiatSats = String(data.balanceSats); renderFiat(fiat); }
                    balance.querySelector('strong').hidden = false;
                    balance.querySelector('[data-balance-error]').hidden = true;
                } else {
                    document.querySelector('[data-history-error]').textContent = '';
                    if (data.map(op => op.id + ':' + op.state + ':' + (op.settledAmountMsat ?? '')).join(';') !== history.dataset.historySnapshot) location.reload();
                }
            } catch {
                if (balance) {
                    balance.querySelector('strong').hidden = true;
                    const fiat = balance.querySelector('[data-fiat-sats]');
                    if (fiat) { fiat.dataset.fiatSats = ''; renderFiat(fiat); }
                    const error = balance.querySelector('[data-balance-error]');
                    error.textContent = 'Balance unavailable. Reconnect or sign in again to refresh it.';
                    error.hidden = false;
                } else document.querySelector('[data-history-error]').textContent = 'History unavailable. Reconnect or sign in again to refresh it.';
            } finally { await fiatRefresh; running = false; }
        }, 30000);
    } else if (fiatNote && fiatElements.length) setInterval(refreshFiat, 30000);
})();
