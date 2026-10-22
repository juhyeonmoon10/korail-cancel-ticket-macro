'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require('jsdom');
const assets = path.join(__dirname, 'assets');

// Production parsers against the saved card fixture, with the pagination DOM
// observed in Korail's public bundle 1.0.221. No site script or network runs.
function fixture(t) {
  const dom = new JSDOM(fs.readFileSync(path.join(assets, 'card-demo.html'), 'utf8'), {
    url: 'https://korail-helper.invalid/card-demo.html', runScripts: 'outside-only', pretendToBeVisual: true
  });
  t.after(() => dom.window.close());
  const w = dom.window, doc = w.document;
  Object.defineProperty(w.HTMLElement.prototype, 'innerText', { configurable: true, get() { return this.textContent; } });
  w.HTMLElement.prototype.getBoundingClientRect = function () { return this.testRect || { left: 10, top: 20, width: 120, height: 40 }; };
  w.HTMLElement.prototype.getClientRects = function () { return [this.getBoundingClientRect()]; };
  for (const method of ['click', 'scroll', 'scrollTo', 'scrollIntoView'])
    w.HTMLElement.prototype[method] = function () { throw new Error('Reader must not generate input: ' + method); };
  w.scrollTo = w.scroll = () => { throw new Error('Reader must not scroll'); };
  w.fetch = w.XMLHttpRequest = () => { throw new Error('Reader must not use network'); };
  const style = doc.createElement('style'); style.textContent = '.btnList {display:block}'; doc.head.append(style);
  const panel = doc.querySelector('#tab_tab_info1'), list = panel.querySelector('.tckWrap > ul'), controls = panel.querySelector('ul.btnList');
  const container = doc.createElement('li'); container.innerHTML = '<div><a class="page_group" href="#none"><span>더보기</span></a></div>';
  controls.prepend(container);
  const more = container.querySelector('a'), refresh = doc.querySelector('#btn_tab_info1 > button');
  let top = more.querySelector('span');
  doc.elementFromPoint = () => top;
  for (const file of ['core.js', 'korail-site.js', 'mouse-refresh.js', 'mouse-more.js'])
    w.eval(fs.readFileSync(path.join(assets, file), 'utf8'));
  const S = w.KorailSiteAdapter, M = w.KorailMouseMore, R = w.KorailMouseRefresh;
  const rows = () => S.cards(doc);
  const target = number => { const id = S.identity(rows().find(row => row.querySelector('.num').textContent.trim() === String(number))); return { ...id }; };
  const append = (number = '561') => {
    const row = rows()[0].cloneNode(true); row.querySelector('.num').textContent = number;
    row.querySelector('h3 span:last-child').textContent = '(20:05 ~ 22:39)'; list.append(row); return row;
  };
  const read = (targets = [], expected = null) => JSON.parse(JSON.stringify(M.read(expected, targets, doc)));
  return { dom, w, doc, panel, list, controls, container, more, refresh, rows, S, M, R, target, append, read, top: value => { top = value; } };
}
function noAction(snapshot, state) {
  assert.equal(snapshot.state, state); assert.equal(snapshot.action, undefined);
}

test('reads the observed pagination link, canonical row keys, and native coordinates without input', t => {
  const f = fixture(t), plan = f.read();
  assert.equal(plan.state, 'ready'); assert.equal(plan.count, 6); assert.equal(plan.more, true); assert.equal(plan.allTargetsPresent, false);
  assert.deepEqual(plan.keys, Array.from(f.rows(), row => f.S.identity(row).key));
  assert.equal(plan.action.kind, 'more'); assert.equal(plan.action.label, '더보기');
  assert.equal(plan.action.selector, '#tab_tab_info1 > ul.btnList > li > div > a.page_group[href="#none"]');
  assert.equal(plan.action.x, 70); assert.equal(plan.action.y, 40); assert.equal(plan.action.inViewport, true);
  assert.equal(f.doc.querySelectorAll(plan.action.selector).length, 1);
  assert.equal(f.doc.querySelector(plan.action.selector), f.more);
  assert.match(plan.queryFingerprint, /"date":"2026-10-22"/);
});

test('a missing exact target becomes present only after strict prefix append, then no more action is offered', t => {
  const f = fixture(t), wanted = { ...f.target('505'), train: 'KTX 561', departure: '20:05', arrival: '22:39' };
  const before = f.read([wanted]); assert.equal(before.allTargetsPresent, false); assert.equal(before.action.kind, 'more');
  f.append(); const after = f.read([wanted], before.queryFingerprint);
  assert.equal(f.M.progress(before.keys, after.keys), 'appended');
  assert.equal(after.allTargetsPresent, true); assert.equal(after.more, true); noAction(after, 'ready');
  assert.equal(after.count, before.count + 1);
});

