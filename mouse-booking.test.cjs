'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
require('./assets/core.js');
const B = require('./assets/mouse-booking.js');
const target = Object.freeze({ date: '2026-10-22', departureStation: '서울', arrivalStation: '전주', train: 'KTX 513', departure: '12:19', arrival: '14:01', grade: '일반실', passengers: 1 });
// Synthetic completion copy, NOT a captured or verified Korail completion DOM.
const receiptText = '승차권 예약이 완료되었습니다. 예약번호 12345-67890 예약 상태 미결제 출발일 2026-10-22 출발역 서울 도착역 전주 KTX 513 출발시각 12:19 도착시각 14:01 일반실 인원 1명 결제기한 2026-10-22 12:00까지';

test('validates complete one-passenger target and rejects malformed selections', () => {
  assert.equal(B.targetError(target), null);
  for (const change of [{ passengers: 2 }, { date: '2026-02-30' }, { date: '2026-13-01' }, { arrivalStation: '서울역' }, { departure: '24:00' }, { grade: '입석' }, { train: '513' }, { train: 'KTX 513 and KTX 505' }])
    assert.ok(B.targetError({ ...target, ...change }), JSON.stringify(change));
});

test('query and train identity must match date, route, count, number and both times', () => {
  const query = { ...target, supported: true };
  assert.equal(B.queryError(query, target), null);
  for (const change of [{ date: '2026-10-23' }, { departureStation: '용산' }, { arrivalStation: '부산' }, { passengers: 2 }, { supported: false }])
    assert.ok(B.queryError({ ...query, ...change }, target));
  assert.equal(B.sameTrain(target, target), true);
  for (const change of [{ train: 'KTX 505' }, { departure: '12:20' }, { arrival: '14:02' }, { departureStation: '용산' }, { arrivalStation: '부산' }])
    assert.equal(B.sameTrain({ ...target, ...change }, target), false);
});

test('allows only exact station-stop guidance with whitespace variation', () => {
  for (const text of ['서대구정차하는 열차입니다.\n도착시간을 확인하시기 바랍니다.', '안내 메시지 서대전 정차하는 열차입니다. 도착시간을 확인하시기 바랍니다.', '서대구 정차하는 열차입니다 . 도착 시간을 확인하시기 바랍니다 .'])
    assert.equal(B.classifyNotice(text).allowed, true);
  for (const text of ['서대구정차하는 열차입니다.', '도착시간을 확인하시기 바랍니다.', '서대구정차하는 열차입니다. 도착시간을 확인하시기 바랍니다. 요금이 추가됩니다.', '자동화된 요청으로 감지되어 차단되었습니다. CODE : -8002', '로그인이 필요합니다.', '오류가 발생했습니다.', '보안 문자를 입력하세요.'])
    assert.equal(B.classifyNotice(text).allowed, false, text);
});

test('standing, free-seat and waitlist labels are never ordinary available seats', () => {
  for (const text of ['입석 + 좌석', '자유석 예매', '예약 대기']) assert.equal(B.forbiddenSeat(text), true);
  for (const text of ['일반실 31,600원', '특실(매진임박) 46,000원', '매진']) assert.equal(B.forbiddenSeat(text), false);
});

test('synthetic receipt requires explicit completion and returns only observed fields', () => {
  const receipt = B.parseReceipt(receiptText, target);
  assert.equal(receipt.reservationNumber, '12345-67890');
  assert.equal(receipt.paymentDeadline, '2026-10-22 12:00까지');
  assert.equal(receipt.evidenceText, receiptText);
  assert.deepEqual(receipt.observedTarget, target);
  assert.notEqual(receipt.observedTarget, target, 'observed values are parsed, not the supplied target object');
  assert.equal(B.parseReceipt(receiptText.replace(/ 결제기한.*/, ''), target).paymentDeadline, '');
  assert.ok(B.parseReceipt(receiptText + ' 예약 취소', target), 'a cancellation button alone is not a cancellation result');
  assert.equal(B.parseReceipt(receiptText.replace('승차권 예약이 완료되었습니다.', '예매 예약확인 /ticket/reservation/complete'), target), null);
});

test('receipt rejects every target mismatch, duplicate reservation, duplicate train and incomplete evidence', () => {
  const invalid = [
    receiptText.replace('2026-10-22', '2026-10-23'), receiptText.replace('출발역 서울', '출발역 용산'),
    receiptText.replace('도착역 전주', '도착역 부산'), receiptText.replace('KTX 513', 'KTX 505'),
    receiptText.replace('출발역 서울 도착역 전주', '전주 → 서울'), receiptText.replace('출발역 서울 도착역 전주', '서울 전주'),
    receiptText.replace('12:19', '12:20'), receiptText.replace('14:01', '14:02'), receiptText.replace('일반실', '특실'),
    receiptText.replace('인원 1명', '인원 2명'), receiptText.replace('인원 1명', ''), receiptText.replace('예약번호 12345-67890', ''),
    receiptText + ' 예약번호 12345-67890', receiptText + ' KTX 513', receiptText + ' 특실',
    receiptText + ' 예약이 취소되었습니다.', receiptText + ' 예약 실패', receiptText + ' 매진',
    receiptText.replace('일반실', '입석 + 좌석'), receiptText.replace('일반실', '예약대기'),
    'https://www.korail.com/ticket/complete 예매 결제하기'
  ];
  for (const value of invalid) assert.equal(B.parseReceipt(value, target), null, value);
});

