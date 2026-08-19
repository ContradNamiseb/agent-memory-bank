/**
 * Phase A chunking: deterministic, no LLM (deterministic, no LLM).
 *
 * Every chunk carries exact 1-based inclusive line numbers, and its body is
 * literally lines[line_start-1 .. line_end-1]. Citations are the whole point,
 * so that invariant is tested rather than assumed.
 */

import path from 'node:path';

export const MAX_CHUNK_LINES = 220;

const EXT_LANG = {
  cc: 'cpp', cpp: 'cpp', cxx: 'cpp', c: 'cpp',
  h: 'cpp', hpp: 'cpp', hxx: 'cpp', cu: 'cpp', cuh: 'cpp', inc: 'cpp',
  py: 'python', pyi: 'python',
  md: 'markdown', markdown: 'markdown',
  textproto: 'textproto', pbtxt: 'textproto', config: 'textproto',
  proto: 'proto',
  yaml: 'yaml', yml: 'yaml',
  sh: 'shell', bash: 'shell', ps1: 'powershell',
  json: 'json', toml: 'toml', ini: 'ini', cfg: 'ini',
  txt: 'text', tcss: 'text', css: 'text', sln: 'text', bat: 'text',
};

const BASENAME_LANG = {
  'CMakeLists.txt': 'text', 'Makefile': 'text', 'Dockerfile': 'text',
  '.clang-format': 'yaml', '.gitignore': 'text', '.gitattributes': 'text',
  '.gitmodules': 'ini',
};

/** Language tag for a repo-relative path, or 'text' when nothing better fits. */
export function langForPath(p) {
  const base = path.basename(p);
  if (BASENAME_LANG[base]) return BASENAME_LANG[base];
  const dot = base.lastIndexOf('.');
  const ext = dot > 0 ? base.slice(dot + 1).toLowerCase() : '';
  return EXT_LANG[ext] ?? 'text';
}

export function splitLines(text) {
  return text.replace(/\r\n?/g, '\n').split('\n');
}

const isBlank = (s) => s.trim() === '';
const indentOf = (s) => s.replace(/\t/g, '    ').match(/^ */)[0].length;

/**
 * A leftover holding only braces, a closing `};` or a bare `#endif` is a row
 * that can never usefully be retrieved. Anything named is always kept.
 */
function hasSubstance(body, symbol) {
  if (symbol) return true;
  const bare = body.replace(/[\s{}();,]/g, '');
  // A short constant such as `CONST = 3` is worth citing; a closing brace is not.
  return bare !== '' && bare !== '#endif' && /[\p{L}\p{N}]/u.test(bare);
}

/**
 * Append lines [start,end] (0-based, inclusive) as a chunk, trimming blank
 * edges and splitting anything oversized into line windows. Windows never
 * overlap: an overlapping copy would return two citations for one line.
 */
function emit(out, lines, start, end, kind, symbol) {
  while (start <= end && isBlank(lines[start])) start++;
  while (end >= start && isBlank(lines[end])) end--;
  if (start > end) return;
  if (!hasSubstance(lines.slice(start, end + 1).join('\n'), symbol)) return;
  const len = end - start + 1;
  if (len <= MAX_CHUNK_LINES) {
    out.push({
      kind, symbol: symbol ?? null,
      line_start: start + 1, line_end: end + 1,
      body: lines.slice(start, end + 1).join('\n'),
    });
    return;
  }
  for (let s = start; s <= end; s += MAX_CHUNK_LINES) {
    const e = Math.min(s + MAX_CHUNK_LINES - 1, end);
    const part = Math.floor((s - start) / MAX_CHUNK_LINES) + 1;
    emit(out, lines, s, e, kind, symbol ? `${symbol} (part ${part})` : null);
  }
}

/* ------------------------------------------------------------------ C / C++ */

/**
 * Blank out string, char and comment content so brace counting is not fooled
 * by a brace inside a literal or a commented-out block. Returns the masked
 * line; block-comment and string state carry across lines in `state`.
 */
