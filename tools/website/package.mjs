import { cp, mkdir, readdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { checkWebsite } from './check.mjs';

const source = fileURLToPath(new URL('../../website/', import.meta.url));
const output = path.resolve(process.argv[2] || 'bin/website-publish');
if (output === path.resolve(source) || path.relative(source, output).split(path.sep)[0] !== '..') {
  throw new Error('The publish directory must be outside the source website directory');
}
await checkWebsite(source);
// A new output directory prevents accidental replacement of another release.
await mkdir(output, { recursive: false });
const indexNowFiles = (await readdir(source)).filter(name => /^indexnow-[a-f0-9]{32}\.txt$/.test(name));
for (const file of ['index.html', 'help.html', '404.html', 'styles.css', 'release.css', 'script.js', 'robots.txt', 'sitemap.xml', '_headers', ...indexNowFiles]) {
  await cp(path.join(source, file), path.join(output, file));
}
await cp(path.join(source, 'assets'), path.join(output, 'assets'), {
  recursive: true,
  filter: file => path.basename(file).toLowerCase() !== 'release'
});
await checkWebsite(output);
const hashes = {};
async function record(directory) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) await record(file);
    else hashes[path.relative(output, file).replaceAll(path.sep, '/')] = createHash('sha256').update(await readFile(file)).digest('hex');
  }
}
await record(output);
await writeFile(`${output}.sha256.json`, JSON.stringify(hashes, null, 2) + '\n');
console.log(JSON.stringify({ output, files: Object.keys(hashes).length, manifest: `${output}.sha256.json` }, null, 2));
