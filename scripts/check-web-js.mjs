#!/usr/bin/env node
// Parse-checks every piece of browser JavaScript the plugin ships.
//
// Why this exists (issue #149): configPage.html carries its whole admin UI in ONE inline <script>.
// A single stray character there — v4.5.0.2 shipped `'... (Groq's 100 ...'`, an unescaped apostrophe
// closing a single-quoted string — is a SyntaxError, and a SyntaxError means the browser executes
// NONE of the block. Every panel then sits on its static markup and every button loses its listener,
// so the page looks like "the backend works but the UI never updates". Nothing server-side logs a
// thing, and the C# test suite cannot see it: the file is an embedded resource, never parsed at build.
//
// Run: node scripts/check-web-js.mjs
// Exits non-zero (and names file + line) on the first file that fails to parse.

import { readFileSync } from 'node:fs';
import { Script } from 'node:vm';
import { fileURLToPath } from 'node:url';
import { dirname, join, relative } from 'node:path';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..');

/** Files that are plain scripts, parsed as-is. */
const SCRIPT_FILES = ['Web/whisperSubs.js'];

/** Files whose inline <script> blocks are extracted and parsed. */
const HTML_FILES = ['Web/configPage.html'];

const INLINE_SCRIPT = /<script\b([^>]*)>([\s\S]*?)<\/script>/gi;

/** Skip blocks that aren't JavaScript (e.g. type="application/json", src-only tags). */
function isJavaScript(attrs) {
  const type = /type\s*=\s*["']([^"']+)["']/i.exec(attrs);
  if (/\bsrc\s*=/i.test(attrs)) return false;
  if (!type) return true;
  return /^(text\/javascript|application\/javascript|module)$/i.test(type[1].trim());
}

/**
 * Compile-only. `new Script` throws on a syntax error and never runs the code, so this is a pure
 * parse check — no DOM, no ApiClient, no side effects.
 * `lineOffset` maps the reported line back to the real line in the source file.
 */
function parse(code, filename, lineOffset) {
  new Script(code, { filename, lineOffset });
}

let failures = 0;

function report(file, err) {
  failures++;
  console.error(`\n✗ ${file}`);
  console.error(`  ${err.message}`);
  // node puts the offending source + caret in the stack preamble; surface it, it is the useful part.
  const preamble = String(err.stack || '').split('\n').slice(0, 5).join('\n');
  if (preamble) console.error(preamble.replace(/^/gm, '  '));
}

for (const rel of SCRIPT_FILES) {
  const abs = join(repoRoot, rel);
  try {
    parse(readFileSync(abs, 'utf8'), rel, 0);
    console.log(`✓ ${rel}`);
  } catch (err) {
    report(rel, err);
  }
}

for (const rel of HTML_FILES) {
  const abs = join(repoRoot, rel);
  const src = readFileSync(abs, 'utf8');
  let blocks = 0;
  let ok = true;

  for (const m of src.matchAll(INLINE_SCRIPT)) {
    if (!isJavaScript(m[1])) continue;
    blocks++;
    // Line number of the block's first line within the HTML file, so errors point at the real line.
    const lineOffset = src.slice(0, m.index + m[0].indexOf('>') + 1).split('\n').length - 1;
    try {
      parse(m[2], rel, lineOffset);
    } catch (err) {
      ok = false;
      report(rel, err);
    }
  }

  if (blocks === 0) {
    failures++;
    console.error(`\n✗ ${rel}: no inline <script> block found — the extractor is broken, not the page.`);
  } else if (ok) {
    console.log(`✓ ${rel} (${blocks} inline <script> block${blocks === 1 ? '' : 's'})`);
  }
}

if (failures > 0) {
  console.error(`\n${failures} file(s) failed to parse. The browser would execute NONE of the ` +
    `affected block, leaving the page on its static markup with dead buttons.`);
  process.exit(1);
}

console.log(`\nAll shipped browser JavaScript parses.`);
