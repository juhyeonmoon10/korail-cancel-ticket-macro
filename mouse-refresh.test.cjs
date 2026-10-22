'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
require('./assets/core.js');
const R = require('./assets/mouse-refresh.js');
const conditions = { supported: true, reason: '', date: '2026-10-22', fromTime: '00:00', departureStation: '서울', arrivalStation: '전주', passengers: 1,
  route: '직통', seatAttribute: '일반석', roundTrip: false, adjacent: false, combined: false };

function fixture() {
  const doc = {
    hidden: false, location: { href: 'https://www.korail.com/ticket/search/list' }, dialogs: [], captcha: [], selected: [], rows: [], observers: [], top: null,
    documentElement: { clientWidth: 1000, clientHeight: 700 },
    querySelectorAll(selector) {
      if (selector === '#tab_tab_info1 > .tckWrap > ul') return this.list ? [this.list] : [];
      if (selector === '#tab_tab_info1') return this.panel ? [this.panel] : [];
      if (selector.includes('[role="dialog"]')) return this.dialogs;
      if (selector.includes('one-time-code')) return this.captcha;
      if (selector.includes('.price_box.active')) return this.selected;
      if (selector === '.tckList') return this.rows;
      return [];
    },
    elementFromPoint: () => doc.top
  };
  class Observer {
    constructor(callback) { this.callback = callback; this.pending = []; doc.observers.push(this); }
    observe(target, options) { this.target = target; this.options = options; }
    takeRecords() { const records = this.pending; this.pending = []; return records; }
    queue(record) { this.pending.push(record); }
    emit(record) { this.callback([record]); }
  }
  doc.defaultView = { innerWidth: 1000, innerHeight: 700, getComputedStyle: node => node.style, MutationObserver: Observer };
  function node(label = '') {
    return { nodeType: 1, isConnected: true, ownerDocument: doc, parentElement: null, innerText: label, textContent: label,
      style: { display: 'block', visibility: 'visible', opacity: '1' }, children: [], attributes: {}, disabled: false,
      rect: { left: 20, top: 100, width: 120, height: 40 },
      contains(other) { return this === other || this.children.some(child => child.contains ? child.contains(other) : child === other); },
      closest() { return null; }, getClientRects() { return this.style.display === 'none' ? [] : [this.rect]; },
      getBoundingClientRect() { return this.rect; }, getAttribute(name) { return this.attributes[name] || null; },
      matches(selector) { return selector.includes('progressbar') && this.attributes.role === 'progressbar' || selector.includes('aria-busy') && this.attributes['aria-busy'] === 'true'; },
      querySelectorAll(selector) { return this.children.filter(child => child.matches && child.matches(selector)); },
      click() { throw new Error('read-only reader must never click'); }, scrollIntoView() { throw new Error('read-only reader must never scroll'); }
    };
  }
  doc.body = node('KTX 513 서울 → 전주 일반실 매진');
  doc.panel = node(); doc.list = node(); doc.panel.children = [doc.list]; doc.body.children = [doc.panel];
  const button = node('전체'); doc.top = button;
  function row(number = '513') {
    const item = node('KTX ' + number), general = node('매진'), special = node('특실 46,000원');
    general.available = false; special.available = true;
    item.identity = { train: 'KTX ' + number, departureStation: '서울', arrivalStation: '전주', departure: '12:19', arrival: '14:01', key: 'KTX ' + number + '|서울|전주|12:19|14:01' };
    item.seats = { 일반실: general, 특실: special }; item.children = [general, special];
    return item;
  }
  doc.rows = [row()]; doc.list.children = doc.rows;
  const adapter = {
    cardSelector: '.tckList', conditions: { ...conditions }, filter: { element: button, selector: '#btn_tab_info1 > button', label: '전체' }, isList: true,
    matchesPage() { return this.isList; }, query() { return this.conditions; }, activeRefresh() { return this.filter; },
    cards: () => doc.rows.filter(item => item.identity), identity: item => item.identity,
    slot: (item, grade) => item.seats[grade] || null, positiveAction: slot => slot && slot.available ? slot : null
  };
  globalThis.KorailSiteAdapter = adapter;
  return { doc, adapter, button, node, row, read: expected => R.read(expected, doc), mutate: record => doc.observers[0].queue(record) };
}

