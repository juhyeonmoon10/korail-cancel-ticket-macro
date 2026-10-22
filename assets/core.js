(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  if (root) root.KorailHelperCore = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  function normalize(text) {
    return String(text == null ? '' : text).replace(/\s+/g, ' ').trim();
  }

  function identity(text) {
    const value = normalize(text);
    const times = Array.from(value.matchAll(/(?<![\d:])(?:[01]\d|2[0-3]):[0-5]\d(?![\d:])/g), match => match[0]);
    if (times.length !== 2) return null;

    const trainPattern = /(?:^|[^\p{L}\p{N}])(KTX(?:\s*[-–—]?\s*(?:산천|이음|청룡))?|ITX(?:\s*[-–—]?\s*(?:새마을|마음|청춘))?|새마을|무궁화|누리로)\s*(\d{1,6})(?![\d:])/giu;
    const trains = new Map();
    for (const match of value.matchAll(trainPattern)) {
      let kind = match[1].replace(/\s+/g, '').replace(/[–—]/g, '-').toUpperCase();
      kind = kind.replace(/^(KTX|ITX)-?(산천|이음|청룡|새마을|마음|청춘)$/, '$1-$2');
      const number = String(Number(match[2]));
      const train = `${kind} ${number}`;
      trains.set(train, train);
    }
    if (trains.size !== 1) return null;
    const train = trains.values().next().value;
    return { train, departure: times[0], arrival: times[1], key: `${train}|${times[0]}|${times[1]}` };
  }

  function isSeatAction(label) {
    return /^(예약|예약하기|예매|예매하기|좌석선택|예약가능)$/.test(normalize(label).replace(/\s/g, ''));
  }

  function isSearchAction(label) {
    return /^(조회하기|조회|열차조회하기|열차조회|다시조회|재조회|검색|검색하기|새로고침)$/.test(normalize(label).replace(/\s/g, ''));
  }

  function blocker(text) {
    const value = normalize(text);
    const automationBlock = /자동화된\s*요청으로\s*감지되어\s*차단되었습니다\.?\s*(?:CODE\s*:\s*(-\d{1,6}))?/i.exec(value);
    if (automationBlock) return `코레일이 자동화 요청을 차단했습니다${automationBlock[1] ? `(${automationBlock[1]})` : ''}. 자동 재시도를 중지합니다.`;
    if (/captcha|자동\s*입력\s*방지|보안\s*문자|로봇이\s*아닙니다|사람인지\s*확인|본인인지\s*확인/i.test(value)) return '보안 확인 필요';
    if (/비정상(?:적(?:인)?)?\s*(?:접속|접근|요청)|(?:접속|접근|이용|요청)(?:이|을|이\s*일시적으로)?\s*(?:차단|제한)|자동화(?:된)?\s*(?:접속|접근|요청|프로그램)|매크로(?:가|\s*사용이)?\s*(?:탐지|감지|차단)/.test(value)) return '접속 제한 확인 필요';
    if (/다시\s*로그인|로그인(?:이|을)?\s*필요|로그인\s*후\s*(?:이용|사용|예약|진행)|(?:로그인\s*(?:정보|상태|시간)|세션)(?:가|이)?\s*(?:만료|종료)|로그인이\s*(?:만료|해제|종료)/.test(value)) return '로그인 확인 필요';
    if (/접속\s*대기|대기\s*(?:인원|인수|순번|순서|자수)|대기자\s*수|현재\s*대기\s*중|순서가\s*되면\s*(?:자동|접속)/.test(value)) return '접속 대기 중';
    if (/오류가\s*발생|정상적으로\s*처리되지|잠시\s*후\s*다시\s*(?:시도|이용|접속)|요청(?:이|에)\s*실패|(?:시스템|서비스)\s*점검/.test(value)) return '사이트 오류 또는 점검';
    if (/안내\s*메시지/.test(value)) return '안내 메시지 확인 필요';
    return null;
  }

  function boundedInteger(value, fallback, minimum, maximum) {
    if (value == null || typeof value === 'boolean' || (typeof value === 'string' && !value.trim())) return fallback;
    const number = Number(value);
    return Number.isFinite(number) ? Math.min(maximum, Math.max(minimum, Math.floor(number))) : fallback;
  }

  function intervalSeconds(value) {
    return boundedInteger(value, 120, 60, 3600);
  }

  function maxChecks(value) {
    return boundedInteger(value, 30, 1, 120);
  }

  return Object.freeze({ normalize, identity, isSeatAction, isSearchAction, blocker, intervalSeconds, maxChecks });
});
