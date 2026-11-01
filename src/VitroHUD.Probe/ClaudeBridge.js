(async () => {
  const c = __VITROHUD_CONFIG__, api = window['claude.web']?.LocalSessions;
  const route = () => location.protocol === 'app:' && location.host === 'localhost' && location.pathname === '/epitaxy/' + c.sessionId;
  const normalize = value => String(value).replace(/\\/g, '/').replace(/\/$/, '').toLowerCase();
  if (!api || !route()) throw Error('请在选定的 Claude Code 原会话中连接。');
  if (['getSession','getTranscriptTail','sendMessage','readSessionFile','writeSessionFile','watchSessionDirectories','onSessionDirectoryChanged','onOnEvent'].some(name=>typeof api[name]!=='function')) throw Error('当前 Claude 会话接口已变化，请更新玻璃板适配。');
  const initial = await api.getSession(c.sessionId);
  if (!initial || initial.sessionId !== c.sessionId || normalize(initial.cwd) !== normalize(c.workspace)) throw Error('原会话或工作目录已变化，请重新生成连接脚本。');
  await window.__vitroHudBridge?.stop?.();
  await window.__agentGlassBridge?.stop?.();
  await window.__agentGlassWatchProbe?.stop?.();
  const path = name => c.relative + '/' + name + '.json';
  const watchId = 'vitrohud-' + c.nonce, seen = new Map();
  let stopped = false, running = false, again = false;
  const label = e => { let value=(e.getAttribute('aria-label') || e.innerText || e.textContent || '').replace(/[\uE000-\uF8FF]/g,'').replace(/\s+/g, ' ').trim(); if(e.getAttribute('role')==='menuitemradio') value=value.replace(/\s+[1-9]$/,''); return value; };
  const shown = e => { const b = e.getBoundingClientRect(), s = getComputedStyle(e); return b.width > 0 && b.height > 0 && s.display !== 'none' && s.visibility !== 'hidden'; };
  const enabled = e => !!e && !e.disabled && e.getAttribute('aria-disabled') !== 'true';
  const editors = () => [...document.querySelectorAll('.ProseMirror[contenteditable="true"]')].filter(shown);
  const modelButton = () => { const all = [...document.querySelectorAll('[data-testid="epitaxy-cds-model-selector"]')].filter(shown); return all.length === 1 ? all[0] : null; };
  const pending = value => Array.isArray(value) ? value.length > 0 : !!value && (typeof value !== 'object' || Object.keys(value).length > 0);
  const active = s => !!(s.turnRunning || s.starting || s.awaitingBrief || pending(s.heldInput) || pending(s.pendingToolPermissions) || s.pendingRefusalFallbackPrompt || s.pendingAutoModeServerFallbackPrompt || s.pendingCwdTrustPrompt);
  async function source(write = false) {
    if (stopped || !route()) throw Error('原客户端已切换会话，请返回绑定的会话或重新连接。');
    const s = await api.getSession(c.sessionId);
    if (!s || s.sessionId !== c.sessionId || normalize(s.cwd) !== normalize(c.workspace)) throw Error('原会话身份已变化，操作已取消。');
    if (write && active(s)) throw Error('原会话正在处理消息或等待操作，请先在原客户端处理。');
    return s;
  }
  function transcript(rows) {
    const output = [], ids = new Set(); let budget = 160000;
    for (const row of rows.slice(-300)) {
      if (row.isMeta || row.isSidechain || row.isCompactSummary || row.isSynthetic || row.isVisibleInTranscriptOnly || row.parent_tool_use_id != null || !['user', 'assistant'].includes(row.type) || row.message?.role !== row.type || !row.uuid || ids.has(row.uuid)) continue;
      if (row.type === 'user' && (row.toolUseResult !== undefined || row.tool_use_result !== undefined || row.origin && typeof row.origin === 'object' && row.origin.kind !== 'human')) continue;
      ids.add(row.uuid);
      const content = row.message.content;
      if (row.type === 'user' && !row.origin && !row.promptSource && typeof content === 'string' && /^\s*<(?:command-name|local-command-(?:stdout|stderr|caveat))>/.test(content)) continue;
      if (Array.isArray(content) && content.some(b => b.type === 'tool_result')) continue;
      const parts = typeof content === 'string' ? [{kind:'body', text:content}] : Array.isArray(content) ? content.filter(b => b.type === 'text' || b.type === 'thinking').map(b => ({kind:b.type === 'thinking' ? 'reasoning' : 'body',text:b.type === 'thinking' ? b.thinking : b.text})).filter(b => typeof b.text === 'string') : [];
      if (parts.length) output.push({id:row.uuid, group:row.type === 'assistant' ? row.message.id || row.uuid : row.uuid, role:row.type, parts});
    }
    const bounded = [];
    for (const row of output.slice(-100).reverse()) {
      if (budget <= 0) break;
      row.parts = row.parts.map(p => { const text = p.text.slice(0, budget); budget -= text.length; return {...p,text}; }).filter(p => p.text.length);
      if (row.parts.length) bounded.push(row);
    }
    return bounded.reverse();
  }
  async function read() {
    const s = await source(), es = editors(), tail = await api.getTranscriptTail(c.sessionId, 300);
    const draft = es.length === 1 ? es[0].textContent || '' : '';
    return {title:s.title || 'Claude Code',model:s.model || '',effort:s.effort || '',draft,busy:active(s),canSend:es.length === 1 && enabled(es[0]) && !active(s) && !draft.trim(),canModel:enabled(modelButton()) && !active(s),partial:!!tail.hasMore,entries:transcript(tail.messages || [])};
  }
  const popupFor = trigger => {
    const controlled = document.getElementById(trigger.getAttribute('aria-controls') || '');
    if (controlled?.getAttribute('role') === 'menu' && shown(controlled)) return controlled;
    const linked = [...document.querySelectorAll('[role="menu"]')].filter(e => shown(e) && (e.getAttribute('aria-labelledby') || '').split(/\s+/).includes(trigger.id));
    return linked.length === 1 ? linked[0] : null;
  };
  const menuItems = root => root ? [...root.querySelectorAll('[role="menuitem"],[role="menuitemradio"],[role="radio"]')].filter(e => e.closest('[role="menu"]') === root && shown(e) && enabled(e) && label(e)) : [];
  function waitFor(predicate, milliseconds = 1600) {
    if (predicate()) return Promise.resolve();
    return new Promise(resolve => { const observer = new MutationObserver(() => { if (predicate()) done(); }); const timer = setTimeout(done, milliseconds); function done(){observer.disconnect();clearTimeout(timer);resolve();} observer.observe(document.documentElement,{childList:true,subtree:true,attributes:true}); });
  }
  function originalChanged(predicate, milliseconds=6500) {
    return new Promise(resolve => {
      let done=false,checking=false,again=false,off=()=>{};
      const timer=setTimeout(()=>void finish(false),milliseconds);
      const observer=new MutationObserver(()=>void check());
      async function finish(value){if(done)return;done=true;clearTimeout(timer);off();observer.disconnect();resolve(value);}
      async function check(){if(done)return;if(checking){again=true;return;}checking=true;try{do{again=false;if(await predicate()){await finish(true);break;}}while(again&&!done);}catch{await finish(false);}finally{checking=false;}}
      off=api.onOnEvent(event=>{if(event?.sessionId===c.sessionId)void check();});
      observer.observe(document.documentElement,{childList:true,subtree:true,attributes:true});
      void check();
    });
  }
  async function openMenu(steps) {
    await source(true);
    if (!Array.isArray(steps) || steps.length > 3 || steps.some(v => typeof v !== 'string' || v.length > 500)) throw Error('模型菜单路径无效。');
    const button = modelButton();
    if (!enabled(button)) throw Error('原客户端的模型选择当前不可用。');
    // Reopen from the root for every operation. A previously open popup may
    // have been dismissed when the source window lost focus.
    if (button.getAttribute('aria-expanded') === 'true') { button.click(); await waitFor(() => button.getAttribute('aria-expanded') !== 'true'); }
    button.click(); await waitFor(() => !!popupFor(button));
    let popup = popupFor(button);
    for (const step of steps) {
      const matches = menuItems(popup).filter(e => label(e) === step);
      if (matches.length !== 1 || matches[0].getAttribute('aria-haspopup') !== 'menu') throw Error('原客户端模型菜单已变化，请重新打开。');
      matches[0].click(); await waitFor(() => !!popupFor(matches[0])); popup = popupFor(matches[0]);
    }
    const items = menuItems(popup);
    if (!items.length) throw Error('未能确认原客户端的模型菜单，请重新连接。');
    return items;
  }
  async function handle(q) {
    if (q.op === 'read') return {data:await read()};
    if (q.op === 'disconnect') { await stop(); return {}; }
    await source(true);
    if (q.op === 'model-menu') return {choices:(await openMenu([])).map(label)};
    if (q.op === 'choose-model') {
      const items = await openMenu(q.menuPath || []), matches = items.filter(e => label(e) === q.value);
      if (matches.length !== 1) throw Error('该选项未在原客户端模型菜单中确认。');
      const selected = matches[0], submenu = selected.getAttribute('aria-haspopup') === 'menu';
      const wasSelected = selected.getAttribute('aria-checked') === 'true';
      const before = await source(true); selected.click();
      if (submenu) { await waitFor(() => !!popupFor(selected)); const choices=menuItems(popupFor(selected)).map(label); if(!choices.length)throw Error('未能确认原客户端的推理菜单。'); return {choices}; }
      // Original menu callbacks may finish their IPC on the next task. Read
      // again using IPC rather than an animation frame in a minimized page.
      if(await originalChanged(async()=>{const after=await source();return after.model!==before.model||after.effort!==before.effort||wasSelected;})) return {data:await read()};
      throw Error('尚未核对到模型设置变化，请检查原客户端。');
    }
    if (q.op !== 'send' || typeof q.text !== 'string' || !q.text.trim() || q.text.length > 60000) throw Error('当前操作不可用。');
    const es = editors();
    if (es.length !== 1 || !enabled(es[0]) || (es[0].textContent || '').trim()) throw Error('原客户端已有草稿或输入框不可用，未覆盖原输入。');
    await source(true);
    // The original client owns this turn. No editor mutation, policy change,
    // alternate session, API key, or automatic resubmission is involved.
    await api.sendMessage(c.sessionId,q.text,undefined,undefined,undefined,undefined,undefined,q.id);
    if(await originalChanged(async()=>{
      const tail = await api.getTranscriptTail(c.sessionId,40);
      return (tail.messages || []).some(row => row.type === 'user' && row.uuid === q.id);
    })) return {};
    throw Error('提交结果待确认，请检查原客户端。不会自动重发，侧栏草稿已保留。');
  }
  async function pump() {
    if (running) { again = true; return; }
    running = true;
    try {
      do {
        again = false;
        let q; try { const file=await api.readSessionFile(c.sessionId,path('request'),{includeHash:false}); if(!file || file.fileSize>300000) continue; q=JSON.parse(file.contents); } catch { continue; }
        if (!q || q.nonce !== c.nonce || !/^[0-9a-f-]{36}$/i.test(q.id || '') || seen.has(q.id) || !Number.isSafeInteger(q.at) || Date.now()-q.at>12000 || q.at>Date.now()+1000) continue;
        for (const [id, at] of seen) if (Date.now() - at > 120000) seen.delete(id);
        if (seen.size >= 4096) { await stop(); break; }
        seen.set(q.id, Date.now());
        let result;
        try { result={ok:true,...await handle(q)}; }
        catch(error) { const message=String(error?.message || ''); result={ok:false,error:/[\u4e00-\u9fff]/.test(message)?message:'原客户端连接暂时不可用，请重新连接。'}; }
        await api.writeSessionFile(c.sessionId,path('response'),JSON.stringify({id:q.id,nonce:c.nonce,at:Date.now(),...result}));
      } while (again && !stopped);
    } finally { running = false; }
  }
  const off = api.onSessionDirectoryChanged((sessionId, directory) => { if (!stopped && sessionId === c.sessionId && normalize(directory).endsWith(normalize(c.relative))) void pump(); });
  async function stop() { stopped=true; off(); await api.watchSessionDirectories({sessionId:c.sessionId,watchId,dirPaths:[]}); }
  window.__vitroHudBridge = {stop,version:1};
  await api.watchSessionDirectories({sessionId:c.sessionId,watchId,dirPaths:[c.relative]});
  await api.writeSessionFile(c.sessionId,path('ready'),JSON.stringify({nonce:c.nonce,at:Date.now(),version:1}));
  void pump();
  console.log('VitroHUD：原会话后台连接已就绪。关闭开发者工具后连接继续有效，退出 Claude 后需重新连接。');
})()
