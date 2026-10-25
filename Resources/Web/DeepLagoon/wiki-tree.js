(() => {
  const config = window.__deepLagoonWikiConfig;
  if (!config || !config.root || !config.locale) return;
  const rootPath = config.root;
  const prefix = '/' + config.locale + '/';
  if (!rootPath.startsWith(prefix)) return;
  const treePath = rootPath.slice(prefix.length);
  const inTree = location.pathname===rootPath || location.pathname.startsWith(rootPath+'/');
  if (!inTree) {
    document.getElementById('dl-game-wiki-tree')?.remove();
    document.getElementById('dl-game-wiki-toggle')?.remove();
    document.getElementById('dl-game-wiki-style')?.remove();
    document.body.classList.remove('dl-game-wiki', 'dl-game-wiki-tree-open');
    window.__deepLagoonGuideTree = false;
    return;
  }
  if (window.__deepLagoonGuideTree) return;
  window.__deepLagoonGuideTree = true;
  const style = document.createElement('style');
  style.id = 'dl-game-wiki-style';
  style.textContent = `
    body.dl-game-wiki { box-sizing:border-box; margin:0; padding-left:240px; }
    .dl-game-wiki .v-navigation-drawer, .dl-game-wiki .v-app-bar,
    .dl-game-wiki .nav-header, .dl-game-wiki .page-col-sd,
    .dl-game-wiki .page-header-section, .dl-game-wiki .v-footer { display:none!important; }
    .dl-game-wiki .v-main, .dl-game-wiki .v-content { padding:0!important; }
    .dl-game-wiki .page-col-content { flex:0 0 100%!important; max-width:100%!important; min-width:0; }
    .dl-game-wiki .container { max-width:100%!important; }
    .dl-game-wiki .contents { padding:16px!important; min-width:0; }
    .dl-game-wiki .dlwiki { min-width:0; }
    .dl-game-wiki .dlwiki .masthead { display:none!important; }
    .dl-game-wiki .dlwiki .wrap { padding:0 16px!important; }
    .dl-game-wiki .dlwiki .article-head { grid-template-columns:minmax(0,1fr)!important; }
    .dl-game-wiki .dlwiki .head-figure { display:none!important; }
    .dl-game-wiki .dlwiki .article-grid { display:flex!important; flex-direction:column-reverse; gap:24px!important; }
    .dl-game-wiki .dlwiki .toc { position:static!important; width:100%; border-left:0!important; padding-left:0!important; }
    .dl-game-wiki .dlwiki .hero, .dl-game-wiki .dlwiki .feature,
    .dl-game-wiki .dlwiki .directory, .dl-game-wiki .dlwiki .dl-entities,
    .dl-game-wiki .dlwiki .dl-reference-index { grid-template-columns:minmax(0,1fr)!important; }
    #dl-game-wiki-tree { box-sizing:border-box; position:fixed; left:0; top:0; bottom:0; width:240px;
      overflow:auto; background:#171c24; color:#ddd; padding:14px; z-index:10000; font:14px/1.5 Arial; }
    #dl-game-wiki-tree h3 { margin:0 0 12px; font-size:16px; }
    #dl-game-wiki-tree button, #dl-game-wiki-toggle { color:#ddd; background:#263440; border:1px solid #526875; padding:8px; cursor:pointer; }
    #dl-game-wiki-tree a { display:block; color:#abc6ec; padding:6px 4px; overflow-wrap:anywhere; }
    #dl-game-wiki-tree summary { cursor:pointer; overflow-wrap:anywhere; }
    #dl-game-wiki-toggle { display:none; }
    @media(max-width:850px) {
      body.dl-game-wiki { padding-left:0; padding-top:42px; }
      #dl-game-wiki-toggle { display:block; position:fixed; top:0; left:0; width:100%; height:42px; z-index:10001; text-align:left; }
      #dl-game-wiki-tree { display:none; top:42px; width:min(320px,100%); }
      .dl-game-wiki-tree-open #dl-game-wiki-tree { display:block; }
    }
  `;
  document.head.append(style);
  document.body.classList.add('dl-game-wiki');
  const toggle = document.createElement('button');
  toggle.id = 'dl-game-wiki-toggle';
  toggle.textContent = 'Разделы справочника';
  toggle.setAttribute('aria-controls', 'dl-game-wiki-tree');
  toggle.setAttribute('aria-expanded', 'false');
  toggle.onclick = () => toggle.setAttribute('aria-expanded', String(document.body.classList.toggle('dl-game-wiki-tree-open')));
  document.body.append(toggle);
  const panel = document.createElement('aside');
  panel.id = 'dl-game-wiki-tree';
  const heading = document.createElement('h3'); heading.textContent = 'Игровая справка'; panel.append(heading);
  const reload = document.createElement('button'); reload.textContent = 'Обновить дерево'; panel.append(reload);
  const tree = document.createElement('div'); panel.append(tree); document.body.append(panel);
  const load = async () => {
    tree.textContent = 'Загрузка…';
    try {
      const response = await fetch('/graphql', {method:'POST',credentials:'omit',headers:{'Content-Type':'application/json'},body:JSON.stringify({query:'query($locale: String!) { pages { tree(mode: ALL, locale: $locale) { id path title isFolder parent } } }', variables:{locale:config.locale}})});
      if (!response.ok) throw Error('HTTP '+response.status);
      const result = await response.json();
      if (!Array.isArray(result.data?.pages?.tree)) throw Error('Дерево страниц недоступно');
      const pages = result.data.pages.tree.filter(p => p.path===treePath || p.path.startsWith(treePath+'/'));
      tree.replaceChildren();
      if (!pages.length) tree.textContent = 'В разделе '+rootPath+' пока нет опубликованных статей.';
      const root = {children:new Map()};
      for (const page of pages) {
        let node=root;
        for (const segment of page.path.slice(treePath.length).split('/').filter(Boolean)) {
          if (!node.children.has(segment)) node.children.set(segment,{children:new Map(),title:segment});
          node=node.children.get(segment);
        }
        node.title=page.title; if (!page.isFolder) node.path=page.path;
      }
      const render = (node,parent) => {
        for (const child of [...node.children.values()].sort((a,b)=>a.title.localeCompare(b.title,config.locale))) {
          let container=parent;
          if (child.children.size) {const details=document.createElement('details');details.open=true;const summary=document.createElement('summary');summary.textContent=child.title;details.append(summary);parent.append(details);container=details;}
          if (child.path) {const link=document.createElement('a');link.textContent=child.title;link.href=prefix+child.path;container.append(link);}
          render(child,container);
        }
      };
      if (root.path) {const link=document.createElement('a');link.textContent=root.title;link.href=prefix+root.path;tree.append(link);}
      render(root,tree);
    } catch (_) {tree.textContent='Не удалось загрузить дерево. Нажмите «Обновить дерево».';}
  };
  reload.onclick=load;load();
})();