function fixture() {
  const doc = {
    hidden: false, location: { href: 'https://www.korail.com/ticket/search/list' },
    body: { innerText: 'KTX 513 서울 → 전주 일반실 31,600원' },
    documentElement: { clientWidth: 1000, clientHeight: 700 },
    defaultView: { innerWidth: 1000, innerHeight: 700, getComputedStyle: node => node.style },
    dialogs: [], details: [], active: [], captcha: [], alerts: [], drawers: [], top: null,
    querySelectorAll(selector) {
      if (selector.includes('[role="dialog"]')) return this.dialogs;
      if (selector.includes('.modal.show')) return [];
      if (selector.includes('one-time-code')) return this.captcha;
      if (selector === '[role="alert"]') return this.alerts;
      if (selector.includes('data-reservation-detail')) return this.details;
      if (selector.includes('.price_box.active')) return this.active;
      if (selector === '#container > .ticket_reserv_wrap') return this.drawers;
      return [];
    },
    elementFromPoint: () => doc.top
  };
  function node(label, isButton = false) {
    return {
      nodeType: 1, isConnected: true, ownerDocument: doc, parentElement: null,
      innerText: label, textContent: label, disabled: false, style: { display: 'block', visibility: 'visible', opacity: '1' },
      childNodes: [{ nodeType: 3, textContent: label }], children: [], attrs: {},
      rect: { left: 100, top: 120, width: 120, height: 40 },
      closest: () => null, getClientRects() { return this.style.display === 'none' ? [] : [this.rect]; },
      getBoundingClientRect() { return this.rect; }, matches: () => isButton,
      getAttribute(name) { return this.attrs[name] || null; },
      contains(other) { return other === this || this.children.some(child => child.contains && child.contains(other)); },
      querySelectorAll() { return this.children; }
    };
  }
  const row = node('KTX 513'), slot = node('일반실 31,600원'), seat = node('일반실 31,600원', true), reserve = node('예매', true);
  doc.top = seat;
  const adapter = {
    cardSelector: '.tckList', queryValue: { ...target, supported: true }, rows: [row], eligible: true, reserve, isList: true,
    matchesPage() { return this.isList; }, query() { return this.queryValue; }, cards() { return this.rows; },
    identity: current => current.identity || target, slot: () => slot,
    positiveAction() { return this.eligible ? seat : null; }, reservationAction() { return this.reserve; },
    noticeEvidence() { return { rows: this.rows.map(current => ({ identity: this.identity(current), slot, available: this.eligible })), active: doc.active }; }
  };
  globalThis.KorailSiteAdapter = adapter;
  return { doc, row, slot, seat, reserve, adapter, node, read: phase => B.read(target, phase, doc) };
}

test('reader returns seat and reserve coordinates without invoking any page action', () => {
  const f = fixture();
  assert.equal(f.read('select').action.kind, 'seat');
  f.doc.top = f.reserve; f.doc.active = [f.slot];
  assert.equal(f.read('drawer').action.kind, 'reserve');
  assert.equal(f.read('drawer').action.x, 160);
});

test('off-screen actions require native scroll and re-observation; covered actions are blocked', () => {
  const f = fixture(); f.seat.rect.top = 900; f.doc.top = null;
  const offscreen = f.read('select');
  assert.equal(offscreen.state, 'ready'); assert.equal(offscreen.action.inViewport, false); assert.equal(offscreen.action.y, 920);
  f.seat.rect.top = 100;
  assert.equal(f.read('select').state, 'blocked');
  f.doc.top = f.node('overlay'); assert.equal(f.read('select').state, 'blocked');
});

test('sold out waits without a refresh; wrong seats and duplicate selections stop', () => {
  const f = fixture(); f.adapter.eligible = false; f.slot.innerText = '매진';
  assert.equal(f.read('select').state, 'waiting'); assert.equal(f.read('select').action, undefined);
  assert.equal(f.read('select').refreshEligible, true);
  assert.equal(f.read('drawer').refreshEligible, false, 'never refresh after selecting a seat');
  f.doc.active = [f.slot]; assert.equal(f.read('select').refreshEligible, false); f.doc.active = [];
  f.doc.drawers = [f.node('예매')]; assert.equal(f.read('select').refreshEligible, false); f.doc.drawers = [];
  f.slot.innerText = '가격 확인 중'; assert.equal(f.read('select').refreshEligible, false);
  f.slot.innerText = '입석 + 좌석'; assert.equal(f.read('select').state, 'blocked');
  f.adapter.eligible = true; f.slot.innerText = '일반실 31,600원';
  f.doc.active = [f.slot, f.node('일반실')]; assert.equal(f.read('select').state, 'blocked');
  f.doc.active = [f.node('특실')]; assert.equal(f.read('select').state, 'blocked');
  f.doc.active = []; f.adapter.rows = [f.row, f.row]; assert.equal(f.read('select').state, 'blocked');
  f.adapter.rows = []; assert.equal(f.read('select').refreshEligible, false, 'a missing train is not a sold-out seat');
});

