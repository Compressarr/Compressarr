renderNav('donate');

const COPY_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="13" height="13" rx="2"></rect><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"></path></svg>';
const CHECK_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"></polyline></svg>';
const HEART_ICON = '<svg viewBox="0 0 24 24" fill="#ef4444" stroke="none"><path d="M12 21s-6.72-4.35-9.34-8.02C.9 10.49 1.02 7.49 3.42 5.6c1.88-1.5 4.46-1.2 6 .5L12 9l2.58-2.9c1.54-1.7 4.12-2 6-.5 2.4 1.89 2.52 4.89.72 7.38C18.72 16.65 12 21 12 21z"></path></svg>';

async function renderCryptoGrid() {
  const grid = document.getElementById('cryptoGrid');
  grid.innerHTML = '<div class="modal-list-empty">Loading...</div>';

  let currencies;
  try {
    const res = await fetch('/api/donate/addresses');
    currencies = await res.json();
  } catch {
    grid.innerHTML = '<div class="modal-list-empty">Could not load donation addresses.</div>';
    return;
  }

  grid.innerHTML = '';

  currencies.forEach((currency, index) => {
    const card = document.createElement('div');
    card.className = 'card crypto-card';
    card.innerHTML = `
      <button type="button" class="crypto-trigger" data-index="${index}">
        <span class="crypto-icon" style="background:${currency.color}">${escapeHtml(currency.glyph)}</span>
        <span class="crypto-name">${escapeHtml(currency.name)}</span>
        <span class="crypto-qr-hint">Click to display QR code</span>
      </button>
      <div class="crypto-address-row">
        <span class="crypto-address" title="${escapeHtml(currency.address)}">${escapeHtml(truncateAddress(currency.address))}</span>
        <button type="button" class="crypto-copy-btn" aria-label="Copy ${escapeHtml(currency.name)} address">${COPY_ICON}</button>
      </div>
    `;
    grid.appendChild(card);

    card.querySelector('.crypto-trigger').addEventListener('click', () => openQrModal(currency));

    const copyBtn = card.querySelector('.crypto-copy-btn');
    copyBtn.addEventListener('click', () => copyAddress(currency.address, copyBtn));
  });

  const thanks = document.createElement('div');
  thanks.className = 'card donate-thanks';
  thanks.innerHTML = `
    ${HEART_ICON}
    <h3>Thank you for donating</h3>
    <p>Every contribution helps keep Compressarr free and open source for everyone.</p>
  `;
  grid.appendChild(thanks);
}

async function copyAddress(address, button) {
  try {
    await navigator.clipboard.writeText(address);
  } catch {
    // Clipboard API can be unavailable (no secure context, permission denied) - fall back to the
    // old execCommand path rather than leaving the click silently do nothing.
    const textarea = document.createElement('textarea');
    textarea.value = address;
    textarea.style.position = 'fixed';
    textarea.style.opacity = '0';
    document.body.appendChild(textarea);
    textarea.select();
    try { document.execCommand('copy'); } catch { /* best effort */ }
    document.body.removeChild(textarea);
  }

  const original = button.innerHTML;
  button.innerHTML = CHECK_ICON;
  button.classList.add('copied');
  setTimeout(() => {
    button.innerHTML = original;
    button.classList.remove('copied');
  }, 1500);
}

// ---- QR modal: built once, reused (same pattern as browse.js's folder-browse modal) ----
let _qrModal = null;

function ensureQrModal() {
  if (_qrModal) return _qrModal;

  const overlay = document.createElement('div');
  overlay.className = 'modal-overlay hidden';
  overlay.innerHTML = `
    <div class="modal qr-modal">
      <div class="qr-modal-head">
        <span class="crypto-icon" id="qrModalIcon"></span>
        <h3 id="qrModalName" style="margin:0"></h3>
      </div>
      <div class="qr-modal-svg" id="qrModalSvg"></div>
      <div class="qr-modal-address" id="qrModalAddress"></div>
      <div class="modal-actions">
        <button type="button" id="qrModalClose">Close</button>
      </div>
    </div>
  `;
  document.body.appendChild(overlay);

  overlay.addEventListener('click', e => { if (e.target === overlay) closeQrModal(); });
  document.getElementById('qrModalClose').addEventListener('click', closeQrModal);
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape' && !overlay.classList.contains('hidden')) closeQrModal();
  });

  _qrModal = overlay;
  return overlay;
}

function openQrModal(currency) {
  ensureQrModal();

  const icon = document.getElementById('qrModalIcon');
  icon.style.background = currency.color;
  icon.textContent = currency.glyph;
  document.getElementById('qrModalName').textContent = currency.name;
  document.getElementById('qrModalAddress').textContent = currency.address;

  const svgHost = document.getElementById('qrModalSvg');
  const svg = window.CompressarrQR.renderSvg(currency.address);
  svgHost.innerHTML = svg || '<div class="modal-list-empty">Could not generate a QR code for this address.</div>';

  _qrModal.classList.remove('hidden');
}

function closeQrModal() {
  if (_qrModal) _qrModal.classList.add('hidden');
}

// Shown in the card instead of the full address so a long one never wraps to multiple lines and
// stretches the card - the copy button (and this span's own hover title) still use/show the real
// full address, only the on-card text is shortened.
function truncateAddress(address) {
  if (address.length <= 14) return address;
  return `${address.slice(0, 5)}....${address.slice(-5)}`;
}

function escapeHtml(text) {
  return String(text ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

renderCryptoGrid();
