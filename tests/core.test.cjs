'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const core = require('../assets/core.js');

test('exports the same API to a browser global without module support', () => {
  const context = vm.createContext({});
  vm.runInContext(fs.readFileSync(require.resolve('../assets/core.js'), 'utf8'), context);
  assert.equal(context.KorailHelperCore.identity('KTX 001 서울 07:30 부산 10:15').key, 'KTX 1|07:30|10:15');
});

test('normalizes newlines, nonbreaking spaces, tabs and missing text', () => {
  assert.equal(core.normalize('  서울\n\t→\u00a0 부산  '), '서울 → 부산');
  assert.equal(core.normalize(null), '');
});

test('parses optional CSV train numbers with stable ordering and normalized duplicates', () => {
  for (const value of ['', '  ', '\t\n']) assert.deepEqual(core.parseTrainNumbers(value), []);
  assert.deepEqual(core.parseTrainNumbers(' 00541, 505,\n561,541,00505 '), ['541', '505', '561']);
  assert.deepEqual(core.parseTrainNumbers('1,99999,00001'), ['1', '99999']);
});

test('rejects malformed, zero, non-string and overlong train-number entries', () => {
  for (const value of [',505', '505,', '505,,541', '505, ,541', '0', '00000', '505,0', '-505', '+505', '5.05', '5e2', 'KTX505', '5 05', '５０５', '000505', '100000', '505;541', '505\n541', null, undefined, 505, true, ['505']])
    assert.throws(() => core.parseTrainNumbers(value), Error, String(value));
});

test('limits CSV selection to twenty unique numbers without counting repeats twice', () => {
  const twenty = Array.from({ length: 20 }, (_, index) => String(index + 1));
  assert.deepEqual(core.parseTrainNumbers(twenty.join(',')), twenty);
  assert.deepEqual(core.parseTrainNumbers(twenty.concat(twenty.slice().reverse()).join(',')), twenty);
  assert.throws(() => core.parseTrainNumbers(twenty.concat('21').join(',')), /20/);
});

test('identifies a train without making its row number part of its identity', () => {
  const result = core.identity('3 KTX-산천\n000123 서울 07:30 부산 10:15 일반실 매진');
  assert.deepEqual(result, { train: 'KTX-산천 123', departure: '07:30', arrival: '10:15', key: 'KTX-산천 123|07:30|10:15' });
  assert.deepEqual(core.identity('99 KTX - 산천 123 서울 07:30 부산 10:15 예약하기'), result);
});

test('supports the requested kinds and normalizes zero-padded numbers', () => {
  for (const kind of ['KTX', 'KTX-이음', 'KTX-청룡', 'ITX-새마을', 'ITX-마음', 'ITX-청춘', '새마을', '무궁화', '누리로']) {
    assert.equal(core.identity(`${kind}\n00001 00:05 23:59`).train, `${kind} 1`, kind);
  }
});

test('does not reinterpret a departure time as a missing train number', () => {
  for (const label of ['KTX 07:30 10:15', 'KTX-산천 09:00 12:00', 'ITX-마음 01:03 04:05']) {
    assert.equal(core.identity(label), null, label);
  }
});

test('rejects missing, additional and invalid clock times', () => {
  for (const label of ['KTX 001 07:30', 'KTX 001 07:30 10:15 12:00', 'KTX 001 25:30 10:15', 'KTX 001 07:60 10:15', 'KTX 001 07:30:00 10:15', 'KTX 001 7:30 10:15']) {
    assert.equal(core.identity(label), null, label);
  }
});

test('rejects transfers or ambiguous rows with distinct train identities', () => {
  assert.equal(core.identity('KTX 001 KTX 002 07:30 10:15'), null);
  assert.equal(core.identity('KTX 001 ITX-마음 001 07:30 10:15'), null);
  assert.equal(core.identity('KTX 001 KTX 0001 07:30 10:15').train, 'KTX 1');
});

