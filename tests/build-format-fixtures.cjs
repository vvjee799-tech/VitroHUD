const fs = require('node:fs');
const path = require('node:path');
const script = fs.readFileSync(path.join(__dirname, '../src/VitroHUD.Probe/PageScript.js'), 'utf8');
const cases = [];
function test(name, html, verify, options = {}) { cases.push({ name, html, verify: verify.toString(), ...options }); }
function deepSeekTest(name, html, verify, options = {}) { test(name, html, verify, { provider: 'DeepSeek', deepSeek: true, ...options }); }
function codexTest(name, html, verify) { test(name, html, verify, { provider: 'ChatGPT', codex: true }); }
const editor = '<textarea aria-label="Prompt"></textarea><button aria-label="Send">Send</button><button>GPT Test Medium</button>';
const article = (body, role = 'assistant', id = 'test-message') => `<article data-message-author-role="${role}" data-message-id="${id}">${body}</article>`;
test('inline-spacing-bold-link', article('<p>Hello <strong>world</strong> ! <em>你好</em> <a href="https://example.com/guide">guide</a></p>') + editor, (r, assert) => {
  const b = r.transcript[0].segments[0].blocks[0];
  assert(b.text === 'Hello world ! 你好 guide', JSON.stringify(b));
  assert(b.inlines.some(x => x.text === 'world' && x.bold), 'bold');
  assert(b.inlines.some(x => x.text === '你好' && x.italic), 'italic');
  assert(b.inlines.some(x => x.text === 'guide' && x.link === 'https://example.com/guide'), 'link');
});
test('line-break-and-inline-code', article('<p>first<br>second <code> x </code> end</p>') + editor, (r, assert) => {
  const b = r.transcript[0].segments[0].blocks[0];
  assert(b.text === 'first\nsecond  x  end', JSON.stringify(b));
  assert(b.inlines.some(x => x.code && x.text === ' x '), 'inline-code');
});
test('code-indentation-blank-lines', article('<pre><code class="language-python">def f():\n    pass\n\n</code></pre>') + editor, (r, assert) => {
  const b = r.transcript[0].segments[0].blocks[0];
  assert(b.kind === 'code' && b.text === 'def f():\n    pass\n\n' && b.language === 'python', JSON.stringify(b));
});
test('user-preserved-whitespace', article('<div class="whitespace-pre-wrap">  user text\n\n    indented</div>', 'user') + editor, (r, assert) => {
  assert(r.transcript[0].role === 'user', 'user-role');
  assert(r.transcript[0].segments[0].blocks[0].text === '  user text\n\n    indented', JSON.stringify(r));
});
test('lists-quote-table', article('<ol start="3"><li>third<ul><li>nested</li></ul></li><li>fourth</li></ol><blockquote><p>quoted</p></blockquote><table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>') + editor, (r, assert) => {
  const b = r.transcript.flatMap(m => m.segments.flatMap(s => s.blocks));
  assert(b[0].kind === 'list-item' && b[0].marker === '3.' && b[0].level === 1, JSON.stringify(b));
  assert(b[1].marker === '•' && b[1].level === 2, 'nested-marker');
  assert(b[2].marker === '4.', 'ordered-next');
  assert(b.some(x => x.kind === 'quote' && x.text === 'quoted'), 'quote');
  assert(b.filter(x => x.kind === 'table-row').map(x => x.text).join('|') === 'A\tB|1\t2', 'table');
});
test('repeated-messages-role-order', article('<p>same</p>', 'user', 'u1') + article('<p>same</p>', 'assistant', 'a1') + article('<p>same</p>', 'user', 'u2') + editor, (r, assert) => {
  assert(r.transcript.length === 3, JSON.stringify(r));
  assert(r.transcript.map(m => m.role).join(',') === 'user,assistant,user', 'role-order');
  assert(new Set(r.transcript.map(m => m.id)).size === 3, 'stable-distinct-id');
});
test('hidden-role-boundaries', '<div class="thread-scroll-container"><h6 class="sr-only">You said:</h6><p>question</p><h6 class="sr-only">ChatGPT said:</h6><p>answer</p></div>' + editor, (r, assert) => {
  assert(r.transcript.map(m => m.role).join(',') === 'user,assistant', JSON.stringify(r));
});
test('claude-articles-hidden-role', '<div role="feed"><article aria-label="Message 1"><h6 class="sr-only">You said:</h6><p>q</p></article><article aria-label="Message 2"><h6 class="sr-only">Claude responded:</h6><p>a</p></article></div>' + editor, (r, assert) => {
  assert(r.transcript.map(m => m.role).join(',') === 'user,assistant', JSON.stringify(r));
});
test('unknown-role-body-heading-not-role', '<div role="feed"><p>You said:</p><p>actual paragraph</p></div>' + editor, (r, assert) => {
  assert(r.transcript.length === 1 && r.transcript[0].role === 'transcript', JSON.stringify(r));
});
test('read-without-editor', '<div role="log">' + article('<p>read-only content</p>') + '</div>', (r, assert) => {
  assert(r.state.canRead && !r.state.editor && r.transcript.length === 1, JSON.stringify(r));
});
test('readonly-editor-not-writable', article('<p>content</p>') + '<textarea readonly>existing draft</textarea><button aria-label="Send">Send</button>', (r, assert) => {
  assert(!r.state.editor && r.state.draft === 'existing draft', JSON.stringify(r.state));
});
test('timing-versus-body', article('<button>Thought for 3s</button><p>Thought for 3s</p><div class="reasoning-content"><p>reasoning text</p></div>') + editor, (r, assert) => {
  const s = r.transcript[0].segments;
  assert(s[0].control && !s[0].paragraph, 'timing-control');
  assert(!s[1].control && s[1].paragraph, 'body-paragraph');
  assert(s[2].reasoning, 'reasoning-context');
});
test('unsafe-links-ui-controls-hidden', article('<p><a href="javascript:void(0)">inert</a> <a href="file:///C:/private">local</a></p><button>Copy</button><div hidden>hidden</div>') + editor, (r, assert) => {
  const b = r.transcript[0].segments[0].blocks[0];
  assert(b.text === 'inert local' && b.inlines.every(x => !x.link), JSON.stringify(b));
  assert(r.transcript[0].segments.length === 1, 'no-controls-or-hidden');
});
test('nested-containers-not-duplicated', '<div data-message-author-role="assistant"><div data-testid="assistant-message"><p>one copy</p></div></div>' + editor, (r, assert) => {
  assert(r.transcript.length === 1 && r.transcript[0].segments.length === 1, JSON.stringify(r));
});
test('message-limit', Array.from({ length: 110 }, (_, i) => article('<p>' + i + '</p>', 'assistant', 'm' + i)).join('') + editor, (r, assert) => {
  assert(r.transcript.length === 100 && r.transcript[0].id === 'm10' && r.transcript[99].id === 'm109', JSON.stringify(r.transcript.map(x => x.id)));
});
test('conversation-identity', '<div role="feed" data-conversation-id="fixture-thread">' + article('<p>content</p>') + '</div>' + editor, (r, assert) => {
  assert(r.state.identity.endsWith('|conversation:fixture-thread'), JSON.stringify(r.state));
  assert(r.state.stableIdentity, 'data-id-is-stable');
});
test('weak-url-title-not-stable', article('<p>content</p>') + editor, (r, assert) => {
  assert(!r.state.stableIdentity && r.state.identity.endsWith('|Formatting fixture'), JSON.stringify(r.state));
});
test('latest-message-survives-character-budget', article('<p>' + 'x'.repeat(160100) + '</p>', 'user', 'old-large') + article('<p>latest short</p>', 'assistant', 'latest') + editor, (r, assert) => {
  assert(r.transcript.length === 2 && r.transcript[0].id === 'old-large' && r.transcript[1].id === 'latest', JSON.stringify(r.transcript.map(m => m.id)));
  assert(r.transcript[1].segments[0].text === 'latest short', 'latest text');
  assert(r.transcript.flatMap(m => m.segments).reduce((n, s) => n + s.text.length, 0) <= 160000, 'character budget');
});
test('latest-message-survives-node-budget', article('<p>' + '<span>x</span>'.repeat(6100) + '</p>', 'user', 'old-many-nodes') + article('<p>latest node</p>', 'assistant', 'latest-node') + editor, (r, assert) => {
  assert(r.transcript.at(-1)?.id === 'latest-node' && r.transcript.at(-1).segments[0].text === 'latest node', JSON.stringify(r.transcript.map(m => m.id)));
});
test('hidden-heading-latest-survives-budget', '<div class="thread-scroll-container"><h6 class="sr-only">You said:</h6><p>' + 'x'.repeat(160100) + '</p><h6 class="sr-only">ChatGPT said:</h6><p>latest heading</p></div>' + editor, (r, assert) => {
  assert(r.transcript.map(m => m.role).join(',') === 'user,assistant', JSON.stringify(r.transcript.map(m => m.role)));
  assert(r.transcript.at(-1).segments[0].text === 'latest heading', 'latest heading text');
});
const boundFeed = '<div role="feed" data-conversation-id="insertion-thread">' + article('<p>message</p>') + '</div>';
test('guarded-insert-rejects-existing-draft', boundFeed + '<textarea>existing draft</textarea><button aria-label="Send">Send</button>', (r, assert) => {
  const input = document.querySelector('textarea');
  assert(r.helpers.insert('new text', r.state.identity) === false, 'existing draft rejected');
  assert(input.value === 'existing draft', 'existing draft preserved');
});
test('guarded-insert-textarea', boundFeed + editor, (r, assert) => {
  const input = document.querySelector('textarea'); let inputs = 0;
  document.body.addEventListener('input', e => { if (e.target === input) inputs++; });
  assert(r.helpers.insert('hello\nworld', r.state.identity + '-wrong') === false && input.value === '', 'wrong identity rejected');
  assert(r.helpers.insert('hello\nworld', r.state.identity) === true, 'textarea inserted');
  assert(input.value === 'hello\nworld' && inputs === 1, 'native setter and bubbling event');
});
test('guarded-insert-focus-draft-change', boundFeed + editor, (r, assert) => {
  const input = document.querySelector('textarea');
  input.addEventListener('focus', () => { input.value = 'focus draft'; }, { once: true });
  assert(r.helpers.insert('new text', r.state.identity) === false, 'focus draft rejected');
  assert(input.value === 'focus draft', 'focus draft preserved');
});
test('guarded-insert-focus-identity-change', boundFeed + editor, (r, assert) => {
  const input = document.querySelector('textarea'), feed = document.querySelector('[role="feed"]');
  input.addEventListener('focus', () => { feed.dataset.conversationId = 'changed-thread'; }, { once: true });
  assert(r.helpers.insert('new text', r.state.identity) === false, 'focus identity change rejected');
  assert(input.value === '', 'no edit after identity change');
});
test('guarded-insert-contenteditable', boundFeed + '<div contenteditable="true" aria-label="Prompt"></div><button aria-label="Send">Send</button>', (r, assert) => {
  const input = document.querySelector('[contenteditable="true"]'); let inputs = 0;
  input.focus();
  const range = document.createRange(), selection = window.getSelection();
  range.selectNodeContents(input); range.collapse(true); selection.removeAllRanges(); selection.addRange(range);
  input.addEventListener('input', () => inputs++);
  assert(r.helpers.insert('contenteditable text', r.state.identity) === true, 'contenteditable inserted');
  assert(input.innerText === 'contenteditable text' && inputs === 1, 'execCommand delivered input');
});
test('guarded-click-rejects-changed-identity-or-draft', boundFeed + editor, (r, assert) => {
  const input = document.querySelector('textarea'), feed = document.querySelector('[role="feed"]'), button = document.querySelector('button[aria-label="Send"]'); let clicks = 0;
  button.addEventListener('click', () => clicks++);
  input.value = 'changed draft';
  assert(!r.helpers.click('send', '', null, r.state.identity, ''), 'changed draft rejected');
  input.value = ''; feed.dataset.conversationId = 'changed-thread';
  assert(!r.helpers.click('send', '', null, r.state.identity, ''), 'changed identity rejected');
  feed.dataset.conversationId = 'insertion-thread';
  assert(r.helpers.click('send', '', null, r.state.identity, '') && clicks === 1, 'unchanged source clicked once');
});
const deepSeekPanel = (content = '', id = 'session-actual', composer = '<div data-composer-input contenteditable="true"></div>') =>
  `<div data-slot="main.conversation"><div data-conversation-content data-conversation-session="${id}" data-content-phase="hero"><div data-conversation-scroll>${content}<div data-composer-seat>${composer}<button aria-label="Send">Send</button></div></div></div></div>`;