test('reads current one-passenger route, active filter action and semantic result fingerprint', () => {
  const f = fixture(), snapshot = f.read();
  assert.equal(snapshot.state, 'ready'); assert.equal(snapshot.count, 1); assert.equal(snapshot.busy, false);
  assert.equal(snapshot.action.kind, 'refresh'); assert.equal(snapshot.action.selector, '#btn_tab_info1 > button');
  assert.equal(snapshot.action.x, 80); assert.equal(snapshot.action.inViewport, true);
  assert.deepEqual(snapshot.query, conditions); assert.equal(snapshot.observesMutations, true);
  assert.equal(f.read().fingerprint, snapshot.fingerprint); assert.equal(f.read().revision, 0);
  assert.equal(f.doc.observers.length, 1, 'repeated reads reuse one read-only observer');
});

test('locks every query option and active filter while accepting normalized station suffixes', () => {
  const f = fixture(), initial = f.read();
  assert.equal(f.read({ departureStation: '서울역', arrivalStation: '전주역', date: conditions.date, passengers: 1 }).state, 'ready');
  for (const change of [{ date: '2026-10-23' }, { departureStation: '용산' }, { arrivalStation: '부산' }, { fromTime: '01:00' }, { seatAttribute: '유아동반석' }]) {
    f.adapter.conditions = { ...conditions, ...change };
    assert.equal(f.read({ queryFingerprint: initial.queryFingerprint }).state, 'blocked', JSON.stringify(change));
  }
  f.adapter.conditions = { ...conditions }; f.adapter.filter = { ...f.adapter.filter, selector: '#btn_tab_info2 > button', label: 'KTX/KTX-산천' };
  assert.equal(f.read({ queryFingerprint: initial.queryFingerprint }).state, 'blocked');
});

test('unsupported route and passengers stop; empty or partial results wait without actions', () => {
  const f = fixture(); f.adapter.conditions.supported = false; f.adapter.conditions.reason = '인원 총 1명으로 조회하세요.';
  assert.equal(f.read().state, 'blocked'); f.adapter.conditions.supported = true;
  f.doc.rows = []; assert.equal(f.read().state, 'loading'); assert.equal(f.read().action, undefined);
  f.doc.rows = [f.row(), f.node('partial train row')];
  assert.equal(f.read().state, 'loading'); assert.equal(f.read().count, 1);
});

test('disabled filter and a result-panel progress indicator are loading observations', () => {
  const f = fixture(); f.button.disabled = true;
  assert.equal(f.read().state, 'loading'); assert.equal(f.read().action, undefined);
  f.button.disabled = false; f.button.attributes['aria-disabled'] = 'true'; assert.equal(f.read().busy, true);
  delete f.button.attributes['aria-disabled'];
  const spinner = f.node(); spinner.attributes.role = 'progressbar'; f.doc.panel.children.push(spinner);
  assert.equal(f.read().state, 'loading');
  f.doc.panel.children.pop(); f.doc.panel.attributes['aria-busy'] = 'true';
  assert.equal(f.read().state, 'loading', 'the panel itself can carry the busy marker');
});

test('official -8002 and other restrictions are terminal and preserve the supplied block code', () => {
  for (const message of ['자동화된 요청으로 감지되어 차단되었습니다. CODE : -8002', '자동화된 요청으로 감지되어 차단되었습니다. CODE : -8003', '로그인이 필요합니다.', '접속 대기 중', '오류가 발생했습니다.']) {
    const f = fixture(); f.doc.body.innerText = message; f.adapter.conditions.supported = false;
    const snapshot = f.read();
    assert.equal(snapshot.state, 'blocked'); assert.equal(snapshot.terminal, true); assert.equal(snapshot.retryAllowed, false); assert.equal(snapshot.action, undefined);
    if (message.includes('CODE')) assert.equal(snapshot.blockCode, message.includes('-8002') ? '-8002' : '-8003');
  }
});

test('all visible modals and captcha controls stop refresh; hidden dialogs do not', () => {
  const f = fixture(), modal = f.node('서대구정차하는 열차입니다. 도착시간을 확인하시기 바랍니다.');
  f.doc.dialogs = [modal]; assert.equal(f.read().state, 'blocked');
  modal.style.display = 'none'; assert.equal(f.read().state, 'ready');
  f.doc.captcha = [f.node('보안 확인')]; assert.equal(f.read().state, 'blocked');
});