test('rejects unsupported train kinds, embedded identifiers and overlong numbers', () => {
  for (const label of ['SRT 301 07:30 10:15', 'AKTX 001 07:30 10:15', 'KTX 1234567 07:30 10:15', '열차 001 07:30 10:15']) {
    assert.equal(core.identity(label), null, label);
  }
  assert.equal(core.identity('KTX 123456 07:30 10:15').train, 'KTX 123456');
});

test('only exact positive seat actions qualify', () => {
  for (const label of ['예약', '예약하기', '예매', '예매하기', '좌석선택', '좌석 선택', '예약 가능', '예약가능', '  좌석\n선택  ']) assert.equal(core.isSeatAction(label), true, label);
  for (const label of ['', '매진', '예약대기', '예약 대기 신청', '입석 예약', '자유석 예매', '결제', '예약 취소', '예약 불가', '좌석선택 불가', '예약하기 안내', '예약 완료', '예약가능 2석']) assert.equal(core.isSeatAction(label), false, label);
});

test('search actions must be exact and cannot match navigation or reservation controls', () => {
  for (const label of ['조회하기', '조회', '열차 조회하기', '열차조회', '다시 조회', '재조회', '검색', '검색하기', '새로고침']) assert.equal(core.isSearchAction(label), true, label);
  for (const label of ['', '예약조회', '예약승차권 조회', '열차 조회 결과', '다음', '결제', '선택', '검색 도움말']) assert.equal(core.isSearchAction(label), false, label);
});

test('static login navigation and waitlist seats do not block monitoring', () => {
  assert.equal(core.blocker('홈 로그인 회원가입 KTX 001 07:30 10:15 일반실 매진 예약대기'), null);
  assert.equal(core.blocker('예약대기 신청 결과 좌석 배정 대기'), null);
});

test('stops for visible security challenges, access restrictions and expired login', () => {
  const cases = [
    ['자동입력 방지 문자를 입력하세요', '보안 확인 필요'],
    ['CAPTCHA verification required', '보안 확인 필요'],
    ['비정상적인 접속으로 접속이 차단되었습니다', '접속 제한 확인 필요'],
    ['비정상적인 접근이 감지되었습니다.', '접속 제한 확인 필요'],
    ['자동화된 요청으로 감지되어 차단되었습니다. CODE : -8003', '코레일이 자동화 요청을 차단했습니다(-8003). 자동 재시도를 중지합니다.'],
    ['요청이 일시적으로 제한되었습니다', '접속 제한 확인 필요'],
    ['세션이 만료되었습니다', '로그인 확인 필요'],
    ['로그인 후 이용해 주세요', '로그인 확인 필요'],
    ['다시 로그인해 주세요', '로그인 확인 필요']
  ];
  for (const [text, expected] of cases) assert.equal(core.blocker(text), expected, text);
});

test('stops on queues, errors and unrecognized notices', () => {
  for (const text of ['서비스 접속 대기 중', '대기자수 122명', '현재 대기 순번 3번']) assert.equal(core.blocker(text), '접속 대기 중', text);
  for (const text of ['오류가 발생했습니다', '잠시 후 다시 시도해 주세요', '시스템 점검 중입니다']) assert.equal(core.blocker(text), '사이트 오류 또는 점검', text);
  assert.equal(core.blocker('안내 메시지 처리할 수 없는 요청입니다'), '안내 메시지 확인 필요');
});

test('interval and check limits use defaults for invalid input and enforce bounds', () => {
  for (const input of [undefined, null, '', ' ', 'oops', NaN, Infinity, true]) {
    assert.equal(core.intervalSeconds(input), 120);
    assert.equal(core.maxChecks(input), 30);
  }
  assert.equal(core.intervalSeconds(-1), 60);
  assert.equal(core.intervalSeconds('90.8'), 90);
  assert.equal(core.intervalSeconds(999999), 3600);
  assert.equal(core.maxChecks(-1), 1);
  assert.equal(core.maxChecks('4.9'), 4);
  assert.equal(core.maxChecks(999999), 120);
});
