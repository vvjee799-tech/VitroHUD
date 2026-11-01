import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import vm from 'node:vm';
import crypto from 'node:crypto';

const config = {sessionId:'local_11111111-2222-4333-8444-555555555555',workspace:'E:\\fixture',relative:'.agentglass-bridge-fixture',nonce:'a'.repeat(32)};
const source = (await fs.readFile(new URL('../src/VitroHUD.Probe/ClaudeBridge.js',import.meta.url),'utf8')).replace('__VITROHUD_CONFIG__',JSON.stringify(config));
const files=new Map(), directoryEvents=new Set(), sessionEvents=new Set();
let draft='', sends=0, listenerCount=0, selectedModel='sonnet', effort='medium', active=false, sequence=0;
const rows=[
  {type:'user',uuid:'u1',origin:{kind:'human'},message:{role:'user',content:'repeat'}},
  {type:'assistant',uuid:'a1',message:{id:'a1',role:'assistant',content:[{type:'text',text:'**answer**\n\n```python\n  a=1\n```'}]}},
  {type:'user',uuid:'u2',message:{role:'user',content:'repeat'}},
  {type:'user',uuid:'meta',isMeta:true,message:{role:'user',content:'hidden'}},
  {type:'user',uuid:'tool',message:{role:'user',content:[{type:'text',text:'hidden'},{type:'tool_result',content:'hidden'}]}},
  {type:'user',uuid:'command',message:{role:'user',content:'<command-name>/model</command-name>'}},
  {type:'user',uuid:'stdout',message:{role:'user',content:'<local-command-stdout>status</local-command-stdout>'}},
  {type:'user',uuid:'literal',origin:{kind:'human'},message:{role:'user',content:'<command-name>literal user text</command-name>'}},
  {type:'user',uuid:'agent',origin:{kind:'agent'},message:{role:'user',content:'hidden'}},
  {type:'system',uuid:'system',message:{role:'system',content:'hidden'}},
  {type:'assistant',uuid:'a1',message:{id:'a1',role:'assistant',content:[{type:'text',text:'duplicate hidden'}]}},
];
function element(id,role,text,attributes={}){
  return {id,textContent:text,innerText:text,disabled:false,...attributes,
    getAttribute(name){if(name==='id')return this.id;if(name==='role')return role;return this.attrs?.[name]??null;},
    getBoundingClientRect(){return {width:100,height:30};},
    closest(){return this.owner;},
    querySelectorAll(){return this.children||[];},
  };
}
const editor=element('editor','textbox','');
Object.defineProperty(editor,'textContent',{get:()=>draft});
const trigger=element('trigger','button','Model');trigger.attrs={'aria-expanded':'false','aria-controls':'root'};
const root=element('root','menu',''),sub=element('sub','menu','');
const sonnet=element('sonnet','menuitemradio','Sonnet 5.5 Description 3'),opus=element('opus','menuitemradio','Opus 5.5 Description 2');
const effortItem=element('effort','menuitem','Effort Medium');effortItem.attrs={'aria-haspopup':'menu','aria-controls':'sub'};
root.children=[sonnet,opus,effortItem];root.children.forEach(e=>e.owner=root);
const low=element('low','menuitemradio','Low'),medium=element('medium','menuitemradio','Medium Recommended');sub.children=[low,medium];sub.children.forEach(e=>e.owner=sub);
let popup=null;
trigger.click=()=>{if(trigger.attrs['aria-expanded']==='true'){trigger.attrs['aria-expanded']='false';popup=null;}else{trigger.attrs['aria-expanded']='true';popup=root;}};
effortItem.click=()=>{popup=sub;};
for(const [e,value] of [[sonnet,'sonnet'],[opus,'opus'],[low,'low'],[medium,'medium']]){
  e.click=()=>{popup=null;trigger.attrs['aria-expanded']='false';setTimeout(()=>{if(e===low||e===medium){effort=value;effortItem.innerText='Effort '+value;}else selectedModel=value;for(const listener of sessionEvents)listener({sessionId:config.sessionId,type:'session_updated'});},15);};
}
const document={documentElement:{},hidden:true,
  querySelectorAll(selector){if(selector.startsWith('.ProseMirror'))return [editor];if(selector.startsWith('[data-testid='))return [trigger];if(selector==='[role="menu"]')return popup?[popup]:[];return [];},
  getElementById(id){const result=id==='root'?root:id==='sub'?sub:null;return result===popup?result:null;},
};
const api={
  async getSession(){return {sessionId:config.sessionId,cwd:config.workspace,title:'fixture',model:selectedModel,effort,turnRunning:active};},
  async getTranscriptTail(){return {messages:rows,hasMore:false};},
  async readSessionFile(id,path){const contents=files.get(path)||'{}';return {contents,fileSize:Buffer.byteLength(contents)};},
  async writeSessionFile(id,path,contents){files.set(path,contents);queueMicrotask(()=>{for(const listener of directoryEvents)listener(id,config.relative);});return {status:'ok'};},
  async watchSessionDirectories(){},
  onSessionDirectoryChanged(listener){directoryEvents.add(listener);return ()=>directoryEvents.delete(listener);},
  onOnEvent(listener){sessionEvents.add(listener);listenerCount++;return ()=>{sessionEvents.delete(listener);listenerCount--;};},
  async sendMessage(id,text,images,tools,attachments,priority,gates,messageId){
    assert.equal(id,config.sessionId);assert.equal(images,undefined);assert.equal(tools,undefined);assert.equal(attachments,undefined);sends++;
    setTimeout(()=>{rows.push({type:'user',uuid:messageId,origin:{kind:'human'},message:{role:'user',content:text}});for(const listener of sessionEvents)listener({sessionId:id,type:'user_message'});},15);
  },
};
const location={protocol:'app:',host:'localhost',pathname:'/epitaxy/'+config.sessionId};
const context={window:{'claude.web':{LocalSessions:api}},location,document,crypto,console:{log(){}},getComputedStyle:()=>({display:'block',visibility:'visible'}),MutationObserver:class{observe(){}disconnect(){}},setTimeout,clearTimeout};
await vm.runInNewContext(source,context);
async function request(op,extra={}){
  const id=crypto.randomUUID();const q={id,nonce:config.nonce,at:Date.now(),op,...extra};
  files.set(config.relative+'/request.json',JSON.stringify(q));
  for(const listener of directoryEvents)listener(config.sessionId,config.relative);
  for(let i=0;i<150;i++){
    await new Promise(r=>setTimeout(r,5));
    const result=JSON.parse(files.get(config.relative+'/response.json')||'{}');
    if(result.id===id)return {result,q};
  }
  throw Error('Fixture request timeout: '+op);
}
let r=(await request('read')).result;
assert.equal(r.ok,true);assert.equal(r.data.entries.length,4);assert.equal(r.data.entries[0].parts[0].text,'repeat');assert.equal(r.data.entries[2].parts[0].text,'repeat');assert.equal(r.data.canSend,true);
assert.equal(r.data.entries[3].id,'literal');
draft='do not overwrite';r=(await request('send',{text:'test'})).result;assert.equal(r.ok,false);assert.equal(sends,0);assert.equal(draft,'do not overwrite');draft='';
active=true;r=(await request('send',{text:'test'})).result;assert.equal(r.ok,false);assert.equal(sends,0);active=false;
location.pathname='/epitaxy/local_other';r=(await request('send',{text:'test'})).result;assert.equal(r.ok,false);assert.equal(sends,0);location.pathname='/epitaxy/'+config.sessionId;
r=(await request('model-menu')).result;assert.equal(r.ok,true);assert.deepEqual(r.choices,['Sonnet 5.5 Description','Opus 5.5 Description','Effort Medium']);
r=(await request('choose-model',{value:'unknown',menuPath:[]})).result;assert.equal(r.ok,false);assert.equal(selectedModel,'sonnet');
r=(await request('choose-model',{value:'Opus 5.5 Description',menuPath:[]})).result;assert.equal(r.ok,true);assert.equal(r.data.model,'opus');assert.equal(listenerCount,0);
r=(await request('choose-model',{value:'Effort Medium',menuPath:[]})).result;assert.deepEqual(r.choices,['Low','Medium Recommended']);
r=(await request('choose-model',{value:'Low',menuPath:['Effort Medium']})).result;assert.equal(r.ok,true);assert.equal(r.data.effort,'low');assert.equal(listenerCount,0);
const sent=await request('send',{text:'single submission'});assert.equal(sent.result.ok,true);assert.equal(sends,1);assert.equal(rows.at(-1).uuid,sent.q.id);assert.equal(listenerCount,0);
files.set(config.relative+'/request.json',JSON.stringify(sent.q));for(const listener of directoryEvents)listener(config.sessionId,config.relative);await new Promise(r=>setTimeout(r,30));assert.equal(sends,1);
files.set(config.relative+'/request.json',JSON.stringify({...sent.q,id:crypto.randomUUID(),at:Date.now()-20000}));for(const listener of directoryEvents)listener(config.sessionId,config.relative);await new Promise(r=>setTimeout(r,30));assert.equal(sends,1);
files.set(config.relative+'/request.json',JSON.stringify({...sent.q,id:crypto.randomUUID(),nonce:'wrong'}));for(const listener of directoryEvents)listener(config.sessionId,config.relative);await new Promise(r=>setTimeout(r,30));assert.equal(sends,1);
await request('disconnect');assert.equal(directoryEvents.size,0);assert.equal(sessionEvents.size,0);
console.log(JSON.stringify({passed:true,checks:['source-route','original-draft','busy-session','private-channel-nonce','request-expiry','no-duplicate-send','original-message-receipt','delayed-model-readback','menu-owner-and-path','model-shortcut-label','transcript-internal-rows','literal-user-tags','repeated-user-messages','listener-cleanup']}));