test('only a single recognized modal with one confirmation action can be closed', () => {
  const f = fixture(), modal = f.node('서대구정차하는 열차입니다. 도착시간을 확인하시기 바랍니다.'), confirm = f.node('확인', true);
  modal.children = [confirm]; modal.childNodes.push(confirm); f.doc.dialogs = [modal]; f.doc.top = confirm;
  f.doc.body.innerText = '안내 메시지 서대구정차하는 열차입니다. 도착시간을 확인하시기 바랍니다. 확인';
  assert.equal(f.read('drawer').action.kind, 'notice');
  assert.equal(f.read('select').state, 'blocked', 'an unrelated notice before any seat click is not auto-dismissed');
  f.doc.active = [f.slot, f.node('다른 좌석')]; assert.equal(f.read('drawer').state, 'blocked'); f.doc.active = [];
  f.adapter.queryValue.date = '2026-10-23'; assert.equal(f.read('drawer').state, 'blocked'); f.adapter.queryValue.date = target.date;
  assert.equal(f.read('submitted').state, 'blocked');
  modal.children.push(f.node('닫기', true)); assert.equal(f.read('drawer').state, 'blocked'); modal.children.pop();
  f.doc.dialogs.push(f.node('다른 안내')); assert.equal(f.read('drawer').state, 'blocked'); f.doc.dialogs.pop();
  modal.childNodes[0].textContent += ' 추가 요금이 부과됩니다.'; assert.equal(f.read('drawer').state, 'blocked');
});

test('site block, login, captcha and errors take priority over recognized notices', () => {
  for (const message of ['자동화된 요청으로 감지되어 차단되었습니다. CODE : -8002', '로그인이 필요합니다.', '보안 문자를 입력하세요.', '오류가 발생했습니다.']) {
    const f = fixture(); f.doc.body.innerText = message;
    assert.equal(f.read('select').state, 'blocked', message);
  }
  const f = fixture(); f.doc.captcha = [f.node('인증')]; assert.equal(f.read('select').state, 'blocked');
});

// Synthetic structural fixture with the real site adapter. Unlike the stub
// fixture above, its modal isolates the background from accessibility/input.
function isolatedNoticeFixture() {
  const { JSDOM } = require('jsdom'), S = require('./assets/korail-site.js');
  const dom = new JSDOM(`<!doctype html><div id="app"><main id="container">
    <input id="labelstart" value="서울"><input id="labelend" value="전주">
    <input id="startDate" value="2026-10-22(목) 00:00"><input id="labelple" value="총 1명">
    <div class="ticketSrchWrap"><div class="select select02"><div class="selected">직통</div></div><div class="select select01"><div class="selected">일반석</div></div></div>
    <input type="checkbox" id="rtYn"><input type="checkbox" id="adjStnScdlOfrFlg"><input type="checkbox" id="adjStnScdlOfrFlg2">
    <div id="tab_tab_info1"><div class="tckWrap"><ul><li class="tckList"><div class="tck_inner">
      <div class="info_inner"><div class="info_box"><div class="tit_box"><div class="flag_wrap"><span class="blind">KTX</span><span class="num">513</span></div></div><div class="data_box"><h3><span>서울</span> → <span>전주</span><span>(12:19 ~ 14:01)</span></h3></div></div></div>
      <div class="price_box gen"><div class="inner"><a><span class="txt_ch">일반실</span><span class="txt_price">31,600원</span></a></div></div>
      <div class="price_box spe"><div class="inner"><a><span class="txt_ch">특실</span><span class="txt_price">46,000원</span></a></div></div>
    </div></li></ul></div></div></main></div>
    <div role="dialog" aria-modal="true"><p>서대전정차하는 열차입니다. 도착시간을 확인하시기 바랍니다.</p><button>확인</button></div>`,
  { url: 'https://www.korail.com/ticket/search/list', pretendToBeVisual: true });
  const doc = dom.window.document, app = doc.getElementById('app'), modal = doc.querySelector('[role="dialog"]'), row = doc.querySelector('.tckList');
  dom.window.HTMLElement.prototype.getClientRects = function () { return [{ left: 100, top: 100, width: 120, height: 40 }]; };
  dom.window.HTMLElement.prototype.getBoundingClientRect = function () { return { left: 100, top: 100, width: 120, height: 40 }; };
  doc.top = modal.querySelector('button'); doc.elementFromPoint = () => doc.top;
  app.setAttribute('aria-hidden', 'true'); app.setAttribute('inert', '');
  globalThis.KorailSiteAdapter = S;
  return { dom, doc, app, modal, row, S, slot: row.querySelector('.gen'), read: (phase = 'drawer') => B.read(target, phase, doc) };
}

