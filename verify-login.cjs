const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const html = fs.readFileSync('Jellyfin.Plugin.TenPlay/Configuration/configPage.html', 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
const nodes = new Map();
function element(id) {
  if (!nodes.has(id)) nodes.set(id, { style: {}, handlers: {}, addEventListener(name, fn) { this.handlers[name] = fn; }, replaceChildren() {} });
  return nodes.get(id);
}
let poll;
let failure = false;
let completeRequests = 0;
const context = vm.createContext({
  document: { querySelector: element, createElement: () => ({}) },
  Dashboard: { showLoadingMsg() {}, hideLoadingMsg() {} },
  console: { error() {} }, Date,
  setInterval(fn) { poll = fn; return 1; }, clearInterval() {},
  ApiClient: {
    getUrl: value => value,
    async fetch(request) {
      if (request.url.endsWith('/start')) return { code: 'TEST', expiresAtUnixSeconds: Math.floor(Date.now() / 1000) + 100 };
      if (request.url.endsWith('/complete')) {
        completeRequests++;
        assert.equal(request.contentType, 'application/json');
        assert.equal(JSON.parse(request.data).code, 'TEST');
        assert.equal(request.body, undefined);
        if (failure) throw { status: 400 };
        return { complete: true };
      }
      return { complete: false };
    }
  }
});
(async () => {
  vm.runInContext(script, context);
  vm.runInContext(script, context); // Jellyfin can evaluate configuration scripts again.
  await element('#signInButton').handlers.click();
  await poll();
  assert.equal(element('#loginStatus').textContent, '10 Play sign-in complete.');
  failure = true;
  await element('#signInButton').handlers.click();
  await poll();
  assert.match(element('#loginStatus').textContent, /HTTP 400/);
  assert.doesNotMatch(element('#loginStatus').textContent, /Approval received/);
  assert.equal(completeRequests, 2);
  console.log('PASS: repeated script load, JSON confirmation payload, success and HTTP failure status.');
})().catch(error => { console.error(error); process.exitCode = 1; });
