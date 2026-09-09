(() => {
  // Preserve old bookmarks, targeting the public canonical route directly.
  const legacySections = new Set(['#migration', '#privacy', '#v2', '#thanks', '#support']);
  const isHome = location.pathname === '/' || location.pathname === '/index.html';
  function redirectLegacySection() {
    if (isHome && legacySections.has(location.hash)) {
      location.replace(new URL(`/help${location.hash}`, location.origin));
      return true;
    }
    return false;
  }
  if (redirectLegacySection()) return;
  window.addEventListener('hashchange', redirectLegacySection);
  // Content and navigation remain usable with JavaScript disabled.
  if (!('IntersectionObserver' in window)) return;
  const links = [...document.querySelectorAll('.lp-nav a[href^="#"], .lp-help-nav a[href^="#"]')];
  const sections = links.map(link => document.getElementById(link.hash.slice(1))).filter(Boolean);
  const observer = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      for (const link of links) {
        if (link.hash === `#${entry.target.id}`) link.setAttribute('aria-current', 'location');
        else link.removeAttribute('aria-current');
      }
    }
  }, { rootMargin: '-15% 0px -65% 0px', threshold: 0 });
  sections.forEach(section => observer.observe(section));
})();