const deepSeekSelected = id => `<div role="treeitem" aria-selected="true" data-row-key="session:${id}"></div>`;

const legacyDeepSeek = (id = 'legacy-session', extra = '') => `<main data-session-id="${id}"><div class="chat-messages">${article('<p>older client question</p>', 'user', 'legacy-u')}${article('<p>older client answer</p>', 'assistant', 'legacy-a')}</div><textarea aria-label="Send a message"></textarea><button aria-label="Send">Send</button>${extra}</main>`;
deepSeekTest('deepseek-legacy-session-identity-and-messages', legacyDeepSeek(), (r, assert) => {
  assert(r.state.stableIdentity && r.state.editor && r.state.canRead, JSON.stringify(r.state));
  assert(r.transcript.map(m=>m.role).join(',') === 'user,assistant', 'legacy message roles');
  assert(r.transcript.map(m=>m.segments[0].text).join('|') === 'older client question|older client answer', 'legacy content');
});
deepSeekTest('deepseek-legacy-file-page-uses-bound-provider', legacyDeepSeek(), (r, assert) => {
  assert(r.state.stableIdentity && r.state.identity.endsWith('|conversation:legacy-session'), JSON.stringify(r.state));
  assert(r.state.editor && r.transcript.length === 2, 'file layout supports scoped legacy transcript');
}, { location: { protocol:'file:',host:'',href:'file:///D:/custom/index.html',pathname:'/D:/custom/index.html',hash:'' } });
deepSeekTest('deepseek-legacy-route-identity', legacyDeepSeek().replace(' data-session-id="legacy-session"',''), (r, assert) => {
  assert(r.state.stableIdentity && r.state.editor, 'explicit route identity accepted');
}, { location: { protocol:'http:',host:'127.0.0.1:12345',href:'http://127.0.0.1:12345/#/chat/legacy-session',pathname:'/',hash:'#/chat/legacy-session' } });
deepSeekTest('deepseek-legacy-unknown-identity-rejected', legacyDeepSeek().replace(' data-session-id="legacy-session"',''), (r, assert) => {
  assert(!r.state.stableIdentity && !r.state.editor && !r.helpers.click('send','',null,r.state.identity,''), 'no title-based write guess');
});
deepSeekTest('deepseek-legacy-conflicting-identities-rejected', legacyDeepSeek('one','<div data-conversation-id="two"></div>'), (r, assert) => {
  assert(!r.state.stableIdentity && !r.state.editor && r.transcript.length === 0, 'conflicting identities rejected');
});
deepSeekTest('deepseek-legacy-multiple-conversations-rejected', legacyDeepSeek('one') + legacyDeepSeek('two'), (r, assert) => {
  assert(!r.state.stableIdentity && !r.state.editor && !r.state.canRead, 'ambiguous main panels rejected');
});
deepSeekTest('deepseek-legacy-original-draft-protected', legacyDeepSeek().replace('</textarea>','original draft</textarea>'), (r, assert) => {
  assert(r.state.draft === 'original draft', 'actual old draft read');
  assert(!r.helpers.insert('test',r.state.identity) && document.querySelector('textarea').value === 'original draft', 'old draft not overwritten');
});
deepSeekTest('deepseek-legacy-source-change-rejected', legacyDeepSeek(), (r, assert) => {
  document.querySelector('main').dataset.sessionId = 'other'; let clicks = 0;
  document.querySelector('button').addEventListener('click',()=>clicks++);
  assert(!r.helpers.click('send','',null,r.state.identity,'') && clicks === 0, 'source changed before send');
});
deepSeekTest('deepseek-modern-slot-name-not-required', deepSeekPanel().replace('data-slot="main.conversation"','data-slot="older-main"'), (r, assert) => {
  assert(r.state.stableIdentity && r.state.editor, 'semantic markers independent of slot naming');
});
deepSeekTest('deepseek-client-renderer-error-is-not-connected', deepSeekPanel('<div data-slot="conversation.session"><div data-slot-error></div></div>'), (r, assert) => {
  assert(r.state.pageError && !r.state.stableIdentity && !r.state.editor && !r.state.canRead, 'native page failure is not mistaken for a working bridge');
  assert(!r.helpers.click('send','',null,r.state.identity,''), 'broken conversation cannot submit');
});
deepSeekTest('deepseek-mixed-era-panel-and-message-layout', deepSeekPanel('<div class="chat-messages">' + article('<p>historical message markup</p>') + '</div>'), (r, assert) => {
  assert(r.state.stableIdentity && r.state.editor, 'session panel still bound');
  assert(r.transcript.length === 1 && r.transcript[0].segments[0].text === 'historical message markup', 'semantic message roles work without flow markers');
});
deepSeekTest('deepseek-panel-identity-and-editor-scope', deepSeekSelected('session-actual') + deepSeekPanel('', 'session-actual', '<div data-composer-input contenteditable="true">source draft</div>') + '<textarea>other draft</textarea><button aria-label="Send">Other send</button>', (r, assert) => {
  assert(r.state.stableIdentity && r.state.identity === 'dsh-app://app/|conversation:session-actual' && r.state.canRead, JSON.stringify(r.state));
  assert(r.state.editor && r.state.draft === 'source draft', 'main panel editor selected');
});
deepSeekTest('deepseek-collapsed-sidebar-empty-hero', deepSeekPanel('<p>Welcome to the application</p>'), (r, assert) => {
  assert(r.state.stableIdentity && r.state.canRead && r.state.editor, JSON.stringify(r.state));
  assert(r.transcript.length === 0, 'hero is not a conversation message');
});
deepSeekTest('deepseek-ambiguous-panels-fail-closed', deepSeekPanel('', 'one') + deepSeekPanel('', 'two'), (r, assert) => {
  assert(!r.state.stableIdentity && !r.state.canRead && !r.state.editor, JSON.stringify(r.state));
  assert(!r.helpers.click('send', '', null, 'dsh-app://app/|conversation:one', ''), 'ambiguous panel cannot submit');
  assert(r.transcript.length === 0, 'ambiguous panel cannot be read');
});
deepSeekTest('deepseek-selected-row-mismatch', deepSeekSelected('other') + deepSeekPanel(), (r, assert) => {
  assert(!r.state.stableIdentity && !r.state.canRead && !r.state.editor, JSON.stringify(r.state));
  assert(!r.helpers.click('send', '', null, 'dsh-app://app/|conversation:session-actual', ''), 'mismatched row cannot submit');
});
deepSeekTest('deepseek-multiple-selected-rows', deepSeekSelected('session-actual') + deepSeekSelected('session-actual') + deepSeekPanel(), (r, assert) => {
  assert(!r.state.stableIdentity && !r.state.canRead, 'duplicate selected rows rejected');
});
deepSeekTest('deepseek-weak-hero-no-fake-greeting', '<div data-slot="main.conversation"><div data-conversation-content><p>Welcome back</p><div data-composer-input contenteditable="true"></div></div></div>', (r, assert) => {
  assert(!r.state.stableIdentity && !r.state.canRead && !r.state.editor, JSON.stringify(r.state));
  assert(r.transcript.length === 0, 'unbound hero is not a message');
});
const deepSeekFlows = '<div data-chat-flow>' +
  '<div data-chat-flow-kind="user" data-chat-flow-key="u1"><p>user request</p></div>' +
  '<div data-chat-flow-kind="steering" data-chat-node-key="u2"><p>follow-up</p></div>' +
  '<div data-chat-flow-kind="tool-call" data-chat-flow-key="tool"><p>tool output</p></div>' +
  '<div data-chat-flow-kind="assistant-step" data-chat-flow-key="assistant" data-chat-group-part="reasoning"><div><div><div><div><div><div><div><p>reasoning</p></div></div></div></div></div></div></div></div>' +
  '<div data-chat-flow-kind="assistant-step" data-chat-flow-key="assistant" data-chat-group-part="response"><p>answer</p></div>' +
  '<div data-chat-flow-kind="turn-process" data-chat-flow-key="process"><p>process status</p></div>' +
  '<div data-chat-flow-kind="turn-tail" data-chat-flow-key="tail"><p>tail status</p></div></div>';

