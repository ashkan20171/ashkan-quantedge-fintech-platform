(() => {
  'use strict';
  const output = document.getElementById('dailyRiskBudget');
  const refresh = document.getElementById('dailyRiskRefresh');
  if (!output || !refresh) return;
  const number = n => new Intl.NumberFormat('en-US', {maximumFractionDigits: 2}).format(n);
  async function load() {
    refresh.disabled = true;
    try {
      const response = await fetch('/api/sql-paper/daily-risk-budget', {credentials: 'same-origin', cache: 'no-store'});
      if (!response.ok) throw new Error(response.status === 404 ? 'Account not created' : `HTTP ${response.status}`);
      const data = await response.json();
      const en = `Today: $${number(data.tradedNotionalUsd)} / $${number(data.maxDailyTradedNotionalUsd)} traded · ${data.fillCount} / ${data.maxDailyFills} fills · $${number(data.remainingNotionalUsd)} left. Resets at ${new Date(data.resetAtUtc).toLocaleString('en-GB', {timeZone:'UTC'})} UTC.`;
      const fa = `امروز: ${number(data.tradedNotionalUsd)} از ${number(data.maxDailyTradedNotionalUsd)} دلار گردش · ${data.fillCount} از ${data.maxDailyFills} اجرا · ${number(data.remainingNotionalUsd)} دلار باقی‌مانده. بازنشانی: ${new Date(data.resetAtUtc).toLocaleString('en-GB', {timeZone:'UTC'})} UTC.`;
      output.dataset.en = en;
      output.dataset.fa = fa;
      output.textContent = document.documentElement.lang === 'fa' ? fa : en;
    } catch (error) {
      const en = `Daily budget unavailable (${error.message}).`;
      const fa = `بودجه روزانه در دسترس نیست (${error.message}).`;
      output.dataset.en = en; output.dataset.fa = fa;
      output.textContent = document.documentElement.lang === 'fa' ? fa : en;
    } finally { refresh.disabled = false; }
  }
  refresh.addEventListener('click', load);
  document.getElementById('sqlCreate')?.addEventListener('click', () => setTimeout(load, 900));
  document.getElementById('sqlRefresh')?.addEventListener('click', load);
  load();
})();
