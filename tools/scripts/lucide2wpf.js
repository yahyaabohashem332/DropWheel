// Converts selected Lucide SVG icons into WPF path-markup strings and writes a C# file.
// Usage: node lucide2wpf.js <lucide-static/icons dir> <output .cs file>
const fs = require('fs');
const path = require('path');

const [iconsDir, outFile] = process.argv.slice(2);

// App icon key -> Lucide icon name
const map = {
  compress: 'minimize-2', tag: 'tag', sliders: 'sliders-horizontal', pen: 'pen-line', frame: 'frame',
  crop: 'crop', eyeOff: 'eye-off', filePlus: 'file-plus-2', collage: 'layout-dashboard', audioLines: 'audio-lines',
  scissors: 'scissors', speakers: 'speaker', waveform: 'audio-waveform', bleep: 'mic-off', volumeOff: 'volume-x',
  gauge: 'gauge', camera: 'camera', film: 'film', filmStack: 'clapperboard', split: 'split', grid: 'layout-grid',
  copy: 'files', archive: 'package-open', share: 'share-2', pin: 'pin', clipboard: 'clipboard-copy',
  folderInput: 'folder-input', link: 'link', terminal: 'square-terminal',
  x: 'x', check: 'check', play: 'play', pause: 'pause', stepBack: 'skip-back', stepForward: 'skip-forward',
  rotateCw: 'rotate-cw', trash: 'trash-2', undo: 'undo-2', plus: 'plus', minus: 'minus', chevronUp: 'chevron-up',
  chevronDown: 'chevron-down', chevronRight: 'chevron-right', chevronLeft: 'chevron-left', image: 'image', search: 'search',
  settings: 'settings', alert: 'circle-alert', folderOpen: 'folder-open', arrowRight: 'arrow-right', type: 'type',
  square: 'square', circle: 'circle', pencil: 'pencil', hash: 'hash', zoomIn: 'zoom-in', zoomOut: 'zoom-out',
  imagePlus: 'image-plus', arrowUpRight: 'move-up-right', highlighter: 'highlighter', sparkles: 'sparkles',
  palette: 'palette', layers: 'layers', music: 'music', video: 'video', fileText: 'file-text', fileArchive: 'file-archive',
  monitor: 'monitor', repeat: 'repeat', copyPlain: 'copy', externalLink: 'external-link', power: 'power',
  keyboard: 'keyboard', pointer: 'mouse-pointer-2', info: 'info', heart: 'heart', file: 'file', folder: 'folder',
  scanFace: 'scan-face', scanText: 'scan-text', refresh: 'refresh-cw', grip: 'grip-vertical', dropper: 'pipette',
  loader: 'loader-circle', circleCheck: 'circle-check', circleX: 'circle-x', moon: 'moon', sun: 'sun',
  flipH: 'flip-horizontal-2', rotateCcw: 'rotate-ccw', duplicate: 'copy-plus', fileImage: 'file-image',
  presentation: 'presentation', sheet: 'sheet', book: 'book-open', captions: 'captions', star: 'star',
  wand: 'wand-sparkles', qr: 'qr-code',
};

const fmt = n => (Math.round(n * 1000) / 1000).toString();

function ellipse(cx, cy, rx, ry) {
  return `M${fmt(cx - rx)},${fmt(cy)} A${fmt(rx)},${fmt(ry)} 0 1 0 ${fmt(cx + rx)},${fmt(cy)} ` +
    `A${fmt(rx)},${fmt(ry)} 0 1 0 ${fmt(cx - rx)},${fmt(cy)} Z`;
}

function rect(x, y, w, h, rx, ry) {
  if (!rx && !ry) return `M${fmt(x)},${fmt(y)} H${fmt(x + w)} V${fmt(y + h)} H${fmt(x)} Z`;
  rx = rx || ry;
  ry = ry || rx;
  rx = Math.min(rx, w / 2);
  ry = Math.min(ry, h / 2);
  const a = `A${fmt(rx)},${fmt(ry)} 0 0 1`;
  return `M${fmt(x + rx)},${fmt(y)} H${fmt(x + w - rx)} ${a} ${fmt(x + w)},${fmt(y + ry)} V${fmt(y + h - ry)} ` +
    `${a} ${fmt(x + w - rx)},${fmt(y + h)} H${fmt(x + rx)} ${a} ${fmt(x)},${fmt(y + h - ry)} ` +
    `V${fmt(y + ry)} ${a} ${fmt(x + rx)},${fmt(y)} Z`;
}

