'use strict';

let regionGraphRequest = 0;
const regionGeometryCache = new Map();

function regionGraphGeometry(graph, game) {
  const key = JSON.stringify([
    graph.nodes.map(node => node.id),
    graph.edges.map(edge => [edge.source, edge.target]),
  ]);
  const cached = regionGeometryCache.get(game);
  if (cached?.key === key) {
    const nodes = graph.nodes.map(node => ({ ...node, ...cached.positions.get(node.id) }));
    return { nodes, start: nodes.find(node => node.id === cached.start), routes: cached.routes };
  }
  const { nodes, start } = layoutRegionNodes(graph, game === 'dark_sun' ? 42 : null);
  const routes = routeRegionLinks(nodes, graph.edges);
  regionGeometryCache.set(game, {
    key, start: start?.id, routes,
    positions: new Map(nodes.map(node => [node.id, { x: node.x, y: node.y }])),
  });
  return { nodes, start, routes };
}

function layoutRegionNodes(graph, startingRegion) {
  const nodes = graph.nodes.map(node => ({ ...node }));
  const byId = new Map(nodes.map(node => [node.id, node]));
  const start = byId.get(startingRegion) || nodes[0];
  const neighbours = new Map(nodes.map(node => [node.id, new Set()]));
  graph.edges.forEach(({ source, target }) => {
    neighbours.get(source).add(target);
    neighbours.get(target).add(source);
  });
  const directions = [[1, 0], [0, 1], [-1, 0], [0, -1]];
  const placed = new Map();
  const occupied = new Set();
  const frontier = new Map([['0,0', { x: 0, y: 0 }]]);
  const queue = start ? [start.id] : [];
  const queued = new Set(queue);
  while (placed.size < nodes.length) {
    if (!queue.length) queue.push(nodes.find(node => !placed.has(node.id)).id);
    const id = queue.shift();
    queued.add(id);
    const adjacent = [...neighbours.get(id)].filter(other => placed.has(other)).map(other => placed.get(other));
    const candidates = [...frontier.values()];
    const score = cell => adjacent.reduce((sum, other) =>
      sum + Math.abs(cell.x - other.x) + Math.abs(cell.y - other.y), 0)
      + (Math.abs(cell.x) + Math.abs(cell.y)) * 0.2;
    candidates.sort((a, b) => score(a) - score(b));
    const cell = candidates[0];
    placed.set(id, cell);
    occupied.add(`${cell.x},${cell.y}`);
    frontier.delete(`${cell.x},${cell.y}`);
    directions.forEach(([dx, dy]) => {
      const next = { x: cell.x + dx, y: cell.y + dy };
      const key = `${next.x},${next.y}`;
      if (!occupied.has(key)) frontier.set(key, next);
    });
    [...neighbours.get(id)].sort((a, b) => a - b).forEach(other => {
      if (!queued.has(other)) {
        queued.add(other);
        queue.push(other);
      }
    });
  }
  nodes.forEach(node => {
    const cell = placed.get(node.id);
    node.x = cell.x * 200;
    node.y = cell.y * 180;
  });
  return { nodes, start };
}

function routeRegionLinks(nodes, edges) {
  const byId = new Map(nodes.map(node => [node.id, node]));
  const blocked = new Set();
  const key = (x, y) => `${x},${y}`;
  nodes.forEach(node => {
    for (let dx = -2; dx <= 2; dx++) {
      for (let dy = -3; dy <= 3; dy++) blocked.add(key(node.x / 25 + dx, node.y / 15 + dy));
    }
  });
  const maxX = Math.max(8, ...nodes.map(node => Math.abs(node.x / 25))) + 6;
  const maxY = Math.max(8, ...nodes.map(node => Math.abs(node.y / 15))) + 6;
  const directions = [[1, 0], [0, 1], [-1, 0], [0, -1]];
  const used = new Set();
  const pairs = new Map();
  edges.forEach(edge => {
    const pair = [edge.source, edge.target].sort((a, b) => a - b).join(',');
    if (pairs.has(pair)) pairs.get(pair).reverse = true;
    else pairs.set(pair, { edge, reverse: false });
  });
  return [...pairs.values()].map(link => {
    const a = byId.get(link.edge.source), b = byId.get(link.edge.target);
    const ports = node => directions.map(([dx, dy]) => ({
      x: node.x / 25 + dx * 3, y: node.y / 15 + dy * 4,
    }));
    const targets = new Set(ports(b).map(p => key(p.x, p.y)));
    const previous = new Map();
    const queue = ports(a);
    queue.forEach(p => previous.set(key(p.x, p.y), null));
    let end = null;
    for (let index = 0; index < queue.length; index++) {
      const at = queue[index];
      if (targets.has(key(at.x, at.y))) { end = at; break; }
      const steps = directions.map(([dx, dy]) => ({ x: at.x + dx, y: at.y + dy }))
        .sort((p, q) => Number(used.has(key(p.x, p.y))) - Number(used.has(key(q.x, q.y))));
      steps.forEach(next => {
        const id = key(next.x, next.y);
        if (Math.abs(next.x) > maxX || Math.abs(next.y) > maxY || blocked.has(id) || previous.has(id)) return;
        previous.set(id, at);
        queue.push(next);
      });
    }
    if (!end) throw new Error(`Unable to route connection ${a.id} -> ${b.id}`);
    const points = [];
    for (let at = end; at; at = previous.get(key(at.x, at.y))) {
      used.add(key(at.x, at.y));
      points.push({ x: at.x * 25, y: at.y * 15 });
    }
    points.reverse();
    const boundary = (node, port, gap) => ({
      x: node.x + Math.sign(port.x - node.x) * (52 + gap),
      y: node.y + Math.sign(port.y - node.y) * (48 + gap),
    });
    points.unshift(boundary(a, points[0], link.reverse ? 5 : 1));
    points.push(boundary(b, points[points.length - 1], 5));
    const corners = points.filter((p, index) => {
      if (index === 0 || index === points.length - 1) return true;
      const before = points[index - 1], after = points[index + 1];
      return !((before.x === p.x && p.x === after.x) || (before.y === p.y && p.y === after.y));
    });
    return { ...link, points: corners };
  });
}

