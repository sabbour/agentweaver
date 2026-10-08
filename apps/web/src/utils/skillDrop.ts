// Helpers for importing skills via drag-and-drop.
//
// Dragging a single file onto a dropzone works because the browser exposes it through
// `DataTransfer.files`. Dragging a FOLDER does not: `DataTransfer.files` contains a bogus
// entry for the directory itself, and reading/uploading it throws net::ERR_ACCESS_DENIED.
// To read folders we must use the `webkitGetAsEntry()` / FileSystemEntry API and recurse
// through directory readers, collecting the real files with their folder-relative paths.

/** File collected from a drop, paired with the path it should keep on the server. */
export interface DroppedSkillFile {
  file: File;
  relativePath: string;
}

/**
 * Minimal structural type for the FileSystemEntry API. The DOM lib types for this API are
 * inconsistent across environments, so we model only the members we use — which also makes
 * the recursion trivial to unit-test with plain mock objects.
 */
export interface FsEntry {
  isFile: boolean;
  isDirectory: boolean;
  name: string;
  file?: (success: (f: File) => void, error?: (e: unknown) => void) => void;
  createReader?: () => FsDirectoryReader;
}

export interface FsDirectoryReader {
  readEntries: (success: (entries: FsEntry[]) => void, error?: (e: unknown) => void) => void;
}

// Skip files that the text-based skill upload endpoint can't ingest.
const MAX_SKILL_FILE_BYTES = 1024 * 1024; // 1 MiB
const BINARY_EXTENSIONS = new Set([
  'png', 'jpg', 'jpeg', 'gif', 'bmp', 'ico', 'svg', 'webp',
  'pdf', 'zip', 'tar', 'gz', 'tgz', '7z', 'rar',
  'exe', 'dll', 'so', 'dylib', 'bin', 'wasm',
  'woff', 'woff2', 'ttf', 'otf', 'eot',
  'mp3', 'mp4', 'wav', 'mov', 'avi', 'webm',
]);

/** True when a file is small enough and looks like text the upload endpoint can read. */
export function isUploadableSkillFile(file: File): boolean {
  if (file.size > MAX_SKILL_FILE_BYTES) return false;
  const ext = file.name.includes('.') ? file.name.split('.').pop()!.toLowerCase() : '';
  return !BINARY_EXTENSIONS.has(ext);
}

function entryToFile(entry: FsEntry): Promise<File> {
  return new Promise((resolve, reject) => {
    if (!entry.file) {
      reject(new Error(`Entry '${entry.name}' is not readable as a file.`));
      return;
    }
    entry.file((f) => resolve(f), (e) => reject(e instanceof Error ? e : new Error(String(e))));
  });
}

// `readEntries` returns directory children in batches and yields an empty array when done,
// so we must keep calling it until it drains.
function readAllEntries(reader: FsDirectoryReader): Promise<FsEntry[]> {
  return new Promise((resolve, reject) => {
    const all: FsEntry[] = [];
    const readBatch = () => {
      reader.readEntries((batch) => {
        if (batch.length === 0) {
          resolve(all);
          return;
        }
        all.push(...batch);
        readBatch();
      }, (e) => reject(e instanceof Error ? e : new Error(String(e))));
    };
    readBatch();
  });
}

/**
 * Recursively collect files from a FileSystemEntry, tagging each with its path relative to the
 * dropped root (e.g. `code-review/SKILL.md`). Directories are read via `createReader()` until
 * empty; individual files are read via `file()`.
 */
export async function collectFilesFromEntry(entry: FsEntry, basePath = ''): Promise<DroppedSkillFile[]> {
  const relativePath = basePath ? `${basePath}/${entry.name}` : entry.name;

  if (entry.isFile) {
    const file = await entryToFile(entry);
    return [{ file, relativePath }];
  }

  if (entry.isDirectory && entry.createReader) {
    const children = await readAllEntries(entry.createReader());
    const nested = await Promise.all(children.map((child) => collectFilesFromEntry(child, relativePath)));
    return nested.flat();
  }

  return [];
}

/**
 * Read every dropped item (file or folder) into a flat list of files with relative paths.
 *
 * The `webkitGetAsEntry()` calls MUST happen synchronously while the drop event is still being
 * handled — the DataTransferItemList is neutered once the handler yields — so entries are
 * captured up front before any awaiting begins.
 */
export async function collectFilesFromDataTransfer(items: DataTransferItemList): Promise<DroppedSkillFile[]> {
  const entries: FsEntry[] = [];
  for (let i = 0; i < items.length; i++) {
    const getAsEntry = (items[i] as DataTransferItem & { webkitGetAsEntry?: () => FsEntry | null }).webkitGetAsEntry;
    const entry = typeof getAsEntry === 'function' ? getAsEntry.call(items[i]) : null;
    if (entry) entries.push(entry);
  }
  const collected = await Promise.all(entries.map((entry) => collectFilesFromEntry(entry)));
  return collected.flat().filter((f) => isUploadableSkillFile(f.file));
}

/** True when the drop exposes the FileSystemEntry API (needed for folder drops). */
export function supportsEntryApi(items: DataTransferItemList | undefined | null): boolean {
  return (
    !!items &&
    items.length > 0 &&
    typeof (items[0] as DataTransferItem & { webkitGetAsEntry?: unknown }).webkitGetAsEntry === 'function'
  );
}
