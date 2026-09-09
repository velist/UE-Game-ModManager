import { readdir, readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

export async function checkWebsite(root = fileURLToPath(new URL('../../website/', import.meta.url))) {
  const origin = 'https://www.modmanger.com';
  const publicPaths = new Map([['index.html', '/'], ['help.html', '/help'], ['404.html', '/404']]);
  const filesByPath = new Map([...publicPaths].map(([name, route]) => [route, name]));
  const names = [...publicPaths.keys()];
  const pages = new Map(await Promise.all(names.map(async name => [name, await readFile(path.join(root, name), 'utf8')])));
  const canonicalUrls = [];
  const localFiles = new Set();
  let checkedLinks = 0;
  for (const [name, html] of pages) {
    const publicUrl = origin + publicPaths.get(name);
    const titles = [...html.matchAll(/<title>([^<]+)<\/title>/g)].map(match => match[1]);
    assert.equal(titles.length, 1, `${name}: expected one page title`);
    assert.ok(titles[0].trim(), `${name}: page title is empty`);
    assert.equal([...html.matchAll(/<h1\b/g)].length, 1, `${name}: expected one page heading`);
    const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]);
    assert.equal(new Set(ids).size, ids.length, `${name}: duplicate anchors`);
    for (const tag of html.matchAll(/<img\b[^>]*>/g)) assert.match(tag[0], /\balt="[^"]*"/, `${name}: image alt is missing`);
    const structuredData = [...html.matchAll(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/g)]
      .flatMap(block => { const value = JSON.parse(block[1]); return value['@graph'] || [value]; });
    if (name !== '404.html') {
      const canonicals = [...html.matchAll(/<link rel="canonical" href="([^"]+)"/g)].map(match => match[1]);
      assert.deepEqual(canonicals, [publicUrl], `${name}: canonical must use the public route`);
      canonicalUrls.push(publicUrl);
      const metadata = new Map([...html.matchAll(/<meta (?:name|property)="([^"]+)" content="([^"]*)"/g)]
        .map(match => [match[1], match[2]]));
      assert.ok(metadata.get('description')?.trim(), `${name}: description is missing`);
      assert.match(metadata.get('robots') || '', /\bindex\b/, `${name}: page must be indexable`);
      assert.doesNotMatch(metadata.get('robots') || '', /noindex|nofollow/i, `${name}: page blocks search indexing`);
      assert.equal(metadata.get('og:url'), publicUrl, `${name}: Open Graph URL differs from canonical`);
      assert.equal(metadata.get('og:title'), titles[0], `${name}: Open Graph title differs from page title`);
      assert.equal(metadata.get('twitter:title'), titles[0], `${name}: Twitter title differs from page title`);
      assert.equal(metadata.get('og:description'), metadata.get('description'), `${name}: descriptions differ`);
      const webpage = structuredData.filter(item => item['@type'] === 'WebPage');
      assert.equal(webpage.length, 1, `${name}: expected one structured WebPage`);
      assert.equal(webpage[0].url, publicUrl, `${name}: structured URL differs from canonical`);
      assert.equal(webpage[0].name, titles[0], `${name}: structured page name differs from title`);
    } else {
      assert.match(html, /<meta name="robots" content="noindex"/, '404 page must not be indexed');
    }
    for (const [, value] of html.matchAll(/\b(?:href|src)="([^"]+)"/g)) {
      const url = new URL(value, publicUrl);
      if (url.origin !== origin) continue;
      const route = decodeURIComponent(url.pathname);
      const target = filesByPath.get(route) || route.replace(/^\//, '');
      if (pages.has(target)) assert.equal(route, publicPaths.get(target), `${name}: link to a redirecting page ${value}`);
      const file = path.join(root, target);
      assert.ok((await stat(file)).size > 0, `${name}: missing or empty resource ${target}`);
      localFiles.add(target);
      if (url.hash && pages.has(target)) {
        const fragment = decodeURIComponent(url.hash.slice(1));
        assert.ok(pages.get(target).includes(`id="${fragment}"`), `${name}: missing anchor ${target}${url.hash}`);
      }
      checkedLinks++;
    }
  }
  const homepage = pages.get('index.html');
  assert.equal([...homepage.matchAll(/<section id="download"/g)].length, 1, 'Download section must remain accessible');
  const sitemap = await readFile(path.join(root, 'sitemap.xml'), 'utf8');
  assert.match(sitemap, /<urlset xmlns="http:\/\/www.sitemaps.org\/schemas\/sitemap\/0.9">/, 'Invalid sitemap namespace');
  const sitemapUrls = [...sitemap.matchAll(/<loc>([^<]+)<\/loc>/g)].map(match => match[1]);
  assert.deepEqual(sitemapUrls.sort(), canonicalUrls.sort(), 'Sitemap must contain exactly the canonical content pages');
  for (const [, entry] of sitemap.matchAll(/<url>([\s\S]*?)<\/url>/g)) {
    const modified = entry.match(/<lastmod>(\d{4}-\d{2}-\d{2})<\/lastmod>/)?.[1];
    assert.ok(modified && new Date(modified).toISOString().startsWith(modified), 'Invalid sitemap lastmod date');
  }
  const robots = await readFile(path.join(root, 'robots.txt'), 'utf8');
  assert.match(robots, /^User-agent: \*\r?\nAllow: \/\r?$/m, 'robots.txt must allow crawling');
  assert.ok(robots.includes(`Sitemap: ${origin}/sitemap.xml`), 'robots.txt must reference the canonical sitemap');
  assert.doesNotMatch(robots, /^Disallow:\s*\S+/m, 'robots.txt unexpectedly blocks a route');
  const indexNowFiles = (await readdir(root)).filter(name => /^indexnow-[a-f0-9]{32}\.txt$/.test(name));
  assert.equal(indexNowFiles.length, 1, 'Expected one IndexNow verification file');
  const indexNowKey = (await readFile(path.join(root, indexNowFiles[0]), 'utf8')).trim();
  assert.equal(indexNowFiles[0], `indexnow-${indexNowKey}.txt`, 'IndexNow key does not match its verification filename');
  const headers = await readFile(path.join(root, '_headers'), 'utf8');
  for (const [, rule, values] of headers.matchAll(/^(\S+)\r?\n((?:[ \t]+[^\r\n]+\r?\n?)+)/gm)) {
    if (!/X-Robots-Tag:\s*[^\r\n]*noindex/i.test(values)) continue;
    const pattern = new RegExp('^' + rule.split('*').map(part => part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('.*') + '$');
    for (const url of canonicalUrls) assert.ok(!pattern.test(new URL(url).pathname), `${rule}: header prevents indexing ${url}`);
  }
  return { pages: names, checkedLinks, localResources: localFiles.size, canonicalUrls, indexNowFile: indexNowFiles[0] };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  console.log(JSON.stringify(await checkWebsite(process.argv[2]), null, 2));
}
