// explorer-header.test.js — guards the CA Explorer's environment header (ticket 16d140e9).
//
// Run:  node Terminal/test/explorer-header.test.js
//
// The header used to read "📦 Clarion10v8 · clbrws" — a root FOLDER name and a solution name run together,
// which the Owner could not decode. It now shows four labelled lines, like the Assistant panel's rows:
//   APP      the open .app's full path (SOLUTION + the .sln when no app is open)  — click: Windows Explorer
//   VERSION  the version NAME ("Clarion 10 Active And Updated")
//   ROOT     that version's root folder                                            — click: Windows Explorer
//   RED      the active .red (unchanged behaviour: click opens it in the CA Editor)
//
// Zero-dependency (no jsdom). Like its neighbours this EXTRACTS the page's real code — the section between
// the "Environment header (16d140e9)" markers in modern-data-pad.html — and runs it against a small fake
// document. It also checks the page and host agree on the message shape, and that the host's click handler
// reads only WHICH line was clicked, never a path.

const fs = require('fs');
const path = require('path');

const HTML_PATH = process.argv[2] || path.join(__dirname, '..', 'modern-data-pad.html');
const CS_PATH = path.join(__dirname, '..', 'ModernDataPad.cs');
const html = fs.readFileSync(HTML_PATH, 'utf8');
const cs = fs.readFileSync(CS_PATH, 'utf8');

let failures = 0, assertions = 0;
function check(ok, what) {
    assertions++;
    if (!ok) { failures++; console.log('  FAIL ' + what); }
    else console.log('  ok   ' + what);
}
function eq(actual, expected, what) {
    const ok = actual === expected;
    check(ok, what + (ok ? '' : '  (got ' + JSON.stringify(actual) + ', want ' + JSON.stringify(expected) + ')'));
}

// ---------- fake DOM ----------
function makeEl(id) {
    const attrs = {}, listeners = {};
    return {
        id, textContent: '', title: '', className: '',
        setAttribute(k, v) { attrs[k] = String(v); },
        getAttribute(k) { return Object.prototype.hasOwnProperty.call(attrs, k) ? attrs[k] : null; },
        removeAttribute(k) { delete attrs[k]; },
        addEventListener(t, fn) { (listeners[t] = listeners[t] || []).push(fn); },
        click() { (listeners.click || []).forEach(fn => fn.call(this, {})); },
    };
}

// ---------- extract ----------
const START = '// ===================== Environment header (16d140e9)';
const END = '// ===================== end Environment header';
const a = html.indexOf(START), b = html.indexOf(END);
check(a >= 0 && b > a, 'page has the Environment header section');
if (a < 0 || b <= a) { finish(); }

const section = html.slice(a, b);
const posted = [];
const els = {};
['hdrAppLabel', 'hdrApp', 'hdrVersion', 'hdrRoot'].forEach(id => { els[id] = makeEl(id); });
const document = { getElementById: id => els[id] || null };
const postToHost = obj => posted.push(obj);
const api = new Function('document', 'postToHost',
    section + '\nreturn { renderEnvHeader: renderEnvHeader, wireEnvHeader: wireEnvHeader, markEnvUnreachable: markEnvUnreachable };')(document, postToHost);

const LRM = '\u200E', DASH = '\u2014';
const isLink = el => / link\b/.test(el.className);

console.log('-- render: an app is open');
api.wireEnvHeader();
api.renderEnvHeader({
    appLabel: 'APP', appPath: 'C:\\Apps\\School\\clbrws.app',
    versionName: 'Clarion 10 Active And Updated', rootPath: 'C:\\Clarion10v8',
});
eq(els.hdrAppLabel.textContent, 'APP', 'label APP');
eq(els.hdrApp.textContent, LRM + 'C:\\Apps\\School\\clbrws.app' + LRM, 'APP shows the full path (LRM-wrapped for rtl truncation)');
check(els.hdrApp.title.indexOf('C:\\Apps\\School\\clbrws.app') === 0, 'APP tooltip starts with the full path');
check(isLink(els.hdrApp), 'APP is clickable');
eq(els.hdrVersion.textContent, 'Clarion 10 Active And Updated', 'VERSION shows the version NAME');
check(!isLink(els.hdrVersion), 'VERSION is not clickable');
eq(els.hdrRoot.textContent, LRM + 'C:\\Clarion10v8' + LRM, 'ROOT shows the root folder');
check(isLink(els.hdrRoot), 'ROOT is clickable');

console.log('-- clicks post WHICH line, never a path');
posted.length = 0;
els.hdrApp.click();
els.hdrRoot.click();
eq(JSON.stringify(posted), JSON.stringify([{ action: 'openHeaderPath', which: 'app' }, { action: 'openHeaderPath', which: 'root' }]),
   'APP -> which:app, ROOT -> which:root');
check(posted.every(m => !('path' in m)), 'no path in the click message');

console.log('-- host reports the path unreachable');
api.markEnvUnreachable('app', 'C:\\Apps\\School\\clbrws.app');
eq(els.hdrApp.textContent, DASH, 'unreachable APP -> dash');
check(/Not reachable/.test(els.hdrApp.title) && els.hdrApp.title.indexOf('C:\\Apps\\School\\clbrws.app') === 0,
      'tooltip names the path and says it is not reachable');