deepSeekTest('deepseek-clock-is-metadata', deepSeekPanel('<div data-chat-flow><div data-chat-flow-kind="user" data-chat-flow-key="u-clock"><p>actual message</p><div data-clock="start"><span class="_1KfHza_timeStart">04:55</span><button>Copy</button></div></div></div>'), (r, assert) => {
  const s = r.transcript[0].segments;
  assert(s.length === 2 && s[0].text === 'actual message' && s[0].paragraph, 'body retained');
  assert(s[1].text === '04:55' && !s[1].paragraph && s[1].classes.includes('agentglass-timestamp') && s[1].blocks.length === 0, 'clock metadata separated');
});
test('semantic-time-inside-paragraph-stays-body', article('<p>At <time datetime="2026-10-31T04:55:00+08:00">04:55</time> we started.</p><time datetime="2026-10-31T05:00:00+08:00">05:00</time>') + editor, (r, assert) => {
  const s = r.transcript[0].segments;
  assert(s[0].text === 'At 04:55 we started.' && s[0].paragraph, 'inline date retained as body');
  assert(s[1].text === '05:00' && !s[1].paragraph && s[1].classes.includes('agentglass-timestamp'), 'standalone semantic time is metadata');
});
deepSeekTest('deepseek-flow-roles-reasoning-and-ids', deepSeekPanel(deepSeekFlows), (r, assert) => {
  assert(r.transcript.map(m => m.role).join(',') === 'user,user,assistant,assistant', JSON.stringify(r.transcript));
  assert(r.transcript.map(m => m.segments[0].text).join('|') === 'user request|follow-up|reasoning|answer', 'flow body order');
  assert(r.transcript[2].segments[0].reasoning && !r.transcript[3].segments[0].reasoning, 'group-part reasoning');
  assert(new Set(r.transcript.map(m => m.id)).size === 4 && r.transcript[2].id !== r.transcript[3].id, 'stable distinct flow identities');
  assert(r.transcript[2].id.includes('reasoning') && r.transcript[3].id.includes('response'), 'split flow parts identified');
});
deepSeekTest('deepseek-duplicate-flow-identity-fails-closed', deepSeekPanel('<div data-chat-flow><div data-chat-flow-kind="user" data-chat-flow-key="same"><p>one</p></div><div data-chat-flow-kind="user" data-chat-flow-key="same"><p>two</p></div></div>'), (r, assert) => {
  assert(r.transcript.length === 0, 'duplicate identities rejected');
});
deepSeekTest('deepseek-insert-focus-changes-session', deepSeekPanel(), (r, assert) => {
  const input = document.querySelector('[data-composer-input]'), panel = document.querySelector('[data-conversation-session]');
  input.addEventListener('focus', () => { panel.dataset.conversationSession = 'other-session'; }, { once: true });
  assert(!r.helpers.insert('new text', r.state.identity), 'session change rejected');
  assert(input.innerText === '', 'no insertion after session change');
});
deepSeekTest('deepseek-multiline-native-paste-handler', deepSeekPanel('', 'session-actual', '<div data-composer-input contenteditable="true" class="whitespace-pre-wrap"></div>'), (r, assert) => {
  const input = document.querySelector('[data-composer-input]'), text = 'first\n  indent\n\nlast'; let pastes = 0;
  input.addEventListener('paste', event => { event.preventDefault(); pastes++; input.textContent = event.clipboardData.getData('text/plain'); });
  assert(r.helpers.insert(text, r.state.identity), 'multiline draft accepted');
  assert(pastes === 1 && input.textContent === text, 'one paste preserves blank lines and indentation');
  assert(r.helpers.click('send', '', null, r.state.identity, text), 'exact draft can submit');
});
deepSeekTest('deepseek-multiline-loss-remains-blocked', deepSeekPanel('', 'session-actual', '<div data-composer-input contenteditable="true" class="whitespace-pre-wrap"></div>'), (r, assert) => {
  const input = document.querySelector('[data-composer-input]'), button = document.querySelector('button[aria-label="Send"]'); let sends = 0, pastes = 0;
  input.addEventListener('paste', event => { event.preventDefault(); pastes++; input.textContent = event.clipboardData.getData('text/plain').replace(/\n/g, ''); });
  button.addEventListener('click', () => sends++);
  assert(!r.helpers.insert('first\nsecond', r.state.identity) && pastes === 1, 'lossy editor response rejected without retry');
  assert(!r.helpers.click('send', '', null, r.state.identity, 'first\nsecond') && sends === 0, 'mismatched draft cannot submit');
});
deepSeekTest('deepseek-multiline-focus-new-draft-preserved', deepSeekPanel('', 'session-actual', '<div data-composer-input contenteditable="true" class="whitespace-pre-wrap"></div>'), (r, assert) => {
  const input = document.querySelector('[data-composer-input]'); let pastes = 0;
  input.addEventListener('focus', () => { input.textContent = 'new source draft'; }, { once: true });
  input.addEventListener('paste', () => pastes++);
  assert(!r.helpers.insert('first\nsecond', r.state.identity), 'draft introduced by focus handler blocks insertion');
  assert(input.textContent === 'new source draft' && pastes === 0, 'source draft preserved without paste');
});
deepSeekTest('deepseek-click-rechecks-identity-and-panel', deepSeekPanel() + '<button aria-label="Send">Other send</button>', (r, assert) => {
  const panel = document.querySelector('[data-conversation-session]'), buttons = Array.from(document.querySelectorAll('button[aria-label="Send"]'));
  let local = 0, other = 0; buttons[0].addEventListener('click', () => local++); buttons[1].addEventListener('click', () => other++);
  assert(!r.helpers.click('send', '', null, null, ''), 'identity is required');
  panel.dataset.conversationSession = 'changed';
  assert(!r.helpers.click('send', '', null, r.state.identity, ''), 'changed session rejected');
  panel.dataset.conversationSession = 'session-actual';
  assert(r.helpers.click('send', '', null, r.state.identity, '') && local === 1 && other === 0, 'only bound panel clicked');
});
deepSeekTest('deepseek-ambiguous-editor-disables-actions', deepSeekPanel('', 'session-actual', '<div data-composer-input contenteditable="true"></div><div data-composer-input contenteditable="true"></div>'), (r, assert) => {
  assert(r.state.stableIdentity && r.state.canRead && !r.state.editor, JSON.stringify(r.state));
  assert(!r.helpers.click('send', '', null, r.state.identity, ''), 'ambiguous editor cannot submit');
});
test('deepseek-mapping-rejects-remote-origin', deepSeekPanel(deepSeekFlows), (r, assert) => {
  assert(!r.state.stableIdentity && r.transcript.length === 0, 'remote origin rejected even with matching markers');
}, { provider: 'DeepSeek', location: {protocol:'https:',host:'example.invalid',href:'https://example.invalid/',pathname:'/',hash:''} });
const codexId = '01234567-89ab-cdef-0123-456789abcdef';
const codexThread = '<div data-thread-find-target="conversation">' + article('<p>thread body</p>', 'assistant', 'codex-message') + '</div>';
const codexComposer = (id = codexId, placement = 'thread', editors = '<textarea aria-label="Prompt"></textarea>') =>
  `<div data-codex-composer-root data-composer-placement="${placement}"><div data-above-composer-portal data-above-composer-conversation-id="${id}" style="display:none"></div>${editors}<button aria-label="Send">Send</button><button aria-label="Model: GPT Main">GPT Main</button></div>`;
