// Minimal PWA service worker - only exists to make the app installable and to make repeat loads
// of the static app shell fast. Deliberately does NOT cache anything under /api/ - every page here
// shows live state (the queue, run status, logs), and a cached API response would show stale data
// indefinitely, which is worse than no offline support at all. Bump CACHE_NAME on any real static
// asset change so old clients pick up the new shell instead of serving a stale cached copy forever.
const CACHE_NAME = 'compressarr-shell-v18';

const SHELL_ASSETS = [
  '/monitor.html',
  '/lanes.html',
  '/encoder.html',
  '/profiles.html',
  '/profile-edit.html',
  '/ffmpeg-edit.html',
  '/scheduler.html',
  '/index.html',
  '/notifications.html',
  '/history.html',
  '/about.html',
  '/donate.html',
  '/styles.css',
  '/nav.js',
  '/announcements.js',
  '/monitor.js',
  '/lanes.js',
  '/encoder.js',
  '/profiles.js',
  '/profile-edit.js',
  '/ffmpeg-edit.js',
  '/scheduler.js',
  '/notifications.js',
  '/history.js',
  '/settings.js',
  '/browse.js',
  '/qrcode.js',
  '/assets/logo.png',
  '/assets/icon-192.png',
  '/assets/favicon.ico'
];

self.addEventListener('install', event => {
  event.waitUntil(
    caches.open(CACHE_NAME)
      .then(cache => cache.addAll(SHELL_ASSETS))
      .then(() => self.skipWaiting())
  );
});

self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys()
      .then(names => Promise.all(names.filter(n => n !== CACHE_NAME).map(n => caches.delete(n))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);

  // Never intercept API calls - always live, never cached, and never allowed to fall back to a
  // stale cached response if the network request fails (better to let the page's own fetch()
  // error handling show "server unreachable" than silently serve old queue/status data).
  if (event.request.method !== 'GET' || url.pathname.startsWith('/api/')) {
    return;
  }

  // Network-first for the static shell: this app ships updates often, so a visitor should always
  // get the latest deployed files when online. The cache is purely a fallback for a dropped
  // connection or a cold start with no network yet, not a performance-first cache.
  event.respondWith(
    fetch(event.request)
      .then(response => {
        const copy = response.clone();
        caches.open(CACHE_NAME).then(cache => cache.put(event.request, copy));
        return response;
      })
      .catch(() => caches.match(event.request))
  );
});