export function maskCppLine(line, state) {
  let out = '';
  for (let i = 0; i < line.length; i++) {
    const c = line[i], nx = line[i + 1];
    if (state.inBlock) {
      if (c === '*' && nx === '/') { state.inBlock = false; i++; out += '  '; }
      else out += ' ';
      continue;
    }
    if (state.inStr) {
      if (c === '\\') { out += '  '; i++; continue; }
      if (c === state.inStr) { state.inStr = null; out += ' '; continue; }
      out += ' ';
      continue;
    }
    if (c === '/' && nx === '*') { state.inBlock = true; i++; out += '  '; continue; }
    if (c === '/' && nx === '/') { out += ' '.repeat(line.length - i); break; }
    if (c === '"' || c === "'") { state.inStr = c; out += ' '; continue; }
    out += c;
  }
  return out;
}

const CPP_CONTAINER = /\b(class|struct|union|namespace)\b/;

function cppSymbol(header) {
  const flat = header.replace(/\s+/g, ' ').trim();
  const container = flat.match(
    /\b(class|struct|union|enum\s+class|enum|namespace)\s+([A-Za-z_][\w:]*)/);
  if (container) return container[2];
  const paren = flat.indexOf('(');
  const left = (paren === -1 ? flat : flat.slice(0, paren))
    .replace(/\btemplate\s*<[^>]*>\s*/g, '')
    .trim();
  const name = left.match(/(operator\s*[^\s(]+|[A-Za-z_~][\w:~]*)\s*$/);
  return name ? name[1].replace(/\s+/g, '') : null;
}

function cppKind(header) {
  if (/\bnamespace\b/.test(header)) return 'namespace';
  if (CPP_CONTAINER.test(header) || /\benum\b/.test(header)) return 'class';
  return 'function';
}

/**
 * Walk one brace level, emitting a chunk per definition and grouping whatever
 * sits between definitions (includes, constants, field declarations) into
 * `toplevel` chunks — that is where things like `float retry_backoff = 1.5f;`
 * live, so dropping them would lose exactly the lines worth citing.
 */
function chunkCppLevel(out, lines, masked, from, to, prefix, depth) {
  let gapStart = from;
  let i = from;
  while (i <= to) {
    const code = masked[i];
    if (isBlank(code) || !code.includes('{')) { i++; continue; }

    // Find where this definition's body closes.
    let level = 0, end = -1;
    for (let j = i; j <= to; j++) {
      for (const ch of masked[j]) {
        if (ch === '{') level++;
        else if (ch === '}') level--;
      }
      if (level <= 0) { end = j; break; }
    }
    if (end === -1) { i++; continue; }        // unbalanced; leave it in the gap

    // Expand backwards over the doc comment, template header and any
    // continuation lines of the signature.
    let head = i;
    while (head > gapStart) {
      const prev = lines[head - 1];
      const prevCode = masked[head - 1].trim();
      if (isBlank(prev)) break;
      if (prev.trim().startsWith('#')) break;
      if (/[;{}]$/.test(prevCode)) break;
      head--;
    }

    const kind = cppKind(masked[i]);
    const symbol = cppSymbol(masked.slice(head, i + 1).join(' '));
    const qualified = symbol && prefix ? `${prefix}${symbol}` : symbol;

    emit(out, lines, gapStart, head - 1, 'toplevel', prefix ? `${prefix}(decls)` : null);

    const tooBig = end - head + 1 > MAX_CHUNK_LINES;
    if (kind === 'namespace') {
      // Transparent: a namespace is not a definition, so index its contents at
      // this level. Only a doc comment above it is worth keeping.
      emit(out, lines, head, i - 1, 'toplevel', null);
      chunkCppLevel(out, lines, masked, i + 1, end - 1, prefix, depth);
    } else if (kind === 'class' && tooBig && depth < 3) {
      // Too big to be one readable chunk: index the members individually.
      emit(out, lines, head, i, 'class',
        qualified ? `${qualified} (declaration)` : null);
      chunkCppLevel(out, lines, masked, i + 1, end - 1, `${qualified ?? ''}::`, depth + 1);
    } else {
      emit(out, lines, head, end, kind, qualified);
    }
    gapStart = end + 1;
    i = end + 1;
  }
  emit(out, lines, gapStart, to, 'toplevel', prefix ? `${prefix}(decls)` : null);
}

export function chunkCpp(lines) {
  const state = { inBlock: false, inStr: null };
  const masked = lines.map(l => maskCppLine(l, state));
  const out = [];
  chunkCppLevel(out, lines, masked, 0, lines.length - 1, '', 0);
  return out;
}

/* ------------------------------------------------------------------ Python */

const PY_DEF = /^(?:async\s+)?def\s+([A-Za-z_]\w*)/;
const PY_CLASS = /^class\s+([A-Za-z_]\w*)/;

function chunkPythonLevel(out, lines, from, to, indent, prefix, depth) {
  let gapStart = from;
  let i = from;
  while (i <= to) {
    const line = lines[i];
    if (isBlank(line) || indentOf(line) !== indent) { i++; continue; }
    const stripped = line.slice(indent);
    const def = stripped.match(PY_DEF), cls = stripped.match(PY_CLASS);
    if (!def && !cls) { i++; continue; }

    // Body runs to the last line that is blank or indented deeper.
    let end = i;
    for (let j = i + 1; j <= to; j++) {
      if (isBlank(lines[j]) || indentOf(lines[j]) > indent) { end = j; continue; }
      break;
    }
    // Decorators and the comment block immediately above belong to the def.
    let head = i;
    while (head > gapStart) {
      const prev = lines[head - 1];
      if (isBlank(prev) || indentOf(prev) !== indent) break;
      const p = prev.slice(indent);
      if (p.startsWith('@') || p.startsWith('#')) { head--; continue; }
      break;
    }

    const symbol = def ? def[1] : cls[1];
    const qualified = prefix ? `${prefix}${symbol}` : symbol;
    emit(out, lines, gapStart, head - 1, 'module', prefix ? `${prefix}(body)` : null);

    const tooBig = end - head + 1 > MAX_CHUNK_LINES;
    if (cls && tooBig && depth < 3) {
      const inner = lines.slice(i + 1, end + 1).find(l => !isBlank(l));
      const innerIndent = inner ? indentOf(inner) : indent + 4;
      emit(out, lines, head, i, 'class', `${qualified} (declaration)`);
      chunkPythonLevel(out, lines, i + 1, end, innerIndent, `${qualified}.`, depth + 1);
    } else {
      emit(out, lines, head, end, cls ? 'class' : 'function', qualified);
    }
    gapStart = end + 1;
    i = end + 1;
  }
  emit(out, lines, gapStart, to, 'module', prefix ? `${prefix}(body)` : null);
}

export function chunkPython(lines) {
  const out = [];
  chunkPythonLevel(out, lines, 0, lines.length - 1, 0, '', 0);
  return out;
}

/* ---------------------------------------------------------------- Markdown */

export function chunkMarkdown(lines) {
  const out = [];
  const heads = [];
  let fence = null;
  lines.forEach((line, i) => {
    const f = line.match(/^\s{0,3}([`~]{3,})/);
    if (f) {
      if (!fence) fence = f[1][0];
      else if (f[1][0] === fence) fence = null;
      return;
    }
    if (fence) return;
    const h = line.match(/^(#{1,6})\s+(.*?)\s*#*\s*$/);
    if (h) heads.push({ i, depth: h[1].length, title: h[2] });
  });
  if (heads.length === 0) {
    emit(out, lines, 0, lines.length - 1, 'doc', null);
    return out;
  }
  emit(out, lines, 0, heads[0].i - 1, 'doc', null);
  heads.forEach((h, k) => {
    const end = k + 1 < heads.length ? heads[k + 1].i - 1 : lines.length - 1;
    emit(out, lines, h.i, end, 'section', h.title);
  });
  return out;
}

/* -------------------------------------------- textproto / proto / braced */

const PROTO_KEYWORD = /^(message|enum|service|oneof|extend|rpc|group)$/;

function bracedSymbol(header) {
  const flat = header.replace(/\s+/g, ' ').trim().replace(/\{.*$/, '').trim();
  const tokens = flat.split(/[\s:=]+/).filter(Boolean);
  if (tokens.length === 0) return null;
  if (PROTO_KEYWORD.test(tokens[0]) && tokens[1]) return `${tokens[0]} ${tokens[1]}`;
  return tokens[0];
}

/** Top-level `name { ... }` blocks, e.g. a loader textproto's `stage {}`. */
export function chunkBraced(lines) {
  const out = [];
  const masked = lines.map(l => l.replace(/#.*$/, '').replace(/\/\/.*$/, ''));
  let gapStart = 0, i = 0;
  while (i < lines.length) {
    if (!masked[i].includes('{')) { i++; continue; }
    let level = 0, end = -1;
    for (let j = i; j < lines.length; j++) {
      for (const ch of masked[j]) {
        if (ch === '{') level++;
        else if (ch === '}') level--;
      }
      if (level <= 0) { end = j; break; }
    }
    if (end === -1) { i++; continue; }
    let head = i;
    while (head > gapStart && !isBlank(lines[head - 1]) &&
      /^\s*(\/\/|#)/.test(lines[head - 1])) head--;
    emit(out, lines, gapStart, head - 1, 'block', null);
    emit(out, lines, head, end, 'block', bracedSymbol(lines.slice(i, i + 2).join(' ')));
    gapStart = end + 1;
    i = end + 1;
  }
  emit(out, lines, gapStart, lines.length - 1, 'block', null);
  return out;
}

/* -------------------------------------------------------------------- YAML */

export function chunkYaml(lines) {
  const out = [];
  const starts = [];
  lines.forEach((line, i) => {
    if (isBlank(line) || /^\s*#/.test(line)) return;
    if (line.trim() === '---' || line.trim() === '...') { starts.push({ i, key: null }); return; }
    if (indentOf(line) !== 0) return;
    const m = line.match(/^([A-Za-z_"'][^:\s]*)\s*:/);
    if (m) starts.push({ i, key: m[1] });
  });
  if (starts.length === 0) {
    emit(out, lines, 0, lines.length - 1, 'block', null);
    return out;
  }
  emit(out, lines, 0, starts[0].i - 1, 'block', null);
  starts.forEach((s, k) => {
    let head = s.i;
    while (head > 0 && /^\s*#/.test(lines[head - 1])) head--;
    const end = k + 1 < starts.length ? starts[k + 1].i - 1 : lines.length - 1;
    emit(out, lines, head, end, 'block', s.key);
  });
  return out;
}

/* ------------------------------------------------------------------ facade */

/** Chunk one file's text. Never throws: an unparsable file falls back to windows. */
export function chunkText(lang, text) {
  const lines = splitLines(text);
  if (lines.every(isBlank)) return [];
  let chunks;
  try {
    switch (lang) {
      case 'cpp': chunks = chunkCpp(lines); break;
      case 'python': chunks = chunkPython(lines); break;
      case 'markdown': chunks = chunkMarkdown(lines); break;
      case 'textproto': case 'proto': case 'json': chunks = chunkBraced(lines); break;
      case 'yaml': chunks = chunkYaml(lines); break;
      default: chunks = []; break;
    }
  } catch {
    chunks = [];
  }
  if (!chunks || chunks.length === 0) {
    chunks = [];
    emit(chunks, lines, 0, lines.length - 1, 'file', null);
  }
  return chunks;
}
