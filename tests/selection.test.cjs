'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const assets = path.join(__dirname, '../assets');

// Use the public local card fixture and production scripts, with no page script
// execution, network, native input, or reservation handler.
function fixture(t, options = {}) {
  const html = options.table ? '<!doctype html><html><body><table><thead><tr><th>열차</th><th>출발</th><th>도착</th><th>일반실</th><th>특실</th></tr></thead><tbody><tr><td>KTX 101 </td><td>06:00 </td><td>08:30 </td><td><button>예매</button> </td><td>매진</td></tr></tbody></table></body></html>' : fs.readFileSync(path.join(assets, 'card-demo.html'), 'utf8');
  const dom = new JSDOM(html, {
    url: options.table ? 'https://korail-helper.invalid/demo.html' : options.demo ? 'https://korail-helper.invalid/card-demo.html' : 'https://www.korail.com/ticket/search/list',
    runScripts: 'outside-only', pretendToBeVisual: true
  });
  t.after(() => dom.window.close());
  const w = dom.window, doc = w.document, sent = [], handlers = [];
  Object.defineProperty(w.HTMLElement.prototype, 'innerText', { configurable: true, get() { return this.textContent; }, set(value) { this.textContent = value; } });
  w.HTMLElement.prototype.getClientRects = function () { return [{ left: 10, top: 10, width: 100, height: 30 }]; };
  let uuid = 0;
  Object.defineProperty(w.crypto, 'randomUUID', { value: () => 'selection-test-' + ++uuid });
  w.chrome = { webview: {
    addEventListener(type, handler) { handlers.push(handler); },
    postMessage(message) {
      sent.push(message);
      if (message.kind === 'lease') w.queueMicrotask(() => handlers.forEach(handler => handler({ data: { kind: 'leaseResult', id: message.id, nonce: message.nonce, ok: true } })));
    }
  } };
  w.desktopNonce = 'selection-test'; w.desktopJourney = options.journey || null;
  if ('nativeMore' in options) w.desktopNativeMore = options.nativeMore;
  w.eval(fs.readFileSync(path.join(assets, 'core.js'), 'utf8'));
  w.eval(fs.readFileSync(path.join(assets, 'korail-site.js'), 'utf8'));
  const S = w.KorailSiteAdapter;
  const first = S.cards(doc).find(row => S.identity(row).train === 'KTX 505');
  if (first) {
    const extra = first.cloneNode(true);
    extra.querySelector('.num').textContent = '561'; extra.querySelector('h3 span:last-child').textContent = '(10:05 ~ 12:39)';
    first.parentElement.append(extra);
  }
  w.eval(fs.readFileSync(path.join(assets, 'content.js'), 'utf8'));
  const shadow = doc.getElementById('korail-one-helper').shadowRoot, $ = id => shadow.getElementById(id);
  const event = (node, name) => node.dispatchEvent(new w.Event(name, { bubbles: true, composed: true }));
  const read = () => JSON.parse(JSON.stringify(w.KorailDesktopHelper.snapshot()));
  const csv = value => { $('trainNumbers').value = value; event($('trainNumbers'), 'input'); $('applyTrain').click(); };
  const single = number => {
    $('trainNumbers').value = ''; event($('trainNumbers'), 'input'); $('scanTrains').click();
    const candidate = read().candidates.findIndex(item => item.train.endsWith(' ' + number));
    assert.ok(candidate >= 0, 'single candidate exists');
    $('trainChoice').value = String(candidate); event($('trainChoice'), 'change'); $('applyTrain').click();
  };
  const receive = message => handlers.forEach(handler => handler({ data: message }));
  return { dom, w, doc, S, $, sent, event, read, csv, single, receive };
}

