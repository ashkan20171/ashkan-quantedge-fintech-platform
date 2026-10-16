(() => {
  const money = v => new Intl.NumberFormat(document.documentElement.lang === 'fa' ? 'fa-IR' : 'en-US', {style:'currency',currency:'USD',maximumFractionDigits:2}).format(v);
  async function refresh() {
    try {
      const response = await fetch('/api/reports/summary', {cache:'no-store'});
      if (!response.ok) throw new Error('HTTP '+response.status);
      const data = await response.json();
      document.getElementById('reportNotional').textContent=money(data.totalTradedNotional);
      document.getElementById('reportBuys').textContent=data.buyOrders;
      document.getElementById('reportSells').textContent=data.sellOrders;
      document.getElementById('reportStatus').textContent='';
    } catch(e) { document.getElementById('reportStatus').textContent='Report unavailable: '+e.message; }
  }
  refresh(); setInterval(refresh, 10000);
  document.getElementById('langSwitch')?.addEventListener('click',()=>setTimeout(refresh,50));
})();
