(() => {
  'use strict';
  if (document.getElementById('korail-one-helper')) return;
  const isDemo = location.hostname === 'korail-helper.invalid';
  const bridgeNonce = desktopNonce;
  const nativeMore = typeof desktopNativeMore !== 'undefined' && desktopNativeMore === true;
  if (!isDemo && globalThis.KorailSiteBlock) {
    globalThis.KorailSiteBlock.install(document, code => {
      chrome.webview.postMessage({ kind: 'siteBlocked', code, nonce: bridgeNonce });
    });
  }
  const journey = !isDemo && typeof desktopJourney !== 'undefined' && desktopJourney && desktopJourney.Enabled === true ? Object.freeze({ ...desktopJourney }) : null;
  const pendingRpc = new Map();
  let rpcSequence = 0;
  chrome.webview.addEventListener('message', event => {
    const message = event.data;
    if (message && message.kind === 'trainReadFinished' && message.nonce === bridgeNonce) {
      showTrainPanel();
      return;
    }
    if (message && message.kind === 'notificationStatus' && message.nonce === bridgeNonce && typeof message.text === 'string') {
      $('notification').textContent = message.text.slice(0, 500);
      log(message.text.slice(0, 500));
      return;
    }
    if (!message || !['leaseResult', 'reservationClickResult'].includes(message.kind) || message.nonce !== bridgeNonce) return;
    const pending = pendingRpc.get(message.id);
    if (!pending || pending.kind !== message.kind) return;
    pendingRpc.delete(message.id);
    clearTimeout(pending.timer);
    pending.resolve(message.ok === true);
  });
  const C = globalThis.KorailHelperCore;
  const S = globalThis.KorailSiteAdapter;
  const cardPage = () => !!S && S.matchesPage(document);
  const host = document.createElement('div');
  host.id = 'korail-one-helper';
  Object.assign(host.style, { position: 'fixed', zIndex: '2147483647', right: '18px', top: '18px' });
  const shadow = host.attachShadow({ mode: 'open' });
  shadow.innerHTML = `
    <style>
      :host{font:14px/1.5 system-ui,'Malgun Gothic',sans-serif;color:#142c3e;text-align:left}
      *{box-sizing:border-box}section{width:350px;max-width:calc(100vw - 36px);max-height:calc(100vh - 120px);overflow:auto;background:#fff;border:1px solid #c7d6e0;border-radius:16px;box-shadow:0 8px 35px #16394d33}
      header{display:flex;align-items:center;justify-content:space-between;background:#123c53;color:white;padding:13px 16px}strong{font-size:16px}main{padding:15px}p{margin:0 0 12px}.muted{font-size:12px;color:#526775}button,input,select{font:inherit}button{cursor:pointer;border:1px solid #b3c7d2;border-radius:7px;background:#f4f8fa;color:#173c50;padding:9px}button:hover{background:#e5f1f5}button:disabled{opacity:.5;cursor:default}header button{padding:0 8px;background:transparent;border-color:#6a8b9c;color:white}
      .row{display:flex;gap:8px;margin:9px 0}.row>*{flex:1;min-width:0}label{display:block;font-size:13px}input[type=number],input[type=text],select{display:block;width:100%;padding:6px;border:1px solid #aec1cc;border-radius:5px;margin-top:3px}.check{display:flex;gap:7px;margin:12px 0;align-items:flex-start}.check input{margin-top:4px}.primary{background:#086c68;color:#fff;border-color:#086c68}.primary:hover{background:#085951}#stop{background:#fff1ed;color:#a43e22;border-color:#edb5a6}#status{white-space:pre-wrap;padding:10px;background:#eaf3f7;border-radius:7px;margin:12px 0;font-size:13px}#target,#journey{white-space:pre-line;word-break:break-word}#journey{padding:9px;background:#f2f6f8;border-radius:6px}#log{max-height:105px;overflow:auto;font-size:11px;color:#526775;white-space:pre-wrap}details{margin:10px 0}summary{cursor:pointer}.small{font-size:11px}
    </style>
    <section aria-label="코레일 한 장 도우미">
      <header><strong>코레일 한 장 도우미</strong><button id="fold" aria-label="접기">−</button></header>
      <main>
        <p class="muted">${isDemo ? '모의 연습 화면입니다. 실제 예약·텔레그램 전송은 발생하지 않습니다. 조회 대기 시간은 약 1초입니다.' : '설정한 여정으로 코레일에서 직접 조회한 뒤, 목록에서 원하는 열차를 선택하세요.'}</p>
        <p id="journey" class="muted"></p>
        <button id="pickSearch">조회 버튼 직접 지정</button>
        <div class="row"><label>② 좌석 등급<select id="grade"><option>일반실</option><option>특실</option><option>우등실</option></select></label><button id="scanTrains">열차 목록 읽기</button></div>
        <label>조회 화면의 열차<select id="trainChoice"><option value="">먼저 열차 목록을 읽어주세요.</option></select></label>
        <label>여러 열차번호 (선택)<input id="trainNumbers" type="text" placeholder="505,541,561" autocomplete="off"></label>
        <p class="muted small">쉼표로 최대 20개를 입력하세요. 비워두면 위 목록의 열차 한 개를 선택합니다.</p>
        <div class="row"><button id="applyTrain">열차 선택 적용</button><button id="pickSeat">좌석 칸 직접 지정</button></div>
        <p id="target" class="muted">아직 지정하지 않았습니다.</p>
        <div class="row"><label>조회 간격 (초)<input id="interval" type="number" min="60" max="3600" value="120"></label><label>최대 추가 조회<input id="limit" type="number" min="1" max="120" value="30"></label></div>
        <label class="check"><input id="auto" type="checkbox"><span>좌석 발견 시 예매 버튼을 한 번 클릭<br><span class="muted">이후 화면과 예약 성공 여부는 직접 확인</span></span></label>
        <label class="check"><input id="confirmed" type="checkbox"><span id="confirmationText">실제 조회 화면의 출발일·구간·인원 1명과 선택한 열차·좌석 등급을 확인했습니다.</span></label>
        <div class="row"><button id="start" class="primary">확인 시작</button><button id="stop" disabled>중지</button><button id="sound" aria-label="알림 소리 시험">소리</button></div>
        <div id="status" role="status" aria-live="polite">대기 중 · 로그인과 결제는 직접 진행하세요.</div>
        <p id="notification" class="muted" role="status" aria-live="polite"></p>
        <details><summary class="muted">사용 안내 · 기록</summary><p class="muted">코레일 직통·1명 조회 결과에서 ‘열차 목록 읽기’를 누르면 현재 열차 분류를 재조회 버튼으로 연결합니다. 가격 칸은 좌석 선택이며, 아래 별도의 ‘예매’ 버튼을 누를 때 텔레그램 알림을 요청합니다. 예약 성공이나 결제 완료 알림은 아닙니다. 저장한 여정은 코레일 조회 화면에 자동 입력되지 않습니다. 창과 PC를 켜 두세요. 최소화·화면 이동·직접 조작·접근 제한·갱신 확인 실패 시 중지됩니다.</p><p class="small">비공식 도구 · 로그인 후 예약·결제는 미검증 · 조회 간격이 이용 제한을 방지하지는 않습니다.</p><div id="log"></div></details>
      </main>
    </section>`;
  document.documentElement.append(host);
  const $ = id => shadow.getElementById(id);
  let searchConfig, targetConfig, picking = null, highlighted = null, oldOutline = '';
  let trainCandidates = [], targetConfigs = [], ambiguousTrainNumbers = new Set(), selectionContext = null, manualPrepared = null, reservationClicks = 0;
  let running = false, starting = false, generation = 0, timer, watchdog, observer;
  let trainReadRequested = false;
  let session, count = 0, audio, pendingRefresh = null;
  let token = crypto.randomUUID();
  const originalTitle = document.title;
  const actionSelector = 'button,a,input[type=button],input[type=submit],[role=button]';
  const visible = el => !!el && el.isConnected && !el.closest('[hidden],[aria-hidden="true"]') &&
    getComputedStyle(el).visibility !== 'hidden' && getComputedStyle(el).display !== 'none' && el.getClientRects().length > 0;
  const enabled = el => visible(el) && !el.disabled && !el.closest('[inert],[aria-disabled="true"]') && !el.classList.contains('disabled');
  const label = el => el ? [el.innerText, el.value, el.getAttribute('aria-label'), el.querySelector('img[alt]')?.alt].map(C.normalize).find(Boolean) || '' : '';
  function log(text) { $('log').textContent = `${new Date().toLocaleTimeString('ko-KR')} ${text}\n${$('log').textContent}`.slice(0, 5000); }
  function status(text) {
    $('status').textContent = text;
    log(text);
    try { chrome.webview.postMessage({ kind: 'status', text, nonce: bridgeNonce }); } catch {}
  }
  async function lock(type, lease = token) {
    return new Promise(resolve => {
      const id = String(++rpcSequence);
      const timer = setTimeout(() => { pendingRpc.delete(id); resolve(false); }, 5000);
      pendingRpc.set(id, { resolve, timer, kind: 'leaseResult' });
      try { chrome.webview.postMessage({ kind: 'lease', id, type, token: lease, nonce: bridgeNonce }); }
      catch { clearTimeout(timer); pendingRpc.delete(id); resolve(false); }
    });
  }
  function reservationMetadata(id, manual, phase) {
    return { kind: 'reservationClick', phase, id, nonce: bridgeNonce, manual, demo: isDemo,
      train: targetConfig.identity.train, departure: targetConfig.identity.departure,
      arrival: targetConfig.identity.arrival, grade: targetConfig.grade };
  }
  function prepareReservation(id, manual) {
    return new Promise(resolve => {
      const timer = setTimeout(() => { pendingRpc.delete(id); resolve(false); }, 5000);
      pendingRpc.set(id, { resolve, timer, kind: 'reservationClickResult' });
      try { chrome.webview.postMessage(reservationMetadata(id, manual, 'prepare')); }
      catch { clearTimeout(timer); pendingRpc.delete(id); resolve(false); }
    });
  }
  function sendReservation(id, manual, phase) {
    try { chrome.webview.postMessage(reservationMetadata(id, manual, phase)); if (phase === 'clicked') reservationClicks++; }
    catch { log('예매 버튼 클릭 알림 요청을 전달하지 못했습니다.'); }
  }
  function beep() {
    try {
      audio ||= new AudioContext();
      audio.resume().catch(() => {});
      for (let i = 0; i < 3; i++) {
        const oscillator = audio.createOscillator(), gain = audio.createGain();
        oscillator.connect(gain); gain.connect(audio.destination);
        oscillator.frequency.value = i === 1 ? 1047 : 784;
        const t = audio.currentTime + i * .28;
        gain.gain.setValueAtTime(.08, t); gain.gain.exponentialRampToValueAtTime(.001, t + .2);
        oscillator.start(t); oscillator.stop(t + .21);
      }
    } catch { log('소리를 재생할 수 없습니다. 화면 알림을 확인하세요.'); }
  }
  function path(el, root = document.body) {
    const parts = [];
    for (let node = el; node && node !== root; node = node.parentElement) {
      if (!node.parentElement) throw new Error('요소 경로를 찾지 못했습니다.');
      parts.unshift(`${node.localName}:nth-child(${Array.from(node.parentElement.children).indexOf(node) + 1})`);
    }
    return parts.join(' > ');
  }
  function rootFor(el) {
    const tableRow = el.closest('tr,[role=row]');
    if (tableRow && C.identity(tableRow.innerText)) return tableRow;
    for (let node = el.parentElement; node && node !== document.body; node = node.parentElement) {
      if (C.identity(node.innerText)) return node;
    }
    return null;
  }
  function slotFor(el, row) {
    let slot = el.closest('td,[role=cell]');
    if (slot && row.contains(slot)) return slot;
    slot = el.closest(actionSelector) || el;
    // A dedicated wrapper survives a sold-out span becoming a reservation button.
    const parent = slot.parentElement;
    if (parent && parent !== row && !C.identity(parent.innerText) &&
        parent.querySelectorAll(actionSelector).length <= 1 && label(parent) === label(slot)) return parent;
    return slot;
  }
  function schema(row) {
    if (row.localName === 'tr') {
      const table = row.closest('table');
      return Array.from(table.querySelectorAll('thead th,thead td')).map(label).join('|');
    }
    return Array.from(row.children).map(el => el.localName).join('|');
  }
  function simpleHeaders(row) {
    if (row.localName !== 'tr') return null;
    const table = row.closest('table');
    const headerRows = table ? Array.from(table.querySelectorAll('thead > tr')).filter(el => el.closest('table') === table) : [];
    if (headerRows.length !== 1) return null;
    const headers = Array.from(headerRows[0].cells), cells = Array.from(row.cells);
    if (headers.length !== cells.length || !headers.length || [...headers, ...cells].some(el => el.colSpan !== 1 || el.rowSpan !== 1)) return null;
    return headers;
  }
  function validDate(value) {
    const match = /^(20\d{2})-(\d{2})-(\d{2})$/.exec(String(value || ''));
    if (!match) return false;
    const d = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
    return d.getFullYear() === Number(match[1]) && d.getMonth() === Number(match[2]) - 1 && d.getDate() === Number(match[3]);
  }
  function journeyError() {
    if (!journey) return null;
    if (!C.normalize(journey.DepartureStation) || !C.normalize(journey.ArrivalStation) || journey.DepartureStation === journey.ArrivalStation || !validDate(journey.Date)) return '설정한 출발역·도착역·날짜가 올바르지 않습니다. 프로그램 설정에서 수정하세요.';
    if (![journey.FromTime, journey.ToTime].every(v => /^(?:[01]\d|2[0-3]):[0-5]\d$/.test(v || '')) || journey.FromTime > journey.ToTime) return '설정한 출발 시간대가 올바르지 않습니다. 프로그램 설정에서 수정하세요.';
    try { C.parseTrainNumbers(journey.TrainNumber ?? ''); }
    catch (error) { return `${error.message} 프로그램 설정에서 수정하세요.`; }
    if (!['일반실', '특실', '우등실'].includes(journey.Grade)) return '프로그램 설정에서 좌석 등급을 선택하세요.';
    return null;
  }
  function matchesJourney(identity, grade) {
    if (!journey) return true;
    const numbers = C.parseTrainNumbers(journey.TrainNumber ?? '');
    return (!numbers.length || numbers.includes(trainNumber(identity))) &&
      identity.departure >= journey.FromTime && identity.departure <= journey.ToTime && grade === journey.Grade;
  }
  function assertJourney(config) {
    const invalid = journeyError(); if (invalid) throw new Error(invalid);
    if (!config || !matchesJourney(config.identity, config.grade)) throw new Error('선택한 열차번호·출발시각·등급이 저장한 여정 조건과 다릅니다. 설정 또는 열차 선택을 확인하세요.');
    if (config.adapter === 'korail-card') {
      const q = S.query(document);
      if (!q.supported) throw new Error(q.reason || '현재 조회 조건은 지원하지 않습니다.');
      if (config.selectionConditions !== conditions()) throw new Error('열차를 선택한 뒤 조회 조건이 바뀌었습니다. 목록을 다시 읽고 선택하세요.');
      if (journey && (q.departureStation !== journey.DepartureStation.replace(/역$/, '') || q.arrivalStation !== journey.ArrivalStation.replace(/역$/, '') || q.date !== journey.Date)) throw new Error('실제 조회 화면의 구간·출발일이 저장한 여정과 다릅니다. 같은 조건으로 다시 조회하세요.');
    }
    if (!journey) return;
    // Only clearly named, visible controls are machine-checked. The user must also compare the actual query.
    const fields = Array.from(document.querySelectorAll('input,select')).filter(el => visible(el) && !['password', 'hidden', 'submit', 'button'].includes(el.type));
    const names = el => [el.getAttribute('aria-label') || '', ...Array.from(el.labels || [], label => label.textContent), el.name || '', el.id || ''].map(C.normalize);
    const station = value => C.normalize(value).replace(/역$/, '');
    const checks = [
      { labels: /^(출발|출발역|from|departureStation)$/i, wanted: journey.DepartureStation, label: '출발역', convert: station },
      { labels: /^(도착|도착역|to|arrivalStation)$/i, wanted: journey.ArrivalStation, label: '도착역', convert: station },
      { labels: /^(날짜|출발일|출발날짜|승차일|date|departureDate)$/i, wanted: journey.Date, label: '출발일', convert: value => {
        const match = /^(20\d{2})\s*(?:[-./]|년)\s*(\d{1,2})\s*(?:[-./]|월)\s*(\d{1,2})\s*일?$/.exec(C.normalize(value));
        return match ? `${match[1]}-${match[2].padStart(2, '0')}-${match[3].padStart(2, '0')}` : null;
      } }
    ];
    for (const check of checks) for (const field of fields.filter(el => names(el).some(name => check.labels.test(name)))) {
      const actual = check.convert(field.tagName === 'SELECT' ? field.selectedOptions[0]?.textContent || field.value : field.value);
      if (actual && actual !== check.convert(check.wanted)) throw new Error(`실제 조회 화면의 ${check.label}이 저장한 여정과 다릅니다. 코레일에서 조건을 바꾸고 다시 조회하세요.`);
    }
  }
  function configFor(row, slot, grade, autoCapable) {
    return { identity: C.identity(row.innerText), tag: row.localName, schema: schema(row), slotPath: path(slot, row), grade, autoCapable };
  }
  function trainNumber(identity) { return String(Number(identity.train.match(/\d+$/)?.[0])); }
  function clearSelection() {
    targetConfig = null; targetConfigs = []; selectionContext = null; manualPrepared = null;
    $('confirmed').checked = false;
  }
  function selectionState() {
    return JSON.stringify({ url: location.href, conditions: conditions(), grade: $('grade').value, numbers: $('trainNumbers').value, option: $('trainChoice').value });
  }
  function validateSelectionState() {
    if (!targetConfigs.length) return;
    try { if (selectionContext === selectionState()) return; } catch { /* A removed query is also a changed query. */ }
    clearSelection(); preview();
    status('조회 조건이나 열차 선택이 바뀌었습니다. 열차를 다시 적용하고 확인하세요.');
  }
  function selectTargets(configs) {
    for (const config of configs) { assertJourney(config); findTarget(config); }
    targetConfigs = configs.slice(); targetConfig = targetConfigs[0] || null;
    selectionContext = selectionState();
    preview();
  }
  function scanTrains() {
    if (running || starting) return false;
    trainReadRequested = false; setControls();
    try {
      clearSelection(); preview(); trainCandidates = []; ambiguousTrainNumbers = new Set();
      const invalid = journeyError(); if (invalid) throw new Error(invalid);
      const grade = $('grade').value;
      if (journey && grade !== journey.Grade) throw new Error('선택한 좌석 등급이 프로그램에 저장한 등급과 다릅니다. 등급 또는 설정을 확인하세요.');
      trainCandidates = [];
      if (cardPage()) {
        const q = S.query(document), refresh = S.activeRefresh(document);
        if (!q.supported) throw new Error(q.reason || '현재 조회 조건은 지원하지 않습니다.');
        if (!refresh || !enabled(refresh.element)) throw new Error('열차 분류와 조회 결과가 표시된 뒤 다시 읽어주세요.');
        searchConfig = { adapter: 'korail-card', path: refresh.selector, label: refresh.label };
        for (const row of S.cards(document)) {
          const identity = S.identity(row), slot = S.slot(row, grade);
          if (!visible(row) || !identity || !slot || !matchesJourney(identity, grade)) continue;
          if (identity.departureStation !== q.departureStation || identity.arrivalStation !== q.arrivalStation) continue;
          trainCandidates.push({ adapter: 'korail-card', identity, grade, autoCapable: true, selectionConditions: conditions() });
        }
      } else for (const row of document.querySelectorAll('table tbody tr,table > tr')) {
        if (!visible(row)) continue;
        const identity = C.identity(row.innerText), headers = simpleHeaders(row);
        if (!identity || !headers || !matchesJourney(identity, grade)) continue;
        const columns = headers.map((el, index) => C.normalize(label(el)) === grade ? index : -1).filter(index => index >= 0);
        if (columns.length !== 1) continue;
        const slot = row.cells[columns[0]];
        if (!slot || !visible(slot)) continue;
        trainCandidates.push(configFor(row, slot, grade, true));
      }
      const duplicateKeys = new Set(trainCandidates.filter((item, index, list) => list.some((other, otherIndex) => index !== otherIndex && item.identity.key === other.identity.key && item.grade === other.grade)).map(item => item.identity.key + '|' + item.grade));
      for (const item of trainCandidates) if (duplicateKeys.has(item.identity.key + '|' + item.grade)) ambiguousTrainNumbers.add(trainNumber(item.identity));
      trainCandidates = trainCandidates.filter(item => !duplicateKeys.has(item.identity.key + '|' + item.grade));
      $('trainChoice').replaceChildren(new Option(trainCandidates.length ? '원하는 열차를 골라주세요.' : '조건에 맞는 열차를 읽지 못했습니다.', ''));
      trainCandidates.forEach((item, index) => $('trainChoice').add(new Option(`${item.identity.train} · ${item.identity.departure} → ${item.identity.arrival} · ${item.grade}`, String(index))));
      $('confirmed').checked = false;
      status(trainCandidates.length ? `${trainCandidates.length}개 열차를 읽었습니다. 목록에서 고르거나 여러 번호를 입력한 뒤 ‘열차 선택 적용’을 누르세요.${duplicateKeys.size ? '\n중복되어 구분할 수 없는 열차는 제외했습니다.' : ''}` : '열차를 읽지 못했습니다. 실제 조회 결과와 저장한 조건을 확인하거나 좌석 칸을 직접 지정하세요.');
      return true;
    } catch (error) { trainCandidates = []; $('trainChoice').replaceChildren(new Option('목록을 다시 읽어주세요.', '')); status(error.message); return false; }
  }
  function requestTrainScan() {
    if (running || starting || trainReadRequested) return false;
    if (!nativeMore || !cardPage()) return scanTrains();
    trainReadRequested = true; setControls();
    status('더보기 아래 열차까지 읽도록 요청했습니다. 프로그램 창을 그대로 두세요.');
    try { chrome.webview.postMessage({ kind: 'readAllTrains', nonce: bridgeNonce }); return true; }
    catch { trainReadRequested = false; setControls(); status('열차 목록 읽기 요청을 전달하지 못했습니다. 다시 시도하세요.'); return false; }
  }
  function showTrainPanel() {
    trainReadRequested = false; setControls(); foldPanel(false);
    return true;
  }
  function applyTrain() {
    if (trainReadRequested || running || starting) return;
    clearSelection(); preview();
    try {
      const numbers = C.parseTrainNumbers($('trainNumbers').value);
      if (numbers.length) {
        if (!scanTrains()) return;
        const next = numbers.map(number => {
          const matches = trainCandidates.filter(item => trainNumber(item.identity) === number);
          if (ambiguousTrainNumbers.has(number) || matches.length > 1) throw new Error(`열차번호 ${number}의 후보가 여러 개입니다. 같은 번호의 종류와 시각을 확인하세요.`);
          if (matches.length !== 1) throw new Error(`열차번호 ${number}을 현재 조회 결과에서 찾지 못했습니다. ${nativeMore && cardPage() ? '‘열차 목록 읽기’로 더보기 아래 열차까지 읽은 뒤 다시 적용하세요.' : '모든 번호가 표시되도록 조회하세요.'}`);
          return matches[0];
        });
        $('trainNumbers').value = numbers.join(',');
        selectTargets(next);
        status(`${next.length}개 열차를 입력 순서대로 선택했습니다. 모든 열차의 날짜·구간·시각·등급을 확인하세요.${next.length > 1 ? '\n여러 열차 실행은 EXE 상단의 마우스 기능을 사용하세요.' : ''}`);
        return;
      }
      const value = $('trainChoice').value;
      if (value === '' || !trainCandidates[Number(value)]) throw new Error('목록에서 원하는 열차를 먼저 골라주세요.');
      const next = trainCandidates[Number(value)];
      selectTargets([next]); status('열차를 선택했습니다. 실제 조회 조건과 선택한 열차·좌석 등급을 확인하세요.');
    } catch (error) { clearSelection(); preview(); status(error.message); }
  }
  function findTarget(config = targetConfig) {
    if (!config) return null;
    if (config.adapter === 'korail-card') {
      if (!cardPage()) throw new Error('열차 조회 화면이 바뀌었습니다. 다시 조회하세요.');
      const rows = S.cards(document).filter(row => visible(row) && S.identity(row)?.key === config.identity.key);
      if (rows.length !== 1) throw new Error('지정한 열차가 없거나 여러 개입니다. 다시 확인하세요.');
      const slot = S.slot(rows[0], config.grade);
      if (!slot || !visible(slot)) throw new Error('선택한 좌석 등급의 구조가 바뀌었습니다. 다시 선택하세요.');
      return { row: rows[0], slot };
    }
    const rows = Array.from(document.querySelectorAll(config.tag)).filter(el => visible(el) && C.identity(el.innerText)?.key === config.identity.key);
    // In nested cards only consider the same structure and the smallest matching root.
    const matches = rows.filter(row => schema(row) === config.schema && !rows.some(other => other !== row && row.contains(other) && schema(other) === config.schema));
    if (matches.length !== 1) throw new Error('지정한 열차가 없거나 여러 개입니다. 다시 지정하세요.');
    const row = matches[0], slot = row.querySelector(`:scope > ${config.slotPath}`);
    if (!slot || !visible(slot)) throw new Error('좌석 칸의 구조가 바뀌었습니다. 다시 지정하세요.');
    if (config.autoCapable) {
      const headers = simpleHeaders(row);
      if (!headers || slot.localName !== 'td' || C.normalize(label(headers[slot.cellIndex])) !== config.grade) throw new Error('좌석 등급의 표 구조가 바뀌었습니다. 열차와 좌석 칸을 다시 선택하세요.');
    }
    return { row, slot };
  }
  function findSearch() {
    if (searchConfig?.adapter === 'korail-card') {
      const current = cardPage() && S.activeRefresh(document);
      if (!current || current.selector !== searchConfig.path || current.label !== searchConfig.label || !enabled(current.element)) throw new Error('열차 분류가 바뀌었습니다. 목록을 다시 읽어주세요.');
      return current.element;
    }
    const el = searchConfig && document.body.querySelector(searchConfig.path);
    if (!el || !enabled(el) || label(el) !== searchConfig.label || !C.isSearchAction(label(el))) throw new Error('조회 버튼이 바뀌었거나 사용할 수 없습니다.');
    return el;
  }
  function seatAction(slot) {
    if (targetConfig?.adapter === 'korail-card') {
      const action = S.positiveAction(slot, targetConfig.grade);
      return action && enabled(action) ? action : null;
    }
    const candidates = [slot, ...slot.querySelectorAll(actionSelector)].filter(el => el.matches(actionSelector) && enabled(el) && C.isSeatAction(label(el)));
    const unique = [...new Set(candidates)].filter(el => !candidates.some(other => other !== el && el.contains(other)));
    if (unique.length > 1) throw new Error('좌석 칸에 예약 버튼이 여러 개 있습니다. 더 좁은 칸을 지정하세요.');
    return unique[0] || null;
  }
  function conditions() {
    if (cardPage()) {
      const q = S.query(document), refresh = S.activeRefresh(document);
      return JSON.stringify({ query: q, filter: refresh?.selector || '' });
    }
    const fields = Array.from(document.querySelectorAll('input,select,textarea')).filter(el =>
      visible(el) && !['password', 'hidden', 'submit', 'button'].includes(el.type)
    ).map(el => [el.id || el.name || '', el.type, el.value, el.checked]);
    const text = document.body.innerText;
    const dates = Array.from(text.matchAll(/20\d{2}\s*(?:[-./]|년)\s*\d{1,2}\s*(?:[-./]|월)\s*\d{1,2}/g), m => C.normalize(m[0]));
    const people = Array.from(text.matchAll(/(?:성인|어른|어린이|유아|경로|장애인|총)\s*:?\s*\d+\s*명/g), m => C.normalize(m[0]));
    return JSON.stringify({ fields, dates: [...new Set(dates)].sort(), people: [...new Set(people)].sort() });
  }
  function blocked() {
    const reason = C.blocker(document.body.innerText);
    if (reason) return reason;
    if (Array.from(document.querySelectorAll('[role=dialog],[aria-modal=true]')).some(visible)) return '안내창이 열렸습니다. 내용을 직접 확인하세요.';
    if (Array.from(document.querySelectorAll('iframe[src*="captcha"],iframe[src*="challenge"],input[autocomplete="one-time-code"]')).some(visible)) return '인증 화면을 직접 확인하세요.';
    return null;
  }
  function guard() {
    if (document.hidden) throw new Error('프로그램 화면이 숨겨져 중지했습니다. 다시 확인하고 시작하세요.');
    if (location.href !== session.url) throw new Error('페이지가 이동했습니다.');
    const reason = blocked(); if (reason) throw new Error(reason);
    if (conditions() !== session.conditions) throw new Error('조회 조건이 바뀌었습니다. 다시 확인하세요.');
    assertJourney(targetConfig);
  }
  function setControls() {
    for (const id of ['pickSearch', 'pickSeat', 'grade', 'scanTrains', 'trainChoice', 'trainNumbers', 'applyTrain', 'interval', 'limit', 'auto', 'confirmed', 'start']) $(id).disabled = running || starting;
    if (trainReadRequested) for (const id of ['pickSearch', 'pickSeat', 'grade', 'scanTrains', 'trainChoice', 'trainNumbers', 'applyTrain', 'confirmed', 'start']) $(id).disabled = true;
    $('stop').disabled = !(running || starting);
  }
  function clearHighlight() { if (highlighted) highlighted.style.outline = oldOutline; highlighted = null; }
  function foldPanel(folded) {
    shadow.querySelector('main').hidden = folded;
    $('fold').textContent = folded ? '+' : '−';
    $('fold').setAttribute('aria-label', folded ? '펼치기' : '접기');
  }
  function stop(message, release = true) {
    if (picking) foldPanel(false);
    generation++; running = false; starting = false; picking = null; pendingRefresh = null;
    clearTimeout(timer); clearInterval(watchdog); observer?.disconnect(); clearHighlight(); setControls();
    if (release) void lock('release');
    if (message) status(message);
  }
  async function found(action, run) {
    if (!running || run !== generation) return;
    const ok = await lock('check');
    if (!running || run !== generation) return;
    if (!ok) { stop('실행 잠금이 만료되었거나 다른 확인 작업이 실행 중입니다.'); return; }
    guard();
    if (busy()) throw new Error('결과가 갱신 중입니다. 조회가 끝난 뒤 다시 시작하세요.');
    const latest = findTarget();
    let currentAction = seatAction(latest.slot);
    if (!currentAction || currentAction !== action) throw new Error('좌석 상태가 바뀌었습니다. 다시 확인하세요.');
    const auto = session.auto && targetConfig.autoCapable;
    let eventId = null;
    if (auto) {
      if (targetConfig.adapter === 'korail-card') {
        // A price link selects a grade; only the separate drawer button submits a reservation.
        let reserve = S.reservationAction(document, latest.row, targetConfig.grade);
        if (!reserve) {
          currentAction.click();
          const until = Date.now() + 3000;
          do {
            await new Promise(resolve => setTimeout(resolve, 100));
            if (!running || run !== generation) return;
            guard();
            const chosen = findTarget();
            if (!seatAction(chosen.slot)) throw new Error('좌석 선택 중 상태가 바뀌었습니다. 다시 확인하세요.');
            reserve = S.reservationAction(document, chosen.row, targetConfig.grade);
          } while (!reserve && Date.now() < until);
        }
        if (!reserve || !enabled(reserve)) throw new Error('선택한 열차와 등급의 예매 버튼을 확인하지 못했습니다. 직접 확인하세요.');
        currentAction = reserve;
      }
      eventId = crypto.randomUUID();
      try {
        if (!await prepareReservation(eventId, false)) throw new Error('예매 클릭 알림을 준비하지 못했습니다. 프로그램 상태를 확인하고 다시 시작하세요.');
        if (!running || run !== generation) { sendReservation(eventId, false, 'cancelled'); return; }
        guard();
        const chosen = findTarget();
        const readyAction = targetConfig.adapter === 'korail-card' ? S.reservationAction(document, chosen.row, targetConfig.grade) : seatAction(chosen.slot);
        if (busy() || readyAction !== currentAction || !enabled(currentAction)) throw new Error('알림 준비 중 좌석 상태가 바뀌었습니다. 다시 확인하세요.');
      } catch (error) { sendReservation(eventId, false, 'cancelled'); throw error; }
    }
    // Disarm before click: mutations or navigation cannot create a second click.
    stop(auto ? '좌석 발견 · 예매 버튼 1회 클릭 후 중지합니다. 예약 성공 여부와 결제는 직접 확인하세요.' : '좌석 발견! 지정한 좌석 칸을 확인하고 직접 예매하세요.', false);
    document.title = '🔔 좌석 발견 · ' + originalTitle;
    latest.slot.style.outline = '4px solid #0c9d86'; latest.slot.scrollIntoView({ block: 'center', behavior: 'smooth' });
    beep();
    try {
      if (auto) {
        currentAction.click();
        sendReservation(eventId, false, 'clicked');
      }
    } catch (error) {
      if (eventId) sendReservation(eventId, false, 'cancelled');
      status('예매 버튼 클릭 중 오류가 발생했습니다. 실제 페이지에서 결과를 확인하세요.');
    } finally { void lock('release'); }
  }
  function schedule(run) {
    if (!running || run !== generation) return;
    if (count >= session.limit) { stop(`최대 ${session.limit}회 조회를 마쳤습니다. 좌석을 발견하지 못했습니다.`); return; }
    status(`확인 중 · ${count}/${session.limit}회 추가 조회\n${isDemo ? '모의 연습 · 다음 조회까지 약 1초' : `다음 조회까지 ${session.interval}초`}`);
    timer = setTimeout(() => refresh(run).catch(error => { if (run === generation) stop(error.message); }), isDemo ? 1200 : session.interval * 1000);
  }
  async function refresh(run) {
    if (!running || run !== generation) return;
    guard();
    const ok = await lock('check');
    if (!running || run !== generation) return;
    if (!ok) { stop('실행 잠금이 만료되었습니다. 다시 시작하세요.'); return; }
    guard();
    const before = findTarget(), search = findSearch();
    count++;
    pendingRefresh = { oldRow: before.row, currentRow: before.row, signature: before.row.innerHTML, oldSearch: search, changed: false, sawBusy: false, since: Date.now(), lastChange: Date.now() };
    status(`${count}/${session.limit}회 조회 · 결과 갱신 확인 중…`);
    search.click();
  }
  function busy() {
    const search = searchConfig && document.body.querySelector(searchConfig.path);
    return !!search?.disabled || search?.getAttribute('aria-disabled') === 'true' || Array.from(document.querySelectorAll('[aria-busy=true],[role=progressbar]')).some(visible);
  }
  async function inspect(run) {
    if (!running || run !== generation) return;
    guard();
    if (!pendingRefresh) return;
    const p = pendingRefresh;
    if (Date.now() - p.since > 30000) throw new Error('30초 안에 결과 갱신을 확인하지 못했습니다. 자동 재시도를 중지합니다.');
    if (busy()) { p.sawBusy = true; p.lastChange = Date.now(); p.stableTarget = null; return; }
    // Do not inspect stale seats immediately after submitting a search.
    if (!(p.changed || p.sawBusy)) return;
    let current;
    try { current = findTarget(); }
    catch {
      // A live refresh can empty or partially rebuild the result list without
      // exposing aria-busy. Missing, ambiguous, or incomplete targets never
      // authorize a click; wait for a complete target or the existing deadline.
      p.stableTarget = null;
      return;
    }
    const signature = current.row.innerHTML, stable = p.stableTarget;
    if (!stable || stable.row !== current.row || stable.slot !== current.slot || stable.signature !== signature) {
      p.stableTarget = { row: current.row, slot: current.slot, signature, since: Date.now() };
      return;
    }
    if (Date.now() - p.lastChange < 700 || Date.now() - stable.since < 700) return;
    pendingRefresh = null;
    const action = seatAction(current.slot);
    if (action) await found(action, run); else schedule(run);
  }
  async function start() {
    validateSelectionState();
    if (targetConfigs.length > 1) { status('여러 열차 실행은 EXE 상단 ‘마우스 자동 조회’ 또는 ‘선택 열차 예약 (마우스)’을 사용하세요. 화면 안의 연습 실행은 한 열차만 지원합니다.'); return; }
    if (!isDemo) { status('EXE 상단 ‘마우스 자동 조회’ 또는 ‘선택 열차 예약 (마우스)’을 사용하세요.'); return; }
    if (starting || running) return;
    if (!searchConfig || !targetConfig) { status('① 조회 버튼을 지정하고 ② 원하는 열차를 선택하세요.'); return; }
    if (!$('confirmed').checked) { status('화면의 여정·인원·열차·좌석 등급을 확인하고 확인란을 체크하세요.'); return; }
    starting = true; setControls();
    const run = ++generation;
    const lease = token = crypto.randomUUID();
    try {
      if (document.hidden) throw new Error('프로그램 화면을 표시한 뒤 시작하세요.');
      const reason = blocked(); if (reason) throw new Error(reason);
      if (busy()) throw new Error('조회가 끝난 뒤 시작하세요.');
      findSearch(); findTarget(); assertJourney(targetConfig);
      if ($('auto').checked && !targetConfig.autoCapable) throw new Error('좌석 등급의 표 머리글을 확인할 수 없어 자동 클릭을 지원하지 않습니다. 알림 모드로 시작하세요.');
      if (!await lock('acquire', lease)) throw new Error('실행 잠금을 얻지 못했습니다. 프로그램 상태를 확인하고 다시 시작하세요.');
      if (run !== generation) { void lock('release', lease); return; }
      if (document.hidden) throw new Error('프로그램 화면이 숨겨져 중지했습니다. 다시 확인하고 시작하세요.');
      session = { url: location.href, conditions: conditions(), interval: C.intervalSeconds($('interval').value), limit: C.maxChecks($('limit').value), auto: $('auto').checked };
      $('interval').value = session.interval; $('limit').value = session.limit;
      document.title = originalTitle;
      audio ||= new AudioContext(); audio.resume().catch(() => {});
      starting = false; running = true; count = 0; setControls();
      observer = new MutationObserver(records => {
        const p = pendingRefresh; if (!p) return;
        if (records.some(r => r.target === p.oldSearch && r.type === 'attributes' && ['disabled', 'aria-disabled'].includes(r.attributeName))) p.sawBusy = true;
        let current;
        try { current = findTarget(); } catch { /* Partial render: wait for a stable, unambiguous row. */ }
        const row = current?.row;
        const signature = row?.innerHTML || '';
        if (row !== p.currentRow || signature !== p.signature) {
          p.lastChange = Date.now(); p.changed = true;
          p.currentRow = row; p.signature = signature;
        }
      });
      observer.observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['disabled','aria-disabled','aria-busy'] });
      let checking = false, lastLease = Date.now();
      watchdog = setInterval(async () => {
        if (checking || !running || run !== generation) return;
        checking = true;
        try {
          if (Date.now() - lastLease > 30000) {
            if (!await lock('check')) throw new Error('실행 잠금이 만료되었습니다.');
            lastLease = Date.now();
          }
          await inspect(run);
        } catch (error) { if (run === generation) stop(error.message); } finally { checking = false; }
      }, 350);
      const current = findTarget(), action = seatAction(current.slot);
      if (action) await found(action, run); else schedule(run);
    } catch (error) { if (run === generation) stop(error.message); else void lock('release', lease); }
  }
  function preview() {
    manualPrepared = null;
    $('confirmed').checked = false;
    const descriptions = targetConfigs.map(item => `${item.identity.train} · ${item.identity.departure} → ${item.identity.arrival} · ${item.grade}`);
    $('target').textContent = `조회: ${searchConfig?.label || '미지정'}\n${descriptions.length > 1 ? `선택 열차 ${descriptions.length}개 (입력 순서):\n${descriptions.map((text, index) => `${index + 1}. ${text}`).join('\n')}` : `열차: ${descriptions[0] || '미지정'}`}`;
  }
  function beginPick(type) {
    if (type === 'seat') { clearSelection(); $('trainNumbers').value = ''; }
    // Choosing the refresh control does not replace an already applied train.
    // Reconfirm afterwards; real query changes still invalidate the selection.
    preview(); picking = type; foldPanel(true);
    status(type === 'search' ? '사이트의 조회/검색 버튼을 클릭하세요. Esc로 취소합니다.' : '원하는 열차의 일반실/특실 칸에서 매진 표시 또는 예약 버튼을 클릭하세요. Esc로 취소합니다.');
  }
  function chosenManualAction(event) {
    if (!event.isTrusted || picking || targetConfigs.length !== 1 || !targetConfig || event.composedPath().includes(host)) return null;
    try {
      const action = event.target.closest(actionSelector), current = findTarget();
      const expected = targetConfig.adapter === 'korail-card' ? S.reservationAction(document, current.row, targetConfig.grade) : seatAction(current.slot);
      if (!action || action !== expected) return null;
      assertJourney(targetConfig);
      return action;
    } catch { return null; }
  }
  document.addEventListener('pointerdown', event => {
    const action = chosenManualAction(event);
    if (!action || event.button !== 0) return;
    const id = crypto.randomUUID();
    manualPrepared = { action, id, at: Date.now(), key: targetConfig.identity.key, grade: targetConfig.grade };
    void prepareReservation(id, true);
  }, true);
  document.addEventListener('pointerover', event => {
    if (!picking || event.composedPath().includes(host)) return;
    clearHighlight(); highlighted = event.target; oldOutline = highlighted.style.outline; highlighted.style.outline = '3px solid #ed8c26';
  }, true);
  document.addEventListener('click', event => {
    if (event.composedPath().includes(host)) return;
    if (!picking) {
      const action = chosenManualAction(event);
      if (action) {
        const id = manualPrepared && manualPrepared.action === action && manualPrepared.key === targetConfig.identity.key && manualPrepared.grade === targetConfig.grade && Date.now() - manualPrepared.at < 5000 ? manualPrepared.id : crypto.randomUUID();
        sendReservation(id, true, 'clicked');
        manualPrepared = null;
      }
      if (event.isTrusted && (running || starting)) stop('화면을 직접 조작하여 중지했습니다.');
      return;
    }
    event.preventDefault(); event.stopImmediatePropagation(); clearHighlight();
    try {
      if (picking === 'search') {
        const el = event.target.closest(actionSelector);
        if (!el || !enabled(el) || !C.isSearchAction(label(el))) throw new Error('조회/검색이라고 표시된 버튼을 선택하세요.');
        searchConfig = { path: path(el), label: label(el) };
      } else {
        if (cardPage()) {
          const row = event.target.closest('li.tckList');
          const grade = $('grade').value;
          const slot = row && S.slot(row, grade), identity = row && S.identity(row);
          if (!identity || !slot || !slot.contains(event.target)) throw new Error('선택한 등급의 좌석 칸을 누르세요. 열차 목록 읽기를 사용해도 됩니다.');
          const refresh = S.activeRefresh(document);
          if (!refresh) throw new Error('조회 결과가 표시된 뒤 다시 선택하세요.');
          const next = { adapter: 'korail-card', identity, grade, autoCapable: true, selectionConditions: conditions() };
          searchConfig = { adapter: 'korail-card', path: refresh.selector, label: refresh.label };
          selectTargets([next]);
        } else {
        const row = rootFor(event.target);
        if (!row) throw new Error('열차번호와 출발·도착시각을 읽지 못했습니다. 이 화면은 지원하지 않습니다.');
        const slot = slotFor(event.target, row);
        if (slot === row || !row.contains(slot)) throw new Error('열차 전체가 아닌 좌석 칸 안의 표시를 선택하세요.');
        let grade = label(slot).slice(0, 30), autoCapable = false;
        if (row.localName === 'tr' && slot.localName === 'td') {
          const headers = simpleHeaders(row);
          grade = label(headers?.[slot.cellIndex] || slot);
          if (/예약대기|입석|자유석/.test(grade)) throw new Error('일반실 또는 특실의 좌석 칸을 지정하세요.');
          autoCapable = !!headers && /^(일반실|특실|우등실)$/.test(C.normalize(grade));
        }
        const next = configFor(row, slot, grade, autoCapable);
        selectTargets([next]);
        }
      }
      picking = null; foldPanel(false); preview(); status(targetConfig && !targetConfig.autoCapable ? '지정했습니다. 이 구조에서는 알림만 지원합니다. 좌석 등급을 직접 확인하세요.' : '지정했습니다. 화면의 조건과 좌석 등급을 확인하세요.');
    } catch (error) { picking = null; foldPanel(false); status(error.message); }
  }, true);
  for (const type of ['input', 'change', 'keydown']) document.addEventListener(type, event => {
    if (type === 'keydown' && event.isTrusted && event.key === 'F8') {
      event.preventDefault(); stop('F8로 중지했습니다.'); return;
    }
    if (event.composedPath().includes(host)) return;
    if (type === 'input' || type === 'change') validateSelectionState();
    if (!event.isTrusted) return;
    if (event.key === 'Escape' && picking) { picking = null; foldPanel(false); clearHighlight(); status('지정을 취소했습니다.'); }
    if ((running || starting) && (type !== 'keydown' || !['Shift','Control','Alt','Meta','Tab'].includes(event.key))) stop('직접 입력하여 중지했습니다.');
  }, true);
  addEventListener('pagehide', () => stop(null));
  document.addEventListener('visibilitychange', () => {
    if (document.hidden && (running || starting || picking)) stop('프로그램 화면이 숨겨져 중지했습니다. 다시 확인하고 시작하세요.');
  });
  window.KorailDesktopHelper = Object.freeze({
    stop: reason => stop(typeof reason === 'string' ? reason : '프로그램에서 중지했습니다.'),
    scanTrains,
    showTrainPanel,
    reset: reason => {
      stop(typeof reason === 'string' ? reason : '화면이 바뀌었습니다. 조회 버튼과 열차를 다시 선택하세요.');
      trainReadRequested = false; setControls();
      searchConfig = null; clearSelection(); trainCandidates = []; ambiguousTrainNumbers = new Set();
      $('trainChoice').replaceChildren(new Option('화면이 바뀌었습니다. 열차 목록을 다시 읽어주세요.', ''));
      preview();
      return true;
    },
    snapshot: () => {
      validateSelectionState();
      return ({
      running, starting, count, generation, demo: isDemo,
      reservationClicks, candidates: trainCandidates.map(item => ({ train: item.identity.train, departure: item.identity.departure, arrival: item.identity.arrival, grade: item.grade })),
      selectedOption: $('trainChoice').value,
      journeyEnabled: !!journey,
      notification: $('notification').textContent,
      status: $('status').textContent,
      picking, pendingRefresh: !!pendingRefresh,
      search: searchConfig ? { label: searchConfig.label } : null,
      target: targetConfig ? {
        train: targetConfig.identity.train,
        departure: targetConfig.identity.departure,
        arrival: targetConfig.identity.arrival,
        grade: targetConfig.grade,
        autoCapable: targetConfig.autoCapable
      } : null,
      targets: targetConfigs.map(item => ({ train: item.identity.train, departure: item.identity.departure, arrival: item.identity.arrival, grade: item.grade, autoCapable: item.autoCapable })),
      auto: $('auto').checked,
      confirmed: $('confirmed').checked
      });
    }
  });
  $('pickSearch').onclick = () => beginPick('search'); $('pickSeat').onclick = () => beginPick('seat');
  $('scanTrains').onclick = requestTrainScan; $('applyTrain').onclick = applyTrain;
  $('trainChoice').onchange = () => { clearSelection(); preview(); };
  $('trainNumbers').oninput = $('trainNumbers').onchange = () => { clearSelection(); preview(); };
  $('grade').onchange = () => { trainCandidates = []; clearSelection(); $('trainChoice').replaceChildren(new Option('등급을 바꿨습니다. 목록을 다시 읽어주세요.', '')); preview(); };
  if (journey) {
    $('journey').textContent = `저장한 여정: ${journey.DepartureStation} → ${journey.ArrivalStation}\n${journey.Date} · 출발 ${journey.FromTime}–${journey.ToTime} · ${journey.Grade}${journey.TrainNumber ? ` · 열차번호 ${journey.TrainNumber}` : ''}\n코레일에서 같은 조건으로 직접 조회하세요.`;
    $('grade').value = journey.Grade;
    $('trainNumbers').value = journey.TrainNumber ?? '';
    $('confirmationText').textContent = '저장한 여정과 실제 조회 화면의 출발일·구간·인원 1명이 일치하며, 선택한 열차·좌석 등급을 직접 확인했습니다.';
    const invalid = journeyError(); if (invalid) status(invalid);
  } else $('journey').textContent = isDemo ? '연습 여정: 서울 → 부산 · 2026-10-25 · 성인 1명' : '저장한 여정 필터 없음 · 실제 조회 화면에서 원하는 열차를 선택하세요.';
  $('start').onclick = start; $('stop').onclick = () => stop('중지했습니다.'); $('sound').onclick = beep;
  $('fold').onclick = () => foldPanel(!shadow.querySelector('main').hidden);
  const selectionObserver = new MutationObserver(validateSelectionState);
  selectionObserver.observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['value', 'checked', 'selected'] });
  for (const type of ['popstate', 'hashchange']) addEventListener(type, () => {
    clearSelection(); preview();
  });
  if (!isDemo) {
    // Keep the legacy JavaScript input loop disabled. The native host owns mouse
    // refresh and booking, including stop-on-block behavior and request limits.
    $('pickSearch').hidden = true;
    $('interval').closest('.row').hidden = true;
    $('auto').closest('label').hidden = true;
    $('start').hidden = true;
    for (const node of [$('pickSearch'), $('interval').closest('.row'), $('auto').closest('label'), $('start')]) node.style.display = 'none';
    shadow.querySelector('details p').textContent = '날짜·구간을 공식 화면에서 직접 조회하고 열차와 등급을 선택하세요. 확인란을 체크한 뒤 EXE 상단 ‘선택 열차 예약 (마우스)’을 누릅니다. 반복 조회는 ‘마우스 자동 조회’에서 간격과 횟수를 정하고, 필요하면 선택 열차 예매까지 진행을 체크하세요. 가격 선택과 정차 안내 확인 후 예매를 한 번 요청하며, 실제 예약 결과가 확인될 때만 텔레그램 알림을 보냅니다. 로그인·알 수 없는 안내·접근 제한에서는 중지합니다. 결제는 표시된 기한까지 직접 진행하세요.';
    $('status').textContent = 'EXE 상단 ‘마우스 자동 조회’에서 반복 조회 설정 · 예매하려면 열차와 등급 선택';
    $('notification').textContent = '텔레그램 알림은 예약 완료 확인 후 전송합니다. 버튼 클릭만으로 성공 알림을 보내지 않습니다.';
  }
})();
