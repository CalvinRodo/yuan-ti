/* ================================================================
   GFF Viewer — Shared utilities
   ================================================================ */

export const $ = id => document.getElementById(id);

export function formatBytes(n) {
  if (n < 1024) return `${n} B`;
  if (n < 1048576) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1048576).toFixed(2)} MB`;
}

export function htmlEsc(s) {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/**
 * Get or create a persistent DOM panel for a scene inside #dom-views.
 * Panels are preserved across scene starts/stops so cached data persists.
 */
const _panels = {};

export function getDomPanel(sceneKey) {
  if (_panels[sceneKey]) return _panels[sceneKey];
  const container = $('dom-views');
  const panel = document.createElement('div');
  panel.className = 'dom-panel hidden';
  panel.id = `panel-${sceneKey}`;
  container.appendChild(panel);
  _panels[sceneKey] = panel;
  return panel;
}

export function showDomPanel(sceneKey) {
  const dv = $('dom-views');
  dv.classList.remove('hidden');
  // Hide all panels, then show ours
  Object.values(_panels).forEach(p => p.classList.add('hidden'));
  const panel = getDomPanel(sceneKey);
  panel.classList.remove('hidden');
  return panel;
}

export function hideDomPanels() {
  const dv = $('dom-views');
  dv.classList.add('hidden');
  Object.values(_panels).forEach(p => p.classList.add('hidden'));
}
