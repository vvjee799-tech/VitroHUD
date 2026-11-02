import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import './build-format-fixtures.cjs';

// A separate headless Chrome profile exercises only synthetic local fixtures.
// It does not attach to the user's browser or connect to an AI client.
const chrome = process.env.CHROME_PATH || path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Google', 'Chrome', 'Application', 'chrome.exe');
await fs.access(chrome);
const fixture = await fs.readFile(new URL('./format-fixtures.html', import.meta.url));
const server = createServer((request, response) => {
  if (request.url !== '/format-fixtures.html') { response.writeHead(404); response.end(); return; }
  response.writeHead(200, {'content-type':'text/html; charset=utf-8'}); response.end(fixture);
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const profile = await fs.mkdtemp(path.join(os.tmpdir(), 'vitrohud-fixture-chrome-'));
try {
  const url = `http://127.0.0.1:${server.address().port}/format-fixtures.html`;
  const markup = await new Promise((resolve, reject) => {
    const child = spawn(chrome, ['--headless=new','--no-first-run','--no-default-browser-check','--no-proxy-server',
      '--disable-extensions','--disable-background-networking','--disable-sync','--user-data-dir='+profile,
      '--virtual-time-budget=15000','--dump-dom',url], {windowsHide:true, stdio:['ignore','pipe','ignore']});
    let output = '';
    const timeout = setTimeout(() => { child.kill(); reject(new Error('Chrome fixture deadline exceeded')); }, 60000);
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', chunk => { output += chunk; if (output.length > 64*1024*1024) { child.kill(); reject(new Error('Fixture output budget exceeded')); } });
    child.on('error', error => { clearTimeout(timeout); reject(error); });
    child.on('close', code => { clearTimeout(timeout); code === 0 ? resolve(output) : reject(new Error('Chrome fixture process exited with '+code)); });
  });
  const decode = text => text.replace(/&lt;/g,'<').replace(/&gt;/g,'>').replace(/&quot;/g,'"').replace(/&#39;/g,"'").replace(/&amp;/g,'&');
  const summary = markup.match(/<p id="summary">(.*?)<\/p>/s)?.[1];
  const raw = markup.match(/<pre id="results">(.*?)<\/pre>/s)?.[1];
  if (!summary || !raw) throw new Error('Chrome did not return fixture results');
  const records = JSON.parse(decode(raw));
  const failures = records.filter(record => !record.passed);
  const expected = Number(summary.match(/\/(\d+) passed/)?.[1]);
  console.log(JSON.stringify({summary:decode(summary),completed:records.length,failures}));
  if (failures.length || records.length !== expected) throw new Error('Page fixtures failed or did not finish');
} finally {
  server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
  const resolved = path.resolve(profile);
  if (path.dirname(resolved) === path.resolve(os.tmpdir()) && path.basename(resolved).startsWith('vitrohud-fixture-chrome-'))
    await fs.rm(resolved, {recursive:true,force:true,maxRetries:3,retryDelay:200});
}