test('recognized station notice observes isolated background without changing DOM or normal input eligibility', () => {
  const f = isolatedNoticeFixture(), before = f.doc.documentElement.outerHTML;
  assert.equal(f.S.cards(f.doc).length, 0);
  assert.equal(f.S.slot(f.row, '일반실'), null);
  assert.equal(f.S.positiveAction(f.slot, '일반실'), null);
  assert.equal(f.read().action.kind, 'notice');
  assert.equal(f.read('select').state, 'blocked');
  assert.equal(f.read('submitted').state, 'blocked');
  assert.equal(f.doc.documentElement.outerHTML, before, 'reader never removes isolation attributes');
  f.app.removeAttribute('aria-hidden');
  assert.equal(f.read().action.kind, 'notice', 'inert alone is evidence-only too');
  assert.equal(f.S.positiveAction(f.slot, '일반실'), null);
});

test('notice background exception never accepts visual hiding or descendant isolation', () => {
  for (const selector of ['#app', '#container', '#tab_tab_info1', '.tckList', '.gen']) {
    for (const css of ['display:none', 'visibility:hidden', 'visibility:collapse', 'opacity:0']) {
      const f = isolatedNoticeFixture(); f.doc.querySelector(selector).style.cssText = css;
      assert.equal(f.read().state, 'blocked', selector + ' ' + css);
    }
    const f = isolatedNoticeFixture(); f.doc.querySelector(selector).setAttribute('hidden', '');
    assert.equal(f.read().state, 'blocked', selector + ' hidden');
  }
  for (const selector of ['#tab_tab_info1', '.tckList', '.gen', '.gen a']) {
    for (const attr of ['aria-hidden', 'inert', 'aria-disabled']) {
      const f = isolatedNoticeFixture(); f.doc.querySelector(selector).setAttribute(attr, 'true');
      assert.equal(f.read().state, 'blocked', selector + ' ' + attr);
    }
  }
});

test('isolated notice retains exact trip, passenger, grade, fare and unique selection checks', () => {
  const changes = [
    doc => { doc.getElementById('startDate').value = '2026-10-23(금) 00:00'; },
    doc => { doc.getElementById('labelple').value = '총 2명'; },
    doc => { doc.getElementById('labelstart').value = '용산'; },
    doc => { doc.querySelector('.select02 .selected').textContent = '환승'; },
    doc => { doc.querySelector('.num').textContent = '515'; },
    doc => { doc.querySelector('h3 span:last-child').textContent = '(12:20 ~ 14:01)'; },
    doc => { doc.querySelector('.gen .txt_ch').textContent = '특실'; },
    doc => { doc.querySelector('.gen .txt_price').textContent = '가격 확인 중'; },
    doc => { doc.querySelector('.gen').classList.add('sold_out'); },
    doc => { doc.querySelector('.spe').classList.add('active'); },
    doc => { doc.querySelector('.tckList').after(doc.querySelector('.tckList').cloneNode(true)); }
  ];
  for (const change of changes) { const f = isolatedNoticeFixture(); change(f.doc); assert.equal(f.read().state, 'blocked'); }
  const f = isolatedNoticeFixture(); f.slot.classList.add('active'); assert.equal(f.read().action.kind, 'notice');
  f.row.querySelector('.spe').classList.add('active'); assert.equal(f.read().state, 'blocked');
});

test('isolated background never authorizes unknown, duplicate, security or covered notices', () => {
  for (const copy of ['운임 조건이 변경되었습니다.', '서대전정차하는 열차입니다. 도착시간을 확인하시기 바랍니다. 동의합니다.', '자동화된 요청으로 감지되어 차단되었습니다. CODE : -8002']) {
    const f = isolatedNoticeFixture(); f.modal.querySelector('p').textContent = copy; assert.equal(f.read().state, 'blocked');
  }
  const duplicate = isolatedNoticeFixture(); duplicate.doc.body.append(duplicate.modal.cloneNode(true)); assert.equal(duplicate.read().state, 'blocked');
  const covered = isolatedNoticeFixture(); covered.doc.top = covered.app; assert.equal(covered.read().state, 'blocked');
  const inside = isolatedNoticeFixture(); inside.app.removeAttribute('aria-hidden'); inside.app.removeAttribute('inert'); inside.doc.getElementById('container').append(inside.modal);
  assert.equal(inside.read().state, 'blocked', 'cannot exempt an ancestor that contains the modal');
});

test('after notice dismissal reservation requires restored ordinary visibility and exact selected grade', () => {
  const f = isolatedNoticeFixture(); f.modal.remove(); f.slot.classList.add('active'); f.slot.querySelector('a').title = '선택';
  f.doc.getElementById('container').insertAdjacentHTML('beforeend', '<div class="ticket_reserv_wrap"><div class="ticket_reserv"><ul class="reserv_first"><li>일반실</li></ul><div class="reservbtnWrap"><button class="reservbtn">예매</button></div></div></div>');
  f.doc.top = f.doc.querySelector('.reservbtn');
  assert.notEqual(f.read().state, 'ready', 'no modal means no accessibility exception');
  f.app.removeAttribute('aria-hidden'); f.app.removeAttribute('inert');
  assert.equal(f.read().action.kind, 'reserve');
  f.doc.querySelector('.reserv_first li').textContent = '특실'; assert.notEqual(f.read().state, 'ready');
});

