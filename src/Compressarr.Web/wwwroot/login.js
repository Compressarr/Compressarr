// The sign-in page. Reached when a request needs credentials (the web interface asked from outside the local network, or "Authentication Required: Enabled"): the server
// sends the browser here with ?returnUrl=<where it was going>. Nothing here is trusted for security - the server checks the return address again.

const returnUrl = new URLSearchParams(location.search).get('returnUrl') || '/';
const form = document.getElementById('loginForm');
const errorBox = document.getElementById('loginError');
const button = document.getElementById('signInBtn');

// Already signed in, or this address needs no sign-in: nothing to do here.
(async () => {
  try {
    const status = await (await fetch('/api/auth/status')).json();
    if (status.method === 'None' || status.free || status.signedIn) location.replace(safeLocal(returnUrl));
    else if (!status.credentialsSet) errorBox.textContent = 'No username and password have been set up yet. Open Settings > Security on the computer running Compressarr, or from your local network.';
    else if (status.method === 'Basic') errorBox.textContent = 'This copy of Compressarr signs in with your browser\'s own sign-in box. Reload the page to see it.';
  } catch { /* the form is still usable */ }
})();

// Only ever a plain path on this site, whatever the address bar says.
function safeLocal(url) {
  if (!url || url[0] !== '/' || url[1] === '/' || url[1] === '\\' || url.includes('\\') || url.startsWith('/login.html') || url.startsWith('/api/')) return '/';
  return url;
}

form.addEventListener('submit', async event => {
  event.preventDefault();
  errorBox.textContent = '';
  button.disabled = true;
  try {
    const res = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        username: document.getElementById('username').value,
        password: document.getElementById('password').value,
        remember: document.getElementById('remember').checked,
        returnUrl
      })
    });
    const body = await res.json().catch(() => ({}));
    if (res.ok) { location.replace(safeLocal(body.redirect || returnUrl)); return; }

    errorBox.textContent = (body.error && body.error.message) || 'Could not sign in.';
    document.getElementById('password').select();
  } catch {
    errorBox.textContent = 'Could not reach Compressarr. Check your connection and try again.';
  } finally {
    button.disabled = false;
  }
});