test('native card scan posts one request, preserves selection and waits for the host to mount and scan more rows', t => {
  const f = fixture(t, { nativeMore: true });
  const extra = f.S.cards(f.doc).find(row => f.S.identity(row).train === 'KTX 561'); extra.remove();
  f.csv('505'); f.$('confirmed').checked = true;
  const before = f.read(), parent = f.S.cards(f.doc)[0].parentElement;
  let siteInputs = 0;
  for (const node of f.doc.querySelectorAll('button,a,input')) for (const name of ['click', 'input', 'change']) node.addEventListener(name, () => siteInputs++);
  f.w.scrollTo = f.w.HTMLElement.prototype.scrollIntoView = () => { siteInputs++; };
  f.$('scanTrains').click(); f.$('scanTrains').onclick();
  assert.deepEqual(JSON.parse(JSON.stringify(f.sent.filter(message => message.kind === 'readAllTrains'))), [{ kind: 'readAllTrains', nonce: 'selection-test' }]);
  assert.equal(f.$('scanTrains').disabled, true);
  assert.deepEqual(f.read().targets, before.targets); assert.equal(f.read().confirmed, true);
  assert.deepEqual(f.read().candidates, before.candidates); assert.equal(siteInputs, 0);
  parent.append(extra);
  assert.equal(f.read().candidates.some(item => item.train === 'KTX 561'), false, 'new rows are not read by the request itself');
  assert.equal(f.w.KorailDesktopHelper.scanTrains(), true);
  assert.equal(f.read().candidates.some(item => item.train === 'KTX 561'), true);
  assert.deepEqual(f.read().targets, []); assert.equal(f.read().confirmed, false);
  assert.equal(siteInputs, 0); assert.equal(f.$('scanTrains').disabled, false);
});

test('native read completion requires the bridge nonce and restores the panel and controls after rejection', t => {
  const f = fixture(t, { nativeMore: true }); f.$('scanTrains').click(); f.$('fold').click();
  const main = f.$('fold').getRootNode().querySelector('main');
  assert.equal(main.hidden, true); assert.equal(f.$('fold').textContent, '+');
  f.receive({ kind: 'trainReadFinished', nonce: 'wrong' }); assert.equal(main.hidden, true); assert.equal(f.$('scanTrains').disabled, true);
  f.receive({ kind: 'trainReadFinished', nonce: 'selection-test' });
  assert.equal(main.hidden, false); assert.equal(f.$('fold').textContent, '−'); assert.equal(f.$('fold').getAttribute('aria-label'), '접기');
  assert.equal(f.$('scanTrains').disabled, false); assert.deepEqual(f.read().candidates, []);
  f.$('scanTrains').click(); assert.equal(f.sent.filter(message => message.kind === 'readAllTrains').length, 2);
  assert.equal(f.w.KorailDesktopHelper.showTrainPanel(), true); assert.equal(f.$('scanTrains').disabled, false);
});

test('failed native bridge request does not leave the list button disabled', t => {
  const f = fixture(t, { nativeMore: true });
  f.w.chrome.webview.postMessage = message => { if (message.kind === 'readAllTrains') throw new Error('bridge unavailable'); };
  f.$('scanTrains').click(); assert.equal(f.$('scanTrains').disabled, false); assert.match(f.read().status, /요청을 전달하지 못/);
});

test('list expansion and host rescan cannot replace a selection while the legacy loop is starting', t => {
  const f = fixture(t, { nativeMore: true, demo: true }); f.csv('505'); f.$('confirmed').checked = true;
  f.w.chrome.webview.postMessage = message => { f.sent.push(message); };
  f.$('start').click(); assert.equal(f.read().starting, true);
  const selected = f.read().targets;
  assert.equal(f.$('scanTrains').onclick(), false); assert.equal(f.w.KorailDesktopHelper.scanTrains(), false);
  f.receive({ kind: 'trainReadFinished', nonce: 'selection-test' });
  assert.equal(f.$('scanTrains').disabled, true); assert.deepEqual(f.read().targets, selected);
  assert.equal(f.sent.some(message => message.kind === 'readAllTrains'), false);
  f.w.KorailDesktopHelper.stop('test cleanup');
});

test('native CSV apply remains synchronous and explains that missing trains need the list expansion', t => {
  const f = fixture(t, { nativeMore: true }); f.csv('505,561');
  assert.deepEqual(f.read().targets.map(item => item.train), ['KTX 505', 'KTX 561']);
  f.csv('505,99999'); assert.deepEqual(f.read().targets, []); assert.match(f.read().status, /열차 목록 읽기.*더보기/);
  assert.equal(f.sent.some(message => message.kind === 'readAllTrains'), false);
});

test('disabled native flag and table pages retain the synchronous local scan', t => {
  for (const options of [{ nativeMore: false }, { nativeMore: true, table: true }]) {
    const f = fixture(t, options); f.$('scanTrains').click();
    assert.ok(f.read().candidates.length > 0); assert.equal(f.$('scanTrains').disabled, false);
    assert.equal(f.sent.some(message => message.kind === 'readAllTrains'), false);
  }
});

