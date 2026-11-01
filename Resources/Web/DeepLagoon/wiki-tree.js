(() => {
  const config = window.__deepLagoonWikiConfig;
  if (!config) return;
  // Re-applied after Wiki.js navigation. Articles supply their own navigation.
  document.getElementById('dl-game-wiki-tree')?.remove();
  document.getElementById('dl-game-wiki-toggle')?.remove();
  document.body.classList.remove('dl-game-wiki-tree-open');
  document.body.classList.add('dl-game-wiki');
  document.body.classList.toggle('dl-game-book', !!config.book);
  document.body.classList.toggle('dl-game-book-content', !!config.book && !!document.querySelector('.contents .book'));
  let style = document.getElementById('dl-game-wiki-style');
  if (!style) {
    style = document.createElement('style');
    style.id = 'dl-game-wiki-style';
    style.textContent = `
      body.dl-game-wiki { box-sizing:border-box; margin:0; padding:0!important; }
      .dl-game-wiki .v-navigation-drawer, .dl-game-wiki .v-app-bar,
      .dl-game-wiki .nav-header, .dl-game-wiki .page-col-sd, .dl-game-wiki .v-btn--fixed,
      .dl-game-wiki .page-header-section, .dl-game-wiki .v-main__wrap > header, .dl-game-wiki .v-footer,
      .dl-game-wiki .dlwiki .masthead, .dl-game-wiki .dlwiki .footer { display:none!important; }
      body.dl-game-wiki #app .v-main, body.dl-game-wiki #app .v-content { transition:none!important; padding:0!important; }
      .dl-game-wiki .page-col-content { flex:0 0 100%!important; max-width:100%!important; min-width:0; padding:0!important; }
      body.dl-game-wiki #app .container { max-width:100%!important; padding:0!important; }
      body.dl-game-wiki #app .layout.row { margin:0!important; }
      .dl-game-wiki .contents { padding:12px!important; min-width:0; }
      .dl-game-wiki .dlwiki { min-width:0; }
      .dl-game-wiki .dlwiki .wrap { padding:0 12px!important; }
      .dl-game-wiki .dlwiki .article-head { grid-template-columns:minmax(0,1fr)!important; }
      .dl-game-wiki .dlwiki .article-grid { display:flex!important; flex-direction:column!important; gap:24px!important; }
      .dl-game-wiki .dlwiki .toc { position:static!important; width:100%; border-left:0!important; padding-left:0!important; }
      .dl-game-wiki .dlwiki .hero, .dl-game-wiki .dlwiki .feature,
      .dl-game-wiki .dlwiki .directory, .dl-game-wiki .dlwiki .dl-entities,
      .dl-game-wiki .dlwiki .dl-reference-index { grid-template-columns:minmax(0,1fr)!important; }
      .dl-game-book-content .contents { padding:0!important; }
      .dl-game-book-content .dlwiki > :not(main),
      .dl-game-book-content .dlwiki main > :not(.book-stage),
      .dl-game-book-content .dlwiki .book-stage > :not(.book) { display:none!important; }
      .dl-game-book-content .dlwiki, .dl-game-book-content .dlwiki .wrap,
      .dl-game-book-content .dlwiki .book-stage { margin:0!important; padding:0!important; width:100%!important; max-width:none!important; }
      .dl-game-book-content .dlwiki .book { box-sizing:border-box; width:100%!important; max-width:none!important; margin:0!important; padding:clamp(20px,5vw,64px)!important; border:0!important; box-shadow:none!important; }
      .dl-game-book a { pointer-events:none!important; cursor:text!important; }
    `;
    document.head.append(style);
  }
  // Some authored articles use target=_blank. Keep links in this viewer.
  for (const link of document.querySelectorAll('a[href]')) {
    if (config.book) {
      if (!link.hasAttribute('data-dl-book-tabindex'))
        link.setAttribute('data-dl-book-tabindex', link.getAttribute('tabindex') ?? '');
      link.setAttribute('aria-disabled', 'true');
      link.setAttribute('tabindex', '-1');
    } else {
      if (link.hasAttribute('data-dl-book-tabindex')) {
        const previous = link.getAttribute('data-dl-book-tabindex');
        if (previous) link.setAttribute('tabindex', previous);
        else link.removeAttribute('tabindex');
        link.removeAttribute('data-dl-book-tabindex');
        link.removeAttribute('aria-disabled');
      }
      link.setAttribute('target', '_self');
    }
  }
  if (!window.__deepLagoonWikiClickGuard) {
    window.__deepLagoonWikiClickGuard = true;
    const guard = event => {
      if (window.__deepLagoonWikiConfig?.book && event.target.closest?.('a[href]')) {
        event.preventDefault();
        event.stopImmediatePropagation();
      }
    };
    document.addEventListener('click', guard, true);
    document.addEventListener('auxclick', guard, true);
  }
})();
