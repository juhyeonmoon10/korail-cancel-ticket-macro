'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const assets = path.join(__dirname, '../assets');
const copy = value => JSON.parse(JSON.stringify(value));

function fixture(t, options = {}) {
  const dom = new JSDOM(fs.readFileSync(path.join(assets, 'card-demo.html'), 'utf8'), {
    url: 'https://www.korail.com/ticket/search/list', runScripts: 'outside-only', pretendToBeVisual: true
  });
  t.after(() => dom.window.close());
  const w = dom.window, doc = w.document, sent = [];
  Object.defineProperty(w.HTMLElement.prototype, 'innerText', { configurable: true, get() { return this.textContent; }, set(value) { this.textContent = value; } });
  w.HTMLElement.prototype.getClientRects = function () { return [{ left: 10, top: 10, width: 100, height: 30 }]; };
  w.chrome = { webview: { addEventListener() {}, postMessage(message) { sent.push(copy(message)); } } };
  w.desktopNonce = 'memory-test'; w.desktopJourney = options.journey || null;
  w.desktopSelection = options.selection || null;
  w.eval(fs.readFileSync(path.join(assets, 'core.js'), 'utf8'));
  w.eval(fs.readFileSync(path.join(assets, 'korail-site.js'), 'utf8'));
  const S = w.KorailSiteAdapter;
  let siteInputs = 0;
  for (const name of ['click', 'input', 'change', 'submit', 'scroll']) doc.addEventListener(name, event => {
    if (!event.composedPath().includes(doc.getElementById('korail-one-helper'))) siteInputs++;
  });
  w.scrollTo = w.HTMLElement.prototype.scrollIntoView = () => siteInputs++;
  w.eval(fs.readFileSync(path.join(assets, 'content.js'), 'utf8'));
  const $ = id => doc.getElementById('korail-one-helper').shadowRoot.getElementById(id);
  const event = (node, name) => node.dispatchEvent(new w.Event(name, { bubbles: true, composed: true }));
  const read = () => copy(w.KorailDesktopHelper.snapshot());
  const csv = value => { $('trainNumbers').value = value; event($('trainNumbers'), 'input'); $('applyTrain').click(); };
  const confirm = checked => { $('confirmed').checked = checked; event($('confirmed'), 'change'); };
  const saved = () => {
    const message = sent.filter(item => item.kind === 'selectionSaved').at(-1);
    assert.ok(message, 'a selection was sent to the native host');
    return { query: message.query, ...message.selection };
  };
  const settle = () => new Promise(resolve => setTimeout(resolve, 160));
  const row = number => S.cards(doc).find(item => S.identity(item).train.endsWith(' ' + number));
  return { dom, w, doc, S, $, sent, event, read, csv, confirm, saved, settle, row, siteInputs: () => siteInputs };
}

test('selection bridge saves only query and ordered train identities, including confirmation changes', t => {
  const f = fixture(t); f.csv('541,505');
  const unconfirmed = f.saved(); assert.equal(unconfirmed.confirmed, false);
  f.confirm(true);
  const saved = f.saved();
  assert.deepEqual(saved.targets.map(item => item.train), ['KTX-산천 541', 'KTX 505']);
  assert.equal(saved.query.date, '2026-10-22'); assert.equal(saved.query.departureStation, '서울');
  assert.equal(saved.query.arrivalStation, '전주'); assert.equal(saved.query.passengers, 1);
  assert.equal(saved.query.fromTime, '00:00'); assert.equal(saved.query.route, '직통'); assert.equal(saved.confirmed, true);
  assert.deepEqual(Object.keys(saved.targets[0]).sort(), ['arrival', 'autoCapable', 'departure', 'grade', 'train']);
  assert.equal(f.sent.filter(item => item.kind === 'selectionSaved').every(item => item.nonce === 'memory-test'), true);
  assert.equal(f.siteInputs(), 0); assert.equal(f.read().running, false);
  f.confirm(false); assert.equal(f.saved().confirmed, false);
});

test('fresh list scan retains matching train choices and a previously confirmed query', t => {
  const f = fixture(t); f.csv('541,505'); f.confirm(true);
  const targets = f.read().targets; f.$('scanTrains').click();
  assert.deepEqual(f.read().targets, targets); assert.equal(f.read().confirmed, true);
  assert.equal(f.siteInputs(), 0); assert.equal(f.read().running, false);
});

