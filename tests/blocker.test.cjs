'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assets = fs.existsSync(path.join(__dirname, 'assets/core.js')) ? path.join(__dirname, 'assets') : path.join(__dirname, '../assets');
const core = require(path.join(assets, 'core.js'));
const source = fs.readFileSync(path.join(assets, 'content.js'), 'utf8');
const begin = source.indexOf('  function guard() {');
const end = source.indexOf('  function setControls() {', begin);
assert.ok(begin >= 0 && end > begin, 'production guard function is present');
const guardSource = source.slice(begin, end);
const actualBlock = '자동화된 요청으로 감지되어 차단되었습니다. CODE : -8002';
const clearReason = '코레일이 자동화 요청을 차단했습니다(-8002). 자동 재시도를 중지합니다.';

test('the observed official -8002 dialog reports the exact block code and stops retries', () => {
  assert.equal(core.blocker(actualBlock), clearReason);
});

test('official block message remains recognized across dialog whitespace and surrounding copy', () => {
  assert.equal(core.blocker('안내 메시지\n자동화된 요청으로\n감지되어 차단되었습니다.\nCODE :\t-8002\n확인'), clearReason);
});

test('a different supplied block code is preserved, without inventing a missing code', () => {
  assert.equal(core.blocker('자동화된 요청으로 감지되어 차단되었습니다. CODE : -8003'),
    '코레일이 자동화 요청을 차단했습니다(-8003). 자동 재시도를 중지합니다.');
  assert.equal(core.blocker('자동화된 요청으로 감지되어 차단되었습니다.'),
    '코레일이 자동화 요청을 차단했습니다. 자동 재시도를 중지합니다.');
});

test('ordinary timetable text and a bare code do not claim an automation block', () => {
  assert.equal(core.blocker('서울 → 전주 KTX 505 06:44 ~ 08:39 일반실 31,500원'), null);
  assert.equal(core.blocker('CODE : -8002'), null);
});

function guardHarness(body, options = {}) {
  const calls = [];
  const context = {
    document: { hidden: false, body: { innerText: body } },
    location: { href: 'https://www.korail.com/ticket/search/list' },
    session: { url: 'https://www.korail.com/ticket/search/list', conditions: 'original query' },
    targetConfig: { adapter: 'korail-card' },
    blocked: () => { calls.push('blocked'); return core.blocker(body); },
    conditions: () => { calls.push('conditions'); if (options.conditionsThrow) throw new Error('Query UI disappeared'); return options.sameConditions ? 'original query' : 'missing query'; },
    assertJourney: () => { calls.push('journey'); if (options.journeyThrow) throw new Error('Unsupported query'); }
  };
  vm.createContext(context);
  vm.runInContext(guardSource + '\nglobalThis.check = guard;', context);
  return { context, calls };
}

test('production guard reports the official block before changed query conditions', () => {
  const h = guardHarness(actualBlock);
  assert.throws(() => h.context.check(), error => error.message === clearReason);
  assert.deepEqual(h.calls, ['blocked']);
});

test('production guard reports the official block even if query controls disappear entirely', () => {
  const h = guardHarness(actualBlock, { conditionsThrow: true, journeyThrow: true });
  assert.throws(() => h.context.check(), error => error.message === clearReason);
  assert.deepEqual(h.calls, ['blocked']);
});

test('production guard still rejects changed conditions when no block is present', () => {
  const h = guardHarness('일반실 31,500원');
  assert.throws(() => h.context.check(), /조회 조건이 바뀌었습니다/);
  assert.deepEqual(h.calls, ['blocked', 'conditions']);
});

test('production guard still validates journey when no block and no condition change exists', () => {
  const h = guardHarness('일반실 31,500원', { sameConditions: true, journeyThrow: true });
  assert.throws(() => h.context.check(), /Unsupported query/);
  assert.deepEqual(h.calls, ['blocked', 'conditions', 'journey']);
});
