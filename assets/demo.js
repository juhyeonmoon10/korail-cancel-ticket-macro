// Local desktop demonstration. All helper lock messages use the real native bridge.
(() => {
  const nativeTimeout = window.setTimeout.bind(window);
  let clicks = 0;
  const tbody = document.getElementById('trains');
  const row = (id, state) => `<tr><td>KTX ${id}</td><td>서울 ${id === 101 ? '07:30' : '08:00'}</td><td>부산 ${id === 101 ? '10:15' : '10:45'}</td><td>${state}</td><td>${id === 103 ? '<button data-book="103" data-grade="특실">예약</button>' : '매진'}</td></tr>`;
  function render(state='매진', reverse=false) {
    const rows = [row(101,state), row(103,'<button data-book="103">예약</button>')];
    tbody.innerHTML = (reverse ? rows.reverse() : rows).join('');
  }
  tbody.addEventListener('click', event => {
    const button = event.target.closest('[data-book]');
    if (button) {
      const result = document.getElementById('result');
      result.textContent = `예매 버튼 클릭: ${++clicks}회 · KTX ${button.dataset.book} · ${button.dataset.grade || '일반실'} (모의 동작 · 텔레그램 전송 없음)`;
      result.dataset.clicks = String(clicks);
      result.dataset.train = button.dataset.book;
      result.dataset.grade = button.dataset.grade || '일반실';
    }
  });
  document.getElementById('reset').onclick = () => location.reload();
  document.getElementById('query').onclick = () => {
    const scenario = document.getElementById('scenario').value;
    if (scenario === 'unchanged') return;
    const button = document.getElementById('query'); button.disabled = true;
    nativeTimeout(() => {
      let state = '<button data-book="101">예약</button>';
      if (scenario === 'waiting') state = '<button data-book="101">예약대기</button>';
      if (scenario === 'disabled') state = '<button data-book="101" disabled>예약</button>';
      if (scenario === 'error') document.getElementById('notice').textContent = '자동화된 요청으로 감지되어 차단되었습니다. CODE : -8003';
      if (scenario === 'condition') document.getElementById('date').value = '2026-10-26';
      render(state, scenario === 'reorder'); button.disabled = false;
      if (scenario === 'unstable') nativeTimeout(() => render('매진'), 500);
    }, 350);
  };
  render();
})();
