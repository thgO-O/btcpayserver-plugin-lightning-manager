'use strict';
const root = new URL('./', self.location.href);
const cacheName = 'ln-wallet-0.2.1-' + root.pathname;
const offlineUrl = new URL('offline', root).href;
const publicAssets = [offlineUrl, ...['assets/wallet.css', 'assets/wallet.js', 'assets/icon-192.png', 'assets/icon-512.png'].map(p => new URL(p, root).href)];
self.addEventListener('install', event => {
    event.waitUntil(caches.open(cacheName).then(cache => cache.addAll(publicAssets.map(url => new Request(url, { credentials: 'omit' })))).then(() => self.skipWaiting()));
});
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', event => {
    const request = event.request;
    if (request.method !== 'GET') return;
    const url = new URL(request.url);
    if (url.origin !== root.origin || !url.pathname.startsWith(root.pathname)) return;
    if (publicAssets.includes(url.href)) {
        event.respondWith(fetch(new Request(request.url, { credentials: 'omit' })).catch(() => caches.match(request.url)));
    } else if (request.mode === 'navigate') {
        // Only a generic offline page is a fallback. Never store authenticated responses.
        event.respondWith(fetch(request).catch(() => caches.match(offlineUrl)));
    }
});