test('blank CSV preserves dropdown selection and the single-target snapshot contract', t => {
  const f = fixture(t);
  assert.equal(f.$('trainNumbers').placeholder, '505,541,561');
  assert.deepEqual(f.read().targets, []); assert.equal(f.read().target, null);
  f.single('505');
  const state = f.read();
  assert.deepEqual(state.targets, [{ train: 'KTX 505', departure: '06:44', arrival: '08:39', grade: '일반실', autoCapable: true }]);
  assert.deepEqual(state.target, state.targets[0]); assert.equal(state.confirmed, false);
  f.$('confirmed').checked = true; assert.equal(f.read().confirmed, true);
});

test('CSV apply rescans all displayed cards and selects unique candidates in input order', t => {
  const f = fixture(t); f.csv('0561, 505, 00541,561');
  const state = f.read();
  assert.deepEqual(state.targets.map(item => item.train), ['KTX 561', 'KTX 505', 'KTX-산천 541']);
  assert.deepEqual(state.target, state.targets[0]); assert.equal(f.$('trainNumbers').value, '561,505,541');
  assert.match(f.$('target').textContent, /선택 열차 3개/);
  assert.ok(f.$('target').textContent.indexOf('KTX 561') < f.$('target').textContent.indexOf('KTX 505'));
  assert.equal(state.confirmed, false); assert.equal(state.reservationClicks, 0);
  assert.equal(f.sent.some(message => message.kind === 'reservationClick'), false);
});

test('any missing or invalid CSV entry clears the whole selection and confirmation', t => {
  for (const value of ['505,99999', '505,,541', '0', '505,']) {
    const f = fixture(t); f.csv('505,541'); f.$('confirmed').checked = true; f.csv(value);
    assert.deepEqual(f.read().targets, [], value); assert.equal(f.read().target, null); assert.equal(f.read().confirmed, false);
    assert.match(f.read().status, /찾지 못|열차번호|빈 항목/);
  }
});

test('duplicate rows and distinct train identities sharing a number are both ambiguous', t => {
  for (const differentTime of [false, true]) {
    const f = fixture(t), original = f.S.cards(f.doc).find(row => f.S.identity(row).train === 'KTX 505');
    const duplicate = original.cloneNode(true);
    if (differentTime) duplicate.querySelector('h3 span:last-child').textContent = '(08:44 ~ 10:39)';
    original.after(duplicate); f.csv('541,505');
    assert.deepEqual(f.read().targets, []); assert.match(f.read().status, /505.*여러 개/);
  }
});

test('CSV apply never relies on a stale previously scanned list', t => {
  const f = fixture(t); f.$('scanTrains').click();
  f.S.cards(f.doc).find(row => f.S.identity(row).train === 'KTX 561').remove();
  f.csv('505,561'); assert.deepEqual(f.read().targets, []); assert.match(f.read().status, /561.*찾지 못/);
});

test('editing CSV, dropdown, grade or rescanning resets every target and confirmation', t => {
  const changes = [
    f => { f.$('trainNumbers').value = '505'; f.event(f.$('trainNumbers'), 'input'); },
    f => { f.$('trainChoice').value = '0'; f.event(f.$('trainChoice'), 'change'); },
    f => { f.$('grade').value = '특실'; f.event(f.$('grade'), 'change'); },
    f => f.$('scanTrains').click(),
    f => f.$('pickSeat').click()
  ];
  for (const change of changes) {
    const f = fixture(t); f.csv('505,541'); f.$('confirmed').checked = true; change(f);
    assert.deepEqual(f.read().targets, []); assert.equal(f.read().confirmed, false); assert.equal(f.read().target, null);
  }
});

test('choosing a refresh button preserves applied trains while requiring confirmation again', t => {
  for (const multiple of [false, true]) {
    const f = fixture(t, { demo: true });
    if (multiple) f.csv('541,505'); else f.single('505');
    const selected = f.read().targets; f.$('confirmed').checked = true;
    f.$('pickSearch').click();
    assert.deepEqual(f.read().targets, selected); assert.equal(f.read().confirmed, false); assert.equal(f.read().picking, 'search');
    f.doc.querySelector('.ticketSrchWrap .btn_bn-blue').click();
    assert.deepEqual(f.read().targets, selected); assert.equal(f.read().picking, null); assert.equal(f.read().search.label, '열차조회');
    assert.equal(f.read().confirmed, false);
    f.$('confirmed').checked = true; assert.equal(f.read().confirmed, true);
    f.doc.getElementById('startDate').value = '2026-10-23(금) 00:00';
    f.event(f.doc.getElementById('startDate'), 'change');
    assert.deepEqual(f.read().targets, []); assert.equal(f.read().confirmed, false, 'actual query changes remain invalidating');
  }
});

