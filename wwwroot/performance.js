(() => {
  const $ = id => document.getElementById(id);
  const fmt = n => new Intl.NumberFormat(document.documentElement.lang === 'fa' ? 'fa-IR':'en-US', {maximumFractionDigits:2}).format(n);
  function draw(points) {
    const canvas=$('equityChart'), ctx=canvas.getContext('2d');
    const width=canvas.clientWidth || 600, height=180, dpr=window.devicePixelRatio||1;
    canvas.width=Math.round(width*dpr); canvas.height=Math.round(height*dpr);
    ctx.setTransform(dpr,0,0,dpr,0,0);ctx.clearRect(0,0,width,height);
    if(!points.length) return;
    const values=points.map(p=>Number(p.equity));
    let low=Math.min(...values),high=Math.max(...values); if(high===low){high+=1;low-=1;}
    const pad=18, x=i=>pad+(width-2*pad)*(points.length===1?.5:i/(points.length-1));
    const y=v=>height-pad-(v-low)/(high-low)*(height-2*pad);
    ctx.strokeStyle='#2d425e';ctx.lineWidth=1;
    for(let i=0;i<4;i++){const yy=pad+(height-2*pad)*i/3;ctx.beginPath();ctx.moveTo(pad,yy);ctx.lineTo(width-pad,yy);ctx.stroke();}
    ctx.strokeStyle='#38d9c5';ctx.lineWidth=2.5;ctx.beginPath();
    values.forEach((v,i)=>i?ctx.lineTo(x(i),y(v)):ctx.moveTo(x(i),y(v)));ctx.stroke();
  }
  async function refresh(){
    try {
      const r=await fetch('/api/performance',{cache:'no-store'});if(!r.ok)throw Error('HTTP '+r.status);
      const data=await r.json();
      $('perfReturn').textContent=fmt(data.returnPct)+'%';
      $('perfDrawdown').textContent=fmt(data.maxDrawdownPct)+'%';
      $('perfPeak').textContent='$'+fmt(data.highWatermark);
      $('perfStatus').textContent=document.documentElement.lang==='fa'?'داده‌های آزمایشی هر ۱۰ ثانیه ثبت می‌شوند.':'Simulated equity sampled every 10 seconds.';
      draw(data.points||[]);
    }catch(err){$('perfStatus').textContent='Performance data unavailable: '+err.message;}
  }
  refresh();setInterval(refresh,10000);window.addEventListener('resize',refresh);
})();
