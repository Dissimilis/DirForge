#!/usr/bin/env node
// Bridges MCP stdio (newline-delimited JSON-RPC on stdin/stdout) to DirForge's
// streamable HTTP endpoint. Usage: node mcp-stdio-bridge.mjs [http://host:port/mcp]
import readline from 'node:readline';

const url = process.argv[2] || 'http://127.0.0.1:8080/mcp';
const rl = readline.createInterface({ input: process.stdin });

rl.on('line', async (line) => {
  line = line.trim();
  if (!line) return;
  try {
    const res = await fetch(url, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        accept: 'application/json, text/event-stream',
      },
      body: line,
    });
    const text = await res.text();
    if (!text.trim()) return; // notification: no response body
    // DirForge pretty-prints JSON; stdio framing requires one line per message
    process.stdout.write(JSON.stringify(JSON.parse(text)) + '\n');
  } catch (err) {
    process.stderr.write(`[mcp-stdio-bridge] ${err}\n`);
  }
});
