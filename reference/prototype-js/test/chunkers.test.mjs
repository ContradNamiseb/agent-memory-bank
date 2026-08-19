import test from 'node:test';
import assert from 'node:assert/strict';

import {
  chunkText, chunkCpp, chunkPython, chunkMarkdown, chunkBraced, chunkYaml,
  langForPath, splitLines, MAX_CHUNK_LINES,
} from '../src/chunkers.mjs';

/**
 * The invariant everything else rests on: a chunk's body is exactly the lines
 * it claims, and no line is claimed by two chunks. Citations are worthless
 * otherwise.
 */
function assertFaithful(text, chunks, what) {
  const lines = splitLines(text);
  const seen = new Set();
  for (const c of chunks) {
    assert.equal(c.body, lines.slice(c.line_start - 1, c.line_end).join('\n'),
      `${what}: body does not match lines ${c.line_start}-${c.line_end}`);
    assert.ok(c.line_start >= 1 && c.line_end <= lines.length, `${what}: line range out of file`);
    assert.ok(c.line_start <= c.line_end, `${what}: inverted range`);
    for (let i = c.line_start; i <= c.line_end; i++) {
      assert.ok(!seen.has(i), `${what}: line ${i} claimed twice`);
      seen.add(i);
    }
  }
}

test('langForPath maps the extensions these repos actually contain', () => {
  assert.equal(langForPath('src/core/worker_pool.cc'), 'cpp');
  assert.equal(langForPath('src/Options.h'), 'cpp');
  assert.equal(langForPath('tools/train.py'), 'python');
  assert.equal(langForPath('README.md'), 'markdown');
  assert.equal(langForPath('configs/pipeline.textproto'), 'textproto');
  assert.equal(langForPath('proto/schema.proto'), 'proto');
  assert.equal(langForPath('configs/settings.yaml'), 'yaml');
  assert.equal(langForPath('scripts/setup.sh'), 'shell');
  assert.equal(langForPath('notes'), 'text');
});

test('C++: functions, classes and the declarations between them', () => {
  const src = [
    '#include <string>',                     // 1
    '',                                      // 2
    'namespace acme {',                    // 3
    '',                                      // 4
    '// Fitted to real measured outcomes.',     // 5
    'struct Options {',                      // 6
    '  float retry_backoff = 1.5f;',            // 7
    '  float retry_jitter = 0.25f;',           // 8
    '};',                                    // 9
    '',                                      // 10
    'int Adder::Add(int a, int b) {',        // 11
    '  return a + b;',                       // 12
    '}',                                     // 13
    '',                                      // 14
    '}  // namespace acme',                // 15
  ].join('\n');
  const chunks = chunkCpp(splitLines(src));
  assertFaithful(src, chunks, 'cpp');

  const opts = chunks.find(c => c.symbol === 'Options');
  assert.ok(opts, 'struct Options chunked');
  assert.equal(opts.kind, 'class');
  assert.equal(opts.line_start, 5, 'doc comment above the struct is included');
  assert.equal(opts.line_end, 9);
  assert.match(opts.body, /retry_jitter = 0\.21f/);

  const fn = chunks.find(c => c.symbol === 'Adder::Add');
  assert.ok(fn, 'qualified function name extracted');
  assert.equal(fn.kind, 'function');
  assert.deepEqual([fn.line_start, fn.line_end], [11, 13]);

  // The namespace is transparent: no chunk is just its opener or its closer.
  assert.ok(!chunks.some(c => c.symbol === 'acme'));
});

test('C++: braces inside strings and comments do not shift the ranges', () => {
  const src = [
    'void f() {',                            // 1
    '  const char* s = "} not a brace {";',  // 2
    '  /* } still not one',                  // 3
    '     } nor this */',                    // 4
    '  g();',                                // 5
    '}',                                     // 6
    'void h() { i(); }',                     // 7
  ].join('\n');
  const chunks = chunkCpp(splitLines(src));
  assertFaithful(src, chunks, 'cpp-masking');
  const f = chunks.find(c => c.symbol === 'f');
  assert.deepEqual([f.line_start, f.line_end], [1, 6], 'string/comment braces ignored');
  assert.ok(chunks.find(c => c.symbol === 'h'), 'one-line function still found');
});

test('C++: an oversized class is split into its members', () => {
  const body = Array.from({ length: MAX_CHUNK_LINES + 40 },
    (_, i) => `  int field_${i} = ${i};`).join('\n');
  const src = `class Big {\n${body}\n  void Method() {\n    Work();\n  }\n};\n`;
  const chunks = chunkCpp(splitLines(src));
  assertFaithful(src, chunks, 'cpp-big-class');
  assert.ok(chunks.some(c => c.symbol === 'Big::Method'),
    'members of a split class keep the qualified name');
  assert.ok(chunks.every(c => c.line_end - c.line_start + 1 <= MAX_CHUNK_LINES),
    'no chunk exceeds the window');
});

