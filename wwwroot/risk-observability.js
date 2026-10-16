(() => {
  'use strict';
  const panel = document.getElementById('risk-observability');
  const message = document.getElementById('riskMessage');
  const fa = () => document.documentElement.lang === 'fa';
  const show = (id, value) => { document.getElementById(id).textContent = value; };
  async function refresh() {
    try {
      const response = await fetch('/api/admin/risk/overview', {credentials:'same-origin', cache:'no-store'});
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const data = await response.json();
      if (!data.connected) throw new Error(fa() ? 'اتصال SQL برقرار نیست' : 'SQL connection unavailable');
      show('riskTotalCash', new Intl.NumberFormat(fa() ? 'fa-IR' : 'en-US', {style:'currency', currency:'USD'}).format(data.totalCash));
      show('riskTrades', data.executedTrades);
      show('riskPending', data.pendingOrders);
      message.textContent = fa() ? 'اطلاعات به‌روزرسانی شد.' : 'Overview refreshed.';
    } catch (error) { message.textContent = error.message; }
  }
  async function init() {
    try {
      const response = await fetch('/api/auth/me', {credentials:'same-origin', cache:'no-store'});
      const me = await response.json();
      panel.hidden = !(me.authenticated && me.role === 'Admin');
      if (!panel.hidden) await refresh();
    } catch { panel.hidden = true; }
  }
  document.getElementById('riskRefresh').addEventListener('click', refresh);
  window.addEventListener('quantedge:authenticated', init);
  init();
})();
