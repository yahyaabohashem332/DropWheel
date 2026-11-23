(function () {
  'use strict';
  var doc = document, root = doc.documentElement, body = doc.body;
  var data;
  try { data = JSON.parse(doc.getElementById('data').textContent); } catch (e) { return; }
  var W = data.w, rtl = root.getAttribute('dir') === 'rtl';
  body.classList.remove('nojs');
  body.classList.add('js');
  if ('scrollRestoration' in history) history.scrollRestoration = 'manual';

  // ───── what is kept in this browser ─────
  var memory = {};
  function load(key) {
    try { var v = window.localStorage.getItem(key); return v ? JSON.parse(v) : null; } catch (e) { return memory[key] || null; }
  }
  function save(key, value) {
    memory[key] = value;
    try { window.localStorage.setItem(key, JSON.stringify(value)); } catch (e) { /* a page that may keep nothing still works */ }
  }
  var KEY = 'dropwheel-study:' + body.getAttribute('data-id'), UI = 'dropwheel-study:ui';
  function fresh() { return { read: {}, q: {}, c: {}, a: {}, s: {}, best: {}, last: 0 }; }
  var state = load(KEY) || fresh();
  ['read', 'q', 'c', 'a', 'best'].forEach(function (k) { if (!state[k] || typeof state[k] !== 'object') state[k] = {}; });
  var ui = load(UI) || {};
  if (ui.answers === undefined) ui.answers = true;
  function keep() { save(KEY, state); }

  // ───── little helpers ─────
  function $(s, el) { return (el || doc).querySelector(s); }
  function $$(s, el) { return Array.prototype.slice.call((el || doc).querySelectorAll(s)); }
  function h(tag, props, kids) {
    var el = doc.createElement(tag);
    if (props) Object.keys(props).forEach(function (k) {
      var v = props[k];
      if (v === null || v === undefined || v === false) return;
      if (k === 'text') el.textContent = v;
      else if (k === 'class') el.className = v;
      else if (k.slice(0, 2) === 'on') el.addEventListener(k.slice(2), v);
      else el.setAttribute(k, v === true ? '' : v);
    });
    (kids || []).forEach(function (c) { if (c !== null && c !== undefined) el.appendChild(typeof c === 'string' ? doc.createTextNode(c) : c); });
    return el;
  }
  function fmt(s) {
    var args = arguments;
    return String(s).replace(/\{(\d+)\}/g, function (m, i) { return args[+i + 1] === undefined ? m : args[+i + 1]; });
  }
  // Text as compared: lower case, without accents or Arabic vowel marks, each Arabic letter in one form.
  function fold(s) {
    return String(s || '').normalize('NFD').replace(/[̀-ًͯ-ٰٟـ‌]/g, '').toLowerCase()
      .replace(/ٱ/g, 'ا').replace(/ة/g, 'ه').replace(/[ىی]/g, 'ي').replace(/ک/g, 'ك').replace(/ı/g, 'i');
  }
  function shuffle(list) {
    for (var i = list.length - 1; i > 0; i--) { var j = Math.floor(Math.random() * (i + 1)); var t = list[i]; list[i] = list[j]; list[j] = t; }
    return list;
  }
  var letters = String(W.letters || 'A B C D').split(' ').filter(Boolean);
  function letter(i) { return letters[i] || String.fromCharCode(65 + i); }
  var say = $('#say');
  function announce(text) { say.textContent = ''; setTimeout(function () { say.textContent = text; }, 40); }
  function flash(el) { el.classList.remove('flash'); void el.offsetWidth; el.classList.add('flash'); }
  var partsByN = {};
  data.parts.forEach(function (p) { partsByN[p.n] = p; });
  function chapterOf(n) { var p = partsByN[n]; return p && p.c ? data.chapters[p.c - 1] : null; }
  function inScope(n, scope) {
    if (!scope || scope === 'all') return true;
    if (scope.charAt(0) === 'c') { var ch = data.chapters[+scope.slice(1) - 1]; return !!ch && ch.p.indexOf(n) >= 0; }
    if (scope.charAt(0) === 'p') return n === +scope.slice(1);
    return true;
  }
  function scopeChoice(selected, onchange) {
    var select = h('select', { onchange: function () { onchange(select.value); } }, [h('option', { value: 'all', text: W.wholeFile })]);
    data.chapters.forEach(function (c) { select.appendChild(h('option', { value: 'c' + c.n, text: c.t })); });
    select.value = selected && $('option[value="' + selected + '"]', select) ? selected : 'all';
    return h('label', null, [W.from + ' ', select]);
  }

  // ───── text size and light or dark ─────
  function applyPrefs() {
    root.style.setProperty('--size', ui.size || 1);
    if (ui.theme) root.setAttribute('data-theme', ui.theme); else root.removeAttribute('data-theme');
    body.classList.toggle('noanswers', !ui.answers);
  }
  applyPrefs();
  $$('.size').forEach(function (b) {
    b.addEventListener('click', function () {
      ui.size = Math.round(Math.min(1.45, Math.max(0.85, (ui.size || 1) + 0.1 * +b.getAttribute('data-d'))) * 100) / 100;
      save(UI, ui); applyPrefs();
    });
  });
  var theme = $('.theme');
  if (theme) theme.addEventListener('click', function () {
    // The other look from the one shown, whatever the system's is.
    var dark = ui.theme ? ui.theme === 'dark' : !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
    ui.theme = dark ? 'light' : 'dark';
    save(UI, ui); applyPrefs(); tellFrames();
  });

  // ───── the contents on a narrow screen ─────
  var menu = $('.menu');
  function closeMenu() { body.classList.remove('menu-open'); if (menu) menu.setAttribute('aria-expanded', 'false'); }
  if (menu) menu.addEventListener('click', function (e) {
    e.stopPropagation();
    var open = body.classList.toggle('menu-open');
    menu.setAttribute('aria-expanded', String(open));
  });
  doc.addEventListener('click', function (e) {
    if (body.classList.contains('menu-open') && !e.target.closest('#toc') && !e.target.closest('.menu')) closeMenu();
  });

  // ───── views ─────
  var views = {};
  $$('.view').forEach(function (v) { views[v.id] = v; });
  var current = null;
  function route() {
    var hash = decodeURIComponent(location.hash.slice(1)) || 'home', name = hash, anchor = null, scope = null;
    if (/^test-/.test(hash)) { name = 'test'; scope = hash.slice(5); }
    else if (/^practice-/.test(hash)) { name = 'practice'; scope = hash.slice(9); }
    else if (/^a[0-9a-f]{10}$/.test(hash)) { var act = actsById[hash]; if (act) { name = 'p' + act.p; anchor = hash; } }
    else if (/^s[0-9a-f]{10}$/.test(hash)) { var sc = scenesById[hash]; if (sc) { name = 'p' + sc.p; anchor = hash; } }
    else if (/^d\d+-\d+$/.test(hash)) { name = 'p' + hash.slice(1).split('-')[0]; anchor = hash; }
    else if (/^t\d+$/.test(hash)) { name = 'terms'; anchor = hash; }
    else if (/^p\d+-\d+$/.test(hash)) { name = hash.split('-')[0]; anchor = hash; }
    if (!views[name]) name = 'home';
    show(name, anchor, scope);
  }
  function show(name, anchor, scope) {
    if (current) current.classList.remove('on');
    current = views[name];
    current.classList.add('on');
    closeMenu(); hidePop(); closeHits();
    $$('#toc a').forEach(function (a) { a.removeAttribute('aria-current'); });
    var link = $('#toc a[href="#' + name + '"]');
    if (link) link.setAttribute('aria-current', 'page');
    if (name === 'cards') Trainer.render();
    if (name === 'test') Exam.render(scope);
    if (name === 'practice') Practice.render(scope);
    if (name === 'progress') Stand.render();
    if (name === 'mistakes') Mistakes.render();
    if (name === 'home') resume();
    if (/^p\d+$/.test(name)) { state.last = +name.slice(1); keep(); }
    var target = anchor && doc.getElementById(anchor);
    if (target) { target.scrollIntoView({ block: 'start' }); flash(target); }
    else {
      window.scrollTo(0, 0);
      // A browser that has just opened the page at its address's part may scroll there again once it is laid out.
      if (window.requestAnimationFrame) window.requestAnimationFrame(function () { window.scrollTo(0, 0); });
    }
    var title = $('h1', current);
    if (title && !target) { try { title.focus({ preventScroll: true }); } catch (e) { title.focus(); } }
    // The part's line in the contents in sight, without moving the page.
    var toc = $('#toc');
    if (link && toc && toc.scrollHeight > toc.clientHeight && (link.offsetTop < toc.scrollTop || link.offsetTop > toc.scrollTop + toc.clientHeight - 48))
      toc.scrollTop = Math.max(0, link.offsetTop - toc.clientHeight / 3);
  }
  window.addEventListener('hashchange', route);
  function resume() {
    var begin = $('.begin a');
    var last = partsByN[state.last];
    if (!begin || !last) return;
    begin.setAttribute('href', '#p' + last.n);
    begin.firstChild.nodeValue = fmt(W.resume, last.t);
  }

  // ───── parts studied ─────
  function refreshMarks() {
    var total = data.parts.length, done = data.parts.filter(function (p) { return state.read[p.n]; }).length;
    $$('a[data-p]').forEach(function (a) { a.classList.toggle('done', !!state.read[a.getAttribute('data-p')]); });
    $$('.mark').forEach(function (b) {
      var on = !!state.read[b.getAttribute('data-p')];
      b.setAttribute('aria-pressed', String(on));
      $('span', b).textContent = on ? W.studied : W.markStudied;
    });
    $$('.part').forEach(function (v) { var s = $('.state', v); if (s) s.textContent = state.read[v.getAttribute('data-p')] ? '✓ ' + W.studied : ''; });
    $$('.tile[data-c]').forEach(function (t) {
      var ch = data.chapters[+t.getAttribute('data-c') - 1];
      var d = ch.p.filter(function (n) { return state.read[n]; }).length;
      $('.bar span', t).style.width = (100 * d / ch.p.length) + '%';
    });
    $$('.tile[data-p]').forEach(function (t) { $('.bar span', t).style.width = state.read[t.getAttribute('data-p')] ? '100%' : '0'; });
    $$('.part .act').forEach(function (el) { var act = actsById[el.getAttribute('data-a')]; el.classList.toggle('done', !!act && solved(act)); });
    var meter = $('.meter');
    if (meter) {
      var pct = total ? Math.round(100 * done / total) : 0;
      meter.style.setProperty('--done', pct);
      $('.num', meter).textContent = pct + '%';
      meter.setAttribute('title', W.partsStudied + ': ' + done + '/' + total);
    }
  }

  // ───── questions in the parts ─────
  function answer(q, choice, record) {
    var right = +q.getAttribute('data-a');
    $$('.opt', q).forEach(function (b, i) {
      b.disabled = true;
      b.classList.toggle('right', i === right);
      b.classList.toggle('wrong', i === choice && i !== right);
      b.setAttribute('aria-pressed', String(i === choice));
    });
    q.classList.add('answered');
    var verdict = $('.verdict', q);
    verdict.textContent = choice === right ? W.right : W.wrong;
    verdict.className = 'verdict ' + (choice === right ? 'good' : 'bad');
    $('.key', q).open = true;
    if (record) {
      state.q[q.getAttribute('data-q')] = { c: choice, r: choice === right ? 1 : 0 };
      keep(); countWrongs();
      announce(verdict.textContent);
    }
  }
  function unanswer(q) {
    $$('.opt', q).forEach(function (b) { b.disabled = false; b.classList.remove('right', 'wrong'); b.removeAttribute('aria-pressed'); });
    q.classList.remove('answered');
    $('.key', q).open = false;
    delete state.q[q.getAttribute('data-q')];
    keep();
    var first = $('.opt', q);
    if (first) first.focus();
  }

  // ───── what a click does ─────
  doc.addEventListener('click', function (e) {
    var t = e.target;
    var opt = t.closest('.part .q .opt');
    if (opt && !opt.disabled) { answer(opt.closest('.q'), +opt.getAttribute('data-i'), true); return; }
    var redo = t.closest('.part .q .redo');
    if (redo) { e.preventDefault(); unanswer(redo.closest('.q')); return; }
    var card = t.closest('.part .card');
    if (card) { card.classList.toggle('flipped'); return; }
    var mark = t.closest('.mark');
    if (mark) {
      var n = mark.getAttribute('data-p');
      if (state.read[n]) delete state.read[n]; else state.read[n] = Date.now();
      keep(); refreshMarks();
      announce(state.read[n] ? W.studied : W.markStudied);
      return;
    }
    var term = t.closest('.term');
    if (term) { e.preventDefault(); if (popFor === term) hidePop(); else showPop(term); return; }
    if (!t.closest('#pop')) hidePop();
  });

  // ───── terms ─────
  var pop = $('#pop'), terms = {}, popFor = null, popTimer = 0;
  data.terms.forEach(function (t) { terms[t.id] = t; });
  $$('.term').forEach(function (el) { el.removeAttribute('title'); el.setAttribute('role', 'button'); el.setAttribute('aria-describedby', 'pop'); });
  function showPop(el) {
    var t = terms[el.getAttribute('data-t')];
    if (!t) return;
    clearTimeout(popTimer);
    pop.textContent = '';
    pop.appendChild(h('strong', { text: t.t }));
    pop.appendChild(h('span', { text: t.m }));
    pop.appendChild(h('a', { class: 'src', href: '#' + t.id, text: W.terms + ' · ' + t.w }));
    pop.hidden = false;
    var r = el.getBoundingClientRect(), pw = pop.offsetWidth, ph = pop.offsetHeight;
    var x = rtl ? r.right - pw : r.left;
    x = Math.max(8, Math.min(x, window.innerWidth - pw - 8));
    var y = r.bottom + 8;
    if (y + ph > window.innerHeight - 8 && r.top - ph - 8 > 0) y = r.top - ph - 8;
    pop.style.left = (x + window.scrollX) + 'px';
    pop.style.top = (y + window.scrollY) + 'px';
    popFor = el;
  }
  function hidePop() { if (pop) pop.hidden = true; popFor = null; }
  var hover = window.matchMedia && window.matchMedia('(hover: hover)').matches;
  if (hover) {
    doc.addEventListener('mouseover', function (e) {
      var term = e.target.closest && e.target.closest('.term');
      if (term && term !== popFor) { clearTimeout(popTimer); popTimer = setTimeout(function () { showPop(term); }, 150); }
      else if (!term && !e.target.closest('#pop') && popFor) { clearTimeout(popTimer); popTimer = setTimeout(hidePop, 250); }
    });
  }
  doc.addEventListener('focusin', function (e) { if (e.target.classList && e.target.classList.contains('term')) showPop(e.target); });
  doc.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') { hidePop(); closeHits(); closeMenu(); }
    if ((e.key === 'Enter' || e.key === ' ') && e.target.classList && e.target.classList.contains('term')) { e.preventDefault(); if (popFor === e.target) hidePop(); else showPop(e.target); }
  });
  window.addEventListener('resize', hidePop);

  // ───── the glossary ─────
  var filter = $('.filter');
  if (filter) filter.addEventListener('input', function () {
    var f = fold(filter.value.trim());
    $$('.g').forEach(function (g) { g.hidden = !!f && fold(g.textContent).indexOf(f) < 0; });
  });

  // ───── finding anything ─────
  var q = $('#q'), hits = $('#hits'), index = null, chosen = -1;
  function buildIndex() {
    index = [];
    data.parts.forEach(function (p) {
      var v = views['p' + p.n];
      if (!v) return;
      // Block by block, so the words of one paragraph don't run into the next.
      var raw = $$('h1, h2, h3, h4, p, li, dt, dd, th, td, blockquote, .card .face', v).map(function (e) { return e.textContent; }).join(' ').replace(/\s+/g, ' ');
      index.push({ href: '#p' + p.n, title: p.t, raw: raw, text: fold(raw), titleText: fold(p.t) });
    });
    data.terms.forEach(function (t) { index.push({ href: '#' + t.id, title: t.t, raw: t.m, text: fold(t.t + ' ' + t.m), titleText: fold(t.t) }); });
  }
  // The sentence of a text a word is in, with the word marked: compared folded, shown as written.
  function snippet(raw, word) {
    var sentences = raw.replace(/([.!?؟۔])\s+/g, '$1\u0001').split('\u0001');
    for (var i = 0; i < sentences.length; i++) {
      var s = sentences[i], chars = Array.from(s), folded = '', map = [];
      chars.forEach(function (ch, k) { var f = fold(ch); for (var j = 0; j < f.length; j++) { folded += f[j]; map.push(k); } });
      var at = folded.indexOf(word);
      if (at < 0) continue;
      var from = map[at], to = map[at + word.length - 1] + 1;
      var start = Math.max(0, from - 60), end = Math.min(chars.length, to + 80);
      return h('span', null, [(start > 0 ? '…' : '') + chars.slice(start, from).join(''), h('mark', { text: chars.slice(from, to).join('') }), chars.slice(to, end).join('') + (end < chars.length ? '…' : '')]);
    }
    return h('span', { text: raw.slice(0, 120) });
  }
  function closeHits() { if (hits) { hits.hidden = true; chosen = -1; } }
  function findNow() {
    var words = fold(q.value.trim()).split(/\s+/).filter(function (w) { return w.length > 1; });
    hits.textContent = '';
    if (!words.length) { closeHits(); return; }
    if (!index) buildIndex();
    var found = index.filter(function (e) { return words.every(function (w) { return e.text.indexOf(w) >= 0; }); })
      .sort(function (a, b) { return (words.every(function (w) { return b.titleText.indexOf(w) >= 0; }) ? 1 : 0) - (words.every(function (w) { return a.titleText.indexOf(w) >= 0; }) ? 1 : 0); })
      .slice(0, 12);
    if (!found.length) hits.appendChild(h('p', { text: W.noResults }));
    found.forEach(function (e) {
      hits.appendChild(h('a', { href: e.href, role: 'option', 'data-word': words[0] }, [h('strong', { text: e.title }), snippet(e.raw, words[0])]));
    });
    hits.hidden = false;
    chosen = -1;
  }
  var findTimer = 0;
  if (q) {
    q.addEventListener('input', function () { clearTimeout(findTimer); findTimer = setTimeout(findNow, 140); });
    q.addEventListener('keydown', function (e) {
      var links = $$('a', hits);
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        if (!links.length) return;
        chosen = (chosen + (e.key === 'ArrowDown' ? 1 : -1) + links.length) % links.length;
        links.forEach(function (l, i) { l.classList.toggle('on', i === chosen); });
      } else if (e.key === 'Enter' && links.length) {
        e.preventDefault();
        go(links[Math.max(0, chosen)]);
      }
    });
    q.addEventListener('focus', function () { if (q.value.trim()) findNow(); });
  }
  if (hits) hits.addEventListener('click', function (e) { var a = e.target.closest('a'); if (a) { e.preventDefault(); go(a); } });
  doc.addEventListener('click', function (e) { if (hits && !e.target.closest('.find')) closeHits(); });
  // To what was found, and the first line of it that says the word lit up.
  function go(a) {
    var word = a.getAttribute('data-word');
    closeHits();
    q.blur();
    if (location.hash === a.getAttribute('href')) route(); else location.hash = a.getAttribute('href');
    setTimeout(function () {
      var place = $$('p, li, dd, dt, .ask, h3, h4', current).filter(function (el) { return fold(el.textContent).indexOf(word) >= 0; })[0];
      if (place) { place.scrollIntoView({ block: 'center' }); flash(place); }
    }, 60);
  }
  doc.addEventListener('keydown', function (e) {
    var typing = e.target.closest && e.target.closest('input, textarea, select');
    if (e.key === '/' && !typing && q) { e.preventDefault(); q.focus(); }
  });

  // ───── cards one by one ─────
  var Trainer = {
    scope: 'all', queue: null, at: null,
    render: function () {
      var slot = $('#cards .slot');
      if (!slot) return;
      var self = this;
      if (!this.queue) this.fill(false);
      this.face = null;
      slot.textContent = '';
      var all = data.cards.filter(function (c) { return inScope(c.p, self.scope); });
      var known = all.filter(function (c) { return state.c[c.id]; }).length;
      var tools = h('div', { class: 'tools' }, [
        scopeChoice(this.scope, function (v) { self.scope = v; self.fill(false); self.render(); }),
        h('span', { class: 'grow' }),
        h('button', { type: 'button', class: 'btn quiet', text: W.shuffle, onclick: function () { self.fill(true); self.render(); } }),
        h('button', { type: 'button', class: 'btn quiet', text: W.startOver, onclick: function () { all.forEach(function (c) { delete state.c[c.id]; }); keep(); self.fill(false); self.render(); } }),
      ]);
      slot.appendChild(tools);
      slot.appendChild(h('div', { class: 'label', text: fmt(W.known, known, all.length) }));
      var bar = h('div', { class: 'meterbar' }, [h('span')]);
      bar.firstChild.style.width = (all.length ? 100 * known / all.length : 0) + '%';
      slot.appendChild(bar);
      var card = this.queue[0];
      if (!card) { slot.appendChild(h('p', { class: 'score', text: W.allKnown })); return; }
      var face = h('button', { type: 'button', class: 'card', 'aria-label': W.turn }, [
        h('span', { class: 'face front' }, [h('span', { class: 't', text: card.f })]),
        h('span', { class: 'face back' }, [h('span', { class: 't', text: card.b }), h('span', { class: 'src', text: card.w })]),
      ]);
      face.addEventListener('click', function () { face.classList.toggle('flipped'); });
      slot.appendChild(h('div', { class: 'stage' }, [face]));
      slot.appendChild(h('div', { class: 'grade' }, [
        h('button', { type: 'button', class: 'btn again', text: W.notYet + ' (1)', onclick: function () { self.grade(false); } }),
        h('button', { type: 'button', class: 'btn knew', text: W.knewIt + ' (2)', onclick: function () { self.grade(true); } }),
      ]));
      slot.appendChild(h('p', { class: 'hint', text: W.cardsHint }));
      this.face = face;
    },
    fill: function (mixed) {
      var self = this;
      var left = data.cards.filter(function (c) { return inScope(c.p, self.scope) && !state.c[c.id]; });
      this.queue = mixed ? shuffle(left) : left;
    },
    grade: function (knew) {
      var card = this.queue.shift();
      if (!card) return;
      if (knew) state.c[card.id] = 1;
      else { delete state.c[card.id]; this.queue.splice(Math.min(3, this.queue.length), 0, card); }
      keep();
      this.render();
      if (this.face) this.face.focus();
    },
  };
  doc.addEventListener('keydown', function (e) {
    if (!current || current.id !== 'cards' || !Trainer.face || (e.target.closest && e.target.closest('input, select, textarea'))) return;
    if (e.key === ' ' || e.key === 'Enter') { if (e.target === Trainer.face) return; e.preventDefault(); Trainer.face.classList.toggle('flipped'); }
    else if (e.key === '1') Trainer.grade(false);
    else if (e.key === '2') Trainer.grade(true);
  });

  // ───── testing yourself ─────
  var Exam = {
    scope: 'all', count: 10, list: null, at: 0, right: 0, wrong: [],
    render: function (scope) {
      if (scope) { this.scope = scope; this.list = null; }
      if (this.list) return this.question();
      this.setup();
    },
    pool: function () { var self = this; return data.quiz.filter(function (x) { return inScope(x.p, self.scope); }); },
    setup: function () {
      var self = this, slot = $('#test .slot');
      slot.textContent = '';
      var count = h('select', { onchange: function () { self.count = +count.value; } }, [10, 20, 0].map(function (n) { return h('option', { value: n, text: n ? String(n) : W.all }); }));
      count.value = String(this.count);
      slot.appendChild(h('div', { class: 'tools' }, [
        scopeChoice(this.scope, function (v) { self.scope = v; self.setup(); }),
        h('label', null, [W.howMany + ' ', count]),
      ]));
      var pool = this.pool();
      if (!pool.length) { slot.appendChild(h('p', { class: 'empty', text: W.noQuestions })); return; }
      if (state.best[this.scope] !== undefined) slot.appendChild(h('p', { class: 'label', text: fmt(W.best, state.best[this.scope]) }));
      slot.appendChild(h('p', null, [h('button', { type: 'button', class: 'btn primary', text: W.start, onclick: function () { self.begin(); } })]));
    },
    begin: function () {
      var pool = shuffle(this.pool().slice());
      this.list = this.count ? pool.slice(0, this.count) : pool;
      this.at = 0; this.right = 0; this.wrong = [];
      this.question();
    },
    question: function () {
      var self = this, slot = $('#test .slot'), x = this.list[this.at];
      slot.textContent = '';
      if (!x) return this.result();
      slot.appendChild(h('p', { class: 'label', text: fmt(W.questionOf, this.at + 1, this.list.length) }));
      var bar = h('div', { class: 'meterbar' }, [h('span')]);
      bar.firstChild.style.width = (100 * this.at / this.list.length) + '%';
      slot.appendChild(bar);
      var box = h('div', { class: 'q' }, [h('p', { class: 'ask', text: x.q })]);
      var opts = h('ol', { class: 'opts' });
      var after = h('div', { class: 'after', hidden: true });
      x.o.forEach(function (o, i) {
        var b = h('button', { type: 'button', class: 'opt' }, [h('span', { class: 'l', text: letter(i) }), h('span', { class: 't', text: o })]);
        b.addEventListener('click', function () { self.choose(x, i, opts, after); });
        opts.appendChild(h('li', null, [b]));
      });
      box.appendChild(opts);
      box.appendChild(after);
      slot.appendChild(box);
      var first = $('.opt', box);
      if (first) first.focus();
    },
    choose: function (x, i, opts, after) {
      var self = this, good = i === x.a;
      $$('.opt', opts).forEach(function (b, k) { b.disabled = true; b.classList.toggle('right', k === x.a); b.classList.toggle('wrong', k === i && !good); });
      if (good) this.right++; else this.wrong.push(x);
      state.q[x.id] = { c: i, r: good ? 1 : 0 };
      keep(); countWrongs();
      after.hidden = false;
      after.appendChild(h('p', { class: 'verdict ' + (good ? 'good' : 'bad'), text: good ? W.right : W.wrong }));
      after.appendChild(h('p', null, [h('strong', { text: letter(x.a) + ') ' + x.o[x.a] }), x.y ? ' — ' + x.y : '']));
      after.appendChild(h('p', null, [h('a', { class: 'src', href: '#p' + x.p, text: x.w + ' · ' + W.readPart })]));
      if (x.x) after.appendChild(h('blockquote', { class: 'from', dir: 'auto' }, [h('span', { class: 'label', text: W.fromOriginal }), ' ', h('q', { text: x.x })]));
      var last = this.at + 1 >= this.list.length;
      var next = h('button', { type: 'button', class: 'btn primary', text: last ? W.finish : W.next, onclick: function () { self.at++; self.question(); } });
      after.appendChild(h('p', null, [next]));
      announce(good ? W.right : W.wrong);
      next.focus();
    },
    result: function () {
      var self = this, slot = $('#test .slot'), pct = Math.round(100 * this.right / this.list.length);
      if (state.best[this.scope] === undefined || pct > state.best[this.scope]) { state.best[this.scope] = pct; keep(); }
      slot.appendChild(h('div', { class: 'stat score' }, [h('div', { class: 'big', text: pct + '%' }), h('div', { text: fmt(W.score, this.right, this.list.length) })]));
      if (this.wrong.length) {
        slot.appendChild(h('h2', { text: W.mistakes }));
        this.wrong.forEach(function (x) {
          slot.appendChild(h('div', { class: 'q' }, [
            h('p', { class: 'ask', text: x.q }),
            h('p', null, [h('strong', { text: letter(x.a) + ') ' + x.o[x.a] }), x.y ? ' — ' + x.y : '']),
            h('a', { class: 'src', href: '#p' + x.p, text: x.w + ' · ' + W.readPart }),
          ]));
        });
      }
      slot.appendChild(h('p', { class: 'row' }, [
        h('button', { type: 'button', class: 'btn primary', text: W.tryAgain, onclick: function () { self.begin(); } }),
        h('button', { type: 'button', class: 'btn quiet', text: W.startOver, onclick: function () { self.list = null; self.setup(); } }),
      ]));
      announce(fmt(W.score, this.right, this.list.length));
      this.list = null;
    },
  };

  // ───── activities: pieces to drag, or to press and then press where they go ─────
  function same(a, b) { return fold(a).replace(/[^\p{L}\p{N}]+/gu, '') === fold(b).replace(/[^\p{L}\p{N}]+/gu, ''); }
  function solved(act) { var s = state.a[act.id]; return !!(s && s.d); }
  function record(act, right, total, shown) {
    var s = state.a[act.id] || {};
    s.r = right; s.n = total;
    if (right === total && total > 0 && !shown) s.d = 1;
    if (shown) s.s = 1;
    state.a[act.id] = s;
    keep(); refreshMarks();
  }

  // One activity, ready to do. onResult(right, total) when it is checked.
  function Board(act, onResult) {
    if (act.k === 'tf') return TrueFalse(act, onResult);
    var root = h('div', { class: 'board', 'data-k': act.k });
    var places = [], held = null, bank = null, drag = null, swallow = false;
    var items = act.i, order = act.o || [];
    function textOf(p) { return $('.t', p).textContent; }
    function piece(text, value) {
      return h('button', { type: 'button', class: 'piece', 'data-v': value, 'aria-pressed': 'false' }, [h('span', { class: 'grip', 'aria-hidden': 'true' }), h('span', { class: 't', text: text })]);
    }
    function place(cls, label, many) {
      var well = h('div', { class: 'well' });
      var el = h('div', { class: 'place ' + cls, role: 'button', tabindex: '0', 'aria-label': label ? fmt(W.placeHere + ': {0}', label) : W.placeHere }, [well]);
      el._well = well; el._many = !!many; el._i = places.length;
      places.push(el);
      return el;
    }
    function homeOf(p) { return p.parentNode && p.parentNode.parentNode && p.parentNode.parentNode._well ? p.parentNode.parentNode : null; }
    function hold(p) {
      if (held) { held.classList.remove('held'); held.setAttribute('aria-pressed', 'false'); }
      held = p;
      root.classList.toggle('holding', !!p);
      if (p) { p.classList.add('held'); p.setAttribute('aria-pressed', 'true'); announce(fmt(W.picked, textOf(p))); }
    }
    function clearMarks() {
      $$('.piece', root).forEach(function (p) { p.classList.remove('ok', 'no'); });
      root.classList.remove('checked', 'shown');
      verdict.textContent = ''; verdict.className = 'verdict';
    }
    // A piece to a place: one that holds a single piece gives the one there back to where the new one came from.
    function put(p, target) {
      var from = homeOf(p);
      if (!target || target === from) { hold(null); return; }
      if (!target._many) {
        var there = $('.piece', target._well);
        if (there && there !== p) (from && !from._many ? from : bank)._well.appendChild(there);
      }
      target._well.appendChild(p);
      hold(null); clearMarks();
      announce(fmt(W.placed, textOf(p)));
    }
    root.addEventListener('click', function (e) {
      if (swallow) { swallow = false; return; }
      var p = e.target.closest('.piece'), target = e.target.closest('.place');
      if (p && held && p !== held) { put(held, homeOf(p)); return; }
      if (p) { hold(held === p ? null : p); return; }
      if (target && held) put(held, target);
    });
    root.addEventListener('keydown', function (e) {
      if ((e.key === 'Enter' || e.key === ' ') && e.target.classList.contains('place')) { e.preventDefault(); if (held) put(held, e.target); }
      if (e.key === 'Escape' && held) { e.stopPropagation(); hold(null); }
    });
    // Dragging: with a mouse or a pen from anywhere on the piece; with a finger from its grip, so the page still scrolls.
    root.addEventListener('pointerdown', function (e) {
      var p = e.target.closest('.piece');
      if (!p || e.button !== 0 || (e.pointerType === 'touch' && !e.target.closest('.grip'))) return;
      drag = { p: p, x: e.clientX, y: e.clientY, id: e.pointerId, on: false, ghost: null, over: null };
      if (e.pointerType === 'touch') e.preventDefault();
    });
    root.addEventListener('pointermove', function (e) {
      if (!drag || e.pointerId !== drag.id) return;
      if (!drag.on) {
        if (Math.abs(e.clientX - drag.x) + Math.abs(e.clientY - drag.y) < 6) return;
        drag.on = true;
        try { drag.p.setPointerCapture(e.pointerId); } catch (x) { /* a page that can't still drags */ }
        var r = drag.p.getBoundingClientRect();
        drag.dx = e.clientX - r.left; drag.dy = e.clientY - r.top;
        drag.ghost = drag.p.cloneNode(true);
        drag.ghost.classList.add('ghost');
        drag.ghost.style.width = r.width + 'px';
        body.appendChild(drag.ghost);
        drag.p.classList.add('lifted');
        hold(null);
      }
      drag.ghost.style.left = (e.clientX - drag.dx) + 'px';
      drag.ghost.style.top = (e.clientY - drag.dy) + 'px';
      var under = doc.elementFromPoint(e.clientX, e.clientY);
      var target = under && under.closest('.place');
      if (target && !root.contains(target)) target = null;
      if (drag.over !== target) { if (drag.over) drag.over.classList.remove('over'); drag.over = target; if (target) target.classList.add('over'); }
      e.preventDefault();
    });
    function drop(e, cancel) {
      if (!drag || e.pointerId !== drag.id) return;
      var d = drag; drag = null;
      if (!d.on) return;
      swallow = true;
      setTimeout(function () { swallow = false; }, 0);
      if (d.ghost) d.ghost.remove();
      d.p.classList.remove('lifted');
      if (d.over) d.over.classList.remove('over');
      if (!cancel && d.over) put(d.p, d.over);
    }
    root.addEventListener('pointerup', function (e) { drop(e, false); });
    root.addEventListener('pointercancel', function (e) { drop(e, true); });

    // The pieces where they first are.
    var slots = [];
    if (act.k === 'order') {
      var list = h('ol', { class: 'slots' });
      items.forEach(function (_, k) { var s = place('slot', String(k + 1)); slots.push(s); list.appendChild(h('li', null, [h('span', { class: 'n', text: String(k + 1) }), s])); });
      order.forEach(function (v, k) { slots[k]._well.appendChild(piece(items[v][0], v)); });
      root.appendChild(list);
    } else if (act.k === 'match') {
      var rows = h('div', { class: 'rows2' });
      items.forEach(function (it) { var s = place('slot', it[0]); slots.push(s); rows.appendChild(h('div', { class: 'pair' }, [h('div', { class: 'left' }, [h('bdi', { text: it[0] })]), s])); });
      root.appendChild(rows);
    } else if (act.k === 'mapfill') {
      // The map: its idea in the middle, each branch a place for its name above what the branch says.
      var map = h('div', { class: 'map fill' }, [h('p', { class: 'hub' }, [h('bdi', { text: act.t })])]);
      var arms = h('ul', { class: 'arms' });
      items.forEach(function (it) {
        var s = place('slot', it[0]); slots.push(s);
        arms.appendChild(h('li', null, [s, h('ul', null, it[0].split('; ').map(function (d) { return h('li', null, [h('bdi', { text: d })]); }))]));
      });
      map.appendChild(arms);
      root.appendChild(map);
    } else if (act.k === 'sort') {
      var bins = h('div', { class: 'bins' });
      act.g.forEach(function (g) { var b = place('bin', g, true); slots.push(b); bins.appendChild(h('div', { class: 'group' }, [h('strong', null, [h('bdi', { text: g })]), b])); });
      root.appendChild(bins);
    } else if (act.k === 'blanks') {
      var words = [], answers = [];
      var sentences = h('ol', { class: 'sentences' });
      items.forEach(function (it) {
        var li = h('li');
        it[0].split(/\[([^\[\]]+)\]/).forEach(function (piece, k) {
          if (k % 2 === 0) { if (piece) li.appendChild(doc.createTextNode(piece)); return; }
          answers.push(piece.trim());
          var s = place('slot gap', String(answers.length)); slots.push(s); li.appendChild(s);
        });
        sentences.appendChild(li);
      });
      words = answers;
      root.appendChild(sentences);
    }
    if (act.k !== 'order') {
      bank = place('bank', W.bank, true);
      root.appendChild(h('p', { class: 'banklabel', text: W.bank }));
      root.appendChild(bank);
      order.forEach(function (v) {
        var text = act.k === 'match' || act.k === 'mapfill' ? items[v][1] : act.k === 'sort' ? items[v][0] : words[v];
        bank._well.appendChild(piece(text, v));
      });
    } else bank = slots[0];
    root.appendChild(h('p', { class: 'hint', text: act.k === 'order' ? W.orderHint : act.k === 'mapfill' ? W.mapHint : W.dragHint }));

    // Whether each piece is where it belongs.
    function rightAt(p, at) {
      var v = +p.getAttribute('data-v');
      if (!at || at === bank && act.k !== 'order') return false;
      if (act.k === 'order' || act.k === 'match' || act.k === 'mapfill') return at._i === v;
      if (act.k === 'sort') return act.g[at._i] === items[v][1];
      return same(words[v], words[at._i]);
    }
    function score() {
      var right = 0, total = act.k === 'sort' ? items.length : slots.length;
      $$('.piece', root).forEach(function (p) {
        var ok = rightAt(p, homeOf(p));
        p.classList.add(ok ? 'ok' : 'no');
        if (ok) right++;
      });
      return [right, total];
    }
    var verdict = h('p', { class: 'verdict', role: 'status' });
    var check = h('button', { type: 'button', class: 'btn primary', text: W.checkIt, onclick: function () {
      clearMarks();
      var s = score();
      root.classList.add('checked');
      verdict.textContent = s[0] === s[1] ? W.allRight : fmt(W.rightOf, s[0], s[1]);
      verdict.className = 'verdict ' + (s[0] === s[1] ? 'good' : 'bad');
      announce(verdict.textContent);
      record(act, s[0], s[1], false);
      if (onResult) onResult(s[0], s[1]);
    } });
    var show = h('button', { type: 'button', class: 'link', text: W.showAnswer, onclick: function () {
      hold(null); clearMarks();
      var all = $$('.piece', root);
      if (act.k === 'sort') all.forEach(function (p) { slots[act.g.indexOf(items[+p.getAttribute('data-v')][1])]._well.appendChild(p); });
      else {
        var free = all.slice();
        slots.forEach(function (s, k) {
          var p = free.filter(function (x) { return rightAt(x, s); })[0];
          if (p) { s._well.appendChild(p); free.splice(free.indexOf(p), 1); }
        });
        free.forEach(function (p) { bank._well.appendChild(p); });
      }
      score();
      root.classList.add('checked', 'shown');
      record(act, 0, slots.length, true);
      if (onResult) onResult(-1, 0);
    } });
    var again = h('button', { type: 'button', class: 'link', text: W.again, onclick: function () {
      var fresh = Board(act, onResult);
      root.replaceWith(fresh);
      var first = $('.piece', fresh) || $('.place', fresh);
      if (first) first.focus();
    } });
    root.appendChild(h('div', { class: 'row tools' }, [check, show, again]));
    root.appendChild(verdict);
    return root;
  }

  // True or false: each statement answered at once, put right when it is false.
  function TrueFalse(act, onResult) {
    var root = h('div', { class: 'board', 'data-k': 'tf' }), answered = 0, right = 0;
    var verdict = h('p', { class: 'verdict', role: 'status' });
    var list = h('ol', { class: 'statements' });
    act.i.forEach(function (it) {
      var truth = it[1] === 'true';
      var note = h('p', { class: 'note', hidden: true });
      var buttons = [true, false].map(function (said) {
        return h('button', { type: 'button', class: 'opt tfb', text: said ? W.true : W.false, onclick: function () {
          buttons.forEach(function (b) { b.disabled = true; });
          var good = said === truth;
          this.classList.add(good ? 'right' : 'wrong');
          if (!good) buttons[said ? 1 : 0].classList.add('right');
          note.hidden = false;
          note.textContent = (good ? W.right : W.wrong) + (it[2] ? ' ' + it[2] : '');
          note.className = 'note ' + (good ? 'good' : 'bad');
          answered++; if (good) right++;
          announce(note.textContent);
          if (answered === act.i.length) {
            verdict.textContent = right === answered ? W.allRight : fmt(W.rightOf, right, answered);
            verdict.className = 'verdict ' + (right === answered ? 'good' : 'bad');
            record(act, right, answered, false);
            if (onResult) onResult(right, answered);
          }
        } });
      });
      list.appendChild(h('li', null, [h('p', null, [h('bdi', { text: it[0] })]), h('div', { class: 'row' }, buttons), note]));
    });
    root.appendChild(list);
    root.appendChild(verdict);
    root.appendChild(h('div', { class: 'row tools' }, [h('button', { type: 'button', class: 'link', text: W.again, onclick: function () { root.replaceWith(TrueFalse(act, onResult)); } })]));
    return root;
  }

  var actsById = {};
  (data.acts || []).forEach(function (a) { actsById[a.id] = a; });
  function kindName(k) { return { order: W.kindOrder, match: W.kindMatch, sort: W.kindSort, blanks: W.kindBlanks, tf: W.kindTrueFalse, mapfill: W.kindMapFill }[k] || ''; }

  // ───── steps gone through one at a time: the steps above as a bar to jump with, the one shown below ─────
  $$('.vis.walk').forEach(function (fig) {
    var steps = $$('ol > li', fig), at = 0;
    if (steps.length < 2) return;
    fig.classList.add('live');
    var count = h('p', { class: 'count', role: 'status' });
    var prev = h('button', { type: 'button', class: 'btn quiet', text: W.previous });
    var next = h('button', { type: 'button', class: 'btn primary', text: W.next });
    var bar = h('div', { class: 'stepbar', role: 'group' });
    var dots = steps.map(function (li, k) {
      var b = h('button', { type: 'button', class: 'dot', 'aria-label': $('.t', li).firstChild.textContent, text: String(k + 1) });
      b.onclick = function () { go(k); };
      bar.appendChild(b);
      return b;
    });
    function go(k) {
      at = Math.max(0, Math.min(steps.length - 1, k));
      steps.forEach(function (li, i) { li.classList.toggle('on', i === at); });
      dots.forEach(function (d, i) { d.classList.toggle('done', i < at); d.classList.toggle('on', i === at); d.setAttribute('aria-current', i === at ? 'step' : 'false'); });
      prev.disabled = at === 0;
      next.disabled = at === steps.length - 1;
      count.textContent = fmt(W.stepOf, at + 1, steps.length);
    }
    prev.onclick = function () { go(at - 1); };
    next.onclick = function () { go(at + 1); announce($('.t', steps[at]).textContent); };
    fig.addEventListener('keydown', function (e) {
      var back = getComputedStyle(fig).direction === 'rtl' ? 'ArrowRight' : 'ArrowLeft', on = back === 'ArrowLeft' ? 'ArrowRight' : 'ArrowLeft';
      if (e.key === on) { e.preventDefault(); go(at + 1); }
      if (e.key === back) { e.preventDefault(); go(at - 1); }
    });
    fig.insertBefore(bar, $('ol', fig));
    fig.appendChild(h('div', { class: 'row ctl' }, [prev, count, next]));
    go(0);
  });
  $$('.part .act').forEach(function (el) {
    var act = actsById[el.getAttribute('data-a')];
    if (act) el.insertBefore(Board(act), $('.key', el));
  });

  // ───── hands-on scenes: drawn and moved here from what the model said, nothing it wrote is run ─────
  var NS = 'http://www.w3.org/2000/svg';
  function sv(tag, props, kids) {
    var el = doc.createElementNS(NS, tag);
    if (props) Object.keys(props).forEach(function (k) { if (props[k] !== null && props[k] !== undefined) el.setAttribute(k, props[k]); });
    (kids || []).forEach(function (c) { if (c) el.appendChild(c); });
    return el;
  }
  var reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  var numbers = (function () { try { return new Intl.NumberFormat(data.lang, { maximumSignificantDigits: 4 }); } catch (e) { return null; } })();
  function num(v) {
    if (!isFinite(v)) return '—';
    var a = Math.abs(v);
    if (a !== 0 && (a >= 1e7 || a < 1e-4)) {
      try { return new Intl.NumberFormat(data.lang, { notation: 'compact', maximumSignificantDigits: 3 }).format(v); } catch (e) { return v.toPrecision(3); }
    }
    return numbers ? numbers.format(v) : String(Math.round(v * 1000) / 1000);
  }
  // What leads to what, pointing the way the line reads.
  var arrow = rtl ? ' ← ' : ' → ';
  function withUnit(v, unit) { return unit ? num(v) + ' ' + unit : num(v); }
  function sceneState(id) { return state.s[id] || (state.s[id] = {}); }
  if (!state.s) state.s = {};
  var COLOURS = { clear: '#cfe6ff', white: '#f5f5ef', grey: '#9aa0a6', black: '#333', red: '#e0483e', orange: '#f08c2e', yellow: '#f2cf3a', green: '#3fae5a', blue: '#3a7fe0', purple: '#8a5bd6', pink: '#ec6fa8', brown: '#8b5a2b' };
  function rgb(hex) { var n = parseInt(hex.slice(1), 16); return [n >> 16 & 255, n >> 8 & 255, n & 255]; }
  function blend(colours) {
    var list = colours.filter(Boolean).map(function (c) { return rgb(COLOURS[c] || COLOURS.clear); });
    if (!list.length) return COLOURS.clear;
    var sum = [0, 0, 0];
    list.forEach(function (c) { sum[0] += c[0]; sum[1] += c[1]; sum[2] += c[2]; });
    return 'rgb(' + sum.map(function (s) { return Math.round(s / list.length); }).join(',') + ')';
  }

  // A formula, read as the file was checked to have it: numbers, names, + − × ÷ ^, brackets and a few functions.
  function formula(text, known) {
    var tokens = String(text).toLowerCase().match(/\d*\.?\d+(?:e[-+]?\d+)?|[\p{L}_][\p{L}\p{N}_]*|[-+*\/^(),]/gu) || [], at = 0;
    var fns = { sqrt: Math.sqrt, abs: Math.abs, sin: Math.sin, cos: Math.cos, tan: Math.tan, log: Math.log10, ln: Math.log, exp: Math.exp,
      round: function (x) { return Math.sign(x) * Math.round(Math.abs(x)); }, pow: Math.pow, min: Math.min, max: Math.max };
    function expr() {
      var l = term();
      while (l && (tokens[at] === '+' || tokens[at] === '-')) { var op = tokens[at++], r = term(); if (!r) return null; l = (function (a, b, o) { return o === '+' ? function (v) { return a(v) + b(v); } : function (v) { return a(v) - b(v); }; })(l, r, op); }
      return l;
    }
    function term() {
      var l = unary();
      while (l && (tokens[at] === '*' || tokens[at] === '/')) { var op = tokens[at++], r = unary(); if (!r) return null; l = (function (a, b, o) { return o === '*' ? function (v) { return a(v) * b(v); } : function (v) { return a(v) / b(v); }; })(l, r, op); }
      return l;
    }
    function unary() {
      if (tokens[at] === '-' || tokens[at] === '+') { var op = tokens[at++], x = unary(); return !x ? null : op === '-' ? function (v) { return -x(v); } : x; }
      var b = atom();
      if (b && tokens[at] === '^') { at++; var e = unary(); return e ? function (v) { return Math.pow(b(v), e(v)); } : null; }
      return b;
    }
    function atom() {
      var t = tokens[at++];
      if (t === undefined) return null;
      if (t === '(') { var x = expr(); return x && tokens[at++] === ')' ? x : null; }
      if (/^[\d.]/.test(t)) { var n = parseFloat(t); return function () { return n; }; }
      if (fns[t]) {
        if (tokens[at++] !== '(') return null;
        var args = [];
        for (;;) { var a = expr(); if (!a) return null; args.push(a); if (tokens[at] === ',') { at++; continue; } if (tokens[at++] !== ')') return null; break; }
        var f = fns[t];
        return function (v) { return f.apply(null, args.map(function (g) { return g(v); })); };
      }
      if (t === 'pi') return function () { return Math.PI; };
      if (t === 'e') return function () { return Math.E; };
      if (known.indexOf(t) < 0) return null;
      return function (v) { return v[t]; };
    }
    var f = expr();
    return f && at === tokens.length ? f : null;
  }

  // ── the lab: things on a shelf, a flask, and what the file says happens when they meet ──
  function Lab(sc) {
    var things = sc.th, mixes = sc.mx, inside = [], kept = sceneState(sc.id), found = kept.f || (kept.f = {});
    var root = h('div', { class: 'live lab' });
    var hint = h('p', { class: 'hint', text: W.labHint });
    var shelf = h('div', { class: 'shelf', role: 'group', 'aria-label': W.labShelf });
    var clip = 'lc' + sc.id;
    var liquid = sv('rect', { class: 'liquid', x: 0, y: 150, width: 120, height: 150, fill: COLOURS.clear });
    var fx = sv('g', { class: 'fx' });
    var above = sv('g', { class: 'above' });
    var shape = 'M50 8 h20 v44 l34 74 q5 14 -10 16 h-68 q-15 -2 -10 -16 l34 -74 z';
    var flask = sv('svg', { class: 'flask', viewBox: '0 -40 120 190', role: 'img', 'aria-label': W.flask }, [
      sv('defs', null, [sv('clipPath', { id: clip }, [sv('path', { d: shape })])]),
      sv('g', { 'clip-path': 'url(#' + clip + ')' }, [liquid, fx]),
      sv('path', { class: 'glass', d: shape }),
      sv('path', { class: 'shine', d: 'M42 92 l14 -30' }),
      above,
    ]);
    var drop = h('div', { class: 'well flaskwell', tabindex: '-1' }, [flask]);
    var tray = h('div', { class: 'inside', 'aria-live': 'polite' });
    var empty = h('button', { type: 'button', class: 'btn quiet', text: W.pourOut });
    var hintBtn = h('button', { type: 'button', class: 'btn quiet', text: W.hintMe });
    var counter = h('p', { class: 'count' });
    var outcome = h('div', { class: 'outcome', role: 'status', 'aria-live': 'polite' });
    var log = h('ol', { class: 'found' });
    var timer = 0;

    function level() { return inside.length === 0 ? 150 : inside.length === 1 ? 100 : inside.length === 2 ? 80 : 66; }
    function paint(colour) { liquid.setAttribute('y', level()); liquid.setAttribute('fill', colour); }
    function clearFx() { while (fx.firstChild) fx.removeChild(fx.firstChild); while (above.firstChild) above.removeChild(above.firstChild); flask.classList.remove('glowing', 'hot', 'cold', 'shake'); clearTimeout(timer); }
    function rand(a, b) { return a + Math.random() * (b - a); }
    function spawn(effect) {
      clearFx();
      if (effect === 'none') return;
      // Without motion the effect is drawn all the same, standing still where it would be seen.
      var top = level(), n, i;
      if (effect === 'bubbles' || effect === 'fizz') {
        n = effect === 'fizz' ? 26 : 12;
        for (i = 0; i < n; i++) fx.appendChild(sv('circle', { class: 'bubble', cx: rand(24, 96), cy: reduced ? rand(top + 6, 136) : 138, r: effect === 'fizz' ? rand(1, 2.4) : rand(2.5, 5), style: 'animation-delay:' + rand(0, 1.6).toFixed(2) + 's;--rise:' + (top - 140) + 'px' }));
        if (effect === 'fizz') fx.appendChild(sv('rect', { class: 'foam', x: 0, y: top - 6, width: 120, height: 9 }));
      } else if (effect === 'solid') {
        for (i = 0; i < 30; i++) fx.appendChild(sv('circle', { class: 'grain', cx: rand(22, 98), cy: reduced ? rand(128, 138) : top + 4, r: rand(1.2, 2.6), style: 'animation-delay:' + rand(0, 1.4).toFixed(2) + 's;--fall:' + (136 - top - rand(0, 6)) + 'px' }));
      } else if (effect === 'smoke') {
        for (i = 0; i < 7; i++) above.appendChild(sv('circle', { class: 'puff', cx: rand(50, 70), cy: reduced ? -i * 6 : 4, r: rand(5, 9), style: 'animation-delay:' + (i * .35).toFixed(2) + 's' }));
      } else if (effect === 'glow') {
        flask.classList.add('glowing');
      } else if (effect === 'heat') {
        flask.classList.add('hot');
        for (i = 0; i < 3; i++) above.appendChild(sv('path', { class: 'wave', d: 'M' + (48 + i * 10) + ' 0 q-5 -8 0 -16 q5 -8 0 -16', style: 'animation-delay:' + (i * .3) + 's' }));
      } else if (effect === 'cold') {
        flask.classList.add('cold');
        for (i = 0; i < 8; i++) fx.appendChild(sv('text', { class: 'flake', x: rand(26, 92), y: rand(top + 10, 136), text: '' }, [doc.createTextNode('❄')]));
      } else if (effect === 'spark') {
        for (i = 0; i < 10; i++) { var a = i / 10 * Math.PI * 2; above.appendChild(sv('line', { class: 'spark', x1: 60, y1: 0, x2: 60 + Math.cos(a) * 22, y2: Math.sin(a) * 18, style: 'animation-delay:' + (i % 3 * .15) + 's' })); }
        flask.classList.add('shake');
      }
      flask.classList.toggle('stir', true);
      timer = setTimeout(function () { flask.classList.remove('stir'); }, 900);
    }
    function key(list) { return list.slice().sort(function (a, b) { return a - b; }).join('+'); }
    function names(list) { return list.map(function (i) { return things[i][0]; }).join(' + '); }
    function showMix(m, fresh) {
      paint(m[4] ? COLOURS[m[4]] || blend(m[0].map(function (i) { return things[i][2]; })) : blend(m[0].map(function (i) { return things[i][2]; })));
      spawn(m[3]);
      outcome.className = 'outcome happened';
      outcome.innerHTML = '';
      outcome.appendChild(h('p', { class: 'what' }, [h('strong', { text: names(m[0]) }), arrow, m[1]]));
      if (m[2]) outcome.appendChild(h('p', { class: 'why' }, [h('span', { class: 'label', text: W.whyItHappens + ' ' }), m[2]]));
      if (fresh) flash(outcome);
    }
    function refresh() {
      var n = Object.keys(found).length;
      counter.textContent = fmt(W.labFound, n, mixes.length);
      root.classList.toggle('all', n === mixes.length);
      log.innerHTML = '';
      mixes.forEach(function (m) {
        if (!found[key(m[0])]) return;
        var b = h('button', { type: 'button', class: 'link', text: names(m[0]) + arrow + m[1] });
        b.onclick = function () { inside = m[0].slice(); drawInside(); showMix(m, true); };
        log.appendChild(h('li', null, [b]));
      });
      $$('.bottle', shelf).forEach(function (b, i) { b.classList.toggle('in', inside.indexOf(i) >= 0); b.setAttribute('aria-pressed', inside.indexOf(i) >= 0 ? 'true' : 'false'); });
    }
    function drawInside() {
      tray.innerHTML = '';
      inside.forEach(function (i) {
        var chip = h('button', { type: 'button', class: 'chip', 'aria-label': fmt(W.takeOut, things[i][0]) }, [things[i][0], h('span', { 'aria-hidden': 'true', text: ' ×' })]);
        chip.onclick = function () { inside.splice(inside.indexOf(i), 1); drawInside(); judge(false); };
        tray.appendChild(chip);
      });
      refresh();
    }
    function judge(added) {
      clearFx();
      var k = key(inside), mix = null, maybe = false;
      mixes.forEach(function (m) {
        if (key(m[0]) === k) mix = m;
        else if (inside.every(function (i) { return m[0].indexOf(i) >= 0; })) maybe = true;
      });
      outcome.innerHTML = ''; outcome.className = 'outcome';
      if (!inside.length) { paint(COLOURS.clear); refresh(); return; }
      if (mix) {
        var fresh = !found[k];
        found[k] = 1; keep();
        showMix(mix, true);
        if (fresh && Object.keys(found).length === mixes.length) { outcome.appendChild(h('p', { class: 'all', text: W.labAll })); root.classList.add('cheer'); }
        announce(names(mix[0]) + ': ' + mix[1]);
      } else {
        paint(blend(inside.map(function (i) { return things[i][2]; })));
        outcome.className = 'outcome ' + (maybe ? 'waiting' : 'nothing');
        outcome.appendChild(h('p', { text: maybe ? W.labMore : W.labNothing }));
        if (!maybe && added) { flask.classList.add('shake'); setTimeout(function () { flask.classList.remove('shake'); }, 500); }
      }
      refresh();
    }
    function add(i) {
      if (inside.indexOf(i) >= 0) { inside.splice(inside.indexOf(i), 1); drawInside(); judge(false); return; }
      if (inside.length >= 3) inside.shift();
      inside.push(i);
      var b = $$('.bottle', shelf)[i];
      if (b && !reduced) { b.classList.remove('pour'); void b.offsetWidth; b.classList.add('pour'); }
      drawInside();
      judge(true);
    }
    things.forEach(function (t, i) {
      var tint = COLOURS[t[2]] || COLOURS.clear;
      var b = h('button', { type: 'button', class: 'bottle', 'aria-pressed': 'false', title: t[1] || '' }, [
        sv('svg', { viewBox: '0 0 24 32', class: 'vial', 'aria-hidden': 'true' }, [sv('path', { d: 'M9 2h6v8l5 16q1 4-3 4H7q-4 0-3-4l5-16z', fill: tint, class: 'tint' }), sv('path', { d: 'M9 2h6v8l5 16q1 4-3 4H7q-4 0-3-4l5-16z', class: 'rim' })]),
        h('span', { class: 'n', text: t[0] }),
        t[1] ? h('span', { class: 'd', text: t[1] }) : null,
      ]);
      b.onclick = function () { if (!b._dragged) add(i); b._dragged = false; };
      // Dragged onto the flask, or anywhere near it.
      b.addEventListener('pointerdown', function (e) {
        if (e.button !== 0) return;
        var ghost = null, x0 = e.clientX, y0 = e.clientY;
        function move(ev) {
          if (!ghost && Math.abs(ev.clientX - x0) + Math.abs(ev.clientY - y0) > 8) { ghost = h('div', { class: 'ghost', text: t[0] }); body.appendChild(ghost); b._dragged = true; }
          if (ghost) { ghost.style.left = ev.clientX + 'px'; ghost.style.top = ev.clientY + 'px'; var r = drop.getBoundingClientRect(); drop.classList.toggle('over', ev.clientX > r.left - 30 && ev.clientX < r.right + 30 && ev.clientY > r.top - 30 && ev.clientY < r.bottom + 30); }
        }
        function up(ev) {
          doc.removeEventListener('pointermove', move); doc.removeEventListener('pointerup', up);
          if (!ghost) return;
          ghost.remove();
          if (drop.classList.contains('over')) add(i);
          drop.classList.remove('over');
        }
        doc.addEventListener('pointermove', move); doc.addEventListener('pointerup', up);
      });
      shelf.appendChild(b);
    });
    empty.onclick = function () { inside = []; drawInside(); judge(false); };
    hintBtn.onclick = function () {
      var next = mixes.filter(function (m) { return !found[key(m[0])]; })[0];
      if (!next) return;
      var pick = next[0].filter(function (i) { return inside.indexOf(i) < 0; })[0];
      if (inside.some(function (i) { return next[0].indexOf(i) < 0; })) { inside = []; drawInside(); judge(false); pick = next[0][0]; }
      var b = $$('.bottle', shelf)[pick];
      if (b) { b.classList.remove('nudge'); void b.offsetWidth; b.classList.add('nudge'); b.focus(); }
      announce(fmt(W.tryThing, things[pick][0]));
      outcome.className = 'outcome waiting'; outcome.innerHTML = ''; outcome.appendChild(h('p', { text: fmt(W.tryThing, things[pick][0]) }));
    };
    root.appendChild(hint);
    root.appendChild(h('div', { class: 'bench' }, [shelf, h('div', { class: 'stand' }, [drop, tray, h('div', { class: 'row' }, [empty, hintBtn])])]));
    root.appendChild(outcome);
    root.appendChild(h('div', { class: 'tally' }, [counter, log]));
    paint(COLOURS.clear);
    refresh();
    return root;
  }

  // ── a simulation: sliders, what follows from them as numbers, a meter and a curve, and what each range means ──
  function Simulate(sc) {
    var vars = sc.v, results = sc.r, levels = sc.l || [], ids = vars.map(function (v) { return v[0]; });
    var fns = [];
    results.forEach(function (r) { fns.push(formula(r[3], ids.concat(results.map(function (x) { return x[0]; })))); });
    if (fns.some(function (f) { return !f; })) return null;
    var values = {}, along = 0, playing = 0;
    vars.forEach(function (v) { values[v[0]] = v[5]; });
    function work(vals) {
      var all = {}; Object.keys(vals).forEach(function (k) { all[k] = vals[k]; });
      results.forEach(function (r, i) { var x; try { x = fns[i](all); } catch (e) { x = NaN; } all[r[0]] = x; });
      return all;
    }
    // How far each result goes, from corners and samples of the sliders.
    var span = results.map(function () { return [Infinity, -Infinity]; });
    for (var s = 0; s < 160; s++) {
      var probe = {};
      vars.forEach(function (v, k) { var t = s < (1 << vars.length) ? (s >> k & 1) : Math.random(); probe[v[0]] = v[3] + (v[4] - v[3]) * t; });
      var out = work(probe);
      results.forEach(function (r, i) { var x = out[r[0]]; if (isFinite(x)) { span[i][0] = Math.min(span[i][0], x); span[i][1] = Math.max(span[i][1], x); } });
    }
    var root = h('div', { class: 'live sim' });
    root.appendChild(h('p', { class: 'hint', text: W.simHint }));
    var grid = h('div', { class: 'simgrid' });
    var controls = h('div', { class: 'sliders' });
    var readouts = h('div', { class: 'readouts' });
    var says = h('div', { class: 'outcome', role: 'status', 'aria-live': 'polite' });
    var inputs = vars.map(function (v) {
      var step = (v[4] - v[3]) / 200, p = Math.pow(10, Math.floor(Math.log10(step || 1)));
      step = Math.max(p, Math.round(step / p) * p);
      var input = h('input', { type: 'range', min: v[3], max: v[4], step: step, value: v[5], 'aria-label': v[1] });
      var shown = h('output', { class: 'val' });
      input.addEventListener('input', function () { values[v[0]] = parseFloat(input.value); update(); });
      controls.appendChild(h('label', { class: 'slider' }, [h('span', { class: 'name' }, [h('bdi', { text: v[1] })]), shown, input,
        h('span', { class: 'ends' }, [h('span', { text: withUnit(v[3], v[2]) }), h('span', { text: withUnit(v[4], v[2]) })])]));
      input._shown = shown;
      return input;
    });
    var meters = results.map(function (r) {
      var big = h('span', { class: 'big' }), fill = h('span', { class: 'fill' });
      readouts.appendChild(h('div', { class: 'readout' }, [h('span', { class: 'name' }, [h('bdi', { text: r[1] })]), big, h('span', { class: 'gauge' }, [fill])]));
      return { big: big, fill: fill, shown: NaN };
    });
    // The curve of the first result as one slider goes from end to end, the others where they are.
    var W0 = 320, H0 = 170, padL = 8, padR = 8, padT = 10, padB = 22;
    var path = sv('path', { class: 'curve' }), dot = sv('circle', { class: 'dot', r: 5 }), axis = sv('path', { class: 'axis', d: 'M' + padL + ' ' + (H0 - padB) + 'H' + (W0 - padR) });
    var lowLabel = sv('text', { class: 'lbl', x: padL, y: H0 - 6 }), highLabel = sv('text', { class: 'lbl end', x: W0 - padR, y: H0 - 6 });
    var topLabel = sv('text', { class: 'lbl', x: padL + 2, y: padT + 8 });
    var chart = sv('svg', { class: 'chart', viewBox: '0 0 ' + W0 + ' ' + H0, role: 'img', 'aria-label': results[0][1] }, [axis, path, dot, lowLabel, highLabel, topLabel]);
    var tabs = h('div', { class: 'tabs', role: 'group', 'aria-label': W.along });
    if (vars.length > 1) vars.forEach(function (v, k) {
      var b = h('button', { type: 'button', class: 'tab', 'aria-pressed': k === 0 ? 'true' : 'false', text: v[1] });
      b.onclick = function () { along = k; $$('.tab', tabs).forEach(function (t, j) { t.setAttribute('aria-pressed', j === k ? 'true' : 'false'); }); update(); };
      tabs.appendChild(b);
    });
    var play = h('button', { type: 'button', class: 'btn primary play' }, [h('span', { class: 'glyph', 'aria-hidden': 'true', text: '▶' }), h('span', { class: 't', text: W.play })]);
    var again = h('button', { type: 'button', class: 'btn quiet', text: W.again });
    function draw() {
      var v = vars[along], pts = [], lo = Infinity, hi = -Infinity, k, x, probe;
      for (k = 0; k <= 80; k++) {
        probe = {}; Object.keys(values).forEach(function (n) { probe[n] = values[n]; });
        probe[v[0]] = v[3] + (v[4] - v[3]) * k / 80;
        x = work(probe)[results[0][0]];
        pts.push(x);
        if (isFinite(x)) { lo = Math.min(lo, x); hi = Math.max(hi, x); }
      }
      if (!isFinite(lo)) { path.setAttribute('d', ''); return; }
      if (hi - lo < 1e-12) { hi = lo + 1; lo = lo - 1; }
      var flip = rtl, d = '', pen = false;
      function px(t) { var X = padL + (W0 - padL - padR) * t; return flip ? W0 - X : X; }
      function py(y) { return padT + (H0 - padT - padB) * (1 - (y - lo) / (hi - lo)); }
      pts.forEach(function (y, i) { if (!isFinite(y)) { pen = false; return; } d += (pen ? 'L' : 'M') + px(i / 80).toFixed(1) + ' ' + py(y).toFixed(1); pen = true; });
      path.setAttribute('d', d);
      var now = work(values)[results[0][0]], t = (values[v[0]] - v[3]) / (v[4] - v[3]);
      if (isFinite(now)) { dot.setAttribute('cx', px(t)); dot.setAttribute('cy', py(now)); dot.style.display = ''; } else dot.style.display = 'none';
      lowLabel.textContent = withUnit(v[3], v[2]); highLabel.textContent = withUnit(v[4], v[2]);
      lowLabel.setAttribute('x', flip ? W0 - padL : padL); highLabel.setAttribute('x', flip ? padL : W0 - padR);
      lowLabel.setAttribute('class', flip ? 'lbl end' : 'lbl'); highLabel.setAttribute('class', flip ? 'lbl' : 'lbl end');
      topLabel.textContent = results[0][1] + ' ' + withUnit(hi, results[0][2]);
      topLabel.setAttribute('x', flip ? W0 - padL - 2 : padL + 2); topLabel.setAttribute('class', flip ? 'lbl end' : 'lbl');
    }
    function update() {
      inputs.forEach(function (input, k) { input._shown.textContent = withUnit(values[vars[k][0]], vars[k][2]); });
      var all = work(values);
      results.forEach(function (r, i) {
        var x = all[r[0]], m = meters[i];
        m.big.textContent = withUnit(x, r[2]);
        var range = span[i], share = isFinite(x) && range[1] > range[0] ? (x - range[0]) / (range[1] - range[0]) : 0;
        m.fill.style.width = Math.max(0, Math.min(100, share * 100)).toFixed(1) + '%';
      });
      says.innerHTML = ''; says.className = 'outcome';
      levels.forEach(function (l) {
        var x = all[l[0]], ok = l[1] === '>' ? x > l[2] : l[1] === '<' ? x < l[2] : l[1] === '>=' ? x >= l[2] : x <= l[2];
        if (isFinite(x) && ok) says.appendChild(h('p', { class: 'level' }, [l[3]]));
      });
      if (says.firstChild) says.className = 'outcome happened';
      draw();
    }
    function stop() { cancelAnimationFrame(playing); playing = 0; root.classList.remove('playing'); $('.glyph', play).textContent = '▶'; $('.t', play).textContent = W.play; }
    play.onclick = function () {
      if (playing) { stop(); return; }
      var v = vars[along], input = inputs[along], start = performance.now(), from = values[v[0]] >= v[4] ? v[3] : values[v[0]], length = 4000 * (v[4] - from) / (v[4] - v[3]) || 4000;
      root.classList.add('playing'); $('.glyph', play).textContent = '❚❚'; $('.t', play).textContent = W.pause;
      function frame(now) {
        var t = Math.min(1, (now - start) / length);
        values[v[0]] = from + (v[4] - from) * t; input.value = values[v[0]]; update();
        if (t < 1) playing = requestAnimationFrame(frame); else stop();
      }
      playing = requestAnimationFrame(frame);
    };
    again.onclick = function () { stop(); vars.forEach(function (v, k) { values[v[0]] = v[5]; inputs[k].value = v[5]; }); update(); };
    grid.appendChild(controls);
    grid.appendChild(h('div', { class: 'result' }, [readouts, tabs.firstChild ? h('p', { class: 'label', text: W.along }) : null, tabs.firstChild ? tabs : null, chart]));
    root.appendChild(grid);
    root.appendChild(says);
    root.appendChild(h('div', { class: 'row' }, [play, again]));
    update();
    sceneState(sc.id).t = 1;
    return root;
  }

  // ── a system: press a part up or down and watch the pushes run along its arrows ──
  function System(sc) {
    var parts = sc.n, links = sc.l, n = parts.length;
    var root = h('div', { class: 'live sys' });
    root.appendChild(h('p', { class: 'hint', text: W.sysHint }));
    var stage = h('div', { class: 'stage' }), Wd = 400, Ht = 300;
    var spots = parts.map(function (p, i) {
      var a = -Math.PI / 2 + i / n * Math.PI * 2, x = Wd / 2 + Math.cos(a) * 150, y = Ht / 2 + Math.sin(a) * 110;
      return [rtl ? Wd - x : x, y];
    });
    var arrows = sv('svg', { class: 'arrows', viewBox: '0 0 ' + Wd + ' ' + Ht, 'aria-hidden': 'true' });
    var defs = sv('defs');
    ['up', 'down'].forEach(function (k) { defs.appendChild(sv('marker', { id: 'm' + k + sc.id, viewBox: '0 0 10 10', refX: 9, refY: 5, markerWidth: 7, markerHeight: 7, orient: 'auto-start-reverse' }, [sv('path', { d: 'M0 0L10 5L0 10z', class: k })])); });
    arrows.appendChild(defs);
    var signs = [];
    var lines = links.map(function (l) {
      var k = l[1] ? 'up' : 'down';
      var p = sv('path', { class: 'link ' + k, 'marker-end': 'url(#m' + k + sc.id + ')' });
      arrows.appendChild(p);
      var dot = sv('circle', { r: 8 }), mark = sv('text', null, [doc.createTextNode(l[1] ? '+' : '−')]);
      arrows.appendChild(sv('g', { class: 'sign ' + k }, [dot, mark]));
      signs.push([dot, mark]);
      return p;
    });
    // Each arrow from the edge of one part's box to the edge of the other's, bent a little so two ways between the
    // same parts don't lie on one another: drawn again whenever the stage's size changes, as the boxes keep theirs.
    function geometry() {
      var unit = stage.clientWidth / Wd;
      if (!unit || !nodes) return;
      links.forEach(function (l, i) {
        var a = spots[l[0]], b = spots[l[2]], dx = b[0] - a[0], dy = b[1] - a[1], len = Math.sqrt(dx * dx + dy * dy) || 1;
        var ux = dx / len, uy = dy / len, bend = 18;
        function cut(node) {
          var hw = node.offsetWidth / 2 / unit + 5, hh = node.offsetHeight / 2 / unit + 5;
          return Math.min(hw / Math.max(Math.abs(ux), 1e-6), hh / Math.max(Math.abs(uy), 1e-6));
        }
        var c1 = cut(nodes[l[0]]), c2 = cut(nodes[l[2]]) + 3;
        var x1 = a[0] + ux * c1, y1 = a[1] + uy * c1, x2 = b[0] - ux * c2, y2 = b[1] - uy * c2;
        var mx = (x1 + x2) / 2 - uy * bend, my = (y1 + y2) / 2 + ux * bend;
        lines[i].setAttribute('d', 'M' + x1.toFixed(1) + ' ' + y1.toFixed(1) + 'Q' + mx.toFixed(1) + ' ' + my.toFixed(1) + ' ' + x2.toFixed(1) + ' ' + y2.toFixed(1));
        var cx = (x1 + 2 * mx + x2) / 4, cy = (y1 + 2 * my + y2) / 4;
        signs[i][0].setAttribute('cx', cx); signs[i][0].setAttribute('cy', cy);
        signs[i][1].setAttribute('x', cx); signs[i][1].setAttribute('y', cy + 4);
      });
    }
    var nodes = null;
    var runner = sv('circle', { class: 'runner', r: 5, cx: -20, cy: -20 });
    arrows.appendChild(runner);
    stage.appendChild(arrows);
    nodes = parts.map(function (p, i) {
      var up = h('button', { type: 'button', class: 'push up', 'aria-label': fmt(W.moreOf, p), text: '▲' });
      var down = h('button', { type: 'button', class: 'push down', 'aria-label': fmt(W.lessOf, p), text: '▼' });
      var badge = h('span', { class: 'badge', 'aria-hidden': 'true' });
      var node = h('div', { class: 'node', style: 'left:' + (spots[i][0] / Wd * 100).toFixed(2) + '%;top:' + (spots[i][1] / Ht * 100).toFixed(2) + '%' }, [up, h('span', { class: 'n' }, [h('bdi', { text: p })]), down, badge]);
      up.onclick = function () { run(i, 1); };
      down.onclick = function () { run(i, -1); };
      stage.appendChild(node);
      return node;
    });
    var story = h('ol', { class: 'story', 'aria-live': 'polite' });
    var again = h('button', { type: 'button', class: 'btn quiet', text: W.again });
    var token = 0;
    function reset() {
      token++;
      nodes.forEach(function (nd) { nd.classList.remove('up', 'down', 'both', 'start', 'hit'); $('.badge', nd).textContent = ''; });
      lines.forEach(function (l) { l.classList.remove('on'); });
      story.innerHTML = '';
      runner.setAttribute('cx', -20);
    }
    function mark(i, sign) {
      var nd = nodes[i];
      nd.classList.remove('up', 'down'); nd.classList.add(sign > 0 ? 'up' : 'down');
      $('.badge', nd).textContent = sign > 0 ? '↑' : '↓';
      nd.classList.remove('hit'); void nd.offsetWidth; nd.classList.add('hit');
    }
    function said(i, sign) { return fmt(sign > 0 ? W.moreOf : W.lessOf, parts[i]); }
    function travel(path, done, mine) {
      if (reduced) { done(); return; }
      var total = path.getTotalLength(), start = performance.now();
      runner.classList.add('go');
      function frame(now) {
        if (mine !== token) return;
        var t = Math.min(1, (now - start) / 650), pt = path.getPointAtLength(total * t);
        runner.setAttribute('cx', pt.x); runner.setAttribute('cy', pt.y);
        if (t < 1) requestAnimationFrame(frame); else { runner.classList.remove('go'); done(); }
      }
      requestAnimationFrame(frame);
    }
    function run(first, sign) {
      reset();
      var mine = token, signs = {}, queue = [[first, sign]], used = {};
      signs[first] = sign;
      nodes[first].classList.add('start');
      mark(first, sign);
      story.appendChild(h('li', { class: 'first' }, [h('strong', { text: said(first, sign) })]));
      function step() {
        if (mine !== token) return;
        var next = null;
        while (queue.length && !next) {
          var at = queue[0], from = at[0], s0 = at[1];
          var out = links.map(function (l, k) { return [l, k]; }).filter(function (x) { return x[0][0] === from && !used[x[1]]; })[0];
          if (!out) { queue.shift(); continue; }
          next = [out[0], out[1], s0];
        }
        if (!next) return;
        var l = next[0], k = next[1], s1 = next[2] * (l[1] ? 1 : -1), to = l[2];
        used[k] = 1;
        lines[k].classList.add('on');
        travel(lines[k], function () {
          if (mine !== token) return;
          var line = [said(l[0], next[2]), arrow, h('strong', { text: said(to, s1) })];
          if (l[3]) line.push(h('span', { class: 'why', text: ' ' + l[3] }));
          if (to === first) {
            story.appendChild(h('li', null, line));
            story.appendChild(h('li', { class: 'loop', text: s1 === sign ? W.loopUp : W.loopDown }));
          } else if (signs[to] !== undefined && signs[to] !== s1) {
            nodes[to].classList.add('both'); $('.badge', nodes[to]).textContent = '↕';
            story.appendChild(h('li', null, line));
            story.appendChild(h('li', { class: 'loop', text: fmt(W.bothWays, parts[to]) }));
          } else {
            story.appendChild(h('li', null, line));
            if (signs[to] === undefined) { signs[to] = s1; mark(to, s1); queue.push([to, s1]); }
          }
          setTimeout(step, reduced ? 0 : 160);
        }, mine);
      }
      step();
      sceneState(sc.id).t = 1; keep();
    }
    again.onclick = reset;
    if (window.ResizeObserver) new ResizeObserver(geometry).observe(stage);
    else { window.addEventListener('resize', geometry); window.addEventListener('hashchange', function () { setTimeout(geometry, 0); }); }
    root.appendChild(stage);
    root.appendChild(story);
    root.appendChild(h('div', { class: 'row' }, [again]));
    return root;
  }

  // ── a scenario: a situation, choices, what follows from each ──
  function Scenario(sc) {
    var stages = sc.s, at = 0, good = 0, kept = sceneState(sc.id);
    var root = h('div', { class: 'live scenario' });
    var dots = h('div', { class: 'stepbar' });
    var card = h('div', { class: 'card2' });
    stages.forEach(function () { dots.appendChild(h('span', { class: 'dot' })); });
    function show() {
      $$('.dot', dots).forEach(function (d, i) { d.classList.toggle('done', i < at); d.classList.toggle('on', i === at); });
      card.innerHTML = '';
      card.classList.remove('enter'); void card.offsetWidth; card.classList.add('enter');
      if (at >= stages.length) {
        kept.b = Math.max(kept.b || 0, good); kept.n = stages.length; keep();
        card.appendChild(h('p', { class: 'score', text: fmt(W.goodChoices, good, stages.length) }));
        card.appendChild(h('p', { class: 'stars', 'aria-hidden': 'true', text: '★'.repeat(good) + '☆'.repeat(stages.length - good) }));
        var again = h('button', { type: 'button', class: 'btn primary', text: W.again });
        again.onclick = function () { at = 0; good = 0; show(); };
        card.appendChild(again);
        return;
      }
      var st = stages[at];
      card.appendChild(h('p', { class: 'count', text: fmt(W.situationOf, at + 1, stages.length) }));
      card.appendChild(h('p', { class: 'situation', text: st[0] }));
      var list = h('div', { class: 'choices' });
      var told = h('div', { class: 'outcome', role: 'status', 'aria-live': 'polite' });
      var next = h('button', { type: 'button', class: 'btn primary', text: at + 1 < stages.length ? W.next : W.finish, hidden: true });
      var others = h('button', { type: 'button', class: 'btn quiet', text: W.otherChoices, hidden: true });
      var buttons = st[1].map(function (c, i) {
        var b = h('button', { type: 'button', class: 'choice' }, [h('span', { class: 't', text: c[0] })]);
        b.onclick = function () {
          buttons.forEach(function (x) { x.disabled = true; });
          b.classList.add('picked', c[1] > 0 ? 'good' : c[1] < 0 ? 'bad' : 'ok');
          if (c[1] > 0) good++;
          told.className = 'outcome ' + (c[1] > 0 ? 'good' : c[1] < 0 ? 'bad' : 'ok');
          told.innerHTML = '';
          told.appendChild(h('p', { class: 'judged', text: c[1] > 0 ? W.goodChoice : c[1] < 0 ? W.badChoice : W.partly }));
          told.appendChild(h('p', { text: c[2] }));
          flash(told);
          next.hidden = false; others.hidden = st[1].length < 2;
          next.focus();
        };
        list.appendChild(b);
        return b;
      });
      others.onclick = function () {
        others.hidden = true;
        st[1].forEach(function (c, i) {
          var b = buttons[i];
          if (b.classList.contains('picked')) return;
          b.classList.add('shown', c[1] > 0 ? 'good' : c[1] < 0 ? 'bad' : 'ok');
          b.appendChild(h('span', { class: 'after', text: c[2] }));
        });
      };
      next.onclick = function () { at++; show(); };
      card.appendChild(list);
      card.appendChild(told);
      card.appendChild(h('div', { class: 'row' }, [others, next]));
    }
    root.appendChild(dots);
    root.appendChild(card);
    show();
    return root;
  }

  // ── guess the number: a slider to guess with, then the answer slides into place ──
  function Estimate(sc) {
    var guesses = sc.g, at = 0, near = 0;
    var root = h('div', { class: 'live estimate' });
    var card = h('div', { class: 'card2' });
    function show() {
      card.innerHTML = '';
      card.classList.remove('enter'); void card.offsetWidth; card.classList.add('enter');
      if (at >= guesses.length) {
        card.appendChild(h('p', { class: 'score', text: fmt(W.nearScore, near, guesses.length) }));
        var again = h('button', { type: 'button', class: 'btn primary', text: W.again });
        again.onclick = function () { at = 0; near = 0; show(); };
        card.appendChild(again);
        sceneState(sc.id).b = Math.max(sceneState(sc.id).b || 0, near); keep();
        return;
      }
      var g = guesses[at], lo = g[3], hi = g[4];
      var step = (hi - lo) / 500, p = Math.pow(10, Math.floor(Math.log10(step || 1)));
      step = Math.max(p, Math.round(step / p) * p);
      var mid = lo + (hi - lo) / 2;
      var input = h('input', { type: 'range', min: lo, max: hi, step: step, value: mid, 'aria-label': W.yourGuess });
      var shown = h('output', { class: 'val', text: withUnit(mid, g[2]) });
      var track = h('div', { class: 'track' });
      var truth = h('span', { class: 'truth', 'aria-hidden': 'true' });
      var mine = h('span', { class: 'mine', 'aria-hidden': 'true' });
      var gap = h('span', { class: 'gap', 'aria-hidden': 'true' });
      track.appendChild(gap); track.appendChild(mine); track.appendChild(truth);
      var told = h('div', { class: 'outcome', role: 'status', 'aria-live': 'polite' });
      var reveal = h('button', { type: 'button', class: 'btn primary', text: W.reveal });
      var next = h('button', { type: 'button', class: 'btn', text: at + 1 < guesses.length ? W.next : W.finish, hidden: true });
      function share(v) { return Math.max(0, Math.min(100, (v - lo) / (hi - lo) * 100)); }
      function side(el, v) { el.style[rtl ? 'right' : 'left'] = share(v) + '%'; }
      input.addEventListener('input', function () { shown.textContent = withUnit(parseFloat(input.value), g[2]); side(mine, parseFloat(input.value)); });
      side(mine, mid);
      reveal.onclick = function () {
        var guess = parseFloat(input.value), off = Math.abs(guess - g[1]) / (Math.abs(g[1]) || (hi - lo) || 1);
        input.disabled = true; reveal.hidden = true; next.hidden = false;
        side(truth, guess);
        track.classList.add('shown');
        setTimeout(function () {
          side(truth, g[1]);
          var a = share(Math.min(guess, g[1])), b = share(Math.max(guess, g[1]));
          gap.style[rtl ? 'right' : 'left'] = a + '%'; gap.style.width = (b - a) + '%';
        }, 30);
        var verdict = off <= .1 ? W.spotOn : off <= .3 ? W.nearly : W.farOff;
        if (off <= .3) near++;
        told.className = 'outcome ' + (off <= .1 ? 'good' : off <= .3 ? 'ok' : 'bad');
        told.innerHTML = '';
        told.appendChild(h('p', { class: 'judged', text: verdict }));
        told.appendChild(h('p', null, [fmt(W.theAnswer, ''), h('strong', { text: withUnit(g[1], g[2]) })]));
        if (g[5]) told.appendChild(h('p', { text: g[5] }));
        next.focus();
      };
      next.onclick = function () { at++; show(); };
      card.appendChild(h('p', { class: 'count', text: fmt(W.questionOf, at + 1, guesses.length) }));
      card.appendChild(h('p', { class: 'situation', text: g[0] }));
      card.appendChild(h('label', { class: 'slider' }, [h('span', { class: 'name', text: W.yourGuess }), shown, input,
        h('span', { class: 'ends' }, [h('span', { text: withUnit(lo, g[2]) }), h('span', { text: withUnit(hi, g[2]) })])]));
      card.appendChild(track);
      card.appendChild(told);
      card.appendChild(h('div', { class: 'row' }, [reveal, next]));
    }
    root.appendChild(card);
    show();
    return root;
  }

  var scenesById = {};
  (data.scenes || []).forEach(function (s) { scenesById[s.id] = s; });
  $$('.scene').forEach(function (el) {
    var sc = scenesById[el.getAttribute('data-s')];
    if (!sc) return;
    var live = null;
    try { live = { lab: Lab, sim: Simulate, sys: System, sce: Scenario, est: Estimate }[sc.k](sc); } catch (e) { live = null; }
    if (!live) return;
    el.classList.add('playable');
    el.insertBefore(live, $('.static', el));
  });


  // ───── the scenes the model wrote: each in a sandboxed frame that says how tall it is and whether it failed ─────
  function lookNow() {
    return ui.theme || (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
  }
  function tellFrames() {
    $$('iframe.designed-frame').forEach(function (f) { try { f.contentWindow.postMessage({ dw: 'theme', theme: lookNow() }, '*'); } catch (e) { /* not ready */ } });
  }
  window.addEventListener('message', function (e) {
    var m = e.data;
    if (!m || typeof m !== 'object' || typeof m.dw !== 'string') return;
    var f = $$('iframe.designed-frame').filter(function (x) { return x.contentWindow === e.source; })[0];
    if (!f) return;
    if (m.dw === 'h' && typeof m.h === 'number' && isFinite(m.h)) f.style.height = Math.max(120, Math.min(900, Math.ceil(m.h) + 4)) + 'px';
    else if (m.dw === 'ready') { f.classList.add('ready'); tellFrames(); }
    else if (m.dw === 'fail' && !f.classList.contains('ready')) {
      // A scene that failed before it was ready gives way to its idea in words.
      f.hidden = true;
      var note = f.parentNode && $('.failed', f.parentNode);
      if (note) note.hidden = false;
    }
  });
  $$('iframe.designed-frame').forEach(function (f) { f.addEventListener('load', tellFrames); });
  if (window.matchMedia) { var mq = window.matchMedia('(prefers-color-scheme: dark)'); if (mq.addEventListener) mq.addEventListener('change', tellFrames); }

  // ───── the file's own words beside the explanation, when it is in another language ─────
  function applyBeside() {
    body.classList.toggle('beside', !!ui.beside);
    $$('.beside-switch').forEach(function (b) { b.setAttribute('aria-pressed', ui.beside ? 'true' : 'false'); $('span', b).textContent = ui.beside ? W.hideOriginal : W.showOriginal; });
  }
  $$('.beside-switch').forEach(function (b) { b.addEventListener('click', function () { ui.beside = !ui.beside; save(UI, ui); applyBeside(); }); });
  applyBeside();

  // ───── my mistakes: every question last answered wrong, to answer again ─────
  function wrongs() { return (data.quiz || []).filter(function (x) { var a = state.q[x.id]; return a && !a.r; }); }
  function countWrongs() {
    var n = wrongs().length;
    $$('.wrongs').forEach(function (el) { el.textContent = n ? String(n) : ''; });
  }
  var Mistakes = {
    render: function () {
      var slot = $('#mistakes .slot');
      if (!slot) return;
      slot.textContent = '';
      var list = wrongs();
      if (!list.length) { slot.appendChild(h('p', { class: 'note', text: W.noMistakes })); return; }
      list.forEach(function (x) {
        var box = h('div', { class: 'q mistake' }, [h('p', { class: 'ask', text: x.q })]);
        var had = state.q[x.id];
        box.appendChild(h('p', { class: 'said' }, [fmt(W.youSaid, letter(had.c) + ') ' + x.o[had.c])]));
        var opts = h('ol', { class: 'opts' }), after = h('div', { class: 'after', hidden: true });
        x.o.forEach(function (o, i) {
          var b = h('button', { type: 'button', class: 'opt' }, [h('span', { class: 'l', text: letter(i) }), h('span', { class: 't', text: o })]);
          b.addEventListener('click', function () {
            var good = i === x.a;
            $$('.opt', opts).forEach(function (c, k) { c.disabled = true; c.classList.toggle('right', k === x.a); c.classList.toggle('wrong', k === i && !good); });
            state.q[x.id] = { c: i, r: good ? 1 : 0 };
            keep(); countWrongs(); refreshMarks();
            after.hidden = false;
            after.appendChild(h('p', { class: 'verdict ' + (good ? 'good' : 'bad'), text: good ? W.right : W.wrong }));
            after.appendChild(h('p', null, [h('strong', { text: letter(x.a) + ') ' + x.o[x.a] }), x.y ? ' — ' + x.y : '']));
            after.appendChild(h('p', null, [h('a', { class: 'src', href: '#p' + x.p, text: x.w + ' · ' + W.readPart })]));
            if (good) setTimeout(function () { box.classList.add('gone'); }, 1400);
            announce(good ? W.right : W.wrong);
          });
          opts.appendChild(h('li', null, [b]));
        });
        box.appendChild(opts);
        box.appendChild(after);
        slot.appendChild(box);
      });
    },
  };
  countWrongs();

  // ───── the activities one after another ─────
  var Practice = {
    scope: 'all', list: null, at: 0, done: 0,
    render: function (scope) {
      if (scope) { this.scope = scope; this.list = null; }
      if (this.list) return this.one();
      this.setup();
    },
    pool: function () { var self = this; return (data.acts || []).filter(function (a) { return inScope(a.p, self.scope); }); },
    setup: function () {
      var self = this, slot = $('#practice .slot');
      slot.textContent = '';
      slot.appendChild(h('div', { class: 'tools' }, [scopeChoice(this.scope, function (v) { self.scope = v; self.setup(); })]));
      var pool = this.pool();
      if (!pool.length) { slot.appendChild(h('p', { class: 'empty', text: W.noActivities })); return; }
      var done = pool.filter(solved).length;
      slot.appendChild(h('div', { class: 'label', text: W.activitiesSolved + ': ' + done + '/' + pool.length }));
      var bar = h('div', { class: 'meterbar' }, [h('span')]);
      bar.firstChild.style.width = (100 * done / pool.length) + '%';
      slot.appendChild(bar);
      slot.appendChild(h('p', null, [h('button', { type: 'button', class: 'btn primary', text: W.start, onclick: function () { self.begin(); } })]));
    },
    begin: function () { this.list = this.pool(); this.at = 0; this.done = 0; this.one(); },
    one: function () {
      var self = this, slot = $('#practice .slot'), act = this.list[this.at];
      slot.textContent = '';
      if (!act) return this.result();
      slot.appendChild(h('p', { class: 'label', text: fmt(W.activityOf, this.at + 1, this.list.length) }));
      var bar = h('div', { class: 'meterbar' }, [h('span')]);
      bar.firstChild.style.width = (100 * this.at / this.list.length) + '%';
      slot.appendChild(bar);
      var last = this.at + 1 >= this.list.length;
      var next = h('button', { type: 'button', class: 'btn', text: last ? W.finish : W.skipIt, onclick: function () { self.at++; self.one(); } });
      var counted = false;
      var board = Board(act, function (right, total) {
        if (right >= 0 && right === total && !counted) { counted = true; self.done++; }
        next.textContent = last ? W.finish : W.next;
        next.className = 'btn primary';
      });
      var part = partsByN[act.p];
      slot.appendChild(h('div', { class: 'act solo' }, [
        h('p', { class: 'kind', text: kindName(act.k) }),
        act.t ? h('p', { class: 'ask', text: act.t }) : null,
        board,
        h('p', null, [h('a', { class: 'src', href: '#p' + act.p, text: act.w + (part ? ' · ' + part.t : '') })]),
      ]));
      slot.appendChild(h('p', { class: 'row' }, [next]));
      var first = $('.piece, .opt', board);
      if (first) first.focus();
    },
    result: function () {
      var self = this, slot = $('#practice .slot');
      slot.appendChild(h('div', { class: 'stat score' }, [h('div', { class: 'big', text: this.done + '/' + this.list.length }), h('div', { text: fmt(W.solvedScore, this.done, this.list.length) })]));
      slot.appendChild(h('p', { class: 'row' }, [
        h('button', { type: 'button', class: 'btn primary', text: W.tryAgain, onclick: function () { self.begin(); } }),
        h('button', { type: 'button', class: 'btn quiet', text: W.startOver, onclick: function () { self.list = null; self.setup(); } }),
      ]));
      announce(fmt(W.solvedScore, this.done, this.list.length));
      this.list = null;
    },
  };

  // ───── how far you are ─────
  var Stand = {
    render: function () {
      var slot = $('#progress .slot');
      slot.textContent = '';
      var parts = data.parts.length, read = data.parts.filter(function (p) { return state.read[p.n]; }).length;
      var asked = data.quiz.length, right = data.quiz.filter(function (x) { return state.q[x.id] && state.q[x.id].r; }).length;
      var cards = data.cards.length, known = data.cards.filter(function (c) { return state.c[c.id]; }).length;
      function stat(label, a, b) {
        var bar = h('div', { class: 'meterbar' }, [h('span')]);
        bar.firstChild.style.width = (b ? 100 * a / b : 0) + '%';
        return h('div', { class: 'stat' }, [h('div', { class: 'label', text: label }), h('div', { class: 'val', text: a + '/' + b }), bar]);
      }
      var acts = data.acts || [], solvedCount = acts.filter(solved).length;
      var grid = h('div', { class: 'statgrid' }, [stat(W.partsStudied, read, parts)]);
      if (asked) grid.appendChild(stat(W.questionsRight, right, asked));
      if (cards) grid.appendChild(stat(W.cardsKnown, known, cards));
      if (acts.length) grid.appendChild(stat(W.activitiesSolved, solvedCount, acts.length));
      slot.appendChild(grid);
      if (data.chapters.length) {
        var table = h('table', { class: 'chaps' }, [h('tr', null, [h('th', { text: W.chapter }), h('th', { text: W.partsStudied }), asked ? h('th', { text: W.questionsRight }) : null, acts.length ? h('th', { text: W.activitiesSolved }) : null])]);
        data.chapters.forEach(function (c) {
          var d = c.p.filter(function (n) { return state.read[n]; }).length;
          var qs = data.quiz.filter(function (x) { return c.p.indexOf(x.p) >= 0; });
          var r = qs.filter(function (x) { return state.q[x.id] && state.q[x.id].r; }).length;
          var as = acts.filter(function (a) { return c.p.indexOf(a.p) >= 0; });
          table.appendChild(h('tr', null, [h('td', null, [h('a', { href: '#c' + c.n, text: c.t })]), h('td', { text: d + '/' + c.p.length }), asked ? h('td', { text: qs.length ? r + '/' + qs.length : '–' }) : null,
            acts.length ? h('td', { text: as.length ? as.filter(solved).length + '/' + as.length : '–' }) : null]));
        });
        slot.appendChild(table);
      }
      var answers = h('input', { type: 'checkbox' });
      answers.checked = !!ui.answers;
      answers.addEventListener('change', function () { ui.answers = answers.checked; save(UI, ui); applyPrefs(); });
      slot.appendChild(h('p', { class: 'row' }, [
        h('button', { type: 'button', class: 'btn', text: W.print, onclick: function () { window.print(); } }),
        h('label', null, [answers, ' ' + W.withAnswers]),
      ]));
      slot.appendChild(h('p', { class: 'note', text: W.kept }));
      slot.appendChild(h('p', null, [h('button', { type: 'button', class: 'link', text: W.reset, onclick: function () {
        if (!window.confirm(W.resetSure)) return;
        state = fresh(); keep(); Trainer.queue = null; restore(); refreshMarks(); Stand.render();
      } })]));
    },
  };

  // ───── printing: the answers open when they are wanted ─────
  var opened = [];
  window.addEventListener('beforeprint', function () {
    opened = $$('details.key').filter(function (d) { return !d.open; });
    if (ui.answers) opened.forEach(function (d) { d.open = true; });
  });
  window.addEventListener('afterprint', function () { opened.forEach(function (d) { d.open = false; }); opened = []; });

  // ───── as it was left ─────
  function restore() {
    $$('.part .q').forEach(function (x) {
      var s = state.q[x.getAttribute('data-q')];
      if (s && typeof s.c === 'number') answer(x, s.c, false);
      else if (x.classList.contains('answered')) { $$('.opt', x).forEach(function (b) { b.disabled = false; b.classList.remove('right', 'wrong'); }); x.classList.remove('answered'); $('.key', x).open = false; }
    });
  }
  restore();
  refreshMarks();
  route();
})();
