using System.Globalization;
using System.Net;
using System.Text;

namespace Petal.Engines.Files.Sharing;

/// <summary>The words on the phone's scanner page, in the app's language.</summary>
public sealed record ScanPageText
{
    public string Language { get; init; } = "en";
    public bool RightToLeft { get; init; }
    public string Title { get; init; } = "Scan to {0}";
    public string Hint { get; init; } = "Photograph each page. Petal straightens it and saves them as one PDF on the PC.";
    public string Take { get; init; } = "Take a photo of a page";
    public string Choose { get; init; } = "Choose photos";
    public string DragHint { get; init; } = "Drag the corners onto the corners of the page.";
    public string Retake { get; init; } = "Retake";
    public string Rotate { get; init; } = "Turn";
    public string AddPage { get; init; } = "Add page";
    public string LookDocument { get; init; } = "Document";
    public string LookColor { get; init; } = "Colour";
    public string LookGray { get; init; } = "Grey";
    public string LookBlackWhite { get; init; } = "Black & white";
    public string Preparing { get; init; } = "Preparing…";
    public string Sending { get; init; } = "Sending…";
    public string Straightening { get; init; } = "Straightening…";
    public string Saving { get; init; } = "Saving the PDF…";
    public string Save { get; init; } = "Save as PDF";
    public string Saved { get; init; } = "Saved on the PC: {0}";
    public string Failed { get; init; } = "That didn't work. Try again.";
    public string Unreadable { get; init; } = "This phone can't open that photo. Take it with the camera instead.";
    public string Remove { get; init; } = "Remove";
    public string Footer { get; init; } = "Straight from the PC over Wi-Fi. Nothing goes through the internet.";
}

