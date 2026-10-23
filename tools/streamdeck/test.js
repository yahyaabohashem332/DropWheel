// Tests the Stream Deck plugin without Stream Deck or Petal: this script plays both. It runs a WebSocket
// server as Stream Deck does and a named pipe as Petal does, starts the real plugin, presses its keys
// and asks for its lists, and checks what reaches each side.
'use strict';

const assert = require('assert');
const crypto = require('crypto');
const http = require('http');
const net = require('net');
const path = require('path');
const { spawn } = require('child_process');

const pipeName = 'Petal.Api.Test.' + process.pid;
const pipePath = '\\\\.\\pipe\\' + pipeName;
const asked = [];

// ---------- Petal's side ----------
const petal = net.createServer((socket) => {
  let text = '';
  socket.setEncoding('utf8');
  socket.on('data', (chunk) => {
    text += chunk;
    const end = text.indexOf('\n');
    if (end < 0) return;
    const request = JSON.parse(text.slice(0, end));
    asked.push(request);
    const answer = request.cmd === 'actions'
      ? { ok: true, actions: [{ id: 'screen.full', name: 'Screenshot the Screen', section: 'Screen' }], wheels: [{ id: 'w1', name: 'Work', on: true }] }
      : request.cmd === 'do' && request.action === 'nope' ? { ok: false, error: 'There is no petal "nope".' }
      : { ok: true };
    socket.end(JSON.stringify(answer) + '\n');
  });
});

// ---------- Stream Deck's side: a WebSocket server that speaks just enough ----------
function frame(text) {
  const body = Buffer.from(text, 'utf8');
  const head = body.length < 126 ? Buffer.from([0x81, body.length]) : Buffer.from([0x81, 126, body.length >> 8, body.length & 0xff]);
  return Buffer.concat([head, body]);
}

function unframe(buffer) {
  const messages = [];
  let at = 0;
  while (buffer.length - at >= 2) {
    let length = buffer[at + 1] & 0x7f;
    let p = at + 2;
    if (length === 126) { length = buffer.readUInt16BE(p); p += 2; }
    assert.ok(buffer[at + 1] & 0x80, 'a client must mask what it sends');
    const mask = buffer.slice(p, p + 4);
    p += 4;
    if (buffer.length < p + length) break;
    const body = Buffer.from(buffer.slice(p, p + length));
    for (let i = 0; i < body.length; i++) body[i] ^= mask[i % 4];
    messages.push(body.toString('utf8'));
    at = p + length;
  }
  return { messages, rest: buffer.slice(at) };
}

const fromPlugin = [];
let toPlugin = null;
const deck = http.createServer();
deck.on('upgrade', (request, socket) => {
  const accept = crypto.createHash('sha1').update(request.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ' + accept + '\r\n\r\n');
  let buffer = Buffer.alloc(0);
  socket.on('error', () => {});
  socket.on('data', (data) => {
    const { messages, rest } = unframe(Buffer.concat([buffer, data]));
    buffer = rest;
    for (const m of messages) fromPlugin.push(JSON.parse(m));
  });
  toPlugin = (message) => socket.write(frame(JSON.stringify(message)));
});

const wait = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(check, what) {
  for (let i = 0; i < 100; i++) {
    if (check()) return;
    await wait(50);
  }
  throw new Error('Waited in vain for ' + what);
}

(async () => {
  await new Promise((r) => petal.listen(pipePath, r));
  await new Promise((r) => deck.listen(0, '127.0.0.1', r));
  const port = deck.address().port;
  const plugin = spawn(process.execPath, [path.join(__dirname, 'com.petal.app.sdPlugin', 'bin', 'plugin.js'),
    '-port', String(port), '-pluginUUID', 'PLUGIN', '-registerEvent', 'registerPlugin', '-info', '{}'],
    { env: { ...process.env, PETAL_API_PIPE: pipeName }, stdio: 'inherit' });
  try {
    await until(() => fromPlugin.some((m) => m.event === 'registerPlugin' && m.uuid === 'PLUGIN'), 'the plugin to register');

    // A key set to a petal runs it, and shows the tick.
    toPlugin({ event: 'keyDown', action: 'com.petal.app.do', context: 'K1', payload: { settings: { petal: 'screen.full' } } });
    await until(() => fromPlugin.some((m) => m.event === 'showOk' && m.context === 'K1'), 'the tick on K1');
    assert.deepStrictEqual(asked.at(-1), { cmd: 'do', action: 'screen.full' });

    // Petal saying no shows the warning.
    toPlugin({ event: 'keyDown', action: 'com.petal.app.do', context: 'K2', payload: { settings: { petal: 'nope' } } });
    await until(() => fromPlugin.some((m) => m.event === 'showAlert' && m.context === 'K2'), 'the warning on K2');

    // A key not set up yet asks nothing of Petal.
    const before = asked.length;
    toPlugin({ event: 'keyDown', action: 'com.petal.app.wheel', context: 'K3', payload: { settings: {} } });
    await until(() => fromPlugin.some((m) => m.event === 'showAlert' && m.context === 'K3'), 'the warning on K3');
    assert.strictEqual(asked.length, before);

    // Wheels and the palette.
    toPlugin({ event: 'keyDown', action: 'com.petal.app.wheel', context: 'K4', payload: { settings: { wheel: 'Work' } } });
    await until(() => fromPlugin.some((m) => m.event === 'showOk' && m.context === 'K4'), 'the tick on K4');
    assert.deepStrictEqual(asked.at(-1), { cmd: 'wheel', name: 'Work' });
    toPlugin({ event: 'keyDown', action: 'com.petal.app.commands', context: 'K5', payload: { settings: { text: 'rec' } } });
    await until(() => fromPlugin.some((m) => m.event === 'showOk' && m.context === 'K5'), 'the tick on K5');
    assert.deepStrictEqual(asked.at(-1), { cmd: 'commands', text: 'rec' });

    // The key's settings ask for the lists, and get Petal's.
    toPlugin({ event: 'sendToPlugin', action: 'com.petal.app.do', context: 'K1', payload: { ask: 'lists' } });
    await until(() => fromPlugin.some((m) => m.event === 'sendToPropertyInspector'), 'the lists');
    const lists = fromPlugin.find((m) => m.event === 'sendToPropertyInspector').payload;
    assert.strictEqual(lists.actions[0].id, 'screen.full');
    assert.strictEqual(lists.wheels[0].name, 'Work');

    // Petal not running: a warning, not a hang.
    petal.close();
    await wait(100);
    toPlugin({ event: 'keyDown', action: 'com.petal.app.do', context: 'K6', payload: { settings: { petal: 'screen.full' } } });
    await until(() => fromPlugin.some((m) => m.event === 'showAlert' && m.context === 'K6'), 'the warning on K6');
    console.log('Stream Deck plugin: all checks passed');
  } finally {
    plugin.kill();
    deck.close();
    petal.close();
  }
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
