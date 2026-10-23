(function (root, factory) {
  'use strict';
  const api = factory(root);
  if (typeof module === 'object' && module.exports) module.exports = api;
  if (root) root.KorailQueueQuery = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function (root) {
  'use strict';
  // Read public controls only. The native host owns input, deadlines, and the
  // observed navigation/loading evidence after every submit. No site state,
  // browser storage, private APIs, DOM writes, or synthetic clicks are used.
  const stationModal = '.layerWrap.type_tranin-station-pop_wrap';
  const dateModal = '.layerWrap.type_date-pop_wrap';
  const observations = new WeakMap();
  const normal = value => String(value == null ? '' : value).replace(/\s+/g, ' ').trim();
  const text = node => normal(node && node.textContent);
  const station = value => normal(value).replace(/역$/, '');
  const all = (node, selector) => Array.from(node.querySelectorAll(selector));
  const one = (node, selector) => { const found = all(node, selector); return found.length === 1 ? found[0] : null; };
  const result = (state, message, extra) => Object.assign({ state, message }, extra || {});
  const unknown = message => result('unknown', message);
  const blocked = message => result('blocked', message, { terminal: true, retryAllowed: false });
  const contains = (parent, child) => !!parent && !!child && (parent === child || parent.contains(child));
  function observation(doc) {
    if (!doc || !doc.body || !doc.defaultView || !doc.defaultView.MutationObserver) return { resultRevision: 0, loading: false };
    let state = observations.get(doc);
    if (!state) {
      state = { revision: 0, previous: one(doc, '#tab_tab_info1'), observer: null, consume: null };
      state.consume = records => {
        const current = one(doc, '#tab_tab_info1'), roots = [state.previous, current].filter(Boolean);
        const relevant = records.some(record => {
          if (roots.some(panel => contains(panel, record.target))) return true;
          return record.type === 'childList' && [...record.addedNodes, ...record.removedNodes].some(node => roots.some(panel => contains(node, panel)));
        });
        if (relevant) state.revision++;
        state.previous = current;
      };
      state.observer = new doc.defaultView.MutationObserver(state.consume);
      state.observer.observe(doc.body, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['aria-busy'] });
      observations.set(doc, state);
    } else state.consume(state.observer.takeRecords());
    return { resultRevision: state.revision, loading: all(doc, '#container [role="progressbar"],#container [aria-busy="true"]').some(shown) };
  }
  function shown(node) {
    if (!node || !node.isConnected || node.closest('[hidden],[aria-hidden="true"],#korail-one-helper')) return false;
    const view = node.ownerDocument.defaultView;
    for (let item = node; item && item.nodeType === 1; item = item.parentElement) {
      const css = view.getComputedStyle(item);
      if (css.display === 'none' || css.visibility === 'hidden' || css.visibility === 'collapse' || css.opacity === '0') return false;
    }
    return node.getClientRects().length > 0;
  }
  function enabled(node) {
    return shown(node) && !node.disabled && !node.closest('[inert],[aria-disabled="true"],.disabled,.btn-disabled,.slick-disabled');
  }
  function onlyShown(node, selector) { const found = all(node, selector).filter(shown); return found.length === 1 ? found[0] : null; }
  function visibleText(node) {
    if (node.nodeType === 3) return node.nodeValue || '';
    if (node.nodeType !== 1 || node.matches('script,style,template,noscript,input,textarea,select,#korail-one-helper') || !shown(node)) return '';
    return Array.from(node.childNodes, visibleText).join(' ');
  }
  function dateValid(value) {
    if (!/^20\d\d-\d\d-\d\d$/.test(value || '')) return false;
    const d = new Date(value + 'T00:00:00Z');
    return Number.isFinite(d.getTime()) && d.toISOString().slice(0, 10) === value;
  }
  function expectedQuery(target) {
    if (!target || typeof target !== 'object' || Array.isArray(target) || !dateValid(target.date) ||
      !/^(?:[01]\d|2[0-3]):[0-5]\d$/.test(target.fromTime || '') || target.passengers !== 1 || target.route !== '직통' ||
      target.roundTrip !== false || target.adjacent !== false || target.combined !== false ||
      !['일반석', '유아동반석', '휠체어석', '전동휠체어석', '2층석', '자전거', '대피도우미'].includes(target.seatAttribute)) return null;
    for (const key of ['departureStation', 'arrivalStation'])
      if (typeof target[key] !== 'string' || !normal(target[key]) || target[key].length > 40 || /[<>|\d:\r\n]/.test(target[key])) return null;
    if (station(target.departureStation) === station(target.arrivalStation)) return null;
    return target;
  }
  function timeBounds(target, context) {
    const latest = context.latestDeparture == null ? target.fromTime : context.latestDeparture;
    return typeof latest === 'string' && /^(?:[01]\d|2[0-3]):[0-5]\d$/.test(latest) && latest >= target.fromTime
      ? { first: target.fromTime, last: latest } : null;
  }
  function withinTime(value, bounds) { return /^(?:[01]\d|2[0-3]):[0-5]\d$/.test(value || '') && value >= bounds.first && value <= bounds.last; }
  function coordinates(node, kind, id, doc, extra) {
    if (!enabled(node)) return blocked('다음 조회에 필요한 버튼이 비활성 상태입니다.');
    const box = node.getBoundingClientRect(), width = doc.defaultView.innerWidth, height = doc.defaultView.innerHeight;
    const x = box.left + box.width / 2, y = box.top + box.height / 2;
    if (![x, y, width, height, box.width, box.height].every(Number.isFinite) || width <= 0 || height <= 0 || box.width <= 0 || box.height <= 0)
      return unknown('다음 조회 버튼의 위치를 확인하지 못했습니다.');
    const inViewport = x >= 0 && y >= 0 && x < width && y < height;
    if (inViewport) {
      const hit = doc.elementFromPoint(x, y);
      if (!hit || hit !== node && !node.contains(hit)) return blocked('다른 화면이 다음 조회 버튼을 가리고 있습니다.');
    }
    return result('action', '다음 예약의 조회 조건을 준비합니다.', { action: Object.assign({ kind, id, x, y,
      viewportWidth: width, viewportHeight: height, inViewport }, extra || {}) });
  }
  function click(node, id, doc, extra) { return node ? coordinates(node, 'click', id, doc, extra) : unknown('필요한 조회 버튼을 하나로 식별하지 못했습니다: ' + id); }
  function parseDate(node) {
    const value = normal(node && node.value), found = /^(20\d{2}-\d{2}-\d{2})(?:\([월화수목금토일]\))?\s+((?:[01]\d|2[0-3]):[0-5]\d)$/.exec(value);
    return found && dateValid(found[1]) ? { date: found[1], fromTime: found[2] } : { date: '', fromTime: '' };
  }
  function generalQuery(doc) {
    const start = one(doc, '.ticketSrchWrap input[name="txtGoStart"]'), end = one(doc, '.ticketSrchWrap input[name="txtGoEnd"]');
    const date = one(doc, '#startDate'), people = one(doc, '.total.clear a.data.btn_pop[title="인원"]');
    const round = one(doc, 'input[type="checkbox"][name="rtYn"]');
    const adjacent = all(doc, '.optionWrap input[type="radio"][name="adjStnScdlOfrFlg"]');
    const combined = all(doc, '.optionWrap input[type="radio"][name="adjStnScdlOfrFlg2"]');
    const group = nodes => nodes.length === 2 && nodes.some(n => n.value === 'Y') && nodes.some(n => n.value === 'N') && nodes.filter(n => n.checked).length === 1;
    if (![start, end, date, people, round].every(Boolean) || !start.readOnly || !end.readOnly || !date.readOnly || !group(adjacent) || !group(combined)) return null;
    return Object.assign({ supported: false, departureStation: normal(start.value), arrivalStation: normal(end.value),
      passengers: /^총\s*1\s*명$/.test(text(people)) ? 1 : 0, route: null, seatAttribute: null,
      roundTrip: round.checked, adjacent: adjacent.find(n => n.checked).value === 'Y', combined: combined.find(n => n.checked).value === 'Y' }, parseDate(date));
  }
  function stationPlan(target, doc, modal, context) {
    const key = context.activePicker;
    if (!['departureStation', 'arrivalStation'].includes(key)) return blocked('직접 연 역 선택창은 자동으로 진행하지 않습니다. 닫고 다음 예약을 다시 시작하세요.');
    const input = one(modal, 'input[name="searchTxt"][title="역명을 입력해주세요"]');
    if (!input || input.readOnly || input.disabled) return unknown('역 검색 입력창을 확인하지 못했습니다.');
    const wanted = station(target[key]);
    if (normal(input.value) !== wanted) {
      if (doc.activeElement !== input) return click(input, 'station-focus:' + key, doc);
      return coordinates(input, 'type', 'station-type:' + key + ':' + wanted, doc, { text: wanted });
    }
    const choices = all(modal, '.sch_wrap > ul > li').filter(node => shown(node) && text(node) === wanted);
    if (choices.length > 1) return blocked('역 검색 결과가 중복되어 선택하지 않았습니다.');
    if (!choices.length) return result('waiting', '정확히 일치하는 역 검색 결과를 기다립니다.');
    return click(choices[0], 'station-select:' + key + ':' + wanted, doc, { closePicker: true });
  }
  function datePlan(target, doc, modal, context) {
    if (context.activePicker !== 'date') return blocked('직접 연 날짜 선택창은 자동으로 진행하지 않습니다.');
    const bounds = timeBounds(target, context);
    const current = onlyShown(modal, '.datepk_wrap .slick-active .datepicker');
    const label = current && one(current, 'p.date'), month = /^(20\d{2})\.\s*(\d{1,2})\.?$/.exec(text(label));
    if (!month || Number(month[2]) < 1 || Number(month[2]) > 12) return unknown('현재 달력의 연·월을 하나로 확인하지 못했습니다.');
    const wantedMonth = Number(target.date.slice(0, 4)) * 12 + Number(target.date.slice(5, 7));
    const activeMonth = Number(month[1]) * 12 + Number(month[2]);
    if (wantedMonth !== activeMonth) {
      const direction = wantedMonth > activeMonth ? 'next' : 'prev';
      return click(onlyShown(modal, '.datepk_wrap > .slick-' + direction), 'date-month:' + direction + ':' + activeMonth, doc);
    }
    const days = all(current, 'tbody a').filter(node => shown(node) && text(one(node, ':scope > span.day')) === String(Number(target.date.slice(8))));
    if (days.length !== 1) return unknown('선택할 날짜를 달력에서 하나로 확인하지 못했습니다.');
    const day = days[0];
    if (!enabled(day)) return blocked('다음 예약 날짜가 공식 달력에서 선택 불가 상태입니다.');
    if (day.getAttribute('title') !== '선택한 날짜') return click(day, 'date-day:' + target.date, doc);
    const selectedHours = all(modal, '.timeSelect li.current');
    if (selectedHours.length !== 1 || !/^\d{2}시$/.test(text(selectedHours[0]))) return unknown('선택한 조회 시작 시간을 확인하지 못했습니다.');
    const hourItems = all(modal, '.timeSelect li').filter(node => /^\d{2}시$/.test(text(node)));
    const hourValues = hourItems.map(node => Number(text(node).slice(0, 2)));
    if (hourItems.length !== 24 || new Set(hourValues).size !== 24 || hourValues.some(n => n < 0 || n > 23)) return unknown('공식 시간 선택 목록을 완전히 확인하지 못했습니다.');
    const available = hourItems.filter(node => one(node, ':scope > a[href="#none"]'));
    // A disabled span means a past hour. An offscreen anchor is still a valid
    // hour, reached using the public slider arrows rather than synthetic input.
    if (hourItems.some(node => !one(node, ':scope > a[href="#none"]') && !one(node, ':scope > span.disabled[aria-disabled="true"]')))
      return unknown('시간 선택 가능 여부를 확인하지 못했습니다.');
    const underlying = parseDate(one(doc, '#startDate'));
    let minimumHour = Number(bounds.first.slice(0, 2));
    // The picker may apply the current minute for today's current hour. Try
    // that observed hour once; if it has already produced an earlier time on
    // the exact target date, move to the next hour instead of reopening forever.
    if (!bounds.first.endsWith(':00') && underlying.date === target.date &&
      Number(underlying.fromTime.slice(0, 2)) === minimumHour && underlying.fromTime < bounds.first) minimumHour++;
    const candidates = available.map(node => Number(text(node).slice(0, 2))).filter(hour => hour >= minimumHour && hour <= Number(bounds.last.slice(0, 2))).sort((a, b) => a - b);
    if (!candidates.length) return blocked('첫 후보 열차의 출발 전까지 선택 가능한 조회 시간이 없습니다.');
    const selectedHour = candidates[0], firstAvailable = Math.min(...available.map(node => Number(text(node).slice(0, 2))));
    if (underlying.date === target.date && Number(underlying.fromTime.slice(0, 2)) === selectedHour && selectedHour === firstAvailable &&
      !underlying.fromTime.endsWith(':00') && underlying.fromTime > bounds.last)
      return blocked('공식 화면의 가장 이른 조회 시간이 첫 후보 열차의 출발 시각을 지났습니다.');
    const wantedHour = String(selectedHour).padStart(2, '0') + '시';
    if (text(selectedHours[0]) !== wantedHour) {
      const hours = all(modal, '.timeSelect li > a').filter(node => text(node) === wantedHour);
      if (hours.length !== 1) return blocked('선택할 조회 시작 시간이 없거나 이미 지났습니다.');
      const hour = hours[0];
      if (enabled(hour) && hour.getAttribute('tabindex') !== '-1') return click(hour, 'date-hour:' + wantedHour.slice(0, 2) + ':00', doc);
      const visibleHours = all(modal, '.timeSelect li > a').filter(node => enabled(node) && node.getAttribute('tabindex') === '0');
      if (!visibleHours.length) return unknown('시간 선택 목록의 현재 위치를 확인하지 못했습니다.');
      const values = visibleHours.map(node => Number(text(node).slice(0, 2))), wanted = Number(wantedHour.slice(0, 2));
      const direction = wanted < Math.min(...values) ? 'prev' : wanted > Math.max(...values) ? 'next' : '';
      if (!direction) return blocked('조회 시작 시간이 비활성 상태입니다.');
      const arrow = onlyShown(modal, '.timeSelect .slideWrap > .slick-' + direction);
      // The public picker removes its hour arrows in the compact layout. A
      // visible hour still works above; only an inaccessible hour needs resize.
      if (!arrow && doc.defaultView.innerWidth <= 1024)
        return blocked('창이 좁아 시간 이동 버튼이 숨겨졌습니다. 도우미 창을 최대화하거나 넓힌 뒤 예약 관리에서 이 항목을 다시 시작하세요.');
      return click(arrow, 'date-hours:' + direction + ':' + Math.min(...values) + '-' + Math.max(...values), doc);
    }
    const apply = all(modal, '.btn_wrap > button.btn_bn-blue').filter(node => shown(node) && text(node) === '적용');
    return click(apply.length === 1 ? apply[0] : null, 'date-apply:' + target.date + ':' + wantedHour.slice(0, 2) + ':00', doc, { closePicker: true });
  }
  function optionPlan(doc, isList, query) {
    const fields = [ ['roundTrip', 'rtYn'], ['combined', 'adjStnScdlOfrFlg2'], ['adjacent', 'adjStnScdlOfrFlg'] ];
    for (const [key, name] of fields) {
      if (!query[key]) continue;
      if (isList || key === 'roundTrip') {
        const checkbox = one(doc, 'input[type="checkbox"][name="' + name + '"]') || (isList ? one(doc, '#' + name) : null);
        const label = checkbox && checkbox.id && one(doc, 'label[for="' + checkbox.id + '"]');
        return click(label, 'option-off:' + key, doc);
      }
      const input = one(doc, '.optionWrap input[type="radio"][name="' + name + '"][value="N"]');
      const label = input && one(doc, 'label[for="' + input.id + '"]');
      if (label && !shown(label)) return click(onlyShown(doc, '.optionWrap button.btnToggle[aria-expanded="false"]'), 'options-open', doc);
      return click(label, 'option-off:' + key, doc);
    }
    return null;
  }
  function dropdown(doc, selector, wanted, id) {
    const group = one(doc, selector), selected = group && one(group, '.selected > a');
    if (!selected) return unknown('조회 선택 메뉴를 확인하지 못했습니다: ' + id);
    const matches = all(group, '.options > ul > li > a').filter(node => shown(node) && text(node) === wanted);
    if (matches.length > 1) return blocked('조회 선택 메뉴가 중복되었습니다.');
    // The observed public list component calls handleSearch from both option
    // callbacks, so selecting an option is itself a request. The host must bind
    // its result evidence before another edit or explicit query is permitted.
    return matches.length ? coordinates(matches[0], 'submit-query', id + '-select:' + wanted, doc) : click(selected, id + '-open', doc);
  }
  function observe(target, doc, context) {
    target = expectedQuery(target); context = context || {};
    if (!target) return blocked('다음 예약의 날짜·구간·1명·직통 조회 조건을 확인하세요.');
    const bounds = timeBounds(target, context);
    if (!bounds) return blocked('다음 예약의 조회 시작 시간과 첫 후보 출발 시간을 확인하세요.');
    if (!doc || !doc.body || !root.KorailSiteAdapter || !root.KorailHelperCore) return unknown('다음 조회 분석기가 준비되지 않았습니다.');
    if (doc.hidden) return blocked('조회 화면이 숨겨졌습니다.');
    const url = new URL(doc.location.href), fixture = url.hostname === 'korail-helper.invalid' && url.pathname === '/card-demo.html';
    const isList = url.pathname === '/ticket/search/list' || fixture, isGeneral = url.pathname === '/ticket/search/general';
    if (url.protocol !== 'https:' || url.port || !fixture && !['www.korail.com', 'korail.com'].includes(url.hostname) || !isList && !isGeneral)
      return blocked('확인한 코레일 조회 화면이 아닙니다. 로그인·인증 화면은 직접 확인하세요.');
    const pageText = visibleText(doc.body), reason = root.KorailHelperCore.blocker(pageText);
    if (reason) {
      const stopped = blocked(reason), code = /CODE\s*:\s*(-\d{1,6})/i.exec(pageText);
      if (code) stopped.blockCode = code[1];
      return stopped;
    }
    if (all(doc, 'input[type="password"],input[autocomplete="one-time-code"],iframe[src*="captcha"],iframe[src*="challenge"]').some(shown))
      return blocked('로그인 또는 인증 화면을 직접 확인하세요.');
    const pickers = all(doc, stationModal + ',' + dateModal).filter(shown);
    if (pickers.length > 1) return blocked('여러 선택창이 함께 열려 자동 진행을 중지했습니다.');
    const modal = pickers[0];
    const dialogs = all(doc, '.layerWrap,[role="dialog"],[role="alertdialog"],[aria-modal="true"],.modal.show,.modal.on,.modal[open],.popup.show,.popup.on,.modal_wrap,.modal-wrap,.popup_wrap,.pop_wrap,.popWrap').filter(shown);
    if (dialogs.some(node => !modal || !node.contains(modal) && !modal.contains(node))) return blocked('다른 안내창이 열려 다음 조회를 중지합니다.');
    if (modal) return modal.matches(stationModal) ? stationPlan(target, doc, modal, context) : datePlan(target, doc, modal, context);
    const query = isList ? root.KorailSiteAdapter.query(doc) : generalQuery(doc);
    if (!query || isList && (!one(doc, '#labelstart') || !one(doc, '#labelend') || !one(doc, '#startDate') || !one(doc, '#labelple') || !query.route || !query.seatAttribute))
      return unknown('현재 조회 조건의 화면 구조를 확인하지 못했습니다.');
    const rawRows = isList ? all(doc, root.KorailSiteAdapter.cardSelector).filter(shown) : [];
    const fingerprint = JSON.stringify({ query, rows: rawRows.map(node => ({ identity: root.KorailSiteAdapter.identity(node), text: text(node) })) });
    const enrich = plan => Object.assign(plan, { query, fingerprint, resultCount: rawRows.length, pickerClosed: !!context.activePicker });
    if (all(doc, '#container [role="progressbar"],#container [aria-busy="true"]').some(shown)) return enrich(result('waiting', '조회 결과가 표시되기를 기다립니다.', { loading: true }));
    const submitSelector = isList ? '.ticketSrchWrap .btnWrap.btn_box > button.btn_bn-blue' : '.btnWrap.type_bg > button.btn_lookup';
    const submitControls = all(doc, submitSelector).filter(shown);
    if (submitControls.some(node => node.disabled || node.getAttribute('aria-disabled') === 'true') || isList && !rawRows.length)
      return enrich(result('waiting', '진행 중인 조회 결과가 준비되기를 기다립니다.', { loading: true }));
    if (all(doc, '.ticket_reserv_wrap,' + root.KorailSiteAdapter.cardSelector + ' > .tck_inner > .price_box.active').some(shown)) return enrich(blocked('선택 좌석 또는 예매창이 열려 다음 조회를 중지합니다.'));
    if (query.passengers !== 1) return enrich(blocked('다음 예약은 인원 총 1명 조회만 지원합니다. 공식 화면에서 인원을 확인하세요.'));
    const options = optionPlan(doc, isList, query);
    if (options) return enrich(options);
    for (const key of ['departureStation', 'arrivalStation']) {
      if (station(query[key]) === station(target[key])) continue;
      let button;
      if (isList) button = onlyShown(doc, 'a.btn_pop-openStationPop[title="' + (key === 'departureStation' ? '출발역 선택' : '도착역 선택') + '"]');
      else {
        const input = one(doc, '.ticketSrchWrap input[name="' + (key === 'departureStation' ? 'txtGoStart' : 'txtGoEnd') + '"]');
        const wrapper = input && input.closest(key === 'departureStation' ? '.start' : '.end');
        button = wrapper && onlyShown(wrapper, ':scope > a.btn_pop-open');
      }
      return enrich(click(button, 'station-open:' + key, doc, { picker: key }));
    }
    if (query.date !== target.date || !withinTime(query.fromTime, bounds)) {
      const date = one(doc, '#startDate');
      const button = isList ? onlyShown(doc, '#dateinput__input-wrapper') : date && onlyShown(date.parentElement, ':scope > a.btn_pop.btn_d-day');
      return enrich(click(button, 'date-open:' + query.date + ':' + query.fromTime, doc, { picker: 'date' }));
    }
    if (isList && query.route !== target.route) return enrich(dropdown(doc, '.ticketSrchWrap .select.select02', target.route, 'route'));
    if (isList && query.seatAttribute !== target.seatAttribute) return enrich(dropdown(doc, '.ticketSrchWrap .select.select01', target.seatAttribute, 'seat-attribute'));
    if (isList) {
      const refresh = root.KorailSiteAdapter.activeRefresh(doc), allTab = one(doc, '#btn_tab_info1 > button');
      if (!refresh || !allTab) return enrich(unknown('열차 종류 선택 버튼을 확인하지 못했습니다.'));
      if (refresh.element !== allTab) return enrich(coordinates(allTab, 'submit-query', 'query-all-trains', doc));
      if (context.submitted === true) {
        const rows = root.KorailSiteAdapter.cards(doc), keys = rows.map(node => root.KorailSiteAdapter.identity(node));
        if (!rows.length) return enrich(result('waiting', '조회 결과에 열차가 표시되기를 기다립니다.', { loading: true }));
        if (!query.supported || rows.length !== rawRows.length || keys.some(key => !key) || new Set(keys.map(key => key.key)).size !== rows.length)
          return enrich(blocked('조회 결과의 구간 또는 열차 식별이 일치하지 않습니다.'));
        return enrich(result('ready', '다음 예약의 조회 조건과 열차 목록을 확인했습니다.', { count: rows.length, loading: false }));
      }
    }
    if (context.submitted === true) return enrich(result('waiting', '요청한 열차 조회 화면을 기다립니다.', { loading: true }));
    const submit = submitControls.filter(node => text(node).replace(/\s/g, '') === '열차조회');
    return enrich(submit.length === 1 ? coordinates(submit[0], 'submit-query', 'query-submit:' + (isList ? 'list' : 'general'), doc) : unknown('공식 열차 조회 버튼을 하나로 확인하지 못했습니다.'));
  }
  function read(target, suppliedDocument, context) {
    try {
      const doc = suppliedDocument || root.document, before = observation(doc), plan = observe(target, doc, context);
      return Object.assign({}, before, plan);
    }
    catch { return unknown('화면 구조가 바뀌어 다음 조회를 확인하지 못했습니다.'); }
  }
  return Object.freeze({ read, observation });
});