/// <summary>
/// The phone's scanner page: take a photo, drag the corners onto the page, add it; repeat; save.
/// The phone turns photos upright and sends JPEGs; the PC finds the page, straightens it, and
/// makes the PDF. Talks to "api/photo", "api/page", "api/thumb", "api/remove", "api/finish" and "api/state".
/// </summary>
public static class ScanPage
{
    public static string Html(ScanPageText t, string computerName)
    {
        static string H(string s) => WebUtility.HtmlEncode(s);
        static string J(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '<': sb.Append("\\u003c"); break;
                    case < ' ': sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }
        var title = string.Format(CultureInfo.InvariantCulture, t.Title, computerName);
        return $$"""
            <!doctype html>
            <html lang="{{H(t.Language)}}" dir="{{(t.RightToLeft ? "rtl" : "ltr")}}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
            <meta name="referrer" content="no-referrer">
            <title>{{H(title)}}</title>
            <style>
            :root { color-scheme: light dark; --bg: #f5f5f7; --card: #fff; --text: #16161a; --muted: #6b6b75; --line: #e4e4ea; --accent: #e8590c; --ok: #2b8a3e; --bad: #c92a2a; }
            @media (prefers-color-scheme: dark) { :root { --bg: #141417; --card: #1f1f24; --text: #f2f2f5; --muted: #a0a0aa; --line: #2e2e35; --accent: #ff8a3d; --ok: #69db7c; --bad: #ff8787; } }
            * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
            body { margin: 0; background: var(--bg); color: var(--text); font: 16px/1.4 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; padding: max(16px, env(safe-area-inset-top)) 16px max(24px, env(safe-area-inset-bottom)); }
            main { max-width: 560px; margin: 0 auto; }
            h1 { font-size: 22px; margin: 8px 0 2px; }
            p { color: var(--muted); margin: 0 0 16px; }
            [hidden] { display: none !important; }
            .big, .primary { display: block; width: 100%; text-align: center; padding: 15px; border: 0; border-radius: 12px; background: var(--accent); color: #fff; font: 600 17px/1.2 inherit; font-family: inherit; }
            .big input, .link input { display: none; }
            .link { display: block; text-align: center; margin-top: 14px; color: var(--accent); font-weight: 600; }
            .stage { display: flex; justify-content: center; background: #000; border-radius: 12px; overflow: hidden; }
            canvas { display: block; touch-action: none; }
            .small { font-size: 14px; margin: 10px 0; text-align: center; }
            .looks { display: grid; grid-template-columns: repeat(4, 1fr); gap: 6px; margin: 0 0 12px; }
            .looks button { padding: 9px 4px; border-radius: 10px; border: 1px solid var(--line); background: var(--card); color: var(--text); font: 500 13px/1.2 inherit; font-family: inherit; }
            .looks button[aria-checked="true"] { border-color: var(--accent); color: var(--accent); font-weight: 700; }
            .row { display: flex; gap: 8px; }
            .row .primary { flex: 1; }
            .ghost { padding: 13px 16px; border-radius: 12px; border: 1px solid var(--line); background: var(--card); color: var(--text); font: 600 16px/1.2 inherit; font-family: inherit; }
            #pages { margin-top: 26px; background: var(--card); border: 1px solid var(--line); border-radius: 14px; padding: 14px; }
            #strip { display: flex; gap: 10px; overflow-x: auto; padding-bottom: 12px; }
            .thumb { position: relative; flex: none; width: 84px; }
            .thumb img { display: block; width: 84px; height: 116px; object-fit: cover; border-radius: 6px; border: 1px solid var(--line); background: #fff; }
            .thumb b { position: absolute; bottom: 4px; inset-inline-start: 4px; background: rgba(0,0,0,.6); color: #fff; font-size: 12px; padding: 1px 6px; border-radius: 8px; }
            .thumb button { position: absolute; top: -6px; inset-inline-end: -6px; width: 26px; height: 26px; border-radius: 13px; border: 0; background: var(--bad); color: #fff; font-size: 16px; line-height: 26px; padding: 0; }
            #busy { position: fixed; inset: 0; background: rgba(0,0,0,.55); display: grid; place-items: center; z-index: 9; }
            #busy div { background: var(--card); color: var(--text); padding: 18px 22px; border-radius: 14px; min-width: 200px; text-align: center; font-weight: 600; }
            #busy i { display: block; width: 28px; height: 28px; margin: 0 auto 10px; border-radius: 50%; border: 3px solid var(--line); border-top-color: var(--accent); animation: spin 0.8s linear infinite; }
            @keyframes spin { to { transform: rotate(360deg); } }
            #note { margin-top: 16px; padding: 12px 14px; border-radius: 12px; background: var(--card); border: 1px solid var(--line); color: var(--ok); font-weight: 600; }
            #note.bad { color: var(--bad); }
            footer { margin-top: 24px; text-align: center; font-size: 13px; color: var(--muted); }
            </style>
            </head>
            <body>
            <main>
            <h1 dir="auto">{{H(title)}}</h1>
            <p>{{H(t.Hint)}}</p>
            <section id="start">
              <label class="big">{{H(t.Take)}}<input id="camera" type="file" accept="image/*" capture="environment"></label>
              <label class="link">{{H(t.Choose)}}<input id="gallery" type="file" accept="image/*" multiple></label>
            </section>
            <section id="adjust" hidden>
              <div class="stage"><canvas id="view"></canvas></div>
              <p class="small">{{H(t.DragHint)}}</p>
              <div class="looks" role="radiogroup">
                <button type="button" role="radio" data-look="document" aria-checked="true">{{H(t.LookDocument)}}</button>
                <button type="button" role="radio" data-look="color" aria-checked="false">{{H(t.LookColor)}}</button>
                <button type="button" role="radio" data-look="gray" aria-checked="false">{{H(t.LookGray)}}</button>
                <button type="button" role="radio" data-look="bw" aria-checked="false">{{H(t.LookBlackWhite)}}</button>
              </div>
              <div class="row">
                <button type="button" id="retake" class="ghost">{{H(t.Retake)}}</button>
                <button type="button" id="rotate" class="ghost" aria-label="{{H(t.Rotate)}}" title="{{H(t.Rotate)}}">&#x21bb;</button>
                <button type="button" id="add" class="primary">{{H(t.AddPage)}}</button>
              </div>
            </section>
            <section id="pages" hidden>
              <div id="strip"></div>
              <button type="button" id="save" class="primary">{{H(t.Save)}}</button>
            </section>
            <div id="note" hidden></div>
            <div id="busy" hidden><div><i></i><span id="busyText"></span></div></div>
            <footer>{{H(t.Footer)}}</footer>
            </main>
            <script>
            const T = { preparing: {{J(t.Preparing)}}, sending: {{J(t.Sending)}}, straightening: {{J(t.Straightening)}}, saving: {{J(t.Saving)}},
              saved: {{J(t.Saved)}}, failed: {{J(t.Failed)}}, unreadable: {{J(t.Unreadable)}}, remove: {{J(t.Remove)}} };
            const $ = id => document.getElementById(id);
            const view = $('view'), ctx = view.getContext('2d');
            let photo = null, corners = null, image = null, look = 'document', queue = [], drag = -1, W = 0, H = 0;

            function busy(text) { $('busy').hidden = !text; if (text) $('busyText').textContent = text; }
            function note(text, bad) { const n = $('note'); n.hidden = !text; n.textContent = text || ''; n.classList.toggle('bad', !!bad); }

            function post(path, body, progress) {
              return new Promise((resolve, reject) => {
                const xhr = new XMLHttpRequest();
                xhr.open('POST', path);
                xhr.responseType = 'json';
                if (progress) xhr.upload.onprogress = e => { if (e.lengthComputable) progress(e.loaded / e.total); };
                xhr.onload = () => xhr.status === 200 && xhr.response && xhr.response.ok ? resolve(xhr.response) : reject(new Error('server'));
                xhr.onerror = () => reject(new Error('network'));
                xhr.send(body);
              });
            }

            // Upright (the browser applies the photo's rotation) and at most 3200 px, as a JPEG.
            async function prepare(file) {
              const url = URL.createObjectURL(file);
              try {
                const img = new Image();
                img.src = url;
                await img.decode();
                const scale = Math.min(1, 3200 / Math.max(img.naturalWidth, img.naturalHeight));
                const c = document.createElement('canvas');
                c.width = Math.round(img.naturalWidth * scale);
                c.height = Math.round(img.naturalHeight * scale);
                c.getContext('2d').drawImage(img, 0, 0, c.width, c.height);
                const blob = await new Promise(done => c.toBlob(done, 'image/jpeg', 0.92));
                if (!blob) throw new Error('encode');
                return { canvas: c, blob };
              } finally {
                URL.revokeObjectURL(url);
              }
            }

            async function take(file) {
              note('');
              busy(T.preparing);
              let prepared;
              try {
                prepared = await prepare(file);
              } catch (e) {
                busy('');
                note(T.unreadable, true);
                next();
                return;
              }
              try {
                const r = await post('api/photo', prepared.blob, f => busy(T.sending + ' ' + Math.round(f * 100) + '%'));
                photo = r.id;
                corners = r.corners;
                image = prepared.canvas;
                busy('');
                openAdjust();
              } catch (e) {
                busy('');
                note(T.failed, true);
                next();
              }
            }

            function next() {
              if (queue.length) take(queue.shift());
              else { $('adjust').hidden = true; $('start').hidden = false; }
            }

            function openAdjust() {
              $('start').hidden = true;
              $('adjust').hidden = false;
              const width = Math.min(document.querySelector('main').clientWidth, 560);
              const maxHeight = window.innerHeight * 0.62;
              const scale = Math.min(width / image.width, maxHeight / image.height);
              W = Math.round(image.width * scale);
              H = Math.round(image.height * scale);
              const dpr = window.devicePixelRatio || 1;
              view.width = Math.round(W * dpr);
              view.height = Math.round(H * dpr);
              view.style.width = W + 'px';
              view.style.height = H + 'px';
              ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
              draw();
              view.scrollIntoView({ block: 'center' });
            }

            const pt = i => ({ x: corners[2 * i] * W, y: corners[2 * i + 1] * H });

            function draw() {
              ctx.clearRect(0, 0, W, H);
              ctx.drawImage(image, 0, 0, W, H);
              ctx.beginPath();
              ctx.rect(0, 0, W, H);
              for (let i = 0; i < 4; i++) { const p = pt(i); i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y); }
              ctx.closePath();
              ctx.fillStyle = 'rgba(0,0,0,.45)';
              ctx.fill('evenodd');
              const accent = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim() || '#e8590c';
              ctx.strokeStyle = accent;
              for (let i = 0; i < 4; i++) {
                const a = pt(i), b = pt((i + 1) % 4);
                ctx.lineWidth = i === 0 ? 5 : 2;
                ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke();
              }
              // The top of the finished page: a small arrow in from the thick edge.
              const a = pt(0), b = pt(1), d = pt(3);
              const mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2, dx = (d.x - a.x), dy = (d.y - a.y), len = Math.hypot(dx, dy) || 1;
              const ux = dx / len, uy = dy / len;
              ctx.fillStyle = accent;
              ctx.beginPath();
              ctx.moveTo(mx + ux * 8, my + uy * 8);
              ctx.lineTo(mx + ux * 24 - uy * 9, my + uy * 24 + ux * 9);
              ctx.lineTo(mx + ux * 24 + uy * 9, my + uy * 24 - ux * 9);
              ctx.closePath();
              ctx.fill();
              for (let i = 0; i < 4; i++) {
                const p = pt(i);
                ctx.beginPath(); ctx.arc(p.x, p.y, 13, 0, Math.PI * 2);
                ctx.fillStyle = 'rgba(255,255,255,.92)'; ctx.fill();
                ctx.lineWidth = 3; ctx.strokeStyle = accent; ctx.stroke();
              }
              if (drag >= 0) loupe(pt(drag), accent);
            }

            // A magnified view above the finger, so the corner can be placed exactly.
            function loupe(p, accent) {
              const r = 52, zoom = 2.5;
              let lx = Math.min(Math.max(p.x, r + 4), W - r - 4), ly = p.y - 90;
              if (ly < r + 4) ly = p.y + 90;
              const s = r / zoom * (image.width / W), cx = p.x / W * image.width, cy = p.y / H * image.height;
              ctx.save();
              ctx.beginPath(); ctx.arc(lx, ly, r, 0, Math.PI * 2); ctx.clip();
              ctx.fillStyle = '#000'; ctx.fillRect(lx - r, ly - r, 2 * r, 2 * r);
              ctx.drawImage(image, cx - s, cy - s, 2 * s, 2 * s, lx - r, ly - r, 2 * r, 2 * r);
              ctx.strokeStyle = accent; ctx.lineWidth = 1.5;
              ctx.beginPath(); ctx.moveTo(lx - 10, ly); ctx.lineTo(lx + 10, ly); ctx.moveTo(lx, ly - 10); ctx.lineTo(lx, ly + 10); ctx.stroke();
              ctx.restore();
              ctx.beginPath(); ctx.arc(lx, ly, r, 0, Math.PI * 2); ctx.lineWidth = 3; ctx.strokeStyle = '#fff'; ctx.stroke();
            }

            function local(e) { const b = view.getBoundingClientRect(); return { x: e.clientX - b.left, y: e.clientY - b.top }; }
            view.addEventListener('pointerdown', e => {
              const p = local(e);
              let best = -1, distance = 1e9;
              for (let i = 0; i < 4; i++) { const q = pt(i), d = Math.hypot(q.x - p.x, q.y - p.y); if (d < distance) { distance = d; best = i; } }
              if (distance > 48) return;
              drag = best;
              view.setPointerCapture(e.pointerId);
              e.preventDefault();
              draw();
            });
            view.addEventListener('pointermove', e => {
              if (drag < 0) return;
              const p = local(e);
              corners[2 * drag] = Math.min(1, Math.max(0, p.x / W));
              corners[2 * drag + 1] = Math.min(1, Math.max(0, p.y / H));
              draw();
            });
            const end = () => { if (drag >= 0) { drag = -1; draw(); } };
            view.addEventListener('pointerup', end);
            view.addEventListener('pointercancel', end);

            document.querySelectorAll('.looks button').forEach(button => button.addEventListener('click', () => {
              look = button.dataset.look;
              document.querySelectorAll('.looks button').forEach(b => b.setAttribute('aria-checked', b === button ? 'true' : 'false'));
            }));
            // A quarter turn: the corner on the left becomes the top-left.
            $('rotate').addEventListener('click', () => { corners = corners.slice(6).concat(corners.slice(0, 6)); draw(); });
            $('retake').addEventListener('click', () => { photo = null; next(); });
            $('add').addEventListener('click', async () => {
              busy(T.straightening);
              try {
                const c = corners.map(v => v.toFixed(5)).join(',');
                const r = await post('api/page?id=' + photo + '&look=' + look + '&c=' + c, null);
                addThumb(r.page);
                busy('');
                photo = null;
                next();
              } catch (e) {
                busy('');
                note(T.failed, true);
              }
            });

            function renumber() {
              const thumbs = [...document.querySelectorAll('.thumb')];
              thumbs.forEach((t, i) => t.querySelector('b').textContent = i + 1);
              $('pages').hidden = thumbs.length === 0;
            }

            function addThumb(page) {
              const box = document.createElement('div');
              box.className = 'thumb';
              const img = document.createElement('img');
              img.src = 'api/thumb?page=' + page;
              img.alt = '';
              const n = document.createElement('b');
              const remove = document.createElement('button');
              remove.type = 'button';
              remove.textContent = '×';
              remove.setAttribute('aria-label', T.remove);
              remove.addEventListener('click', async () => {
                try { await post('api/remove?page=' + page, null); box.remove(); renumber(); } catch (e) { note(T.failed, true); }
              });
              box.append(img, n, remove);
              $('strip').append(box);
              renumber();
              box.scrollIntoView({ inline: 'end', block: 'nearest' });
            }

            $('save').addEventListener('click', async () => {
              busy(T.saving);
              try {
                const r = await post('api/finish', null);
                $('strip').replaceChildren();
                renumber();
                busy('');
                note(T.saved.replace('{0}', r.name));
              } catch (e) {
                busy('');
                note(T.failed, true);
              }
            });

            $('camera').addEventListener('change', e => { const f = e.target.files[0]; e.target.value = ''; if (f) take(f); });
            $('gallery').addEventListener('change', e => {
              const files = [...e.target.files];
              e.target.value = '';
              if (files.length) { queue = files.slice(1); take(files[0]); }
            });

            // After a reload, the pages already on the PC.
            fetch('api/state').then(r => r.json()).then(s => (s.pages || []).forEach(addThumb)).catch(() => {});
            </script>
            </body>
            </html>
            """;
    }
}