codexTest('codex-local-id-scopes-composer-and-transcript', codexThread + codexComposer() + '<textarea>unrelated draft</textarea><button aria-label="Send">Other send</button><button aria-label="Model: GPT Other">Other model</button>', (r, assert) => {
  assert(r.state.stableIdentity && r.state.identity === 'app://-/index.html|conversation:01234567-89ab-cdef-0123-456789abcdef', JSON.stringify(r.state));
  assert(r.state.canRead && r.state.editor && r.state.draft === '' && r.state.model === 'Model: GPT Main', 'bound composer fields');
  assert(r.transcript.length === 1 && r.transcript[0].segments[0].text === 'thread body', 'bound transcript');
  let local = 0, other = 0; const b = document.querySelectorAll('button[aria-label="Send"]'); b[0].addEventListener('click', () => local++); b[1].addEventListener('click', () => other++);
  assert(r.helpers.click('send', '', null, r.state.identity, '') && local === 1 && other === 0, 'only bound send clicked');
});
codexTest('codex-chatgpt-id-matches-transcript', '<div data-thread-find-target="conversation" data-chatgpt-conversation-selection-target="' + codexId + '">' + article('<p>chat body</p>') + '</div>' + codexComposer('chatgpt:' + codexId), (r, assert) => {
  assert(r.state.stableIdentity && r.state.identity.endsWith('|conversation:chatgpt:01234567-89ab-cdef-0123-456789abcdef'), JSON.stringify(r.state));
  assert(r.transcript.length === 1 && r.transcript[0].segments[0].text === 'chat body', 'matching chat transcript');
});
codexTest('codex-conflicting-chatgpt-transcript-id', '<div data-thread-find-target="conversation" data-chatgpt-conversation-selection-target="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa">' + article('<p>chat body</p>') + '</div>' + codexComposer('chatgpt:' + codexId), (r, assert) => {
  assert(!r.state.stableIdentity && !r.state.canRead && r.transcript.length === 0, 'conflicting transcript rejected');
  assert(!r.helpers.click('send', '', null, 'wrong', ''), 'conflicting source cannot submit');
});
codexTest('codex-missing-id-fails-closed', codexThread + '<div data-codex-composer-root data-composer-placement="thread"><div data-above-composer-portal></div><textarea></textarea><button aria-label="Send">Send</button></div>', (r, assert) => {
  assert(!r.state.stableIdentity && !r.state.canRead && !r.state.editor && r.transcript.length === 0, JSON.stringify(r.state));
  assert(!r.helpers.click('send', '', null, r.state.identity, ''), 'missing id cannot submit');
});
for (const [name, id] of [['new', 'new'], ['cloud', 'cloud:task'], ['chatgpt-new', 'chatgpt:new'], ['malformed', '01234567-89ab-cdef-0123-456789abcdeg']]) {
  codexTest('codex-' + name + '-id-rejected', codexThread + codexComposer(id), (r, assert) => {
    assert(!r.state.stableIdentity && !r.state.canRead && r.transcript.length === 0, 'noncanonical id rejected');
  });
}
codexTest('codex-duplicate-visible-composer-roots', codexThread + codexComposer() + codexComposer(), (r, assert) => {
  assert(!r.state.stableIdentity && !r.state.editor && !r.state.canRead, JSON.stringify(r.state));
  assert(!r.helpers.click('send', '', null, r.state.identity, ''), 'duplicate roots cannot submit');
});
codexTest('codex-side-placement-excluded', codexThread + codexComposer() + codexComposer('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'side', '<textarea>side draft</textarea>'), (r, assert) => {
  assert(r.state.stableIdentity && r.state.editor && r.state.draft === '' && r.state.model === 'Model: GPT Main', JSON.stringify(r.state));
  const side = document.querySelector('[data-composer-placement="side"] button[aria-label="Send"]'); let clicks = 0; side.addEventListener('click', () => clicks++);
  assert(r.helpers.click('send', '', null, r.state.identity, '') && clicks === 0, 'side composer excluded');
});
codexTest('codex-side-only-is-not-main', codexThread + codexComposer(codexId, 'side'), (r, assert) => {
  assert(!r.state.stableIdentity && !r.state.editor && !r.state.canRead, JSON.stringify(r.state));
});
codexTest('codex-ambiguous-editors', codexThread + codexComposer(codexId, 'thread', '<textarea></textarea><textarea></textarea>'), (r, assert) => {
  assert(!r.state.stableIdentity && !r.state.editor && !r.state.canRead, 'multiple editors rejected');
  assert(!r.helpers.click('send', '', null, r.state.identity, ''), 'ambiguous editor cannot submit');
});
codexTest('codex-ambiguous-transcript-regions', codexThread + '<div data-thread-find-target="conversation"><p>other conversation</p></div>' + codexComposer(), (r, assert) => {
  assert(!r.state.stableIdentity && !r.state.canRead && r.transcript.length === 0, 'multiple transcript regions rejected');
});
codexTest('codex-nested-transcript-region-is-single', '<div role="feed"><div data-thread-find-target="conversation">' + article('<p>nested body</p>') + '</div></div>' + codexComposer(), (r, assert) => {
  assert(r.state.stableIdentity && r.state.canRead && r.transcript.length === 1, 'nested regions represent one conversation');
});
codexTest('codex-duplicate-id-portals', codexThread + '<div data-codex-composer-root data-composer-placement="thread"><div data-above-composer-portal data-above-composer-conversation-id="' + codexId + '"></div><div data-above-composer-portal data-above-composer-conversation-id="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"></div><textarea></textarea><button aria-label="Send">Send</button></div>', (r, assert) => {
  assert(!r.state.stableIdentity && !r.state.canRead, 'multiple portal identities rejected');
});
codexTest('codex-focus-changing-session-rejected', codexThread + codexComposer(), (r, assert) => {
  const input = document.querySelector('textarea'), portal = document.querySelector('[data-above-composer-portal]');
  input.addEventListener('focus', () => { portal.dataset.aboveComposerConversationId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'; }, { once: true });
  assert(!r.helpers.insert('new text', r.state.identity) && input.value === '', 'focus session change rejected');
});
codexTest('codex-click-changing-session-rejected', codexThread + codexComposer(), (r, assert) => {
  const portal = document.querySelector('[data-above-composer-portal]'), button = document.querySelector('button[aria-label="Send"]'); let clicks = 0;
  button.addEventListener('click', () => clicks++); portal.dataset.aboveComposerConversationId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
  assert(!r.helpers.click('send', '', null, r.state.identity, '') && clicks === 0, 'changed session cannot submit');
});
codexTest('codex-legacy-layout-stays-read-only', '<div role="feed">' + article('<p>legacy body</p>') + '</div>' + editor, (r, assert) => {
  assert(!r.state.stableIdentity && r.state.canRead && r.transcript.length === 1, JSON.stringify(r.state));
  assert(!r.helpers.click('send', '', null, r.state.identity, ''), 'weak identity cannot submit');
});
test('codex-marker-requires-app-origin', codexThread + codexComposer(), (r, assert) => {
  assert(!r.state.stableIdentity, 'non-Codex origin rejected');
});
const style = '<style>body{font:16px sans-serif}.sr-only{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0,0,0,0)}.whitespace-pre-wrap,code{white-space:pre-wrap}textarea{width:400px;height:50px}[contenteditable="true"]{width:400px;min-height:30px}article{padding:5px}table{border-collapse:collapse}td,th{padding:5px}</style>';
const pages = cases.map((c, i) => {
  const fixtureLocation = c.location ? JSON.stringify(c.location) : c.deepSeek ? JSON.stringify({ protocol: 'dsh-app:', host: 'app', href: 'dsh-app://app/', pathname: '/', hash: '' }) : c.codex ? JSON.stringify({ protocol: 'app:', host: '-', href: 'app://-/index.html', pathname: '/index.html', hash: '' }) : 'window.location';
  const boundScript = script.replace('(() => {', '(() => { const clientProvider = ' + JSON.stringify(c.provider || 'ChatGPT') + ';');
  const run = `(location=>{let checks=0;try{const result=${boundScript}\nreturn {transcript:transcript(${JSON.stringify(c.provider || 'ChatGPT')}),state:state('GPT Test Medium'),helpers:{insert,click}};})(),verify=(${c.verify});verify(result,(ok,msg)=>{checks++;if(!ok)throw Error(msg)});parent.postMessage({fixture:${i},name:${JSON.stringify(c.name)},passed:true,checks},parent.location.origin)}catch(e){parent.postMessage({fixture:${i},name:${JSON.stringify(c.name)},passed:false,error:String(e)},parent.location.origin)}})(${fixtureLocation});`;
  return '<!doctype html><meta charset="utf-8"><title>Formatting fixture</title>' + style + c.html + '<script>' + run.replace(/<\/script/gi, '<\\/script') + '</script>';
});
const harness = `<!doctype html><meta charset="utf-8"><title>VitroHUD extraction checks</title><style>body{font:16px sans-serif;padding:20px;background:#18211e;color:#eee}iframe{width:700px;height:700px;border:0}.pass{color:#b9dbac}.fail{color:#ffb8ac}</style><h1>VitroHUD extraction checks</h1><p id="summary">Running ${cases.length} fixtures…</p><pre id="results"></pre><div id="fixtures"></div><script>const pages=${JSON.stringify(pages).replace(/<\/script/gi, '<\\/script')},results=[];addEventListener('message',e=>{if(e.origin!==location.origin||!Number.isInteger(e.data?.fixture))return;const i=e.data.fixture;if(e.source!==document.querySelectorAll('iframe')[i]?.contentWindow)return;results[i]=e.data;document.getElementById('results').textContent=JSON.stringify(results.filter(Boolean),null,2);const done=results.filter(Boolean);document.getElementById('summary').textContent=done.filter(x=>x.passed).length+'/'+pages.length+' passed; '+done.filter(x=>!x.passed).length+' failed; '+done.reduce((a,x)=>a+(x.checks||0),0)+' assertions';});pages.forEach(p=>{const f=document.createElement('iframe');f.srcdoc=p;document.getElementById('fixtures').append(f)});</script>`;
fs.writeFileSync(path.join(__dirname, 'format-fixtures.html'), harness);
console.log(JSON.stringify({ fixtures: cases.length, path: path.join(__dirname, 'format-fixtures.html') }));
