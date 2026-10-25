'use strict';

let regionGraphRequest = 0;
const regionGeometryCache = new Map();
const REGION_DIRECTIONS = { north: [0, -1], east: [1, 0], south: [0, 1], west: [-1, 0] };
const OPPOSITE_SIDE = { north: 'south', south: 'north', east: 'west', west: 'east' };

function regionLinks(nodes, edges) {
  const byId = new Map(nodes.map(node => [node.id, node]));
  const pairs = new Map();
  edges.forEach(edge => {
    const key = [edge.source, edge.target].sort((a, b) => a - b).join(',');
    if (pairs.has(key)) pairs.get(key).back = edge;
    else pairs.set(key, { edge });
  });
  return [...pairs.values()].map(({ edge, back }) => {
    const candidates = [edge, back].filter(candidate => candidate?.geographic && candidate.exit);
    candidates.sort((a, b) => b.exit.area - a.exit.area);
    const primary = candidates[0];
    let sourceExit = null, targetExit = null;
    if (primary) {
      const destination = byId.get(primary.target);
      const arrival = primary.transitions.find(t => t.trigger
        && t.trigger.width * t.trigger.height === primary.exit.area) || primary.transitions[0];
      const entry = {
        side: OPPOSITE_SIDE[primary.exit.side],
        x: arrival.x === null ? 0.5 : arrival.x / Math.max(1, (destination.width || 128) - 1),
        y: arrival.y === null ? 0.5 : arrival.y / Math.max(1, (destination.height || 98) - 1),
      };
      [sourceExit, targetExit] = primary === edge ? [primary.exit, entry] : [entry, primary.exit];
    }
    return { edge, reverse: Boolean(back), sourceExit, targetExit };
  });
}