test('covered buttons stop and off-screen buttons return only a scroll target', () => {
  const f = fixture(); f.doc.top = f.node('overlay'); assert.equal(f.read().state, 'blocked');
  f.button.rect.top = 900; const outside = f.read();
  assert.equal(outside.state, 'ready'); assert.equal(outside.action.inViewport, false); assert.equal(outside.action.y, 920);
});

test('selected seats, drawers, duplicate trains and unrecognized filters cannot be refreshed', () => {
  const f = fixture(); f.doc.selected = [f.node('예매')]; assert.equal(f.read().state, 'blocked'); f.doc.selected = [];
  f.doc.rows = [f.row(), f.row()]; assert.equal(f.read().state, 'blocked'); f.doc.rows = [f.row()];
  f.adapter.filter = null; assert.equal(f.read().state, 'unknown'); assert.equal(f.read().action, undefined);
});

test('same-content result replacement increments revision without pretending fares changed', () => {
  const f = fixture(), first = f.read(), old = f.doc.rows[0], next = f.row();
  f.doc.rows = [next]; f.doc.list.children = [next];
  f.mutate({ type: 'childList', target: f.doc.list, addedNodes: [next], removedNodes: [old] });
  const second = f.read();
  assert.equal(second.fingerprint, first.fingerprint); assert.equal(second.revision, first.revision + 1);
  assert.equal(f.read().revision, second.revision, 'draining the observer does not double count changes');
});

test('replacing the entire result list is tracked through the old and new list roots', () => {
  const f = fixture(), first = f.read(), oldList = f.doc.list;
  f.doc.list = f.node(); f.doc.list.children = f.doc.rows; f.doc.panel.children = [f.doc.list];
  f.mutate({ type: 'childList', target: f.doc.panel, addedNodes: [f.doc.list], removedNodes: [oldList] });
  assert.equal(f.read().revision, first.revision + 1);
});

test('only result content and query loading mutations affect revision', () => {
  const f = fixture(); f.read();
  for (const record of [
    { type: 'childList', target: f.node('helper'), addedNodes: [f.node('time')], removedNodes: [] },
    { type: 'characterData', target: { nodeType: 3, textContent: '12:00:01' } },
    { type: 'attributes', attributeName: 'class', target: f.doc.rows[0].seats.일반실 },
    { type: 'childList', target: f.doc.body, addedNodes: [f.node('예매 drawer')], removedNodes: [] }
  ]) f.mutate(record);
  assert.equal(f.read().revision, 0);
  f.button.disabled = true; f.mutate({ type: 'attributes', attributeName: 'disabled', target: f.button });
  assert.equal(f.read().revision, 1);
  f.button.disabled = false; f.mutate({ type: 'attributes', attributeName: 'disabled', target: f.button });
  assert.equal(f.read().revision, 2);
});

test('new active-button node retains query identity and its loading is observed', () => {
  const f = fixture(); f.read(); const replacement = f.node('전체');
  f.adapter.filter.element = replacement; f.doc.top = replacement;
  replacement.disabled = true;
  f.mutate({ type: 'attributes', attributeName: 'disabled', target: replacement });
  assert.equal(f.read().revision, 1); assert.equal(f.read().state, 'loading');
});

test('price updates change fingerprint and result revision; unrelated text never supplies success evidence', () => {
  const f = fixture(), before = f.read(), general = f.doc.rows[0].seats.일반실;
  general.innerText = '일반실 31,600원'; general.available = true;
  f.mutate({ type: 'childList', target: general, addedNodes: [{ nodeType: 3, textContent: '31,600원' }], removedNodes: [] });
  const after = f.read(); assert.notEqual(after.fingerprint, before.fingerprint); assert.equal(after.revision, 1);
});

test('unapproved origins, hidden pages and layout failures never expose a click action', () => {
  const f = fixture(); f.doc.location.href = 'https://example.com/ticket/search/list'; assert.equal(f.read().state, 'blocked');
  f.doc.location.href = 'https://www.korail.com:8443/ticket/search/list'; assert.equal(f.read().state, 'blocked');
  f.doc.location.href = 'https://www.korail.com/ticket/search/list'; f.doc.hidden = true; assert.equal(f.read().state, 'blocked');
  f.doc.hidden = false; f.adapter.query = () => { throw new Error('layout changed'); };
  assert.equal(f.read().state, 'unknown'); assert.equal(f.read().action, undefined);
});
