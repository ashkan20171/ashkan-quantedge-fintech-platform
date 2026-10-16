(() => {
  'use strict';
  const target = document.getElementById('sqlRiskPolicy');
  if (!target) return;
  const format = value => new Intl.NumberFormat('en-US', {maximumFractionDigits: 0}).format(value);
  async function load() {
    try {
      const response = await fetch('/api/sql-paper/risk-policy', {credentials: 'same-origin'});
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const policy = await response.json();
      const en = `SQL limits · $${format(policy.maxOrderNotionalUsd)} max fill · $${format(policy.maxDailyTradedNotionalUsd)} daily turnover · ${policy.maxDailyFills} fills/day · ${policy.maxGrossExposurePercent}% gross exposure (risk-increasing only).`;
      const fa = `محدودیت SQL · سقف هر معامله ${format(policy.maxOrderNotionalUsd)} دلار · گردش روزانه ${format(policy.maxDailyTradedNotionalUsd)} دلار · ${policy.maxDailyFills} اجرا در روز · سقف مواجهه ${policy.maxGrossExposurePercent}٪.`;
      target.dataset.en = en;
      target.dataset.fa = fa;
      target.textContent = document.documentElement.lang === 'fa' ? fa : en;
    } catch {
      target.textContent = document.documentElement.lang === 'fa' ? 'وضعیت محدودیت ریسک در دسترس نیست.' : 'Risk limits unavailable.';
    }
  }
  load();
})();
