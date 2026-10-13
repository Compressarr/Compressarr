// The announcements bell in the top bar (nav.js adds the button and loads this file). A dot means there is a message from the last
// 30 days that has not been seen; clicking opens the list and marks everything in it read. Everything shown here is plain text set with
// textContent - never as HTML - and a link is only offered when the server already passed it and it is https.
(function () {
  const button = document.getElementById('announcementsBtn');
  if (!button) return; // no toolbar on this page

  const dot = button.querySelector('.bell-dot');
  let overlay = null;

  async function refresh() {
    try {
      const view = await (await fetch('/api/announcements')).json();
      button.classList.toggle('hidden', !view.enabled);
      dot.classList.toggle('hidden', !(view.enabled && view.unread > 0));
      button.title = view.unread > 0 ? `Announcements (${view.unread} new)` : 'Announcements';
      return view;
    } catch {
      return null; // best-effort: the server being unreachable just leaves the bell as it was
    }
  }

  function kindLabel(kind) {
    return { release: 'Release', beta: 'Beta', catalog: 'Catalog', notice: 'News' }[kind] || 'News';
  }

  function itemNode(item) {
    const node = document.createElement('div');
    node.className = 'ann-item' + (item.read ? '' : ' unread') + (item.important ? ' important' : '');

    const head = document.createElement('div');
    head.className = 'ann-head';
    const kind = document.createElement('span');
    kind.className = 'ann-kind';
    kind.textContent = kindLabel(item.kind);
    const date = document.createElement('span');
    date.className = 'ann-date';
    date.textContent = item.date;
    head.append(kind, date);
    if (!item.read) {
      const fresh = document.createElement('span');
      fresh.className = 'ann-new';
      fresh.textContent = 'New';
      head.append(fresh);
    }

    const title = document.createElement('div');
    title.className = 'ann-title';
    title.textContent = item.title;
    node.append(head, title);

    if (item.text) {
      const text = document.createElement('div');
      text.className = 'ann-text';
      text.textContent = item.text;
      node.append(text);
    }

    if (item.link && item.link.startsWith('https://')) {
      const link = document.createElement('a');
      link.className = 'ann-link';
      link.href = item.link;
      link.target = '_blank';
      link.rel = 'noopener noreferrer';
      link.textContent = 'Read more';
      node.append(link);
    }

    return node;
  }

  function close() {
    if (overlay) overlay.classList.add('hidden');
  }

  async function open() {
    const view = await refresh();
    if (!view) return;

    if (!overlay) {
      overlay = document.createElement('div');
      overlay.className = 'modal-overlay hidden';
      overlay.innerHTML = '<div class="modal ann-modal" role="dialog" aria-label="Announcements"><h3>Announcements</h3><div class="ann-list"></div><div class="modal-actions"><button type="button" class="ann-close">Close</button></div></div>';
      overlay.addEventListener('click', e => { if (e.target === overlay) close(); });
      overlay.querySelector('.ann-close').addEventListener('click', close);
      document.addEventListener('keydown', e => { if (e.key === 'Escape') close(); });
      document.body.appendChild(overlay);
    }

    const list = overlay.querySelector('.ann-list');
    list.textContent = '';
    if (view.needsNewerApp) {
      const note = document.createElement('p');
      note.className = 'enc-note';
      note.textContent = 'There are newer announcements, but a newer Compressarr is needed to read them.';
      list.append(note);
    }
    if (view.items.length === 0) {
      const empty = document.createElement('p');
      empty.className = 'muted';
      empty.textContent = 'No announcements in the last 30 days.';
      list.append(empty);
    } else {
      view.items.forEach(item => list.append(itemNode(item)));
    }
    overlay.classList.remove('hidden');

    // Opening the list is "seen": the dot clears now, while this view still shows which ones were new.
    if (view.unread > 0) {
      try {
        await fetch('/api/announcements/read', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ all: true }) });
      } catch { /* the dot just stays until the next try */ }
      refresh();
    }
  }

  button.addEventListener('click', open);
  document.addEventListener('compressarr:settings-saved', refresh); // Settings > Announcements was just changed
  refresh();
  setInterval(refresh, 10 * 60 * 1000); // slow: the server only fetches from GitHub about once a day anyway
})();
