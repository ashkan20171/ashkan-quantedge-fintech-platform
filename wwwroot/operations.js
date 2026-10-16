(() => {
  const section = document.getElementById('operations');
  const message = document.getElementById('opsMessage');
  const $ = id => document.getElementById(id);
  const fa = () => document.documentElement.lang === 'fa';
  async function json(url, options) {
    const r = await fetch(url, {credentials:'same-origin', cache:'no-store', ...options});
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    return r.json();
  }
  async function refresh() {
    try {
      const data = await json('/api/admin/operations/summary');
      $('opsDb').textContent = data.sqlConnected ? 'ONLINE' : 'OFFLINE';
      $('opsSafety').textContent = data.safety?.paused === false ? 'ACTIVE' : 'PAUSED';
      $('opsPending').textContent = data.pendingOrders ?? '—';
      $('opsAccounts').textContent = data.accounts ?? '—';
      $('opsResume').disabled = !data.sqlConnected || data.safety?.initialized === false;
      message.textContent = fa() ? 'وضعیت به‌روزرسانی شد.' : 'Status updated.';
    } catch(e) { message.textContent = (fa() ? 'خطا در دریافت وضعیت: ' : 'Status unavailable: ') + e.message; }
  }
  async function action(which) {
    if (!confirm(fa() ? 'تغییر وضعیت معاملات آزمایشی SQL را تأیید می‌کنید؟' : 'Confirm changing SQL paper trading safety state?')) return;
    $('opsPause').disabled = $('opsResume').disabled = true;
    try { await json('/api/admin/trading-safety/' + which, {method:'POST'}); await refresh(); }
    catch(e) { message.textContent = e.message; }
    finally { $('opsPause').disabled = false; }
  }
  $('opsRefresh').addEventListener('click', refresh);
  $('opsPause').addEventListener('click', () => action('pause'));
  $('opsResume').addEventListener('click', () => action('resume'));
  async function init() {
    try { const me = await json('/api/auth/me'); if (me.authenticated && me.role === 'Admin') {
      section.hidden = false; await refresh();
    } else section.hidden = true; } catch { section.hidden = true; }
  }
  window.addEventListener('quantedge:authenticated', init);
  init();
})();
