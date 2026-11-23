namespace Petal.Tests;

/// <summary>Scenes as a model in the cloud writes them, to read, check and try by hand.</summary>
internal static class StudyDesignedSamples
{
    /// <summary>A scene whose script fails as it starts: the page shows its idea in words instead.</summary>
    internal const string Failing = """
        SCENE: A scene that breaks
        IDEA: When a scene can't run, its idea is still said in words.
        <html>
        <div class="broken"><p>This scene's script fails as soon as it starts, on purpose, to try the page.</p></div>
        <script>
        (function () {
          'use strict';
          var nothing = null;
          nothing.start();
        })();
        </script>
        </html>
        """;

    /// <summary>Two scenes: dust that gathers into a star, and two boxes pushed with sliders.</summary>
    internal const string Reply = """
        SCENE: Give birth to a star
        IDEA: Gravity pulls the gas and dust of a nebula together until its centre is hot enough for a star to shine.
        <html>
        <div class="star-lab">
          <p class="hint">Press the button and watch gravity pull the nebula together.</p>
          <canvas width="640" height="300" aria-label="A nebula of gas and dust"></canvas>
          <div class="row"><button type="button" class="go">Pull it together</button><button type="button" class="again">Again</button></div>
          <p class="say" aria-live="polite">This is a nebula: a cloud of gas and dust.</p>
        </div>
        <style>
          .star-lab canvas { width: 100%; height: auto; background: #0b0d1a; border-radius: 12px; display: block; }
          .star-lab .row { display: flex; gap: 8px; margin: 10px 0; }
          .star-lab button { padding: 8px 14px; border-radius: 10px; border: 1px solid var(--line); background: var(--surface); color: var(--text); }
          .star-lab .go { background: var(--accent); color: var(--accent-ink); border-color: var(--accent); }
          .star-lab .say { font-weight: 600; }
          .star-lab .hint { color: var(--muted); }
        </style>
        <script>
        (function () {
          'use strict';
          var root = document.querySelector('.star-lab');
          var canvas = root.querySelector('canvas'), ctx = canvas.getContext('2d');
          var say = root.querySelector('.say');
          var still = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
          var dots = [], pulling = false, heat = 0, frame = 0;
          function scatter() {
            dots = [];
            for (var i = 0; i < 260; i++) {
              var a = Math.random() * Math.PI * 2, r = 40 + Math.random() * 230;
              dots.push({ x: 320 + Math.cos(a) * r, y: 150 + Math.sin(a) * r * 0.5, s: 1 + Math.random() * 2, hue: 200 + Math.random() * 140 });
            }
            pulling = false; heat = 0;
            say.textContent = 'This is a nebula: a cloud of gas and dust.';
          }
          function draw() {
            ctx.clearRect(0, 0, 640, 300);
            dots.forEach(function (d) {
              if (pulling) { d.x += (320 - d.x) * 0.03; d.y += (150 - d.y) * 0.03; }
              ctx.fillStyle = 'hsla(' + d.hue + ', 80%, 70%, 0.8)';
              ctx.beginPath(); ctx.arc(d.x, d.y, d.s, 0, Math.PI * 2); ctx.fill();
            });
            if (pulling) heat = Math.min(1, heat + 0.006);
            if (heat > 0.05) {
              var g = ctx.createRadialGradient(320, 150, 0, 320, 150, 20 + heat * 60);
              g.addColorStop(0, 'rgba(255,255,230,' + heat + ')');
              g.addColorStop(1, 'rgba(255,200,80,0)');
              ctx.fillStyle = g; ctx.beginPath(); ctx.arc(320, 150, 20 + heat * 60, 0, Math.PI * 2); ctx.fill();
            }
            if (pulling && heat > 0.3 && heat < 0.9) say.textContent = 'Gravity gathers the gas and dust in the middle, and it gets hotter and hotter…';
            if (heat >= 1) say.textContent = 'Hot enough: a star is born and begins to shine!';
            frame = requestAnimationFrame(draw);
          }
          root.querySelector('.go').addEventListener('click', function () {
            pulling = true;
            if (still) { dots.forEach(function (d) { d.x = 320; d.y = 150; }); heat = 1; }
          });
          root.querySelector('.again').addEventListener('click', scatter);
          scatter();
          draw();
        })();
        </script>
        </html>

        SCENE: Two boxes, the same work?
        IDEA: Work is force times the distance moved, so a smaller force over a longer way can do the same work.
        <html>
        <div class="work-lab">
          <p class="hint">Change each box's force and distance, and compare the work.</p>
          <div class="boxes"></div>
          <p class="sum" aria-live="polite"></p>
          <button type="button" class="again">Again</button>
        </div>
        <style>
          .work-lab .boxes { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
          .work-lab .box { padding: 10px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); }
          .work-lab label { display: block; margin: 6px 0; }
          .work-lab input { width: 100%; }
          .work-lab .bar { height: 14px; border-radius: 7px; background: var(--accent); transition: width .2s; }
          .work-lab .sum { font-weight: 700; margin-top: 10px; }
          .work-lab button { padding: 6px 12px; border-radius: 10px; border: 1px solid var(--line); background: var(--surface); color: var(--text); }
        </style>
        <script>
        (function () {
          'use strict';
          var root = document.querySelector('.work-lab'), holder = root.querySelector('.boxes'), sum = root.querySelector('.sum');
          var boxes = [{ name: 'Box A', f: 80, d: 3 }, { name: 'Box B', f: 40, d: 6 }];
          function slider(label, min, max, value, unit, set) {
            var wrap = document.createElement('label');
            var input = document.createElement('input');
            input.type = 'range'; input.min = min; input.max = max; input.value = value;
            var shown = document.createElement('strong');
            function show() { shown.textContent = ' ' + input.value + ' ' + unit; }
            input.addEventListener('input', function () { set(+input.value); show(); update(); });
            wrap.appendChild(document.createTextNode(label)); wrap.appendChild(shown); wrap.appendChild(input);
            show();
            return wrap;
          }
          var bars = [];
          function build() {
            holder.textContent = ''; bars = [];
            boxes.forEach(function (b) {
              var card = document.createElement('div'); card.className = 'box';
              var title = document.createElement('strong'); title.textContent = b.name; card.appendChild(title);
              card.appendChild(slider('Force', 10, 100, b.f, 'N', function (v) { b.f = v; }));
              card.appendChild(slider('Distance', 1, 10, b.d, 'm', function (v) { b.d = v; }));
              var bar = document.createElement('div'); bar.className = 'bar'; card.appendChild(bar); bars.push(bar);
              holder.appendChild(card);
            });
            update();
          }
          function update() {
            var w = boxes.map(function (b) { return b.f * b.d; });
            bars.forEach(function (bar, i) { bar.style.width = Math.min(100, w[i] / 10) + '%'; });
            sum.textContent = 'A: ' + w[0] + ' J · B: ' + w[1] + ' J — ' + (w[0] === w[1] ? 'the same work!' : w[0] > w[1] ? 'A does more work.' : 'B does more work.');
          }
          root.querySelector('.again').addEventListener('click', function () { boxes = [{ name: 'Box A', f: 80, d: 3 }, { name: 'Box B', f: 40, d: 6 }]; build(); });
          build();
        })();
        </script>
        </html>
        """;
}