test('preparation mode keeps loading remaining pages while frozen targets stop at all-present', t => {
  const f = fixture(t), wanted = [f.target('505'), f.target('541')];
  noAction(f.read(wanted), 'ready'); assert.equal(f.read(wanted).allTargetsPresent, true);
  assert.equal(f.read([]).action.kind, 'more'); assert.equal(f.read([]).allTargetsPresent, false);
  f.container.remove(); noAction(f.read([]), 'ready'); assert.equal(f.read([]).more, false);
});

test('normalizes station suffixes while matching full train, route, and both times', t => {
  const f = fixture(t), target = f.target('505');
  assert.equal(f.read([{ ...target, departureStation: '서울역', arrivalStation: '전주역' }]).allTargetsPresent, true);
  for (const changed of [{ train: 'KTX-산천 505' }, { departure: '06:45' }, { arrival: '08:40' }]) {
    const plan = f.read([{ ...target, ...changed }]); assert.equal(plan.allTargetsPresent, false); assert.equal(plan.action.kind, 'more');
  }
  noAction(f.read([{ ...target, arrivalStation: '부산' }]), 'blocked');
});

test('rejects malformed, ambiguous, duplicate, or non-array frozen target input', t => {
  const f = fixture(t), target = f.target('505');
  for (const targets of [null, {}, [null], [{}], [{ ...target, train: '505' }], [{ ...target, departure: '24:00' }],
    [{ ...target, train: 'KTX 00505' }], [target, target], [target, { ...target, departureStation: '서울역' }], Array(21).fill(target)])
    noAction(f.read(targets), 'blocked');
  for (const fingerprint of ['', {}, 12]) noAction(f.read([], fingerprint), 'blocked');
});

test('query inspection preserves all guards but does not need the offscreen refresh coordinates', t => {
  const f = fixture(t);
  assert.equal(f.R.read(null, f.doc).state, 'blocked', 'pagination target covers the refresh point in this fixture');
  const inspected = f.R.inspect(null, f.doc); assert.equal(inspected.state, 'ready'); assert.equal(inspected.action, undefined);
  f.refresh.testRect = { left: 0, top: -500, width: 0, height: 0 };
  assert.equal(f.read().state, 'ready', 'more remains readable when refresh geometry is unavailable');
});

test('date, route, and active train filter changes invalidate the original query', t => {
  for (const change of [
    f => { f.doc.querySelector('#startDate').value = '2026-10-23(금) 00:00'; },
    f => { f.doc.querySelector('#labelend').value = '부산'; },
    f => { f.refresh.setAttribute('aria-pressed', 'false'); f.doc.querySelector('#btn_tab_info2').classList.add('active'); f.doc.querySelector('#btn_tab_info2 > button').setAttribute('aria-pressed', 'true'); }
  ]) {
    const f = fixture(t), before = f.read(); change(f); noAction(f.read([], before.queryFingerprint), 'blocked');
  }
});

test('visible notices, verification controls, active seats, and booking drawers prevent pagination', t => {
  const changes = [
    f => f.panel.insertAdjacentHTML('beforeend', '<div role="dialog">안내</div>'),
    f => f.panel.insertAdjacentHTML('beforeend', '<input autocomplete="one-time-code">'),
    f => f.rows()[0].querySelector('.price_box').classList.add('active'),
    f => f.doc.querySelector('#container').insertAdjacentHTML('beforeend', '<div class="ticket_reserv_wrap">예매</div>')
  ];
  for (const change of changes) { const f = fixture(t); change(f); noAction(f.read(), 'blocked'); }
});

test('official blocking codes remain terminal and are never hidden by pagination', t => {
  const f = fixture(t); f.panel.insertAdjacentHTML('beforeend', '<p>자동화된 요청으로 감지되어 차단되었습니다. CODE : -8002</p>');
  const plan = f.read(); noAction(plan, 'blocked'); assert.equal(plan.blockCode, '-8002');
  assert.equal(plan.terminal, true); assert.equal(plan.retryAllowed, false);
});

test('invalid, duplicate, and wrong-route visible rows never expose a more action', t => {
  const changes = [
    f => f.list.append(f.rows()[0].cloneNode(true)),
    f => { f.rows()[0].querySelector('.num').textContent = 'not a train'; },
    f => { f.rows()[0].querySelector('h3 span:nth-child(2)').textContent = '부산'; }
  ];
  for (const change of changes) { const f = fixture(t); change(f); noAction(f.read(), 'blocked'); }
});