const couplingCopy = '이용안내 선택하신 열차는 KTX-산천 2개 편성을 연결하여 운행하는 열차로서, 반드시 열차번호와 해당호차를 확인하시고 승차하시기 바랍니다.';
const couplingTarget = Object.freeze({ ...target, date: '2026-10-23', train: 'KTX-산천 541', departure: '07:05', arrival: '09:39' });
function couplingFixture(copy = couplingCopy) {
  const f = isolatedNoticeFixture();
  f.doc.getElementById('startDate').value = '2026-10-23(금) 00:00';
  f.row.querySelector('.blind').textContent = 'KTX-산천'; f.row.querySelector('.num').textContent = '541';
  f.row.querySelector('h3 span:last-child').textContent = '(07:05 ~ 09:39)';
  f.slot.classList.add('active'); f.slot.querySelector('a').title = '선택';
  f.modal.querySelector('p').textContent = copy;
  return { ...f, read: (phase = 'submitted', selected = couplingTarget) => B.read(selected, phase, f.doc) };
}

test('observed coupling guidance permits acknowledgement only in submitted phase on the exact official list path', () => {
  const f = couplingFixture(), before = f.doc.documentElement.outerHTML;
  assert.equal(f.read().state, 'ready'); assert.equal(f.read().action.kind, 'receipt-notice');
  assert.equal(f.read().receipt, undefined, 'guidance alone is not reservation completion');
  assert.equal(f.doc.documentElement.outerHTML, before);
  for (const phase of ['select', 'drawer']) assert.equal(f.read(phase).state, 'blocked', phase);
  for (const url of ['https://www.korail.com/ticket/reservation/detail', 'https://www.korail.com/ticket/search/list-other', 'https://www.korail.com:8443/ticket/search/list', 'https://example.com/ticket/search/list']) {
    f.dom.reconfigure({ url }); assert.equal(f.read().state, 'blocked', url);
  }
  const spaced = couplingFixture(couplingCopy.replace('이용안내 ', '').replaceAll(' ', '\n  '));
  assert.equal(spaced.read().action.kind, 'receipt-notice');
});

test('coupling acknowledgement requires one exact selected train, grade, query and passenger', () => {
  const changes = [
    f => f.slot.classList.remove('active'),
    f => { f.slot.querySelector('a').title = ''; },
    f => { f.row.querySelector('.spe').classList.add('active'); },
    f => { f.slot.classList.remove('active'); f.row.querySelector('.spe').classList.add('active'); },
    f => { f.doc.getElementById('labelple').value = '총 2명'; },
    f => { f.doc.getElementById('startDate').value = '2026-10-24(토) 00:00'; },
    f => { f.doc.getElementById('labelstart').value = '용산'; },
    f => { f.doc.querySelector('.select02 .selected').textContent = '환승'; },
    f => { f.row.querySelector('.num').textContent = '543'; },
    f => { f.row.querySelector('h3 span:last-child').textContent = '(07:06 ~ 09:39)'; },
    f => { f.slot.querySelector('.txt_ch').textContent = '특실'; },
    f => { f.slot.querySelector('.txt_price').textContent = '가격 확인 중'; },
    f => { f.slot.classList.add('sold_out'); },
    f => { f.row.after(f.row.cloneNode(true)); }
  ];
  for (const change of changes) { const f = couplingFixture(); change(f); assert.equal(f.read().state, 'blocked'); }
  const otherType = couplingFixture(); otherType.row.querySelector('.blind').textContent = 'KTX';
  assert.equal(otherType.read('submitted', { ...couplingTarget, train: 'KTX 541' }).state, 'blocked');
});

test('coupling acknowledgement rejects modified terms, consent, extra controls and security prompts', () => {
  for (const copy of [couplingCopy.replace('2개', '3개'), couplingCopy.replace('해당호차를', '운임을'), couplingCopy + ' 동의합니다.', couplingCopy + ' 결제하기', couplingCopy + ' 자동화된 요청으로 감지되어 차단되었습니다. CODE : -8002'])
    assert.equal(couplingFixture(copy).read().state, 'blocked', copy);
  for (const tag of ['input', 'select', 'textarea']) { const f = couplingFixture(); f.modal.append(f.doc.createElement(tag)); assert.equal(f.read().state, 'blocked', tag); }
  const editable = couplingFixture(), field = editable.doc.createElement('div'); field.contentEditable = 'true'; field.setAttribute('contenteditable', 'true'); editable.modal.append(field);
  assert.equal(editable.read().state, 'blocked');
  for (const label of ['닫기', '결제하기']) { const f = couplingFixture(); f.modal.querySelector('button').textContent = label; assert.equal(f.read().state, 'blocked', label); }
  const extra = couplingFixture(); extra.modal.append(extra.modal.querySelector('button').cloneNode(true)); assert.equal(extra.read().state, 'blocked');
  const inactiveExtra = couplingFixture(), payment = inactiveExtra.doc.createElement('button'); payment.disabled = true; payment.textContent = '결제하기'; inactiveExtra.modal.append(payment);
  assert.equal(inactiveExtra.read().state, 'blocked', 'a disabled extra visible action still changes the observed single-confirmation dialog');
  const duplicate = couplingFixture(); duplicate.doc.body.append(duplicate.modal.cloneNode(true)); assert.equal(duplicate.read().state, 'blocked');
});