test('Python: defs, decorated defs and module-level code', () => {
  const src = [
    'import tensorflow as tf',        // 1
    '',                               // 2
    '# Fitted, not chosen.',          // 3
    '@tf.function',                   // 4
    'def train_step(x):',             // 5
    '    return x + 1',               // 6
    '',                               // 7
    'class Net:',                     // 8
    '    def __init__(self):',        // 9
    '        self.n = 1',             // 10
    '',                               // 11
    'CONST = 3',                      // 12
  ].join('\n');
  const chunks = chunkPython(splitLines(src));
  assertFaithful(src, chunks, 'python');

  const step = chunks.find(c => c.symbol === 'train_step');
  assert.equal(step.kind, 'function');
  assert.equal(step.line_start, 3, 'decorator and comment above belong to the def');
  assert.equal(step.line_end, 6);

  const net = chunks.find(c => c.symbol === 'Net');
  assert.deepEqual([net.line_start, net.line_end], [8, 10]);
  assert.ok(chunks.some(c => c.kind === 'module' && c.body.includes('CONST = 3')),
    'module-level code is kept, not dropped');
});

test('Markdown: sections split on headings, not on headings inside fences', () => {
  const src = [
    'intro text',            // 1
    '',                      // 2
    '## The two knobs',      // 3
    'body',                  // 4
    '',                      // 5
    '```bash',               // 6
    '# not a heading',       // 7
    '```',                   // 8
    '',                      // 9
    '## Next',               // 10
    'more',                  // 11
  ].join('\n');
  const chunks = chunkMarkdown(splitLines(src));
  assertFaithful(src, chunks, 'markdown');
  assert.deepEqual(chunks.map(c => c.symbol), [null, 'The two knobs', 'Next']);
  const knobs = chunks.find(c => c.symbol === 'The two knobs');
  assert.equal(knobs.line_end, 8, 'fenced "# not a heading" stays inside its section');
});

test('textproto: top-level blocks keep their names', () => {
  const src = [
    '# loader config',   // 1
    'stage {',           // 2
    '  name: "shuffle"', // 3
    '  inner { a: 1 }',  // 4
    '}',                 // 5
    'stage {',           // 6
    '  name: "batch"',   // 7
    '}',                 // 8
  ].join('\n');
  const chunks = chunkBraced(splitLines(src));
  assertFaithful(src, chunks, 'textproto');
  assert.equal(chunks.length, 2);
  assert.deepEqual([chunks[0].line_start, chunks[0].line_end], [1, 5]);
  assert.equal(chunks[0].symbol, 'stage');
  assert.deepEqual([chunks[1].line_start, chunks[1].line_end], [6, 8]);
});

test('YAML: one chunk per top-level key', () => {
  const src = [
    'name: pipeline',        // 1
    'training:',        // 2
    '  batch_size: 64', // 3
    '  lr: 0.1',        // 4
    'model:',           // 5
    '  layers: 15',     // 6
  ].join('\n');
  const chunks = chunkYaml(splitLines(src));
  assertFaithful(src, chunks, 'yaml');
  assert.deepEqual(chunks.map(c => c.symbol), ['name', 'training', 'model']);
  assert.equal(chunks[1].body, 'training:\n  batch_size: 64\n  lr: 0.1');
});

test('chunkText falls back to windows for unknown languages and never throws', () => {
  const src = Array.from({ length: MAX_CHUNK_LINES * 2 + 5 }, (_, i) => `line ${i}`).join('\n');
  const chunks = chunkText('shell', src);
  assertFaithful(src, chunks, 'fallback');
  assert.equal(chunks.length, 3);
  assert.ok(chunks.every(c => c.line_end - c.line_start + 1 <= MAX_CHUNK_LINES));

  assert.deepEqual(chunkText('cpp', ''), []);
  assert.deepEqual(chunkText('python', '\n\n  \n'), []);
  assert.doesNotThrow(() => chunkText('cpp', 'class Broken { void f() {'));
});

test('brace-only leftovers are not indexed as chunks', () => {
  const chunks = chunkCpp(splitLines('void f() {\n  g();\n}\n}\n};\n'));
  assert.ok(chunks.every(c => c.symbol || /[A-Za-z0-9]/.test(c.body.replace(/[\s{}();,]/g, ''))));
  assert.equal(chunks.filter(c => c.symbol === 'f').length, 1);
});