test('SPA navigation keeps visible pending choices and restores exact identities in priority order', async t => {
  const f = fixture(t); f.csv('541,505'); f.confirm(true);
  const before = f.read().targets;
  f.dom.reconfigure({ url: 'https://www.korail.com/ticket/main' });
  f.w.KorailDesktopHelper.reset('test navigation');
  assert.deepEqual(f.read().targets, []); assert.equal(f.read().confirmed, false);
  assert.match(f.$('target').textContent, /저장한 선택 2개/); assert.match(f.$('target').textContent, /KTX-산천 541/);
  await f.settle(); assert.deepEqual(f.read().targets, []);
  f.dom.reconfigure({ url: 'https://www.korail.com/ticket/search/list' });
  f.w.dispatchEvent(new f.w.PopStateEvent('popstate')); await f.settle();
  assert.deepEqual(f.read().targets, before); assert.equal(f.read().confirmed, true);
  assert.equal(f.$('trainNumbers').value, '541,505'); assert.equal(f.read().running, false); assert.equal(f.read().starting, false);
  assert.equal(f.siteInputs(), 0); assert.equal(f.sent.some(item => item.kind === 'reservationClick'), false);
});

test('new document injection restores stored selection without reusing any old DOM elements', t => {
  const first = fixture(t); first.csv('541,505'); first.confirm(true);
  const next = fixture(t, { selection: first.saved() });
  assert.deepEqual(next.read().targets, first.read().targets); assert.equal(next.read().confirmed, true);
  assert.equal(next.siteInputs(), 0); assert.equal(next.read().running, false);
  assert.equal(next.sent.some(item => item.kind === 'selectionSaved'), false, 'restoration does not rewrite native state');
});

test('a missing later train keeps the entire selection pending until all fresh rows arrive', async t => {
  const f = fixture(t); f.csv('541,505'); f.confirm(true);
  const missing = f.row('541'), parent = missing.parentNode; missing.remove();
  f.w.KorailDesktopHelper.reset('list rebuild'); await f.settle();
  assert.deepEqual(f.read().targets, []); assert.equal(f.read().confirmed, false);
  assert.match(f.$('target').textContent, /저장한 선택 2개/);
  parent.append(missing); await f.settle();
  assert.deepEqual(f.read().targets.map(item => item.train), ['KTX-산천 541', 'KTX 505']);
  assert.equal(f.read().confirmed, true); assert.equal(f.siteInputs(), 0);
});

test('changed date, departure time, route, passengers or seat preference never rebind stored choices', async t => {
  const mutations = [
    f => { f.doc.getElementById('startDate').value = '2026-10-23(금) 00:00'; },
    f => { f.doc.getElementById('startDate').value = '2026-10-22(목) 04:00'; },
    f => { f.doc.getElementById('labelstart').value = '용산'; },
    f => { f.doc.getElementById('labelple').value = '총 2명'; },
    f => { f.doc.querySelector('.select02 .selected').textContent = '환승'; },
    f => { f.doc.querySelector('.select01 .selected').textContent = '다른 좌석 조건'; },
    f => { f.doc.getElementById('adjStnScdlOfrFlg').checked = true; }
  ];
  for (const mutate of mutations) {
    const f = fixture(t); f.csv('505'); f.confirm(true); mutate(f);
    f.w.KorailDesktopHelper.reset('query changed'); await f.settle();
    assert.deepEqual(f.read().targets, []); assert.equal(f.read().confirmed, false);
    assert.match(f.$('target').textContent, /저장한 선택 1개/); assert.equal(f.siteInputs(), 0);
  }
});

test('same number with changed times, duplicate rows or hidden seats cannot restore confirmation', async t => {
  for (const mutate of [
    f => { f.row('505').querySelector('h3 span:last-child').textContent = '(09:44 ~ 11:39)'; },
    f => { f.row('505').after(f.row('505').cloneNode(true)); },
    f => { f.S.slot(f.row('505'), '일반실').hidden = true; }
  ]) {
    const f = fixture(t); f.csv('505'); f.confirm(true); mutate(f);
    f.w.KorailDesktopHelper.reset('rows changed'); await f.settle();
    assert.deepEqual(f.read().targets, []); assert.equal(f.read().confirmed, false); assert.equal(f.siteInputs(), 0);
  }
});

