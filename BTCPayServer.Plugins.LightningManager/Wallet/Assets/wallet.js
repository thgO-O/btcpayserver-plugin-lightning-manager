'use strict';
(() => {
    const wallet = document.querySelector('[data-wallet-root]');
    if (!wallet) return;
    const root = wallet.dataset.walletRoot;
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
                status.textContent = data.state;
                status.dataset.state = data.state;
                if (data.final) { clearInterval(timer); location.reload(); }
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
            if (document.hidden || running) return;
            running = true;
            try {
                const response = await fetch(balance ? balance.dataset.balanceUrl : history.dataset.historyUrl,
                    { headers: { Accept: 'application/json' }, cache: 'no-store' });
                if (!response.ok || response.redirected) throw new Error();
                const data = await response.json();
                if (balance) {
                    balance.querySelector('[data-balance-amount]').textContent = new Intl.NumberFormat('en-US', { maximumFractionDigits: 3 }).format(data.balanceSats);
                    balance.querySelector('strong').hidden = false;
                    balance.querySelector('[data-balance-error]').hidden = true;
                } else {
                    document.querySelector('[data-history-error]').textContent = '';
                    if (data.map(op => op.id + ':' + op.state + ':' + (op.settledAmountMsat ?? '')).join(';') !== history.dataset.historySnapshot) location.reload();
                }
            } catch {
                if (balance) {
                    balance.querySelector('strong').hidden = true;
                    const error = balance.querySelector('[data-balance-error]');
                    error.textContent = 'Balance unavailable. Reconnect or sign in again to refresh it.';
                    error.hidden = false;
                } else document.querySelector('[data-history-error]').textContent = 'History unavailable. Reconnect or sign in again to refresh it.';
            } finally { running = false; }
        }, 30000);
    }
})();
