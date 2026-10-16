(() => {
  'use strict';
  const el = id => document.getElementById(id);
  const money = n => new Intl.NumberFormat('en-US', {style:'currency', currency:'USD'}).format(Number(n || 0));
  const lang = () => document.documentElement.lang === 'fa' ? 'fa' : 'en';
  const msg = (en, fa, error=false) => { const node=el('sqlMessage'); node.textContent=lang()==='fa'?fa:en; node.style.color=error?'#fb7185':'#34d399'; };
  async function api(path, options={}) {
    const response = await fetch('/api/sql-paper'+path, {credentials:'same-origin', headers:{'Content-Type':'application/json'}, ...options});
    const payload = await response.json().catch(()=>null);
    if (!response.ok) throw new Error(payload?.error || `HTTP ${response.status}`);
    return payload;
  }
  const cell = (row, value) => { const td=document.createElement('td'); td.textContent=String(value??'—'); row.appendChild(td); return td; };
  async function refresh() {
    try {
      const [account, orders, positions] = await Promise.all([api('/account'),api('/orders'),api('/positions')]);
      el('sqlCash').textContent=money(account.cashBalance);
      el('sqlEquity').textContent=money(positions.estimatedEquity);
      const body=el('sqlOrders'); body.replaceChildren();
      for (const o of orders) {
        const row=document.createElement('tr');
        [o.symbol,o.side,o.quantity,o.limitPrice,o.status].forEach(v=>cell(row,v));
        const td=cell(row,'');
        if(o.status==='Pending') {
          const execute=document.createElement('button'); execute.type='button'; execute.className='chart-mode'; execute.textContent=lang()==='fa'?'اجرای آزمایشی':'Try fill';
          execute.addEventListener('click',()=>act(`/orders/${encodeURIComponent(o.id)}/execute`,'POST')); td.appendChild(execute);
          const cancel=document.createElement('button'); cancel.type='button'; cancel.className='chart-mode'; cancel.textContent=lang()==='fa'?'لغو':'Cancel';
          cancel.addEventListener('click',()=>act(`/orders/${encodeURIComponent(o.id)}`,'DELETE')); td.appendChild(cancel);
        }
        body.appendChild(row);
      }
      const positionsNode=el('sqlPositions'); positionsNode.replaceChildren();
      for(const p of positions.positions || []) {const line=document.createElement('p');line.textContent=`${p.symbol} · ${p.quantity} · ${money(p.marketValue)}`;positionsNode.appendChild(line);}
      if(!positions.positions?.length) positionsNode.textContent=lang()==='fa'?'موقعیت بازی وجود ندارد':'No open SQL positions';
      msg('SQL account connected.','حساب SQL متصل شد.');
    } catch(e) { msg(`SQL workspace: ${e.message}`,`محیط SQL: ${e.message}`,true); }
  }
  async function act(path,method,body) {
    try {await api(path,{method,...(body?{body:JSON.stringify(body)}:{})});await refresh();}
    catch(e) {msg(e.message,e.message,true);}
  }
  el('sqlCreate').addEventListener('click',()=>act('/account','POST'));
  el('sqlPlace').addEventListener('click',()=>act('/orders/limit','POST',{
    symbol:el('sqlSymbol').value,side:el('sqlSide').value,quantity:Number(el('sqlQty').value),limitPrice:Number(el('sqlLimit').value)
  }));
  el('sqlRefresh').addEventListener('click',refresh);
  refresh();
})();
