import { fileURLToPath } from 'node:url';
import path from 'node:path';

export const PROJECT_ROOT = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)), '..');

/** Where the indexed repositories live. */
export const REPO_ROOT = process.env.MEMORY_BANK_REPO_ROOT ??
  'C:/path/to/your/repos';

/** Repositories indexed by default; configure locally. */
export const REPOS = (process.env.MEMORY_BANK_REPOS ??
  'first-repo,second-repo,third-repo')
  .split(',').map(s => s.trim()).filter(Boolean);

export const DB_PATH = process.env.MEMORY_BANK_DB ??
  path.join(PROJECT_ROOT, 'memory.db');

/** Vendored trees, skipped everywhere. Matched as leading path segments. */
export const VENDORED_DIRS = ['vendor', 'third_party', 'external', 'node_modules'];

/** Files larger than this are indexed as a citation stub only. */
export const MAX_FILE_BYTES = 2 * 1024 * 1024;

/** Extensions never worth reading as text. */
export const BINARY_EXTS = new Set([
  'pdf', 'png', 'jpg', 'jpeg', 'gif', 'ico', 'bmp', 'webp', 'svgz',
  'gz', 'zip', 'xz', 'bz2', '7z', 'tar', 'bin', 'exe', 'dll', 'so', 'dylib',
  'o', 'a', 'lib', 'pyc', 'pyd', 'npz', 'npy', 'pb', 'onnx', 'pt', 'pth',
  'ttf', 'otf', 'woff', 'woff2', 'mp4', 'mp3', 'wav', 'pgn', 'polyglot',
]);
