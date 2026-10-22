(() => {
  'use strict';
  const scenario = new URLSearchParams(location.search).get('case') || 'available';
  const mouseScenario = ['mouse-notice', 'mouse-alert', 'mouse-unknown', 'mouse-result-mismatch', 'mouse-refresh'].includes(scenario);
  const stopNotice = '서대구정차하는 열차입니다. 도착시간을 확인하시기 바랍니다.';
  const list = document.querySelector('.tckWrap > ul');
  const state = { scenario, gradeClicks: 0, reserveClicks: 0, filterQueries: 0, mainQueries: 0, clearedAt: 0, restoredAt: 0, lastTrain: '', lastGrade: '' };
  let pending = false;
  let selectedJourney = null;
  const number = row => row.querySelector('.flag_wrap .num').textContent.trim();
  const rowByNumber = train => [...document.querySelectorAll('.tckList')].find(row => number(row) === String(train));
  for (const row of document.querySelectorAll('.tckList')) row.dataset.fixtureTrain = number(row);
  if (mouseScenario) {
    const navigation = document.createElement('nav');
    navigation.setAttribute('aria-label', '마우스 예약 모의 시나리오');
    navigation.style.cssText = 'display:flex;flex-wrap:wrap;gap:8px;margin:12px 0';
    for (const [name, label] of [
      ['mouse-notice', '정차 안내 모달'], ['mouse-alert', '정차 안내 alert'],
      ['mouse-unknown', '알 수 없는 안내'], ['mouse-result-mismatch', '완료 여정 불일치'], ['mouse-refresh', '재조회 후 좌석 표시']
    ]) {
      const link = document.createElement('a');
      link.href = `?case=${name}`; link.textContent = label;
      link.style.cssText = 'padding:8px 12px;border:1px solid #adc3d0;border-radius:6px;background:white';
      if (name === scenario) { link.setAttribute('aria-current', 'page'); link.style.background = '#dff2ed'; }
      // Fixture train links are prevented below; scenario links must navigate.
      link.addEventListener('click', event => event.stopPropagation());
      navigation.append(link);
    }
    document.getElementById('card-result').before(navigation);
  }
  function drawResult() {
    document.getElementById('card-result').textContent = `등급 선택 ${state.gradeClicks}회 · 모의 예매 ${state.reserveClicks}회 · 활성 분류 새로고침 ${state.filterQueries}회`;
  }
  function closeDrawer() {
    const old = document.querySelector('.ticket_reserv_wrap');
    if (old) old.remove();
  }
  function drawer(grade) {
    closeDrawer();
    const element = document.createElement('div');
    element.className = 'ticket_reserv_wrap';
    element.innerHTML = `<div class="ticket_reserv_inner"><div class="reserv_wrapbtn"><button type="button" class="reserv_btn"><span class="blind">예매 숨기기 버튼</span></button></div><div class="ticket_reserv clear oneline"><ul class="reserv_first clear" tabindex="0"><li>${grade}</li><li><button class="tck-trn-select-info__free-seat-info-opener">자유석1량&gt;</button></li></ul><ul class="reserv_center clear"><li><a href="#">열차시각</a></li><li><a href="#">운임요금</a></li><li><a href="#">좌석선택</a></li></ul><div class="reservbtnWrap one_btn"><button type="button" class="btn_bn-blue02 reservbtn" tabindex="0">예매</button></div></div></div>`;
    document.getElementById('container').append(element);
  }
  function closeMouseNotice() {
    const overlay = document.getElementById('card-mouse-notice-overlay');
    if (overlay) overlay.remove();
  }
  function showMouseNotice() {
    if (scenario === 'mouse-alert') { window.alert(stopNotice); return; }
    if (scenario !== 'mouse-notice' && scenario !== 'mouse-unknown' && scenario !== 'mouse-refresh') return;
    closeMouseNotice();
    const overlay = document.createElement('div');
    overlay.id = 'card-mouse-notice-overlay';
    overlay.style.cssText = 'position:fixed;inset:0;z-index:99999;background:#183a4b66;display:grid;place-items:center;padding:24px';
    const dialog = document.createElement('section');
    dialog.setAttribute('role', 'dialog'); dialog.setAttribute('aria-modal', 'true');
    dialog.setAttribute('aria-label', '열차 안내');
    dialog.style.cssText = 'width:min(460px,90vw);padding:24px;border-radius:10px;background:white;box-shadow:0 8px 36px #0005;text-align:center';
    const paragraph = document.createElement('p');
    paragraph.textContent = scenario === 'mouse-unknown'
      ? '운임 및 여정 조건이 변경되었습니다. 내용을 확인한 뒤 직접 진행하시기 바랍니다.'
      : stopNotice;
    const confirm = document.createElement('button');
    confirm.type = 'button'; confirm.textContent = '확인';
    confirm.addEventListener('click', event => { event.stopPropagation(); closeMouseNotice(); });
    dialog.append(paragraph, confirm); overlay.append(dialog); document.body.append(overlay);
    confirm.focus();
  }
  function rememberJourney(row, grade) {
    const times = row.querySelector('.data_box h3').textContent.match(/(\d{2}:\d{2})\s*~\s*(\d{2}:\d{2})/);
    const kind = row.querySelector('.flag_wrap .blind');
    selectedJourney = {
      train: number(row), kind: kind ? kind.textContent.trim() : '',
      departure: times ? times[1] : '', arrival: times ? times[2] : '', grade
    };
  }
  function showMockReservation() {
    // This fixture deliberately does not imitate an unobserved real result DOM.
    // Visible labeled fields let the verifier test evidence consistency.
    closeMouseNotice(); closeDrawer();
    const section = document.createElement('section');
    section.id = 'card-mock-reservation';
    section.setAttribute('data-reservation-detail', '');
    section.setAttribute('aria-label', '모의 예약 완료 상세');
    section.style.cssText = 'padding:24px;background:white;border:2px solid #126b78;border-radius:10px';
    const note = document.createElement('p'); note.className = 'fixture-note';
    note.textContent = '이 예약 결과 화면은 로컬 테스트를 위해 만든 모의 화면이며 실제 코레일 예약 결과 DOM이 아닙니다.';
    const heading = document.createElement('h2'); heading.textContent = '예약이 완료되었습니다.';
    section.append(note, heading);
    const details = [
      ['예약번호', 'TEST-20261022-12345'],
      ['예약 상태', '미결제'],
      ['승차일', scenario === 'mouse-result-mismatch' ? '2026-10-23' : '2026-10-22'],
      ['출발역', '서울'], ['도착역', '전주'],
      ['열차', `${selectedJourney.kind} ${selectedJourney.train}`.trim()],
      ['출발시각', selectedJourney.departure], ['도착시각', selectedJourney.arrival],
      ['좌석등급', selectedJourney.grade], ['인원', '총 1명'],
      ['결제기한', '2026-10-22 00:30']
    ];
    for (const [label, value] of details) {
      const paragraph = document.createElement('p'); paragraph.textContent = `${label}: ${value}`;
      section.append(paragraph);
    }
    document.getElementById('container').replaceChildren(section);
    section.scrollIntoView({ block: 'start' });
  }
  function makeAvailable(row, grade) {
    const index = grade === '일반실' ? 0 : 1;
    const slot = row.querySelectorAll('.tck_inner > .price_box')[index];
    slot.className = 'price_box fl-l ' + (grade === '일반실' ? 'gen' : 'spe');
    slot.innerHTML = `<div class="inner type02"><a href="#" title=""><p class="txt_ch">${grade}</p><p class="txt_price txt_bk">31,500원</p><p class="txt_gr">5%적립</p></a></div>`;
  }
  function refresh() {
    if (pending) return;
    pending = true; state.filterQueries++;
    closeDrawer();
    const copies = [...list.children].map(row => row.cloneNode(true));
    list.replaceChildren(); state.clearedAt = performance.now();
    const loading = document.createElement('p'); loading.id = 'card-loading'; loading.textContent = '열차 목록을 불러오는 중'; loading.setAttribute('role', 'status');
    list.parentElement.append(loading); drawResult();
    // Real card refresh temporarily removes every result. Keep it absent beyond the stability delay.
    setTimeout(() => {
      if (scenario === 'reorder') copies.reverse();
      list.replaceChildren(...copies);
      for (const box of document.querySelectorAll('.price_box.active')) box.classList.remove('active');
      for (const anchor of document.querySelectorAll('.price_box a[title="선택"]')) anchor.title = '';
      if (scenario === 'refresh' || scenario === 'reorder' || scenario === 'mouse-refresh') makeAvailable(rowByNumber('541'), '일반실');
      loading.remove(); state.restoredAt = performance.now(); pending = false; drawResult();
    }, 1400);
  }
  document.addEventListener('click', event => {
    const anchor = event.target.closest('a'); if (anchor) event.preventDefault();
    const slot = event.target.closest('.price_box');
    if (slot) {
      if (slot.classList.contains('sold_out') || slot.classList.contains('yms') || !slot.querySelector('.txt_price')) return;
      for (const other of document.querySelectorAll('.price_box.active')) other.classList.remove('active');
      for (const other of document.querySelectorAll('.price_box a[title="선택"]')) other.title = '';
      slot.classList.add('active'); slot.querySelector('a').title = '선택';
      const grade = slot.classList.contains('spe') ? '특실' : '일반실';
      state.gradeClicks++; state.lastTrain = number(slot.closest('.tckList')); state.lastGrade = grade;
      if (mouseScenario) rememberJourney(slot.closest('.tckList'), grade);
      drawer(scenario === 'mismatch' ? (grade === '일반실' ? '특실' : '일반실') : grade);
      drawResult();
      if (mouseScenario) showMouseNotice();
      return;
    }
    const button = event.target.closest('button'); if (!button) return;
    if (button.matches('.reservbtn')) {
      if (mouseScenario && document.getElementById('card-mouse-notice-overlay')) return;
      state.reserveClicks++; drawResult();
      if (mouseScenario && selectedJourney) showMockReservation();
      return;
    }
    if (button.matches('.reserv_btn')) { closeDrawer(); return; }
    if (button.closest('.tab_button.active')) { refresh(); return; }
    if (button.textContent.trim() === '열차조회') { state.mainQueries++; drawResult(); }
  });
  window.__cardDemo = Object.freeze({
    snapshot: () => ({ ...state }),
    row: rowByNumber,
    makeAvailable: (train, grade = '일반실') => makeAvailable(rowByNumber(train), grade),
    refresh
  });
  drawResult();
})();
