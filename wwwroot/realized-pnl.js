(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const button = $('sqlPnlRefresh');
  if (!button) return;
  const money = value => `${Number(value) < 0 ? '−' : ''}$${new Intl.NumberFormat('en-US', {minimumFractionDigits: 2, maximumFractionDigits: 2}).format(Math.abs(Number(value)))}`;
  const set = (id, value) => { if ($(id)) $(id).textContent = value; };
  const message = (en, fa) => {
    const el = $('sqlPnlMessage'); if (!el) return;
    el.dataset.en = en; el.dataset.fa = fa;
    el.textContent = document.documentElement.lang === 'fa' ? fa : en;
  };
  async function refresh() {
    button.disabled = true;
    try {
      const response = await fetch('/api/sql-paper/realized-pnl', {credentials:'same-origin', cache:'no-store'});
      if (!response.ok) throw new Error(response.status === 404 ? 'SQL paper account not created' : `HTTP ${response.status}`);
      const result = await response.json();
      set('sqlPnlToday', money(result.realizedTodayUsd));
      set('sqlPnlAll', money(result.realizedAllTimeUsd));
      set('sqlPnlSells', String(result.matchedSellFills));
      message('FIFO realized P&L · synthetic data · read-only.', 'سود و زیان FIFO · داده شبیه‌سازی‌شده · فقط خواندنی.');
    } catch (error) {
      set('sqlPnlToday', '—'); set('sqlPnlAll', '—'); set('sqlPnlSells', '—');
      message(`P&L unavailable (${error.message}).`, `سود و زیان در دسترس نیست (${error.message}).`);
    } finally { button.disabled = false; }
  }
  button.addEventListener('click', refresh);
  $('sqlCreate')?.addEventListener('click', () => setTimeout(refresh, 900));
  $('sqlRefresh')?.addEventListener('click', refresh);
  refresh();
})();
