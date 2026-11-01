(() => {
const label = e => (e.getAttribute('aria-label') || e.innerText || e.getAttribute('title') || '').replace(/\s+/g, ' ').trim();
const visible = e => { const r = e.getBoundingClientRect(), s = getComputedStyle(e); return r.width > 0 && r.height > 0 && s.display !== 'none' && s.visibility !== 'hidden'; };
const isDeepSeekPage = () => location.protocol === 'dsh-app:' && location.host === 'app';
const isCodexPage = () => location.protocol === 'app:' && location.host === '-';
const deepSeekBinding = () => {
  if (!isDeepSeekPage()) return null;
  const panels = Array.from(document.querySelectorAll('[data-slot="main.conversation"] [data-conversation-content][data-conversation-session]'));
  if (panels.length !== 1) return null;
  const panel = panels[0], id = panel.getAttribute('data-conversation-session');
  if (!id || !id.trim()) return null;
  const selected = Array.from(document.querySelectorAll('[role="treeitem"][aria-selected="true"][data-row-key^="session:"]'));
  if (selected.length > 1 || selected.length === 1 && selected[0].getAttribute('data-row-key').slice('session:'.length) !== id) return null;
  const editors = Array.from(panel.querySelectorAll('[data-composer-input][contenteditable="true"]')).filter(visible);
  return { panel, id, editor: editors.length === 1 ? editors[0] : null };
};
const codexBinding = () => {
  if (!isCodexPage()) return null;
  const roots = Array.from(document.querySelectorAll('[data-codex-composer-root][data-composer-placement="thread"]')).filter(visible);
  if (roots.length !== 1) return null;
  const root = roots[0], portals = Array.from(root.children).filter(e => e.matches('[data-above-composer-portal][data-above-composer-conversation-id]'));
  if (portals.length !== 1) return null;
  const id = portals[0].getAttribute('data-above-composer-conversation-id') || '';
  const rawId = id.startsWith('chatgpt:') ? id.slice('chatgpt:'.length) : id;
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(rawId)) return null;
  const editors = Array.from(root.querySelectorAll('textarea,[contenteditable="true"]')).filter(visible);
  if (editors.length !== 1) return null;
  const selectors = '.thread-scroll-container,[role="feed"],[role="log"],[data-thread-find-target="conversation"],[data-chatgpt-conversation-selection-target]';
  const regions = Array.from(document.querySelectorAll(selectors)).filter(e => visible(e) && !e.closest('nav,header,button,[role="button"],[contenteditable="true"],[role="navigation"],[role="toolbar"],[role="menu"]'));
  const top = regions.filter(e => !regions.some(other => other !== e && other.contains(e)));
  if (top.length !== 1) return null;
  const chatgptRegions = regions.filter(e => e.hasAttribute('data-chatgpt-conversation-selection-target'));
  if (chatgptRegions.some(e => 'chatgpt:' + e.getAttribute('data-chatgpt-conversation-selection-target') !== id)) return null;
  return { root, id, editor: editors[0], transcriptRoot: top[0] };
};
const buttons = (root = document) => Array.from(root.querySelectorAll('button,[role="button"]')).filter(visible);
const editor = () => {
  if (isDeepSeekPage()) return deepSeekBinding()?.editor ?? null;
  if (isCodexPage()) {
    const binding = codexBinding();
    if (binding) return binding.editor;
    if (document.querySelector('[data-codex-composer-root]')) return null;
  }
  return Array.from(document.querySelectorAll('textarea,[contenteditable="true"]')).filter(e => visible(e) && !(/search|搜索/i.test([e.getAttribute('aria-label'), e.getAttribute('placeholder'), e.className].join(' ')))).at(-1);
};
const draft = e => e ? (e.tagName === 'TEXTAREA' ? e.value : e.innerText).replace(/\r\n/g, '\n') : '';
const enabled = e => !!e && !e.disabled && !e.readOnly && e.getAttribute('aria-disabled') !== 'true' && e.getAttribute('aria-readonly') !== 'true';
const sourceButtons = () => {
  if (isDeepSeekPage()) { const binding = deepSeekBinding(); return binding ? buttons(binding.panel) : []; }
  if (isCodexPage()) { const binding = codexBinding(); if (binding) return buttons(binding.root); if (document.querySelector('[data-codex-composer-root]')) return []; }
  return buttons();
};
const send = () => sourceButtons().find(e => /^(发送|发送消息|Send|Send message|提交|Submit)$/i.test(label(e)));
const stop = () => sourceButtons().find(e => /^(停止|停止生成|Stop|Stop generating|停止响应)$/i.test(label(e)));
const model = m => { const binding = codexBinding(); const candidates = buttons(binding?.root || document).filter(e => !e.closest('[role="feed"],article,[role="menu"],[role="listbox"]') && !/^(menuitem|menuitemradio|option|radio)$/.test(e.getAttribute('role') || '')); return candidates.find(e => /^(选择模型|Choose model|Model:)/i.test(label(e))) || candidates.find(e => label(e) === m) || candidates.filter(e => /^(GPT[-\s]|Claude\s|Opus\s|Sonnet\s|Haiku\s|DeepSeek[-\s])/i.test(label(e))).at(-1); };
const menu = () => Array.from(document.querySelectorAll('[role="menuitem"],[role="menuitemradio"],[role="option"],[role="radio"]')).filter(e => visible(e) && enabled(e) && label(e));
const messageSelector = '[data-message-author-role],[data-role="user"],[data-role="assistant"],[data-testid="user-message"],[data-testid="assistant-message"],.user-message,.assistant-message,[aria-label^="Message "]';
const conversationRoot = () => {
  if (isDeepSeekPage()) return deepSeekBinding()?.panel ?? null;
  const binding = codexBinding(); if (binding) return binding.transcriptRoot;
  if (isCodexPage() && document.querySelector('[data-codex-composer-root]')) return null;
  return Array.from(document.querySelectorAll('.thread-scroll-container,[role="feed"],[role="log"],' + messageSelector)).find(e => visible(e) && !e.closest('nav,header,button,[role="button"],[contenteditable="true"],[role="navigation"],[role="toolbar"],[role="menu"]'));
};
const pageIdentityInfo = root => {
  if (isDeepSeekPage()) {
    const binding = deepSeekBinding();
    return binding && binding.panel === root ? { identity: location.href + '|conversation:' + binding.id, stable: true } : { identity: '', stable: false };
  }
  if (isCodexPage()) {
    const binding = codexBinding();
    return binding && binding.transcriptRoot === root ? { identity: location.href + '|conversation:' + binding.id, stable: true } : { identity: location.href + '|' + document.title, stable: false };
  }
  const host = root && root.closest('[data-conversation-id],[data-thread-id]');
  const id = host && (host.getAttribute('data-conversation-id') || host.getAttribute('data-thread-id') || '').trim();
  if (id) return { identity: location.href + '|conversation:' + id, stable: true };
  const route = location.pathname + location.hash;
  const match = route.match(/(?:^|\/)(?:chat\/s|c|chat|chats|conversation|conversations|thread|threads|session|sessions)\/([^/?#]+)/i);
  const canonical = match && !/^(?:new|s|home|index|default)$/i.test(match[1]);
  return { identity: canonical ? location.href : location.href + '|' + document.title, stable: !!canonical };
};
const state = m => { const input = editor(), modelButton = model(m), root = conversationRoot(), binding = pageIdentityInfo(root); return { identity: binding.identity, stableIdentity: binding.stable, url: location.href, title: document.title, canRead: !!root, draft: draft(input), editor: enabled(input), send: !!send(), stop: enabled(stop()), model: label(modelButton || document.createElement('span')), modelEnabled: enabled(modelButton) }; };
const normalizeDraft = t => t.replace(/\r\n/g, '\n').replace(/\n+$/, '');
let insertionAttempted = false;
const insert = (text, expectedIdentity) => {
  if (typeof text !== 'string' || typeof expectedIdentity !== 'string' || !expectedIdentity) return false;
  const initial = state(''), target = editor();
  if (!initial.stableIdentity || initial.identity !== expectedIdentity || !enabled(target) || normalizeDraft(draft(target)) !== '') return false;
  target.focus({ preventScroll: true });
  const ready = () => {
    const current = state('');
    return current.stableIdentity && current.identity === expectedIdentity && editor() === target && enabled(target) &&
      normalizeDraft(draft(target)) === '' && (document.activeElement === target || target.contains(document.activeElement));
  };
  // Focus handlers can synchronously change the source, editor, or draft.
  if (!ready()) return false;
  if (target.tagName === 'TEXTAREA') {
    const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')?.set;
    if (!setter || !ready()) return false;
    insertionAttempted = true;
    setter.call(target, text);
    target.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
  } else {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount !== 1 || !selection.isCollapsed) return false;
    const range = selection.getRangeAt(0);
    if (!range.collapsed || !target.contains(range.startContainer) || !target.contains(range.endContainer) || !ready()) return false;
    if (isDeepSeekPage() && /[\r\n]/.test(text)) {
      // DeepSeek's controlled editor strips line breaks from insertText.
      // Route multiline text through its normal clipboard handler so that
      // the editor's own document and undo state retain those line breaks.
      if (typeof DataTransfer !== 'function' || typeof ClipboardEvent !== 'function' || !ready()) return false;
      const clipboard = new DataTransfer();
      clipboard.setData('text/plain', text.replace(/\r\n/g, '\n'));
      insertionAttempted = true;
      target.dispatchEvent(new ClipboardEvent('paste', { clipboardData: clipboard, bubbles: true, cancelable: true, composed: true }));
    } else {
      // Some controlled editors accept the command in their own input handler.
      // Always verify the actual editor state before allowing any submission.
      insertionAttempted = true;
      document.execCommand('insertText', false, text);
    }
  }
  const after = state('');
  const acceptedEditor = editor();
  return after.stableIdentity && after.identity === expectedIdentity && enabled(acceptedEditor) && normalizeDraft(draft(acceptedEditor)) === normalizeDraft(text);
};
const click = (kind, m, value, expectedIdentity = null, expectedDraft = null) => {
  const initial = state(m);
  if ((isDeepSeekPage() || isCodexPage()) && (!initial.stableIdentity || !expectedIdentity)) return false;
  if (expectedIdentity !== null && initial.identity !== expectedIdentity) return false;
  if (expectedDraft !== null && normalizeDraft(draft(editor())) !== normalizeDraft(expectedDraft)) return false;
  const e = kind === 'send' ? send() : kind === 'stop' ? stop() : kind === 'model' ? model(m) : menu().find(e => label(e) === value);
  if (!enabled(e)) return false;
  if (isDeepSeekPage()) {
    const current = state(m), binding = deepSeekBinding();
    if (!binding?.editor || !current.stableIdentity || current.identity !== expectedIdentity ||
        (kind === 'send' || kind === 'stop') && !binding.panel.contains(e) ||
        expectedDraft !== null && normalizeDraft(draft(binding.editor)) !== normalizeDraft(expectedDraft)) return false;
  }
  if (isCodexPage()) {
    const current = state(m), binding = codexBinding();
    if (!binding || !current.stableIdentity || current.identity !== expectedIdentity ||
        (kind === 'send' || kind === 'stop' || kind === 'model') && !binding.root.contains(e) ||
        expectedDraft !== null && normalizeDraft(draft(binding.editor)) !== normalizeDraft(expectedDraft)) return false;
  }
  e.click(); return true;
};

const transcript = provider => {
  const deepSeek = provider === 'DeepSeek';
  const binding = deepSeek ? deepSeekBinding() : null;
  const codex = !deepSeek && isCodexPage() ? codexBinding() : null;
  if (deepSeek && !binding) return [];
  if (!deepSeek && isCodexPage() && document.querySelector('[data-codex-composer-root]') && !codex) return [];
  const input = editor();
  const selector = messageSelector;
  const roleLabel = text => /^(?:You said|你说)\s*[:：]?$/i.test(text) ? 'user' : /^(?:ChatGPT\s*(?:said|说)|Claude\s*responded)\s*[:：]?$/i.test(text) ? 'assistant' : '';
  const hiddenHeading = e => e.matches('h1,h2,h3,h4,h5,h6,[role="heading"],.sr-only') && !!e.closest('.sr-only');
  const elementRole = e => {
    const role = (e.getAttribute('data-message-author-role') || e.getAttribute('data-role') || '').toLowerCase();
    if (role === 'user' || role === 'assistant') return role;
    const test = (e.getAttribute('data-testid') || '').toLowerCase();
    if (test === 'user-message' || e.classList.contains('user-message')) return 'user';
    if (test === 'assistant-message' || e.classList.contains('assistant-message')) return 'assistant';
    const aria = e.getAttribute('aria-label') || '';
    if (/^Message\s+(?:from\s+)?(?:you|user)\b/i.test(aria)) return 'user';
    if (/^Message\s+(?:from\s+)?(?:assistant|Claude|ChatGPT)\b/i.test(aria)) return 'assistant';
    return '';
  };
  const scopeRole = e => {
    const direct = elementRole(e); if (direct) return direct;
    const headings = [];
    for (const h of e.querySelectorAll('.sr-only')) {
      if (hiddenHeading(h)) { const role = roleLabel((h.textContent || '').replace(/\s+/g, ' ').trim()); if (role) headings.push(role); }
    }
    return headings.length === 1 ? headings[0] : '';
  };
  const candidates = deepSeek ? [] : Array.from((codex?.transcriptRoot || document).querySelectorAll(selector)).filter(e => visible(e) && !e.closest('nav,header,button,[role="button"],[contenteditable="true"],[role="navigation"],[role="toolbar"],[role="menu"],[role="menubar"]'));
  const allCandidates = new Set(candidates);
  const set = new Set(candidates.filter(e => {
    const direct = elementRole(e);
    const descendants = Array.from(e.querySelectorAll(selector)).filter(child => allCandidates.has(child) && elementRole(child));
    const descendantSet = new Set(descendants);
    const nestedRoots = descendants.filter(child => {
      let parent = child.parentElement && child.parentElement.closest(selector);
      while (parent && parent !== e) { if (descendantSet.has(parent)) return false; parent = parent.parentElement && parent.parentElement.closest(selector); }
      return true;
    });
    return direct ? nestedRoots.length <= 1 && !nestedRoots.some(child => elementRole(child) !== direct) : nestedRoots.length === 0;
  }));
  const allRoots = candidates.filter(e => {
    if (!set.has(e)) return false;
    let ancestor = e.parentElement && e.parentElement.closest(selector);
    while (ancestor && !set.has(ancestor)) ancestor = ancestor.parentElement && ancestor.parentElement.closest(selector);
    return !ancestor;
  });
  const roots = allRoots.slice(-100), rootOffset = allRoots.length - roots.length;
  const conversation = deepSeek || roots.length ? null : codex?.transcriptRoot || Array.from(document.querySelectorAll('.thread-scroll-container,[role="feed"],[role="log"]')).find(e => visible(e) && !e.closest('nav,header,[role="navigation"],[role="toolbar"],[role="menu"]'));
  if (!deepSeek && !roots.length && !conversation && !input) return [];
  const fallback = !deepSeek && !roots.length && !conversation;
  const root = conversation || document.body;
  const inputRect = fallback && input ? input.getBoundingClientRect() : null;
  const out = [], budget = { nodes: 0, chars: 0 };
  const isExhausted = () => budget.nodes >= 12000 || budget.chars >= 160000;
  const blocked = 'script,style,noscript,nav,header,footer,textarea,input,select,[contenteditable="true"],[role="navigation"],[role="toolbar"],[role="menu"],[role="menubar"],[role="listbox"]';
  const blockTags = new Set(['P', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'PRE', 'LI', 'BLOCKQUOTE', 'TR']);
  const status = /^(?:用时|已处理|已思考|思考|耗时|Thought|Thinking|Worked|Working|Reasoned|Compacted session|上下文已自动压缩|运行了命令|编辑了文件|编辑了多个文件|已查看|已使用|已搜索网页|Searched |Ran |Edited )/i;
  const hidden = e => { if (e.hidden || e.getAttribute('aria-hidden') === 'true' || e.classList.contains('sr-only')) return true; const s = getComputedStyle(e); return s.display === 'none' || s.visibility === 'hidden'; };
  const preserves = e => /(?:^|\s)whitespace-pre-wrap(?:\s|$)/.test(e.getAttribute('class') || '') || /^(?:pre|pre-wrap|break-spaces)$/.test(getComputedStyle(e).whiteSpace);
  const inRegion = node => {
    if (!inputRect) return true;
    const range = document.createRange(); range.selectNodeContents(node); const r = range.getBoundingClientRect();
    return r.width > 0 && r.height > 0 && r.left >= inputRect.left - 60 && r.bottom <= inputRect.top - 10 && r.top >= 100;
  };
  const context = e => {
    const classes = []; let reasoning = false, paragraph = false;
    for (let p = e, n = 0; p && n < 7; p = p.parentElement, n++) {
      const c = p.getAttribute('class') || ''; classes.push(c);
      reasoning ||= p.getAttribute('data-chat-group-part') === 'reasoning' || /(?:^|[\s_/-])(?:reasoning|thinking|thoughts?)(?:[\s_/-]|$)/i.test(c);
      paragraph ||= p.tagName === 'P' || /_Paragraph_|whitespace-pre-wrap|markdown/.test(c);
    }
    reasoning ||= !!e.closest('[data-chat-group-part="reasoning"]');
    return { classes: classes.join(' '), reasoning, paragraph };
  };
  const safeLink = e => { try { const href = e.getAttribute('href'); if (!href) return null; const u = new URL(href, document.baseURI); return /^(?:http:|https:)$/.test(u.protocol) ? u.href : null; } catch { return null; } };
  const sameStyle = (a, b) => a.bold === b.bold && a.italic === b.italic && a.code === b.code && a.link === b.link;
  const append = (runs, value, style, preserve, lineBreak = false) => {
    if (!value) return;
    const remaining = (runs.limit ?? (160000 - budget.chars)) - (runs.used || 0);
    if (remaining <= 0) return;
    value = value.slice(0, remaining);
    if (!preserve) {
      value = value.replace(/[\t\n\f\r ]+/g, ' ');
      const last = runs.at(-1); if (last && !runs.preservedEnd && /[ \n]$/.test(last.text) && value.startsWith(' ')) value = value.slice(1);
    }
    if (!value) return;
    const last = runs.at(-1);
    if (last && !last.lineBreak && !lineBreak && sameStyle(last, style)) last.text += value;
    else runs.push({ text: value, bold: style.bold, italic: style.italic, code: style.code, link: style.link, lineBreak });
    runs.used = (runs.used || 0) + value.length;
    runs.preservedEnd = preserve;
  };
  const cleanRuns = (runs, preserve) => {
    if (!preserve) {
      while (runs.length && !runs[0].code && !runs[0].lineBreak && !(runs[0].text = runs[0].text.replace(/^[\t\n\f\r ]+/, ''))) runs.shift();
      while (runs.length && !runs.at(-1).code && !runs.at(-1).lineBreak && !(runs.at(-1).text = runs.at(-1).text.replace(/[\t\n\f\r ]+$/, ''))) runs.pop();
    }
    for (const run of runs) delete run.lineBreak;
    return runs;
  };
  const styled = (style, e) => ({ bold: style.bold || /^(?:STRONG|B)$/.test(e.tagName), italic: style.italic || /^(?:EM|I)$/.test(e.tagName), code: style.code || e.tagName === 'CODE', link: e.tagName === 'A' ? safeLink(e) : style.link });
  const baseStyle = () => ({ bold: false, italic: false, code: false, link: null });
  const readInline = (element, preserve, nestedLists, limit = 160000 - budget.chars, startDepth = 0) => {
    const runs = []; runs.limit = limit;
    const read = (node, style, keep, depth) => {
      if (depth > 64 || isExhausted()) return;
      if (node.nodeType === Node.TEXT_NODE) { budget.nodes++; if (inRegion(node)) append(runs, node.nodeValue || '', style, keep); return; }
      if (node.nodeType !== Node.ELEMENT_NODE) return;
      const e = node;
      if (nestedLists && /^(?:UL|OL)$/.test(e.tagName)) { nestedLists.push({ element: e, depth }); return; }
      budget.nodes++;
      if (e.matches(blocked) || e.matches('button,[role="button"]') || hidden(e)) return;
      if (e.tagName === 'BR') { append(runs, '\n', style, true, true); return; }
      const next = styled(style, e), retain = keep || e.tagName === 'CODE' || preserves(e);
      const separate = /^(?:P|DIV)$/.test(e.tagName) && runs.length > 0;
      if (separate && !runs.at(-1).text.endsWith('\n')) append(runs, '\n', style, true, true);
      for (const child of e.childNodes) read(child, next, retain, depth + 1);
    };
    for (const child of element.childNodes) read(child, baseStyle(), preserve, startDepth + 1);
    return cleanRuns(runs, preserve);
  };
  const listMarker = li => {
    const list = li.parentElement;
    if (!list || list.tagName !== 'OL') return '•';
    const items = Array.from(list.children).filter(e => e.tagName === 'LI'), reverse = list.hasAttribute('reversed');
    let number = Number.parseInt(list.getAttribute('start'), 10); if (!Number.isFinite(number)) number = reverse ? items.length : 1;
    for (const item of items) { const value = Number.parseInt(item.getAttribute('value'), 10); if (Number.isFinite(value)) number = value; if (item === li) break; number += reverse ? -1 : 1; }
    return number + '.';
  };
  const listLevel = li => { let level = 0; for (let p = li.parentElement, n = 0; p && n < 64; p = p.parentElement, n++) if (/^(?:UL|OL)$/.test(p.tagName)) level++; return Math.max(1, level); };
  const makeBlock = (e, nestedLists, depth) => {
    const tag = e.tagName, kind = tag === 'PRE' ? 'code' : /^H[1-6]$/.test(tag) ? 'heading' : tag === 'LI' ? 'list-item' : tag === 'BLOCKQUOTE' ? 'quote' : tag === 'TR' ? 'table-row' : 'paragraph';
    const keep = tag === 'PRE' || preserves(e);
    let inlines = null, text = '';
    if (kind === 'table-row') {
      const cells = Array.from(e.children).filter(c => /^(?:TH|TD)$/.test(c.tagName));
      const values = []; let remaining = 160000 - budget.chars;
      for (const cell of cells) {
        if (remaining <= 0 || isExhausted()) break;
        if (values.length) remaining--;
        budget.nodes++;
        const value = readInline(cell, false, null, Math.max(0, remaining), depth + 1).map(r => r.text).join('');
        values.push(value); remaining -= value.length;
      }
      text = values.join('\t');
    } else {
      inlines = readInline(e, keep, nestedLists, 160000 - budget.chars, depth); text = inlines.map(r => r.text).join('');
    }
    const code = tag === 'PRE' ? e.querySelector('code') : null;
    const lang = (((code && code.getAttribute('class')) || '') + ' ' + (e.getAttribute('class') || '')).match(/(?:^|\s)language-([\w+.-]+)/i);
    return { kind, text, inlines: kind === 'code' || kind === 'table-row' ? null : inlines, level: kind === 'heading' ? Number(tag[1]) : kind === 'list-item' ? listLevel(e) : 0, language: lang ? lang[1] : null, marker: kind === 'list-item' ? listMarker(e) : null };
  };
  const parse = (scope, role, id, delimit, region) => {
    let current = { id, role, segments: [] }, loose = [], looseContext = null, ordinal = 0;
    const flushMessage = () => { if (current.segments.length) out.push(current); };
    const add = (text, control, paragraph, info, blocks) => {
      if (!text || (!text.trim() && blocks[0]?.kind !== 'code' && !blocks[0]?.inlines?.some(run => run.code)) || budget.chars >= 160000) return;
      const remaining = 160000 - budget.chars;
      if (text.length > remaining) {
        text = text.slice(0, remaining);
        if (blocks.length) {
          blocks[0].text = text;
          if (blocks[0].inlines) { let left = remaining; blocks[0].inlines = blocks[0].inlines.map(r => { const t = r.text.slice(0, left); left -= t.length; return { ...r, text: t }; }).filter(r => r.text); }
        }
      }
      if (!text) return;
      current.segments.push({ text, control, paragraph, classes: info.classes, reasoning: info.reasoning, blocks }); budget.chars += text.length;
    };
    const flushLoose = () => {
      if (!looseContext) return;
      const runs = cleanRuns(loose, looseContext.preserve), text = runs.map(r => r.text).join('');
      add(text, false, looseContext.info.paragraph, looseContext.info, [{ kind: 'paragraph', text, inlines: runs, level: 0, language: null, marker: null }]);
      loose = []; looseContext = null;
    };
    const walk = (node, style, keep, depth) => {
      if (depth > 64 || isExhausted()) return;
      if (node.nodeType === Node.TEXT_NODE) {
        budget.nodes++; if (!node.parentElement || !inRegion(node)) return;
        const info = context(node.parentElement);
        if (looseContext && (looseContext.info.reasoning !== info.reasoning || looseContext.info.paragraph !== info.paragraph || looseContext.preserve !== keep)) flushLoose();
        looseContext ||= { info, preserve: keep }; append(loose, node.nodeValue || '', style, keep); return;
      }
      if (node.nodeType !== Node.ELEMENT_NODE) return;
      const e = node; budget.nodes++;
      if (delimit && hiddenHeading(e)) {
        const next = roleLabel((e.textContent || '').replace(/\s+/g, ' ').trim());
        if (next) { flushLoose(); flushMessage(); current = { id: id + ':part-' + ordinal++ + '-' + next, role: next, segments: [] }; }
        return;
      }
      if (e.matches(blocked) || hidden(e)) return;
      if (e.matches('button,[role="button"]')) {
        const text = label(e); if (status.test(text) && (!region || inRegion(e))) { flushLoose(); add(text, true, false, context(e), []); } return;
      }
      // The source's message-action clock is metadata, not another paragraph.
      // Paragraph/preformatted contents use readInline and retain inline dates.
      const messageClock = deepSeek && e.parentElement?.matches('[data-clock="start"],[data-clock="end"]') &&
        Array.from(e.classList).some(c => c.endsWith('_timeStart') || c.endsWith('_timeEnd'));
      if (messageClock || e.tagName === 'TIME' && e.hasAttribute('datetime')) {
        flushLoose(); const info = context(e);
        add((e.innerText || e.textContent || '').trim(), false, false, { ...info, classes: info.classes + ' agentglass-timestamp' }, []);
        return;
      }
      if (blockTags.has(e.tagName) || /(?:^|\s)whitespace-pre-wrap(?:\s|$)/.test(e.getAttribute('class') || '')) {
        flushLoose(); const nested = e.tagName === 'LI' ? [] : null, block = makeBlock(e, nested, depth), info = context(e);
        add(block.text, false, true, info, [block]);
        if (nested) for (const list of nested) walk(list.element, baseStyle(), false, list.depth);
        return;
      }
      if (e.tagName === 'BR') { looseContext ||= { info: context(e), preserve: keep }; append(loose, '\n', style, true, true); return; }
      const boundary = /^(?:DIV|SECTION|ARTICLE|MAIN|UL|OL|TABLE|TBODY|THEAD|TFOOT)$/.test(e.tagName);
      if (boundary) flushLoose();
      const next = styled(style, e), retain = keep || e.tagName === 'CODE' || preserves(e);
      for (const child of e.childNodes) walk(child, next, retain, depth + 1);
      if (boundary) flushLoose();
    };
    if (Array.isArray(scope)) for (const node of scope) walk(node, baseStyle(), false, 1);
    else walk(scope, baseStyle(), false, 0);
    flushLoose(); flushMessage();
  };
  const parseNewestFirst = groups => {
    const chronological = [];
    for (let i = groups.length - 1; i >= 0 && !isExhausted(); i--) {
      const group = groups[i], start = out.length;
      parse(group.scope, group.role, group.id, group.delimit, group.region);
      chronological.unshift(...out.splice(start));
    }
    out.push(...chronological);
  };
  if (deepSeek) {
    const flowNodes = Array.from(binding.panel.querySelectorAll('[data-chat-flow] [data-chat-flow-kind]')).filter(e => visible(e) && !e.closest('[hidden],[aria-hidden="true"],[inert]'));
    const ids = new Set(), groups = [];
    for (const e of flowNodes) {
      const kind = e.getAttribute('data-chat-flow-kind');
      const role = kind === 'user' || kind === 'steering' ? 'user' : kind === 'assistant-step' ? 'assistant' : '';
      const key = e.getAttribute('data-chat-flow-key') || e.getAttribute('data-chat-node-key');
      if (!role || !key) continue;
      const id = 'deepseek:' + JSON.stringify([key, e.getAttribute('data-chat-group-part') || '']);
      if (ids.has(id)) return [];
      ids.add(id); groups.push({ scope: e, role, id, delimit: false, region: false });
    }
    parseNewestFirst(groups.slice(-100));
  } else if (roots.length) {
    const groups = roots.map((e, index) => {
      const role = scopeRole(e) || 'transcript';
      return { scope: e, role, id: e.getAttribute('data-message-id') || e.id || 'message-' + (rootOffset + index) + '-' + role, delimit: role === 'transcript', region: false };
    });
    parseNewestFirst(groups);
  } else {
    // Direct hidden role headings are reliable boundaries even without message containers.
    const nodes = Array.from(root.childNodes), headings = [];
    for (let i = 0; i < nodes.length; i++) {
      const e = nodes[i];
      if (e.nodeType === Node.ELEMENT_NODE && hiddenHeading(e) && roleLabel((e.textContent || '').replace(/\s+/g, ' ').trim())) headings.push(i);
    }
    if (headings.length > 1) {
      const groups = [];
      if (headings[0] > 0) groups.push({ scope: nodes.slice(0, headings[0]), role: 'transcript', id: 'loaded-transcript', delimit: true, region: fallback });
      for (let i = 0; i < headings.length; i++) groups.push({ scope: nodes.slice(headings[i], headings[i + 1] ?? nodes.length), role: 'transcript', id: 'heading-' + i, delimit: true, region: fallback });
      parseNewestFirst(groups);
    } else parse(root, 'transcript', 'loaded-transcript', true, fallback);
  }
  return out.slice(-100);
};