test('date, station and passenger changes clear stale selections before returning a snapshot', t => {
  for (const [id, value] of [['startDate', '2026-10-23(금) 00:00'], ['labelstart', '용산'], ['labelend', '부산'], ['labelple', '총 2명']]) {
    const f = fixture(t); f.csv('505,541'); f.$('confirmed').checked = true;
    f.doc.getElementById(id).value = value;
    assert.deepEqual(f.read().targets, [], id); assert.equal(f.read().confirmed, false);
  }
  const f = fixture(t); f.csv('505,541'); f.$('confirmed').checked = true;
  f.doc.getElementById('startDate').value = '2026-10-23(금) 00:00'; f.event(f.doc.getElementById('startDate'), 'change');
  assert.equal(f.$('confirmed').checked, false, 'query change event updates the visible checkbox immediately');
});

test('query DOM changes and SPA reset invalidate confirmation and all targets', async t => {
  const f = fixture(t); f.csv('505,541'); f.$('confirmed').checked = true;
  f.doc.querySelector('.select02 .selected').textContent = '환승';
  await new Promise(resolve => f.w.queueMicrotask(resolve));
  assert.equal(f.$('confirmed').checked, false); assert.deepEqual(f.read().targets, []);
  for (const reset of [
    g => g.w.KorailDesktopHelper.reset('SPA changed'),
    g => g.dom.reconfigure({ url: 'https://www.korail.com/ticket/main' }),
    g => g.w.dispatchEvent(new g.w.PopStateEvent('popstate'))
  ]) {
    const g = fixture(t); g.csv('505,541'); g.$('confirmed').checked = true; reset(g);
    assert.deepEqual(g.read().targets, []); assert.equal(g.read().confirmed, false);
  }
});

test('programmatic overlay selection edits cannot leave a confirmed stale snapshot', t => {
  for (const [id, value] of [['trainNumbers', '505'], ['trainChoice', '0'], ['grade', '특실']]) {
    const f = fixture(t); f.csv('505,541'); f.$('confirmed').checked = true; f.$(id).value = value;
    assert.deepEqual(f.read().targets, []); assert.equal(f.read().confirmed, false);
  }
});

const savedJourney = { Enabled: true, DepartureStation: '서울', ArrivalStation: '전주', Date: '2026-10-22', FromTime: '00:00', ToTime: '23:59', Grade: '일반실', TrainNumber: '00541,505' };
test('saved CSV journey prefills the input, filters candidates and preserves requested order', t => {
  const f = fixture(t, { journey: savedJourney });
  assert.equal(f.$('trainNumbers').value, '00541,505'); f.$('applyTrain').click();
  assert.deepEqual(f.read().targets.map(item => item.train), ['KTX-산천 541', 'KTX 505']);
  assert.deepEqual(f.read().candidates.map(item => item.train), ['KTX 505', 'KTX-산천 541']);
  f.csv('505,561'); assert.deepEqual(f.read().targets, []); assert.match(f.read().status, /561.*찾지 못/);
});

test('invalid saved CSV is rejected instead of silently widening the journey filter', t => {
  const f = fixture(t, { journey: { ...savedJourney, TrainNumber: '505,,541' } });
  f.$('scanTrains').click(); assert.deepEqual(f.read().candidates, []); assert.deepEqual(f.read().targets, []);
  assert.match(f.read().status, /빈 항목/);
});

test('legacy demo loop explicitly defers multiple trains to the EXE without acquiring or clicking', t => {
  const f = fixture(t, { demo: true }); f.csv('505,541'); f.$('confirmed').checked = true; f.$('auto').checked = true;
  f.$('start').click();
  assert.equal(f.read().running, false); assert.equal(f.read().starting, false); assert.equal(f.read().reservationClicks, 0);
  assert.match(f.read().status, /여러 열차.*EXE 상단/);
  assert.equal(f.sent.some(message => message.kind === 'lease' || message.kind === 'reservationClick'), false);
});

test('snapshot targets are detached copies and changing them cannot alter the applied selection', t => {
  const f = fixture(t); f.csv('541,505');
  const external = f.w.KorailDesktopHelper.snapshot(); external.targets[0].train = 'KTX 999'; external.targets.pop();
  assert.deepEqual(f.read().targets.map(item => item.train), ['KTX-산천 541', 'KTX 505']);
});

