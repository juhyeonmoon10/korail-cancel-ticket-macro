(function (root, factory) {
  'use strict';
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  if (root) root.KorailSiteBlock = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';
  // Observe a displayed site notice only. Never activate controls or inspect
  // browser storage, input values, response bodies, or network requests.
  const installed = new WeakMap();
  const excluded = '#korail-one-helper,script,style,template,noscript,input,textarea,select,option,datalist,[contenteditable]:not([contenteditable="false"])';
  const exactNotice = /자동화된\s*요청으로\s*감지되어\s*차단되었습니다\.?\s*CODE\s*:\s*(-8002)(?!\d)/i;

  function officialPage(doc) {
    try {
      const url = new URL(doc.location.href);
      return url.protocol === 'https:' && !url.port &&
        ['www.korail.com', 'korail.com'].includes(url.hostname) && url.pathname.startsWith('/ticket/');
    } catch { return false; }
  }

  function skip(element, view) {
    if (element.matches(excluded) || element.hidden || element.getAttribute('aria-hidden') === 'true') return true;
    const css = view.getComputedStyle(element);
    if (css.display === 'none' || css.visibility === 'hidden' || css.visibility === 'collapse' || css.opacity === '0') return true;
    return css.display !== 'contents' && element.getClientRects().length === 0;
  }

  function read(doc) {
    if (!officialPage(doc) || doc.hidden || !doc.body || !doc.defaultView) return null;
    const parts = [];
    function visit(node) {
      if (node.nodeType === 3) { parts.push(node.nodeValue || ''); return; }
      if (node.nodeType !== 1 || skip(node, doc.defaultView)) return;
      for (const child of node.childNodes) visit(child);
      parts.push(' ');
    }
    visit(doc.body);
    const match = exactNotice.exec(parts.join(' ').replace(/\s+/g, ' ').trim());
    return match && match[1] === '-8002' ? { code: '-8002' } : null;
  }

  function install(doc, report) {
    if (!officialPage(doc) || typeof report !== 'function' || !doc.body || !doc.defaultView) return null;
    if (installed.has(doc)) return installed.get(doc);
    const view = doc.defaultView;
    let timer = null, observer = null, stopped = false, reported = false;
    function disconnect() {
      stopped = true;
      if (timer !== null) view.clearTimeout(timer);
      timer = null;
      if (observer) observer.disconnect();
      doc.removeEventListener('visibilitychange', schedule);
      view.removeEventListener('pagehide', disconnect);
    }
    function inspect() {
      timer = null;
      if (stopped || reported) return;
      let notice;
      try { notice = read(doc); } catch { return; }
      if (!notice) return;
      reported = true;
      // Disarm before reporting: a host status update cannot trigger itself.
      disconnect();
      try { report(notice.code); } catch { /* A report failure must not cause a request or retry. */ }
    }
    function schedule() {
      if (stopped || reported) return;
      // Coalesce mutations without postponing forever on an animated page.
      if (timer !== null) return;
      timer = view.setTimeout(inspect, 150);
    }
    observer = new view.MutationObserver(records => {
      if (records.some(record => {
        const element = record.target.nodeType === 1 ? record.target : record.target.parentElement;
        return element && !element.closest(excluded);
      })) schedule();
    });
    observer.observe(doc.body, { subtree: true, childList: true, characterData: true, attributes: true,
      attributeFilter: ['hidden', 'aria-hidden', 'class', 'style', 'open'] });
    doc.addEventListener('visibilitychange', schedule);
    view.addEventListener('pagehide', disconnect);
    const controller = Object.freeze({ disconnect });
    installed.set(doc, controller);
    inspect();
    return controller;
  }

  return Object.freeze({ officialPage, read, install });
});
