#!/usr/bin/env node
// Zero-dependency static HTTP server for Unity WebGL builds (.wasm, .br, .gz, optional COOP/COEP).

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const args = process.argv.slice(2);
let rootDir = '.';
let port = 8080;
let isolated = false;

for (let i = 0; i < args.length; i++) {
  if (args[i] === '--dir' && args[i + 1]) rootDir = args[++i];
  else if (args[i] === '--port' && args[i + 1]) port = parseInt(args[++i], 10);
  else if (args[i] === '--isolated') isolated = true;
}

rootDir = path.resolve(rootDir);

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.mjs': 'application/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.wasm': 'application/wasm',
  '.data': 'application/octet-stream',
  '.symbols': 'application/octet-stream',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon',
};

const server = http.createServer((req, res) => {
  const urlPath = (req.url || '/').split('?')[0];
  let rel = decodeURIComponent(urlPath);
  if (rel === '/') rel = '/index.html';

  const filePath = path.resolve(rootDir, '.' + rel);
  if (!filePath.startsWith(rootDir)) {
    res.writeHead(403);
    res.end('Forbidden');
    return;
  }

  fs.stat(filePath, (err, st) => {
    if (err || !st.isFile()) {
      res.writeHead(404);
      res.end('Not Found');
      return;
    }

    const headers = {};
    if (isolated) {
      headers['Cross-Origin-Opener-Policy'] = 'same-origin';
      headers['Cross-Origin-Embedder-Policy'] = 'require-corp';
    }

    let baseName = filePath;
    if (filePath.endsWith('.br')) {
      headers['Content-Encoding'] = 'br';
      baseName = filePath.slice(0, -3);
    } else if (filePath.endsWith('.gz')) {
      headers['Content-Encoding'] = 'gzip';
      baseName = filePath.slice(0, -3);
    }

    const ext = path.extname(baseName).toLowerCase();
    headers['Content-Type'] = MIME[ext] || 'application/octet-stream';
    headers['Cache-Control'] = 'no-cache';

    res.writeHead(200, headers);
    fs.createReadStream(filePath).pipe(res);
  });
});

server.listen(port, '127.0.0.1', () => {
  console.log(`[serve-webgl] Serving ${rootDir} at http://127.0.0.1:${port} (isolated=${isolated})`);
});