// SVG allows arc flags without separators ("a2 2 0 012 2"); WPF does not, so re-tokenise.
function normalizePath(d) {
  const tokens = [];
  const re = /([MmLlHhVvCcSsQqTtAaZz])|(-?(?:\d+\.?\d*|\.\d+)(?:[eE]-?\d+)?)/g;
  let m;
  while ((m = re.exec(d))) tokens.push(m[0]);
  const argc = { m: 2, l: 2, h: 1, v: 1, c: 6, s: 4, q: 4, t: 2, a: 7, z: 0 };
  const out = [];
  let cmd = null;
  let i = 0;
  while (i < tokens.length) {
    const t = tokens[i];
    if (/[A-Za-z]/.test(t)) {
      cmd = t;
      out.push(t);
      i++;
      if (cmd.toLowerCase() === 'z') continue;
    }
    const n = argc[cmd.toLowerCase()];
    const args = [];
    while (args.length < n && i < tokens.length && !/[A-Za-z]/.test(tokens[i])) {
      const tok = tokens[i++];
      if (cmd.toLowerCase() === 'a' && (args.length === 3 || args.length === 4) && /^[01][01.\d]/.test(tok)) {
        args.push(tok[0]);
        tokens.splice(i, 0, tok.slice(1));
        continue;
      }
      args.push(tok);
    }
    out.push(args.join(' '));
  }
  // Each <path> starts from the origin, so a leading relative "m" is really absolute once paths are joined.
  if (out[0] === 'm') out[0] = 'M';
  return out.join(' ').replace(/\s+/g, ' ').trim();
}

const attr = (s, n) => {
  const m = new RegExp(`\\s${n}="([^"]*)"`).exec(s);
  return m ? m[1] : undefined;
};
const num = (a, n, d = 0) => (attr(a, n) === undefined ? d : Number(attr(a, n)));

const lines = [];
for (const [key, name] of Object.entries(map)) {
  const file = path.join(iconsDir, name + '.svg');
  if (!fs.existsSync(file)) {
    console.error('missing icon', name);
    process.exit(1);
  }
  const svg = fs.readFileSync(file, 'utf8');
  const parts = [];
  for (const el of svg.matchAll(/<(path|circle|ellipse|rect|line|polyline|polygon)\b([^>]*)\/>/g)) {
    const [, tag, a] = el;
    if (tag === 'path') parts.push(normalizePath(attr(a, 'd')));
    else if (tag === 'circle') parts.push(ellipse(num(a, 'cx'), num(a, 'cy'), num(a, 'r'), num(a, 'r')));
    else if (tag === 'ellipse') parts.push(ellipse(num(a, 'cx'), num(a, 'cy'), num(a, 'rx'), num(a, 'ry')));
    else if (tag === 'rect') parts.push(rect(num(a, 'x'), num(a, 'y'), num(a, 'width'), num(a, 'height'), num(a, 'rx'), num(a, 'ry')));
    else if (tag === 'line') parts.push(`M${attr(a, 'x1')},${attr(a, 'y1')} L${attr(a, 'x2')},${attr(a, 'y2')}`);
    else {
      const p = attr(a, 'points').trim().split(/[\s,]+/);
      let s = `M${p[0]},${p[1]}`;
      for (let k = 2; k < p.length; k += 2) s += ` L${p[k]},${p[k + 1]}`;
      if (tag === 'polygon') s += ' Z';
      parts.push(s);
    }
  }
  lines.push(`        ["${key}"] = "${parts.join(' ')}",`);
}

const cs = `// <auto-generated>
// Icons from Lucide (https://lucide.dev), ISC License. Copyright (c) Lucide Contributors.
// Generated by tools/scripts/lucide2wpf.js. Each value is WPF path markup in a 24x24 box, drawn as a 2px stroke.
// </auto-generated>
namespace Petal.App.Visuals;

public static partial class Icons
{
    private static readonly Dictionary<string, string> Data = new()
    {
${lines.join('\n')}
    };
}
`;
fs.writeFileSync(outFile, cs);
console.log('wrote', Object.keys(map).length, 'icons');