test('coupling acknowledgement retains modal hit testing and background isolation guards', () => {
  const covered = couplingFixture(); covered.doc.top = covered.app; assert.equal(covered.read().state, 'blocked');
  const disabled = couplingFixture(); disabled.modal.querySelector('button').disabled = true; assert.equal(disabled.read().state, 'blocked');
  for (const selector of ['#app', '#tab_tab_info1', '.tckList', '.gen']) {
    const f = couplingFixture(); f.doc.querySelector(selector).style.display = 'none'; assert.equal(f.read().state, 'blocked', selector);
  }
  for (const attr of ['aria-hidden', 'inert']) { const f = couplingFixture(); f.slot.setAttribute(attr, 'true'); assert.equal(f.read().state, 'blocked', attr); }
  const captcha = couplingFixture(), input = captcha.doc.createElement('input'); input.autocomplete = 'one-time-code'; captcha.modal.append(input);
  assert.equal(captcha.read().state, 'blocked');
});

test('after coupling acknowledgement submitted list waits without any second reserve or completion', () => {
  const f = couplingFixture(); assert.equal(f.read().action.kind, 'receipt-notice');
  f.modal.remove(); f.app.removeAttribute('aria-hidden'); f.app.removeAttribute('inert');
  f.doc.getElementById('container').insertAdjacentHTML('beforeend', '<div class="ticket_reserv_wrap"><div class="ticket_reserv"><ul class="reserv_first"><li>일반실</li></ul><div class="reservbtnWrap"><button class="reservbtn">예매</button></div></div></div>');
  f.doc.top = f.doc.querySelector('.reservbtn');
  const waiting = f.read(); assert.equal(waiting.state, 'unknown'); assert.equal(waiting.action, undefined); assert.equal(waiting.receipt, undefined);
});

test('submitted never returns actions; success needs one complete matching detail region', () => {
  const f = fixture();
  assert.equal(f.read('submitted').state, 'unknown'); assert.equal(f.read('submitted').action, undefined);
  const detail = f.node(receiptText); f.doc.details = [detail]; f.doc.body.innerText = receiptText;
  f.adapter.isList = false;
  assert.equal(f.read('submitted').state, 'completed'); assert.equal(f.read('submitted').action, undefined);
  f.doc.details.push(f.node(receiptText)); assert.equal(f.read('submitted').state, 'unknown');
  f.doc.details.pop(); detail.innerText = receiptText.replace('KTX 513', 'KTX 505'); assert.equal(f.read('submitted').state, 'unknown');
  detail.innerText = receiptText + ' 예약이 취소되었습니다.'; assert.equal(f.read('submitted').state, 'blocked');
});

test('nested detail wrappers count once, but different reservation details do not merge', () => {
  const f = fixture(), outer = f.node(receiptText), inner = f.node(receiptText);
  f.adapter.isList = false;
  outer.children = [inner]; f.doc.details = [outer, inner]; f.doc.body.innerText = receiptText;
  assert.equal(f.read('submitted').state, 'completed');
  f.doc.details = [f.node(receiptText.slice(0, 80)), f.node(receiptText.slice(80))];
  assert.equal(f.read('submitted').state, 'unknown');
});

test('unpaid status is required, missing deadline stays empty, and paid or issued results are rejected', () => {
  for (const value of [receiptText.replace('예약 상태 미결제 ', ''), receiptText.replace('미결제', '결제 완료'), receiptText + ' 발권 완료', receiptText + ' 결제 상태: 완료'])
    assert.equal(B.parseReceipt(value, target), null, value);
  assert.ok(B.parseReceipt(receiptText.replace('미결제', '결제 대기'), target));
  assert.ok(B.parseReceipt(receiptText.replace('예약 상태 미결제', '결제가 완료되지 않았습니다.'), target));
  assert.equal(B.parseReceipt(receiptText.replace(/ 결제기한.*/, ''), target).paymentDeadline, '');
});

test('old result list after submission waits despite other sold-out seats and free-seat information', () => {
  const f = fixture(); f.doc.body.innerText += ' 다른 열차 매진 자유석1량 예약대기';
  assert.equal(f.read('submitted').state, 'unknown');
  f.doc.alerts = [f.node('선택한 좌석이 매진되었습니다.')];
  assert.equal(f.read('submitted').state, 'blocked', 'an explicit processing error still stops');
});

test('receipt failures are scoped to the smallest detail, not unrelated footer and outer copy', () => {
  const f = fixture(), detail = f.node(receiptText), outer = f.node(receiptText + ' 다른 열차 매진 예약대기 자유석1량');
  f.adapter.isList = false; outer.children = [detail]; f.doc.details = [outer, detail];
  f.doc.body.innerText = receiptText + ' 다른 열차 매진 예약대기 자유석1량';
  assert.equal(f.read('submitted').state, 'completed');
  detail.innerText += ' 예약이 취소되었습니다.';
  assert.equal(f.read('submitted').state, 'blocked');
});