function nativeReaderFixture(t) {
  const f = fixture(t);
  f.w.eval(fs.readFileSync(path.join(assets, 'mouse-booking.js'), 'utf8'));
  const rows = () => Array.from(f.doc.querySelectorAll(f.S.cardSelector));
  f.w.HTMLElement.prototype.getBoundingClientRect = function () {
    return { left: 10, top: 40 + rows().indexOf(this.closest('.tckList')) * 40, width: 120, height: 24 };
  };
  f.doc.elementFromPoint = (x, y) => {
    const row = rows().find(item => { const rect = item.getBoundingClientRect(); return y >= rect.top && y < rect.top + rect.height; });
    return row && row.querySelector('.price_box > .inner > a');
  };
  const row = number => rows().find(item => f.S.identity(item).train.endsWith(' ' + number));
  const available = (number, yes) => {
    const slot = f.S.slot(row(number), '일반실');
    slot.className = 'price_box ' + (yes ? 'gen' : 'sold_out');
    slot.innerHTML = yes ? '<div class="inner"><a><p class="txt_ch">일반실</p><p class="txt_price">31,500원</p></a></div>' : '<div class="inner"><a>매진</a></div>';
  };
  const targets = () => {
    const query = f.S.query(f.doc);
    return f.read().targets.map(item => ({ ...item, date: query.date, departureStation: query.departureStation, arrivalStation: query.arrivalStation, passengers: query.passengers }));
  };
  let inputCount = 0;
  const watchInputs = () => {
    for (const kind of ['click', 'input', 'change', 'submit', 'scroll']) f.doc.addEventListener(kind, () => inputCount++);
  };
  return { ...f, row, available, targets, watchInputs, inputCount: () => inputCount,
    plan: (selected = targets()) => JSON.parse(JSON.stringify(f.w.KorailMouseBooking.readMany(selected, f.doc))) };
}

test('CSV snapshot and displayed query feed readMany and skip an explicitly sold-out first choice', t => {
  const f = nativeReaderFixture(t); f.available('505', false); f.available('541', true); f.csv('505,541');
  f.$('confirmed').checked = true; f.watchInputs();
  const before = f.doc.documentElement.outerHTML, plan = f.plan();
  assert.equal(plan.state, 'ready'); assert.equal(plan.action.kind, 'seat'); assert.equal(plan.selectedTarget.train, 'KTX-산천 541');
  assert.equal(plan.selectedTarget.date, '2026-10-22'); assert.equal(plan.selectedTarget.departureStation, '서울'); assert.equal(plan.selectedTarget.arrivalStation, '전주');
  assert.equal(plan.selectedTarget.passengers, 1); assert.equal(plan.selectedTarget.grade, '일반실');
  assert.equal(f.inputCount(), 0); assert.equal(f.doc.documentElement.outerHTML, before);
});

test('readMany preserves CSV priority when every selected train is available', t => {
  const f = nativeReaderFixture(t); f.available('505', true); f.available('541', true); f.available('561', true);
  f.csv('561,541,505'); f.watchInputs();
  const plan = f.plan(); assert.equal(plan.state, 'ready'); assert.equal(plan.selectedTarget.train, 'KTX 561');
  assert.equal(f.inputCount(), 0); assert.equal(f.read().reservationClicks, 0);
});

test('all selected CSV candidates being explicitly sold out is eligible for native refresh only', t => {
  const f = nativeReaderFixture(t); f.available('505', false); f.available('541', false); f.csv('541,505'); f.watchInputs();
  const plan = f.plan(); assert.equal(plan.state, 'waiting'); assert.equal(plan.refreshEligible, true);
  assert.equal(plan.action, undefined); assert.equal(plan.selectedTarget, undefined); assert.equal(f.inputCount(), 0);
});

test('readMany receives no click authorization for missing or mixed CSV candidates', t => {
  const missing = nativeReaderFixture(t); missing.available('505', true); missing.available('541', true); missing.csv('505,541');
  missing.row('541').remove(); missing.watchInputs();
  const missingPlan = missing.plan(); assert.equal(missingPlan.state, 'unknown'); assert.equal(missingPlan.action, undefined); assert.equal(missing.inputCount(), 0);
  const mixed = nativeReaderFixture(t); mixed.available('505', true); mixed.available('541', true); mixed.csv('505,541'); mixed.watchInputs();
  const targets = mixed.targets(); targets[1].grade = '특실';
  const mixedPlan = mixed.plan(targets); assert.equal(mixedPlan.state, 'blocked'); assert.equal(mixedPlan.action, undefined); assert.equal(mixed.inputCount(), 0);
  mixed.row('541').querySelector('.txt_price').textContent = '가격 확인 중';
  const uncertain = mixed.plan(); assert.equal(uncertain.state, 'unknown'); assert.equal(uncertain.action, undefined); assert.notEqual(uncertain.refreshEligible, true);
});