test('editing a saved choice stays editable and only a successful apply replaces native intent', async t => {
  const f = fixture(t); f.csv('541,505'); f.confirm(true);
  f.$('trainNumbers').value = '505'; f.event(f.$('trainNumbers'), 'input');
  f.doc.body.append(f.doc.createElement('span')); await f.settle();
  assert.equal(f.$('trainNumbers').value, '505'); assert.deepEqual(f.read().targets, []); assert.equal(f.read().confirmed, false);
  assert.deepEqual(f.saved().targets.map(item => item.train), ['KTX-산천 541', 'KTX 505']);
  f.$('applyTrain').click();
  assert.deepEqual(f.read().targets.map(item => item.train), ['KTX 505']); assert.equal(f.saved().confirmed, false);
  assert.deepEqual(f.saved().targets.map(item => item.train), ['KTX 505']);
});

test('invalid edited numbers never restore the old choices behind the user input', async t => {
  const f = fixture(t); f.csv('505'); f.confirm(true); f.csv('505,99999');
  f.doc.body.append(f.doc.createElement('span')); await f.settle();
  assert.equal(f.$('trainNumbers').value, '505,99999'); assert.deepEqual(f.read().targets, []); assert.equal(f.read().confirmed, false);
  assert.equal(f.saved().targets.length, 1);
});

test('explicit reset removes saved choices and informs the native host once', async t => {
  const f = fixture(t); f.csv('541,505'); f.confirm(true); f.$('clearSavedSelection').click();
  assert.deepEqual(f.sent.filter(item => item.kind === 'selectionCleared'), [{ kind: 'selectionCleared', nonce: 'memory-test' }]);
  assert.equal(f.$('trainNumbers').value, ''); assert.deepEqual(f.read().targets, []);
  assert.doesNotMatch(f.$('target').textContent, /저장한 선택/);
  f.w.KorailDesktopHelper.reset('later navigation'); await f.settle();
  assert.deepEqual(f.read().targets, []); assert.equal(f.read().confirmed, false);
});

test('host can replace the retained selection for a later job but cannot mutate it through its input object', t => {
  const f = fixture(t); f.csv('541,505'); f.confirm(true);
  const later = f.saved(); later.targets = [later.targets[1]]; later.confirmed = false;
  assert.equal(f.w.KorailDesktopHelper.restoreSelection(later), true);
  later.targets[0].train = 'KTX 999'; later.query.date = '2026-10-24';
  assert.deepEqual(f.read().targets.map(item => item.train), ['KTX 505']); assert.equal(f.read().confirmed, false);
  assert.equal(f.siteInputs(), 0);
});

test('malformed stored intent is rejected rather than turning it into a partial or wider selection', t => {
  const f = fixture(t); f.csv('505'); f.confirm(true); const original = f.saved();
  for (const change of [
    value => { value.query.passengers = 2; },
    value => { delete value.query.fromTime; },
    value => { value.targets.push(copy(value.targets[0])); },
    value => { value.targets[0].autoCapable = false; },
    value => { value.targets[0].train = '<script>505</script>'; },
    value => { value.targets[0].departure = '25:00'; },
    value => { value.targets = []; }
  ]) {
    const value = copy(original); change(value);
    assert.equal(f.w.KorailDesktopHelper.restoreSelection(value), false);
    assert.deepEqual(f.read().targets.map(item => item.train), ['KTX 505']);
  }
});

test('saved journey constraints still prevent automatic restoration for conflicting choices', t => {
  const first = fixture(t); first.csv('505'); first.confirm(true);
  const next = fixture(t, { selection: first.saved(), journey: { Enabled: true, DepartureStation: '서울', ArrivalStation: '전주',
    Date: '2026-10-22', FromTime: '12:00', ToTime: '23:59', Grade: '일반실', TrainNumber: '' } });
  assert.deepEqual(next.read().targets, []); assert.equal(next.read().confirmed, false);
  assert.match(next.$('target').textContent, /저장한 선택 1개/); assert.equal(next.siteInputs(), 0);
});