// Synthetic semantic HTML reconstructed from the observed UIA list item and
// visible text. This is NOT a captured official reservation-detail DOM.
const detailTarget = Object.freeze({ date: '2026-10-23', departureStation: '서울', arrivalStation: '전주', train: 'KTX 505', departure: '06:44', arrival: '08:39', grade: '일반실', passengers: 1 });
const detailNow = Date.parse('2026-10-22T22:00:00+09:00');
const detailText = 'KTX KTX 505 2026년10월23일(금) 서울 → 전주 (06:44 ~ 08:39) 일반실 9호차 12A (역방향) 좌석정보 더보기 결제기한: 2026. 10. 22. 22:17';
function detailFixture(value = detailText) {
  const { JSDOM } = require('jsdom');
  const dom = new JSDOM('<!doctype html><main><h1>승차권 예약</h1><ul><li id="receipt"><span id="copy"></span> <button>예약취소</button> <button>장바구니</button></li></ul><p>결제기한 내 미결제시 승차권이 자동 취소됩니다.</p><button id="pay">결제하기</button></main>', { url: 'https://www.korail.com/ticket/reservation/detail', pretendToBeVisual: true });
  const doc = dom.window.document;
  doc.getElementById('copy').textContent = value;
  dom.window.HTMLElement.prototype.getClientRects = function () { return [{ left: 10, top: 10, width: 100, height: 30 }]; };
  dom.window.HTMLElement.prototype.getBoundingClientRect = function () { return { left: 10, top: 10, width: 100, height: 30 }; };
  doc.elementFromPoint = () => doc.topElement;
  globalThis.KorailSiteAdapter = { matchesPage: () => false, query: () => ({ supported: false }) };
  return { dom, doc, read: () => B.read(detailTarget, 'submitted', doc, detailNow) };
}

test('observed seat-detail copy returns a seat evidence receipt without inventing a reservation number', () => {
  const f = detailFixture(), result = f.read();
  assert.equal(result.state, 'completed');
  assert.equal(result.receipt.evidenceType, 'seat-detail');
  assert.equal(result.receipt.reservationNumber, null);
  assert.equal(result.receipt.seat, '9호차 12A');
  assert.equal(result.receipt.paymentDeadline, '2026. 10. 22. 22:17');
  assert.deepEqual(result.receipt.observedTarget, detailTarget);
  assert.equal(result.action, undefined);
});

test('seat-detail accepts duplicated expanded copy of the same seat, never a second seat', () => {
  assert.equal(detailFixture(detailText + ' 일반실 9호차12A').read().state, 'completed');
  for (const suffix of [' 일반실 9호차 12B', ' 9호차 12B', ' 특실 9호차 12A', ' 성인 2명'])
    assert.equal(detailFixture(detailText + suffix).read().state, 'unknown', suffix);
});

test('seat-detail requires exact observed train, route, times, date, and grade', () => {
  for (const [before, after] of [['505', '507'], ['서울 → 전주', '전주 → 서울'], ['06:44', '06:45'], ['08:39', '08:40'], ['2026년10월23일', '2026년10월24일'], ['일반실', '특실'], ['9호차 12A', '']])
    assert.equal(detailFixture(detailText.replace(before, after)).read().state, 'unknown', after);
  assert.equal(detailFixture(detailText + ' KTX 507').read().state, 'unknown');
});

test('seat-detail requires a valid future full KST deadline before departure', () => {
  for (const replacement of ['2026. 10. 22. 21:59', '2026. 10. 22. 22:00', '2026. 10. 23. 06:45', '2026. 02. 30. 22:17', '22:17'])
    assert.equal(detailFixture(detailText.replace('2026. 10. 22. 22:17', replacement)).read().state, 'unknown', replacement);
  assert.equal(detailFixture(detailText + ' 결제기한: 2026. 10. 22. 22:18').read().state, 'unknown');
  assert.equal(B.parseSeatDetail(detailText, detailTarget, NaN), null);
});

test('seat-detail rejects paid, issued, cancelled, expired and failed state evidence', () => {
  for (const suffix of [' 결제 완료', ' 발권 완료', ' 예약 취소 완료', ' 예약이 취소되었습니다.', ' 기한 만료', ' 예약 만료', ' 예약 실패', ' 예약대기', ' 입석'])
    assert.notEqual(detailFixture(detailText + suffix).read().state, 'completed', suffix);
  const outside = detailFixture(), message = outside.doc.createElement('p'); message.textContent = '결제 상태: 완료'; outside.doc.querySelector('main').append(message);
  assert.equal(outside.read().state, 'unknown');
});

test('seat-detail requires exactly one payment, cancel and cart control and one semantic receipt', () => {
  for (const label of ['결제하기', '예약취소', '장바구니']) {
    const f = detailFixture();
    Array.from(f.doc.querySelectorAll('button')).find(node => node.textContent === label).disabled = true;
    assert.equal(f.read().state, 'unknown', label);
  }
  const duplicate = detailFixture();
  duplicate.doc.querySelector('ul').append(duplicate.doc.getElementById('receipt').cloneNode(true));
  assert.equal(duplicate.read().state, 'unknown');
  const payments = detailFixture(); payments.doc.querySelector('main').append(payments.doc.getElementById('pay').cloneNode(true));
  assert.equal(payments.read().state, 'unknown');
  const noUnpaid = detailFixture(); noUnpaid.doc.querySelector('p').remove(); assert.equal(noUnpaid.read().state, 'unknown');
  const unrelated = detailFixture(), other = unrelated.doc.createElement('li'); other.textContent = detailText.replace('505', '507'); unrelated.doc.querySelector('ul').append(other);
  assert.equal(unrelated.read().state, 'unknown', 'another trip without active controls remains ambiguous');
});