test('loading indicators, disabled refresh, and an empty result list wait without input', t => {
  const changes = [
    f => { f.refresh.disabled = true; },
    f => { f.panel.setAttribute('aria-busy', 'true'); },
    f => f.panel.insertAdjacentHTML('beforeend', '<div role="progressbar"></div>'),
    f => f.list.replaceChildren()
  ];
  for (const change of changes) { const f = fixture(t); change(f); noAction(f.read(), 'loading'); }
});

test('offscreen more returns a scroll target while occluded or invalid geometry blocks', t => {
  const f = fixture(t); f.more.testRect = { left: 10, top: 1200, width: 120, height: 40 };
  const plan = f.read(); assert.equal(plan.state, 'ready'); assert.equal(plan.action.inViewport, false);
  f.more.testRect.top = 20; f.top(f.doc.body); noAction(f.read(), 'blocked');
  f.top(f.more); f.more.testRect.width = 0; noAction(f.read(), 'blocked');
  f.more.testRect.width = NaN; noAction(f.read(), 'blocked');
});

test('disabled more waits; inert containers stop instead of supplying coordinates', t => {
  const f = fixture(t); f.more.setAttribute('aria-disabled', 'true'); noAction(f.read(), 'loading');
  f.more.removeAttribute('aria-disabled'); f.more.disabled = true; noAction(f.read(), 'loading');
  f.more.disabled = false; f.container.setAttribute('inert', ''); noAction(f.read(), 'blocked');
  f.container.removeAttribute('inert'); f.container.classList.add('disabled'); noAction(f.read(), 'blocked');
});

test('duplicate pagination links are ambiguous even when their label and destination match', t => {
  const f = fixture(t); f.controls.append(f.container.cloneNode(true)); noAction(f.read(), 'blocked');
});

test('the next-day button, unrelated footer link, and unobserved structures are never clicked', t => {
  const f = fixture(t); f.doc.body.insertAdjacentHTML('beforeend', '<footer><a href="#none">더보기</a></footer>');
  assert.equal(f.read().action.selector, '#tab_tab_info1 > ul.btnList > li > div > a.page_group[href="#none"]');
  f.container.remove(); noAction(f.read(), 'unknown');
  f.doc.querySelector('footer').remove(); noAction(f.read(), 'ready'); assert.equal(f.read().more, false);
  f.panel.insertAdjacentHTML('beforeend', '<button>더보기</button>'); noAction(f.read(), 'unknown');
});

test('hidden more means no next page; changed labels and non-observed destinations remain unknown', t => {
  const f = fixture(t); f.more.style.display = 'none'; noAction(f.read(), 'ready'); assert.equal(f.read().more, false);
  f.more.style.display = ''; f.more.querySelector('span').textContent = '더보기 (10)'; noAction(f.read(), 'unknown');
  f.more.querySelector('span').textContent = ' \n 더보기\t '; assert.equal(f.read().action.kind, 'more');
  f.more.href = '#next'; noAction(f.read(), 'unknown');
});

test('pagination must follow the one recognized list, never precede it or sit inside a card', t => {
  const f = fixture(t); f.panel.prepend(f.controls); noAction(f.read(), 'blocked');
  f.rows()[0].append(f.controls); noAction(f.read(), 'unknown');
});

test('hidden documents, unapproved locations, and malformed layouts never offer actions', t => {
  const f = fixture(t); Object.defineProperty(f.doc, 'hidden', { configurable: true, value: true }); noAction(f.read(), 'blocked');
  Object.defineProperty(f.doc, 'hidden', { configurable: true, value: false });
  f.dom.reconfigure({ url: 'https://example.com/ticket/search/list' }); noAction(f.read(), 'blocked');
  f.dom.reconfigure({ url: 'https://korail-helper.invalid/card-demo.html' }); f.panel.id = 'changed-panel'; noAction(f.read(), 'blocked');
});

test('strict append progress rejects shrink, reorder, replacement, duplicates, and invalid keys', t => {
  const f = fixture(t), before = f.read().keys;
  assert.equal(f.M.progress(before, before.slice()), 'unchanged');
  assert.equal(f.M.progress(before, before.concat('KTX 561|서울|전주|20:05|22:39')), 'appended');
  for (const after of [before.slice(1), before.slice().reverse(), before.slice(0, -1), before.concat(before[0]),
    ['replacement', ...before.slice(1)], before.concat(''), before.concat(null), null, {}])
    assert.equal(f.M.progress(before, after), 'invalid');
  for (const initial of [[], null, {}, [before[0], before[0]], ['', 'b']]) assert.equal(f.M.progress(initial, before), 'invalid');
});
