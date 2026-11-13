namespace Compressarr.Web.Endpoints;

/// <summary>
/// The Save and Share buttons on a report opened in the browser (3.1). They are added only when the report is served for viewing: a downloaded or saved
/// copy never carries them, and they are hidden when the page is printed. The page itself is the same self-contained report either way.
/// </summary>
internal static class ReportToolbar
{
    /// <summary>Where the report generator leaves room for the buttons (an HTML comment, so a saved copy shows nothing).</summary>
    public const string Marker = Compressarr.Core.Reporting.HtmlReportGenerator.ToolbarMarker;

    private const string Html = """
<style>
  .report-toolbar { position: fixed; top: 1rem; right: 4rem; display: flex; gap: .5rem; align-items: center; font-family: Segoe UI, Verdana, sans-serif; z-index: 5; }
  .rt-btn { border: 1px solid #ddd; background: #fff; color: #1c1c1c; border-radius: 6px; padding: .4rem .9rem; font-size: .85rem; cursor: pointer; text-decoration: none; box-shadow: 0 1px 4px rgba(0,0,0,.12); }
  .rt-btn:hover { background: #f0f0f0; }
  .rt-menu { position: absolute; top: 2.6rem; right: 0; background: #fff; border: 1px solid #ddd; border-radius: 8px; box-shadow: 0 4px 16px rgba(0,0,0,.18); min-width: 260px; padding: .35rem; }
  .rt-menu[hidden] { display: none; }
  .rt-item { display: block; width: 100%; text-align: left; background: none; border: 0; padding: .5rem .7rem; font-size: .85rem; color: #1c1c1c; border-radius: 6px; cursor: pointer; text-decoration: none; box-sizing: border-box; }
  .rt-item:hover { background: #f0f0f0; }
  .rt-note { padding: .35rem .7rem .5rem; font-size: .75rem; color: #666; }
  .rt-done { padding: .35rem .7rem; font-size: .8rem; color: #16693a; font-weight: 600; }
  .rt-trash-cell { width: 2rem; text-align: center; }
  .rt-trash { border: 0; background: none; cursor: pointer; font-size: 1rem; opacity: .55; padding: 0 .25rem; }
  .rt-trash:hover { opacity: 1; }
  @media print { .rt-trash-cell, th:last-child:empty { display: none !important; } }
  @media print { .report-toolbar { display: none !important; } }
</style>
<div class="report-toolbar" id="rt">
  <a class="rt-btn" id="rtSave" href="?download=1" title="Download this report as a file you can keep or send">Save</a>
  <button type="button" class="rt-btn" id="rtShareBtn" aria-haspopup="true" aria-expanded="false">Share &#9662;</button>
  <div class="rt-menu" id="rtMenu" hidden>
    <button type="button" class="rt-item" id="rtCopyLink">Copy link</button>
    <button type="button" class="rt-item" id="rtNativeShare" hidden>Share with an app&hellip;</button>
    <a class="rt-item" id="rtMail" href="#">Email a summary</a>
    <a class="rt-item" id="rtSaveClean" href="?download=1&redact=1">Save a copy without file names</a>
    <a class="rt-item" id="rtViewClean" href="?redact=1">View without file names</a>
    <div class="rt-note">A link only works for people who can reach this Compressarr. The copy without file names replaces every file name, path and message with &ldquo;Removed&rdquo;.</div>
    <div class="rt-done" id="rtDone" hidden></div>
  </div>
</div>
<script>
(function () {
  var base = location.href.split('?')[0];
  var menu = document.getElementById('rtMenu'), btn = document.getElementById('rtShareBtn'), done = document.getElementById('rtDone');
  function summary() {
    var muted = document.querySelector('.muted'), parts = [];
    document.querySelectorAll('.summary-grid .stat').forEach(function (s) {
      parts.push(s.querySelector('.label').textContent.trim() + ': ' + s.querySelector('.value').textContent.trim());
    });
    return 'Compressarr report - ' + (muted ? muted.textContent.replace(/\s+/g, ' ').trim() : '') + '\n' + parts.join('\n');
  }
  function say(text) { done.textContent = text; done.hidden = false; setTimeout(function () { done.hidden = true; }, 2500); }
  function setMenu(open) { menu.hidden = !open; btn.setAttribute('aria-expanded', open ? 'true' : 'false'); }
  btn.addEventListener('click', function (e) { e.stopPropagation(); setMenu(menu.hidden); });
  document.addEventListener('click', function (e) { if (!document.getElementById('rt').contains(e.target)) setMenu(false); });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') setMenu(false); });
  document.getElementById('rtCopyLink').addEventListener('click', function () {
    function fallback() {
      var t = document.createElement('textarea'); t.value = base; document.body.appendChild(t); t.select();
      try { document.execCommand('copy'); say('Link copied.'); } catch (x) { window.prompt('Copy this link:', base); }
      document.body.removeChild(t);
    }
    if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(base).then(function () { say('Link copied.'); }, fallback); else fallback();
  });
  if (navigator.share) {
    var ns = document.getElementById('rtNativeShare'); ns.hidden = false;
    ns.addEventListener('click', function () { navigator.share({ title: document.title, text: summary(), url: base }).catch(function () {}); setMenu(false); });
  }
  // A trash can at the end of each file row (3.1): removes the file's name and messages from History while its numbers stay in every total.
  var fileRows = document.querySelectorAll('tr[data-fid]');
  if (fileRows.length) {
    var runId = location.pathname.split('/').pop();
    document.querySelectorAll('table').forEach(function (table) {
      if (!table.querySelector('tr[data-fid]')) return;
      table.querySelectorAll('thead tr').forEach(function (tr) { tr.appendChild(document.createElement('th')); });
      table.querySelectorAll('tbody tr').forEach(function (tr) {
        var td = document.createElement('td');
        td.className = 'rt-trash-cell';
        var fid = tr.getAttribute('data-fid');
        if (fid) {
          var b = document.createElement('button');
          b.type = 'button'; b.className = 'rt-trash'; b.title = 'Remove this file from History'; b.setAttribute('aria-label', 'Remove this file from History');
          b.innerHTML = '&#128465;';
          b.addEventListener('click', function () {
            if (!window.confirm('Remove this file from History?\n\nIts name, path and messages become "Removed". Its sizes, time and savings stay in your totals. Saved report copies, log files and notifications already sent are not changed.')) return;
            fetch('/api/reports/' + encodeURIComponent(runId) + '/files/' + encodeURIComponent(fid) + '/scrub', { method: 'POST' })
              .then(function (r) { if (r.ok) location.reload(); else window.alert('Could not remove it.'); })
              .catch(function () { window.alert('Could not remove it.'); });
          });
          td.appendChild(b);
        }
        tr.appendChild(td);
      });
    });
  }

  document.getElementById('rtMail').addEventListener('click', function (e) {
    e.preventDefault();
    location.href = 'mailto:?subject=' + encodeURIComponent(document.title) + '&body=' + encodeURIComponent(summary() + '\n\n' + base);
  });
})();
</script>
""";

    /// <summary>Puts the buttons into a report page: at the generator's marker when it is there, otherwise just before the end of the page (a report saved by an older version).</summary>
    public static byte[] AddTo(byte[] page)
    {
        var text = System.Text.Encoding.UTF8.GetString(page);
        if (text.Contains(Marker, StringComparison.Ordinal)) text = text.Replace(Marker, Html, StringComparison.Ordinal);
        else
        {
            var end = text.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (end < 0) return page;
            text = text.Insert(end, Html);
        }
        return System.Text.Encoding.UTF8.GetBytes(text);
    }
}