function regionGraphGeometry(graph, game) {
  const key = JSON.stringify([
    graph.nodes.map(node => [node.id, node.width, node.height, node.test_region]),
    graph.edges.map(edge => [edge.source, edge.target, edge.geographic, edge.exit, edge.transitions]),
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
  const geographic = regionLinks(nodes, graph.edges).filter(link => link.sourceExit);
  const placed = new Map();
  const occupied = new Set();
  const frontier = new Map([['0,0', { x: 0, y: 0 }]]);
  const queue = start ? [start.id] : [];
  const queued = new Set(queue);
  const constraintsFor = id => geographic.flatMap(link => {
    if (link.edge.source === id && placed.has(link.edge.target)) {
      return [{ other: placed.get(link.edge.target), side: link.targetExit.side }];
    }
    if (link.edge.target === id && placed.has(link.edge.source)) {
      return [{ other: placed.get(link.edge.source), side: link.sourceExit.side }];
    }
    return [];
  });
  const scoreFor = (id, cell) => constraintsFor(id).reduce((sum, { other, side }) => {
    const [dx, dy] = REGION_DIRECTIONS[side];
    const along = (cell.x - other.x) * dx + (cell.y - other.y) * dy;
    const across = Math.abs((cell.x - other.x) * dy - (cell.y - other.y) * dx);
    return sum + (along <= 0 ? 1000 + Math.abs(along) * 100 : (along - 1) * 5) + across * 30;
  }, 0) + [...neighbours.get(id)].filter(other => placed.has(other)).reduce((sum, other) => {
    const point = placed.get(other);
    return sum + Math.abs(cell.x - point.x) + Math.abs(cell.y - point.y);
  }, 0) + (Math.abs(cell.x) + Math.abs(cell.y)) * 0.2;
  while (placed.size < nodes.length) {
    if (!queue.length) queue.push(nodes.find(node => !placed.has(node.id)).id);
    const id = queue.shift();
    queued.add(id);
    const constraints = constraintsFor(id);
    const candidates = [...frontier.values()];
    if (constraints.length) {
      constraints.forEach(({ other, side }) => {
        const [dx, dy] = REGION_DIRECTIONS[side];
        for (let distance = 1; distance <= nodes.length; distance++) {
          const cell = { x: other.x + dx * distance, y: other.y + dy * distance };
          if (!occupied.has(`${cell.x},${cell.y}`)) { candidates.push(cell); break; }
        }
      });
    }
    candidates.sort((a, b) => scoreFor(id, a) - scoreFor(id, b));
    const cell = candidates[0];
    placed.set(id, cell);
    occupied.add(`${cell.x},${cell.y}`);
    frontier.delete(`${cell.x},${cell.y}`);
    directions.forEach(([dx, dy]) => {
      const next = { x: cell.x + dx, y: cell.y + dy };
      const key = `${next.x},${next.y}`;
      if (!occupied.has(key)) frontier.set(key, next);
    });
    const isGeographic = other => geographic.some(link =>
      (link.edge.source === id && link.edge.target === other) || (link.edge.target === id && link.edge.source === other));
    [...neighbours.get(id)].sort((a, b) => Number(isGeographic(b)) - Number(isGeographic(a)) || a - b).forEach(other => {
      if (!queued.has(other)) {
        queued.add(other);
        queue.push(other);
      }
    });
  }
  // Revisit early placements once all of their neighbours are known.
  for (let pass = 0; pass < 8; pass++) {
    let moved = false;
    nodes.filter(node => node !== start && !node.test_region).forEach(node => {
      const current = placed.get(node.id);
      const candidates = [current];
      for (const other of placed.values()) {
        directions.forEach(([dx, dy]) => {
          for (let distance = 1; distance <= 3; distance++) {
            const cell = { x: other.x + dx * distance, y: other.y + dy * distance };
            if (!occupied.has(`${cell.x},${cell.y}`)) candidates.push(cell);
          }
        });
      }
      let best = current, bestScore = scoreFor(node.id, current);
      candidates.forEach(cell => {
        const score = scoreFor(node.id, cell);
        if (score < bestScore) { best = cell; bestScore = score; }
      });
      if (best !== current) {
        occupied.delete(`${current.x},${current.y}`);
        occupied.add(`${best.x},${best.y}`);
        placed.set(node.id, best);
        moved = true;
      }
    });
    if (!moved) break;
  }
  nodes.forEach(node => {
    const cell = placed.get(node.id);
    node.x = cell.x * 200;
    node.y = cell.y * 180;
  });
  nodes.filter(node => node.test_region).forEach((node, index) => {
    node.x = Math.max(0, ...nodes.filter(other => !other.test_region).map(other => other.x)) + 200 * (index + 1);
    node.y = 0;
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
  return regionLinks(nodes, edges).map(link => {
    const a = byId.get(link.edge.source), b = byId.get(link.edge.target);
    const ports = (node, exit) => (exit ? [exit.side] : Object.keys(REGION_DIRECTIONS)).map(side => {
      const [dx, dy] = REGION_DIRECTIONS[side];
      const offsetX = exit && !dx ? Math.max(-1, Math.min(1, Math.round((exit.x - 0.5) * 2))) : 0;
      const offsetY = exit && !dy ? Math.max(-2, Math.min(2, Math.round((exit.y - 0.5) * 4))) : 0;
      return { side, x: node.x / 25 + dx * 3 + offsetX, y: node.y / 15 + dy * 4 + offsetY };
    });
    const sourcePorts = ports(a, link.sourceExit), targetPorts = ports(b, link.targetExit);
    const targets = new Set(targetPorts.map(p => key(p.x, p.y)));
    const previous = new Map();
    const queue = sourcePorts;
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
    const attachment = (node, point, ports, exit, gap) => {
      const port = ports.find(p => p.x * 25 === point.x && p.y * 15 === point.y);
      const [dx, dy] = REGION_DIRECTIONS[port.side];
      const mapWidth = node.width || 128, mapHeight = node.height || 98;
      const scale = Math.min(96 / mapWidth, 56 / mapHeight);
      const offsetX = exit ? Math.max(-44, Math.min(44, (exit.x - 0.5) * mapWidth * scale)) : 0;
      const offsetY = exit ? Math.max(-40, Math.min(8, -16 + (exit.y - 0.5) * mapHeight * scale)) : 0;
      const boundary = {
        x: dx ? node.x + dx * (52 + gap) : node.x + offsetX,
        y: dy ? node.y + dy * (48 + gap) : node.y + offsetY,
      };
      return [boundary, { x: dx ? point.x : boundary.x, y: dy ? point.y : boundary.y }];
    };
    points.unshift(...attachment(a, points[0], sourcePorts, link.sourceExit, link.reverse ? 5 : 1));
    points.push(...attachment(b, points[points.length - 1], targetPorts, link.targetExit, 5).reverse());
    const distinct = points.filter((p, index) => index === 0
      || p.x !== points[index - 1].x || p.y !== points[index - 1].y);
    const corners = distinct.filter((p, index) => {
      if (index === 0 || index === distinct.length - 1) return true;
      const before = distinct[index - 1], after = distinct[index + 1];
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
    <p class="dim">Exit triggers guide map placement and arrow attachment where possible. Two arrowheads mean travel in both directions.
      Dashed connections are non-geographic travel.
      ${graph.nodes.some(node => node.test_region) ? 'Region 255 is an isolated test map.' : ''}
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
      ...(edge.geographic === false ? { 'stroke-dasharray': '6 4' } : {}),
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
    if (node.test_region) {
      const note = document.createElement('p');
      note.textContent = 'Test region - not connected to the game world.';
      details.appendChild(note);
    }
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
            + (transition.trigger ? ` trigger (${transition.trigger.x}, ${transition.trigger.y}; ${transition.trigger.width} x ${transition.trigger.height})` : '')
            + ` -> (${transition.x ?? '?'}, ${transition.y ?? '?'})`
            + (edge.geographic === false ? ' [non-geographic]' : '');
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