check(!isLink(els.hdrApp), 'unreachable APP is no longer a link');
check(isLink(els.hdrRoot), 'ROOT untouched');
posted.length = 0;
els.hdrApp.click();
eq(posted.length, 0, 'clicking the unreachable line posts nothing');
api.markEnvUnreachable('bogus', 'x');
check(isLink(els.hdrRoot), 'an unknown line name changes nothing');
check(/msg\.type === 'envHeaderUnreachable'\)\s*\{[\s\S]{0,200}?markEnvUnreachable\(msg\.which, msg\.path\)/.test(html),
      'page handles envHeaderUnreachable');
check(/"envHeaderUnreachable"/.test(cs), 'host posts envHeaderUnreachable');
check(/_hdrOpenGate\.TryBegin\(which/.test(cs) && /finally \{ _hdrOpenGate\.End\(which/.test(cs),
      'host gates the click and always releases the gate');

console.log('-- render: solution only');
api.renderEnvHeader({ appLabel: 'SOLUTION', appPath: 'C:\\Apps\\School\\clbrws.sln', versionName: 'Clarion 11', rootPath: 'C:\\Clarion11' });
eq(els.hdrAppLabel.textContent, 'SOLUTION', 'label SOLUTION when only a solution is open');
eq(els.hdrApp.textContent, LRM + 'C:\\Apps\\School\\clbrws.sln' + LRM, 'SOLUTION shows the .sln path');

console.log('-- render: nothing known');
api.renderEnvHeader({ appLabel: 'APP', appPath: null, versionName: null, rootPath: '' });
eq(els.hdrAppLabel.textContent, 'APP', 'label back to APP');
eq(els.hdrApp.textContent, DASH, 'unknown APP -> dash');
eq(els.hdrVersion.textContent, DASH, 'unknown VERSION -> dash');
eq(els.hdrRoot.textContent, DASH, 'empty ROOT -> dash');
check(!isLink(els.hdrApp) && !isLink(els.hdrRoot), 'unknown lines are not styled as links');
check(/\bunknown\b/.test(els.hdrApp.className) && /\bunknown\b/.test(els.hdrVersion.className), 'unknown lines carry the muted style');
posted.length = 0;
els.hdrApp.click();
els.hdrRoot.click();
eq(posted.length, 0, 'clicking an unknown line posts nothing');

api.renderEnvHeader(undefined);
eq(els.hdrApp.textContent, DASH, 'a missing payload renders dashes, does not throw');

console.log('-- markup and wiring');
const hdr = (/<div id="envHeader"[\s\S]*?\n    <\/div>/.exec(html) || [''])[0];
['hdrAppLabel', 'hdrApp', 'hdrVersion', 'hdrRoot', 'redBannerPath'].forEach(id =>
    check(hdr.indexOf('id="' + id + '"') >= 0, 'header block contains #' + id));
['Version', 'Root', 'Red'].forEach(l =>
    check(hdr.indexOf('<span class="env-label">' + l + '</span>') >= 0, 'label ' + l.toUpperCase() + ' (uppercased by CSS)'));
check(/\.env-label\s*\{[^}]*text-transform:\s*uppercase/.test(html), '.env-label is uppercase');
check(/body\.dark \.env-hdr\s*\{/.test(html) && /body\.dark \.env-label\s*\{/.test(html), 'dark theme covers the header');
check(html.indexOf('id="verText"') < 0 && html.indexOf('setVerBanner') < 0, 'old one-line banner is gone');
check(/msg\.type === 'setVersionInfo'\)\s*\{[\s\S]{0,400}?renderEnvHeader\(msg\)/.test(html), 'setVersionInfo renders the header');
check(/msg\.type === 'setExplorerData'\)\s*\{[\s\S]{0,800}?renderEnvHeader\(msg\)/.test(html), 'setExplorerData renders the header');
check(/wireEnvHeader\(\);/.test(html.slice(b)), 'wireEnvHeader() is called at startup');

console.log('-- host side agrees');
['appLabel', 'appPath', 'versionName', 'rootPath'].forEach(k =>
    check(cs.indexOf('data["' + k + '"]') >= 0, 'host posts ' + k));
const handler = (/action == "openHeaderPath"\)\s*\{([\s\S]*?)\n\s*\}/.exec(cs) || [null, ''])[1];
check(handler.length > 0, 'host handles openHeaderPath');
check(/ExtractJsonValue\(json, "which"\)/.test(handler) && !/ExtractJsonValue\(json, "path"\)/.test(handler),
      'openHeaderPath reads only "which" from the page');
const opener = (/private void OpenHeaderPathInExplorer\([\s\S]*?\n        \}/.exec(cs) || [''])[0];
check(/TryBuildExplorerArgs\(/.test(opener), 'the path is validated before launch');
check(/UseShellExecute = false/.test(opener) && /"explorer\.exe"/.test(opener) && !/cmd(\.exe)?"/i.test(opener),
      'explorer.exe started directly, no shell, no cmd.exe');

finish();

function finish() {
    console.log('');
    console.log(failures === 0 ? 'PASS  ' + assertions + ' assertions' : 'FAIL  ' + failures + ' of ' + assertions + ' assertions');
    process.exit(failures === 0 ? 0 : 1);
}