async function renderRegionsView() {
  const host = document.getElementById('region-nodes-view');
  const requestId = ++regionGraphRequest;
  const game = state.game;
  host.classList.remove('hidden');
  host.textContent = 'Loading region connections...';
  try {
    const response = await fetch(`/api/region-connections?game=${encodeURIComponent(game)}`);
    if (!response.ok) throw new Error(`HTTP ${response.status}: ${await response.text()}`);
    const graph = await response.json();
    if (requestId !== regionGraphRequest || state.mode !== 'regions' || state.game !== game) return;
    buildRegionGraph(host, graph);
  } catch (error) {
    if (requestId !== regionGraphRequest || state.mode !== 'regions' || state.game !== game) return;
    host.textContent = `Unable to load region connections: ${error.message}`;
    console.error('Region connections:', error);
  }
}

function buildRegionGraph(host, graph) {
  const game = state.game;
  host.innerHTML = `
    <div class="region-graph-toolbar">
      <strong>Region Node Map</strong>
      <span class="dim">${graph.nodes.length} regions / ${graph.edges.length} directed connections</span>
      <button type="button" data-zoom="in" aria-label="Zoom in">+</button>
      <button type="button" data-zoom="out" aria-label="Zoom out">-</button>
      <button type="button" data-zoom="reset">Reset view</button>
    </div>
    <p class="dim">Arrows show possible party travel, not geographic positions. Two arrowheads mean travel in both directions.
      The starting region is centered. Select a region for its connections.
      Drag the background to pan; scroll to zoom.
      ${graph.warnings.length ? 'Partial graph: see the static analysis notes below.' : ''}</p>
    <div class="region-graph-body">
      <svg class="region-graph" role="group" aria-label="Region connections"></svg>
      <aside class="region-graph-details" aria-label="Selected region"></aside>
    </div>
    <details class="region-graph-warnings">
      <summary>Static analysis notes (${graph.warnings.length})</summary>
      <p>Conditional paths are included, but variables and story conditions are not evaluated.
        Dynamic destinations and undecodable scripts may leave connections undiscovered.
        Dashed nodes have no installed region map.</p>
      <ul></ul>
    </details>`;
  const warnings = host.querySelector('.region-graph-warnings ul');
  graph.warnings.forEach(message => {
    const item = document.createElement('li');
    item.textContent = message;
    warnings.appendChild(item);
  });
  const svg = host.querySelector('svg');
  const details = host.querySelector('aside');
  const ns = 'http://www.w3.org/2000/svg';
  function element(tag, attributes, parent = svg) {
    const node = document.createElementNS(ns, tag);
    Object.entries(attributes).forEach(([key, value]) => node.setAttribute(key, value));
    parent.appendChild(node);
    return node;
  }
  const marker = element('marker', {
    id: 'region-arrow', viewBox: '0 0 10 10', refX: 9, refY: 5,
    markerWidth: 7, markerHeight: 7, orient: 'auto-start-reverse',
  }, element('defs', {}));
  element('path', { d: 'M 0 0 L 10 5 L 0 10 z', fill: 'context-stroke' }, marker);
  const { nodes, start, routes } = regionGraphGeometry(graph, game);
  const byId = new Map(nodes.map(node => [node.id, node]));
  const links = routes.map(({ edge, reverse, points }) => {
    const path = element('path', {
      d: points.map((point, index) => `${index ? 'L' : 'M'} ${point.x} ${point.y}`).join(' '),
      class: 'region-edge', 'marker-end': 'url(#region-arrow)',
      ...(reverse ? { 'marker-start': 'url(#region-arrow)' } : {}),
    });
    element('title', {}, path).textContent = `${edge.source} ${reverse ? '<->' : '->'} ${edge.target}`;
    return { edge, path };
  });
  const nodeElements = new Map();
  const nodeLabel = node => node.name || `Region ${node.id}${node === start && game === 'dark_sun' ? ' (start)' : ''}`;
  function updateNodeLabel(node) {
    const group = nodeElements.get(node.id);
    const text = group.querySelector('.region-name');
    const label = nodeLabel(node);
    text.textContent = node.name && label.length > 15 ? `${label.slice(0, 14)}...` : label;
    group.querySelector('title').textContent = `${label} - ${node.file || 'Missing map'}`;
    group.setAttribute('aria-label', node.name ? `${node.name} (${node.file})` : node.file || `Region ${node.id}, missing map`);
  }
  function mapLink(node, text) {
    const link = document.createElement('a');
    link.textContent = text;
    link.href = `#game=${encodeURIComponent(state.game)}&mode=map&gff=${encodeURIComponent(node.file)}`;
    link.addEventListener('click', event => {
      event.preventDefault();
      state.gffByMode.map = node.file;
      activateMode('map');
    });
    return link;
  }
  function select(node) {
    nodeElements.forEach((group, id) => {
      group.classList.toggle('selected', id === node.id);
      group.setAttribute('aria-pressed', id === node.id);
    });
    links.forEach(({ edge, path }) => path.classList.toggle('selected', edge.source === node.id || edge.target === node.id));
    details.replaceChildren();
    const heading = document.createElement('h3');
    heading.textContent = `${node.name ? `${node.name} - ` : ''}Region ${node.id} (0x${node.id.toString(16).toUpperCase()})`;
    details.appendChild(heading);
    if (node.file) {
      details.appendChild(mapLink(node, `Open ${node.file}`));
      const form = document.createElement('form');
      form.className = 'region-name-form';
      const label = document.createElement('label');
      label.textContent = 'Map name';
      label.htmlFor = 'region-name-input';
      const input = document.createElement('input');
      input.id = 'region-name-input';
      input.type = 'text';
      input.value = node.name || '';
      input.placeholder = 'e.g. Draj Arena';
      const save = document.createElement('button');
      save.type = 'submit';
      save.textContent = 'Save name';
      const status = document.createElement('span');
      status.setAttribute('role', 'status');
      status.setAttribute('aria-live', 'polite');
      form.append(label, input, save, status);
      details.appendChild(form);
      form.addEventListener('submit', async event => {
        event.preventDefault();
        save.disabled = true;
        input.disabled = true;
        status.textContent = 'Saving...';
        try {
          const response = await fetch(`/api/gff/${encodeURIComponent(node.file)}/map-name?game=${encodeURIComponent(game)}`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name: input.value }),
          });
          if (!response.ok) throw new Error(`HTTP ${response.status}`);
          const data = await response.json();
          node.name = data.name;
          input.value = data.name;
          updateNodeLabel(node);
          if (details.contains(form)) {
            heading.textContent = `${node.name ? `${node.name} - ` : ''}Region ${node.id} (0x${node.id.toString(16).toUpperCase()})`;
            status.textContent = 'Saved';
          } else if (state.mode === 'regions' && state.game === game && host.contains(details)) {
            const selected = nodes.find(other => nodeElements.get(other.id).getAttribute('aria-pressed') === 'true');
            if (selected) select(selected);
          }
        } catch (error) {
          status.textContent = `Unable to save name: ${error.message}`;
          console.error('Map name:', error);
        } finally {
          save.disabled = false;
          input.disabled = false;
        }
      });
    }
    else {
      const missing = document.createElement('p');
      missing.textContent = 'No installed map file for this destination.';
      details.appendChild(missing);
    }
    for (const outgoing of [true, false]) {
      const heading = document.createElement('h4');
      heading.textContent = outgoing ? 'Travel to' : 'Travel from';
      details.appendChild(heading);
      const edges = graph.edges.filter(edge => (outgoing ? edge.source : edge.target) === node.id);
      if (!edges.length) details.appendChild(document.createTextNode('No known connections.'));
      edges.forEach(edge => {
        const other = byId.get(outgoing ? edge.target : edge.source);
        const button = document.createElement('button');
        button.textContent = other.name ? `${other.name} (${other.file})` : other.file || `Region ${other.id} (missing map)`;
        button.addEventListener('click', () => select(other));
        details.appendChild(button);
        const evidence = document.createElement('ul');
        edge.transitions.forEach(transition => {
          const item = document.createElement('li');
          item.textContent = `${transition.script} ${transition.number} @${transition.address.toString(16).toUpperCase().padStart(4, '0')}`
            + ` -> (${transition.x ?? '?'}, ${transition.y ?? '?'})`;
          evidence.appendChild(item);
        });
        details.appendChild(evidence);
      });
    }
  }
  nodes.forEach(node => {
    const group = element('g', {
      transform: `translate(${node.x}, ${node.y})`,
      class: `region-node${node.file ? '' : ' missing'}${node === start ? ' starting' : ''}`,
      role: 'button', tabindex: 0, 'aria-label': node.file || `Region ${node.id}, missing map`,
    });
    element('rect', { x: -52, y: -48, width: 104, height: 96, rx: 8 }, group);
    if (node.file) {
      const previewStatus = element('text', {
        y: -12, 'text-anchor': 'middle', class: 'region-preview-status',
      }, group);
      previewStatus.textContent = 'Loading...';
      const preview = element('image', {
        x: -48, y: -44, width: 96, height: 56,
        preserveAspectRatio: 'xMidYMid meet', class: 'region-preview',
      }, group);
      preview.addEventListener('load', () => previewStatus.remove());
      preview.addEventListener('error', () => {
        preview.remove();
        previewStatus.textContent = 'Preview failed';
        console.error(`Map thumbnail failed: ${node.file}`);
      });
      preview.setAttribute('href', `/api/gff/${encodeURIComponent(node.file)}/map-render?game=${encodeURIComponent(game)}&thumbnail=1`);
    }
    element('title', {}, group);
    element('text', { y: 28, 'text-anchor': 'middle', class: 'region-name' }, group);
    element('text', { y: 42, 'text-anchor': 'middle', class: 'region-file' }, group).textContent = node.file || 'Missing map';
    group.addEventListener('click', () => select(node));
    group.addEventListener('keydown', event => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        select(node);
      }
    });
    nodeElements.set(node.id, group);
    updateNodeLabel(node);
  });
  const routePoints = routes.flatMap(route => route.points);
  const halfWidth = Math.max(400, ...nodes.map(node => Math.abs(node.x) + 100), ...routePoints.map(p => Math.abs(p.x) + 30));
  const halfHeight = Math.max(300, ...nodes.map(node => Math.abs(node.y) + 75), ...routePoints.map(p => Math.abs(p.y) + 30));
  const initialBox = { x: -halfWidth, y: -halfHeight, width: halfWidth * 2, height: halfHeight * 2 };
  let box = { ...initialBox };
  function applyBox() { svg.setAttribute('viewBox', `${box.x} ${box.y} ${box.width} ${box.height}`); }
  function zoom(factor, x = box.x + box.width / 2, y = box.y + box.height / 2) {
    if (box.width * factor < 150 || box.width * factor > Math.max(4000, initialBox.width * 3)) return;
    box = { x: x + (box.x - x) * factor, y: y + (box.y - y) * factor,
      width: box.width * factor, height: box.height * factor };
    applyBox();
  }
  host.querySelectorAll('[data-zoom]').forEach(button => button.addEventListener('click', () => {
    if (button.dataset.zoom === 'reset') {
      box = { ...initialBox };
      applyBox();
    } else zoom(button.dataset.zoom === 'in' ? 0.8 : 1.25);
  }));
  function point(event) {
    return new DOMPoint(event.clientX, event.clientY).matrixTransform(svg.getScreenCTM().inverse());
  }
  svg.addEventListener('wheel', event => {
    event.preventDefault();
    const at = point(event);
    zoom(event.deltaY < 0 ? 0.9 : 1.1, at.x, at.y);
  }, { passive: false });
  let drag = null;
  svg.addEventListener('pointerdown', event => {
    if (event.button !== 0 || event.target.closest('.region-node')) return;
    drag = { at: point(event) };
    svg.setPointerCapture(event.pointerId);
  });
  svg.addEventListener('pointermove', event => {
    if (!drag) return;
    const at = point(event);
    box.x += drag.at.x - at.x;
    box.y += drag.at.y - at.y;
    applyBox();
  });
  svg.addEventListener('pointerup', () => { drag = null; });
  svg.addEventListener('pointercancel', () => { drag = null; });
  applyBox();
  if (start) select(start);
  else details.textContent = 'No region maps found for this game.';
}