test('seat-detail evidence is never accepted outside exact official reservation-detail path', () => {
  for (const url of ['https://www.korail.com/ticket/search/list', 'https://www.korail.com/ticket/reservation/detail-other', 'https://www.korail.com:8443/ticket/reservation/detail', 'https://example.com/ticket/reservation/detail']) {
    const f = detailFixture(); f.dom.reconfigure({ url });
    assert.notEqual(f.read().state, 'completed', url);
  }
});

const refundCopy = '환불(반환) 위약금 안내 환불(위약금) 안내 표 평시 승차권 광명역 공항버스 승차권에 기재된 출발일, 출발시각 및 영수금액을 기준으로 환불 위약금이 상이하니 상단 탭을 눌러 해당 환불(반환)위약금을 반드시 확인하시기 바랍니다. 출발 후에는 역창구에서만 환불 청구 가능합니다 최저 위약금은 400원입니다. 위약금 면제 대상';
function refundFixture(copy = refundCopy) {
  const f = detailFixture(), modal = f.doc.createElement('div'); modal.setAttribute('role', 'dialog');
  const body = f.doc.createElement('p'); body.textContent = copy; modal.append(body);
  for (const label of ['확인', '레이어닫기']) { const button = f.doc.createElement('button'); button.textContent = label; modal.append(button); }
  f.doc.body.append(modal); f.doc.topElement = modal.querySelector('button');
  return { ...f, modal };
}
test('only the observed informational refund notice allows a submitted-phase confirmation', () => {
  const f = refundFixture();
  assert.equal(f.read().state, 'ready'); assert.equal(f.read().action.kind, 'receipt-notice');
  assert.equal(B.read(detailTarget, 'drawer', f.doc, detailNow).state, 'blocked');
  f.dom.reconfigure({ url: 'https://www.korail.com/ticket/search/list' }); assert.equal(f.read().state, 'blocked');
});

test('refund notice rejects title alone, new consent or payment actions, form inputs and security prompts', () => {
  for (const copy of ['환불(반환) 위약금 안내', refundCopy.replace('최저 위약금은 400원입니다.', ''), refundCopy + ' 동의합니다', refundCopy + ' 보안 문자를 입력하세요.'])
    assert.equal(refundFixture(copy).read().state, 'blocked', copy);
  for (const kind of ['input', 'select', 'textarea']) { const f = refundFixture(); f.modal.append(f.doc.createElement(kind)); assert.equal(f.read().state, 'blocked', kind); }
  const pay = refundFixture(), button = pay.doc.createElement('button'); button.textContent = '결제하기'; pay.modal.append(button); assert.equal(pay.read().state, 'blocked');
  const duplicate = refundFixture(); duplicate.doc.body.append(duplicate.modal.cloneNode(true)); assert.equal(duplicate.read().state, 'blocked');
});

test('refund notice accepts only its exact accessible table caption when omitted from innerText', () => {
  const f = refundFixture(), table = f.doc.createElement('table'), caption = f.doc.createElement('caption');
  caption.textContent = '환불(위약금) 안내 표'; caption.style.display = 'none'; table.append(caption); f.modal.append(table);
  const visibleCopy = refundCopy.replace('환불(위약금) 안내 표 ', '') + ' 확인 레이어닫기';
  Object.defineProperty(f.modal, 'innerText', { configurable: true, value: visibleCopy });
  assert.equal(f.read().action.kind, 'receipt-notice');
  caption.textContent = '다른 안내 표'; assert.equal(f.read().state, 'blocked');
  caption.textContent = '환불(위약금) 안내 표'; table.append(caption.cloneNode(true)); assert.equal(f.read().state, 'blocked');
  table.lastElementChild.remove();
  Object.defineProperty(f.modal, 'innerText', { configurable: true, value: visibleCopy.replace('최저 위약금은 400원입니다.', '') });
  assert.equal(f.read().state, 'blocked', 'hidden body text cannot supply required visible copy');
});

test('refund close control may use the exact accessible label with a close glyph, not an unrelated action', () => {
  for (const glyph of ['', '×', '✕']) {
    const f = refundFixture(), close = f.modal.querySelectorAll('button')[1];
    close.textContent = glyph; close.setAttribute('aria-label', '레이어닫기');
    assert.equal(f.read().action.kind, 'receipt-notice', glyph);
    close.setAttribute('aria-label', '다른 작업'); assert.equal(f.read().state, 'blocked');
  }
  const f = refundFixture(), close = f.modal.querySelectorAll('button')[1];
  close.setAttribute('aria-label', '레이어닫기'); close.textContent = '다른 작업'; assert.equal(f.read().state, 'blocked');
});
