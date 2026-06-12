// Voxa Studio — React-safe icon rendering. Instead of lucide.createIcons (which
// REPLACES the <i> node and breaks React reconciliation), we keep each
// React-owned <i data-lucide> in place and inject the SVG as its innerHTML.
// React sees the <i> as a childless leaf, so it never reconciles the SVG.
(function () {
  function pascal(name) {
    return name.split('-').map((s) => s.charAt(0).toUpperCase() + s.slice(1)).join('');
  }
  window.renderStudioIcons = function (root) {
    var L = window.lucide;
    if (!L || !L.icons) return;
    (root || document).querySelectorAll('i[data-lucide]').forEach(function (el) {
      var name = el.getAttribute('data-lucide');
      if (el.__iconName === name) return;            // already drawn this glyph
      var node = L.icons[pascal(name)] || L.icons[name];
      if (!node) return;
      var w = parseInt(el.style.width, 10) || 16;
      var h = parseInt(el.style.height, 10) || 16;
      var inner = node.map(function (child) {
        var tag = child[0], attrs = child[1] || {};
        var a = Object.keys(attrs).map(function (k) { return k + '="' + attrs[k] + '"'; }).join(' ');
        return '<' + tag + ' ' + a + '></' + tag + '>';
      }).join('');
      el.innerHTML = '<svg xmlns="http://www.w3.org/2000/svg" width="' + w + '" height="' + h +
        '" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" ' +
        'stroke-linecap="round" stroke-linejoin="round" style="display:block">' + inner + '</svg>';
      if (!el.style.display) el.style.display = 'inline-flex';
      el.__iconName = name;
    });
  };
})();
