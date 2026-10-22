// NativeTooltips — WebGL.
//
// Native mode: browsers show a native tooltip for the HTML "title" attribute. We set it on the
// <canvas> when a UI element is hovered and remove it on exit — the delay, look and position come
// from the browser/OS (Chrome, Firefox, Safari, Edge).
//
// Custom bubble: a <div> styled like the OS tooltip (macOS, Windows 11 or a neutral dark bubble;
// light/dark via prefers-color-scheme). Used when a TooltipStyle needs it, for Tooltip.Show() and for
// touch long-press (browsers have no touch tooltips). Placement is computed in C#.

var NativeTooltipsLib = {
  $NativeTooltipsState: {
    canvas: null,
    hadTitle: false,
    originalTitle: null,
    active: false,
    div: null,
    titleEl: null,
    textEl: null,
    width: 0,
    height: 0,
    geom: [0, 0, 0, 0, 0, 0],

    findCanvas: function () {
      if (NativeTooltipsState.canvas && document.body.contains(NativeTooltipsState.canvas))
        return NativeTooltipsState.canvas;
      var c = (typeof Module !== 'undefined' && Module['canvas']) ||
              document.querySelector('#unity-canvas') ||
              document.querySelector('canvas');
      NativeTooltipsState.canvas = c || null;
      return NativeTooltipsState.canvas;
    },

    platform: function () {
      var ua = (navigator.userAgent || '') + ' ' + (navigator.platform || '');
      if (/iPhone|iPad|iPod|Android/i.test(ua)) return 'mobile';
      if (/Mac/i.test(ua)) return 'mac';
      if (/Win/i.test(ua)) return 'windows';
      return 'other';
    },

    // System-like defaults: font px, padding x/y px, radius px, colours, border, shadow.
    theme: function () {
      var dark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
      switch (NativeTooltipsState.platform()) {
        case 'mac':
          return {
            font: '-apple-system, BlinkMacSystemFont, "Helvetica Neue", sans-serif', size: 11, padX: 7, padY: 4, radius: 5,
            bg: dark ? 'rgba(48,48,50,0.96)' : 'rgba(246,246,246,0.97)', fg: dark ? '#fff' : '#000',
            border: dark ? '0.5px solid rgba(255,255,255,0.15)' : '0.5px solid rgba(0,0,0,0.15)',
            shadow: '0 3px 10px rgba(0,0,0,0.22)'
          };
        case 'windows':
          return {
            font: '"Segoe UI Variable Text", "Segoe UI", sans-serif', size: 12, padX: 8, padY: 5, radius: 4,
            bg: dark ? '#2b2b2b' : '#ffffff', fg: dark ? '#ffffff' : '#000000',
            border: dark ? '1px solid #454545' : '1px solid rgba(0,0,0,0.12)',
            shadow: '0 2px 8px rgba(0,0,0,0.20)'
          };
        default:
          return {
            font: 'system-ui, Roboto, "Segoe UI", sans-serif', size: 13, padX: 10, padY: 6, radius: 6,
            bg: 'rgba(32,32,34,0.92)', fg: '#ffffff', border: 'none', shadow: '0 2px 8px rgba(0,0,0,0.25)'
          };
      }
    },

    ensureDiv: function () {
      if (NativeTooltipsState.div && document.body.contains(NativeTooltipsState.div)) return NativeTooltipsState.div;
      var d = document.createElement('div');
      d.setAttribute('role', 'tooltip');
      var s = d.style;
      s.position = 'fixed';
      s.left = '0px';
      s.top = '0px';
      s.zIndex = '2147483647';
      s.pointerEvents = 'none';
      s.boxSizing = 'border-box';
      s.whiteSpace = 'pre-wrap';
      s.overflowWrap = 'anywhere';
      s.lineHeight = '1.3';
      s.display = 'none';
      s.opacity = '0';
      s.transition = 'opacity 120ms ease-out';
      s.userSelect = 'none';
      s.webkitUserSelect = 'none';
      var t = document.createElement('div');
      t.style.fontWeight = 'bold';
      var b = document.createElement('div');
      d.appendChild(t);
      d.appendChild(b);
      document.body.appendChild(d);
      NativeTooltipsState.div = d;
      NativeTooltipsState.titleEl = t;
      NativeTooltipsState.textEl = b;
      return d;
    },

    // Rich text runs: records split by U+001E, fields by U+001F: flags, RRGGBBAA, size, scale, delta, text.
    // flags: 1 bold, 2 italic, 4 underline, 8 strike, 16 colour. Built with textContent — no HTML injection.
    fillRich: function (el, encoded, basePx) {
      el.textContent = '';
      var records = encoded.split('\u001E');
      for (var r = 0; r < records.length; r++) {
        var f = records[r].split('\u001F');
        if (f.length < 6) continue;
        var flags = parseInt(f[0], 10) || 0;
        var span = document.createElement('span');
        span.textContent = f.slice(5).join(' ');
        if (flags & 1) span.style.fontWeight = 'bold';
        if (flags & 2) span.style.fontStyle = 'italic';
        var deco = [];
        if (flags & 4) deco.push('underline');
        if (flags & 8) deco.push('line-through');
        if (deco.length) span.style.textDecoration = deco.join(' ');
        if (flags & 16) {
          var hex = f[1];
          span.style.color = 'rgba(' + parseInt(hex.substr(0, 2), 16) + ',' + parseInt(hex.substr(2, 2), 16) + ',' +
                             parseInt(hex.substr(4, 2), 16) + ',' + (parseInt(hex.substr(6, 2), 16) / 255) + ')';
        }
        var size = parseFloat(f[2]) || 0, scale = parseFloat(f[3]) || 1, delta = parseFloat(f[4]) || 0;
        if (size > 0 || scale !== 1 || delta !== 0)
          span.style.fontSize = (size > 0 ? size : Math.max(1, basePx * scale + delta)) + 'px';
        el.appendChild(span);
      }
    },

    rgba: function (p, i) {
      return 'rgba(' + Math.round(HEAPF32[p + i] * 255) + ',' + Math.round(HEAPF32[p + i + 1] * 255) + ',' +
             Math.round(HEAPF32[p + i + 2] * 255) + ',' + HEAPF32[p + i + 3] + ')';
    }
  },

  // ---------------------------------------------------------------- native "title" mode

  NativeTooltips_SetTitle__deps: ['$NativeTooltipsState'],
  NativeTooltips_SetTitle: function (textPtr) {
    var canvas = NativeTooltipsState.findCanvas();
    if (!canvas) return 0;
    if (!NativeTooltipsState.active) {
      NativeTooltipsState.hadTitle = canvas.hasAttribute('title');
      NativeTooltipsState.originalTitle = canvas.getAttribute('title');
      NativeTooltipsState.active = true;
    }
    canvas.setAttribute('title', UTF8ToString(textPtr));
    return 1;
  },

  NativeTooltips_ClearTitle__deps: ['$NativeTooltipsState'],
  NativeTooltips_ClearTitle: function () {
    var canvas = NativeTooltipsState.findCanvas();
    if (!canvas || !NativeTooltipsState.active) return;
    if (NativeTooltipsState.hadTitle) canvas.setAttribute('title', NativeTooltipsState.originalTitle);
    else canvas.removeAttribute('title');
    NativeTooltipsState.active = false;
  },

  NativeTooltips_IsAvailable__deps: ['$NativeTooltipsState'],
  NativeTooltips_IsAvailable: function () {
    return NativeTooltipsState.findCanvas() ? 1 : 0;
  },

  // ---------------------------------------------------------------- custom bubble
  // params (float32): 0 fontSize, 1 fontScale, 2 bold, 3 padX, 4 padY, 5 maxWidth, 6 overrideColors,
  //                   7-10 background rgba, 11-14 text rgba, 15 cornerRadius, 16 richText

  NativeTooltips_DivPrepare__deps: ['$NativeTooltipsState'],
  NativeTooltips_DivPrepare: function (textPtr, titlePtr, paramsPtr, count) {
    if (count < 17) return 0;
    var S = NativeTooltipsState;
    var d = S.ensureDiv();
    var p = paramsPtr >> 2;
    var th = S.theme();

    var fontSize = HEAPF32[p] > 0 ? HEAPF32[p] : th.size * Math.max(0.1, HEAPF32[p + 1]);
    var padX = HEAPF32[p + 3] >= 0 ? HEAPF32[p + 3] : th.padX;
    var padY = HEAPF32[p + 4] >= 0 ? HEAPF32[p + 4] : th.padY;
    var maxWidth = HEAPF32[p + 5] > 0 ? HEAPF32[p + 5] : 320;
    var colors = HEAPF32[p + 6] > 0.5;
    var radius = HEAPF32[p + 15] >= 0 ? HEAPF32[p + 15] : th.radius;

    var s = d.style;
    s.fontFamily = th.font;
    s.fontSize = fontSize + 'px';
    s.fontWeight = HEAPF32[p + 2] > 0.5 ? 'bold' : 'normal';
    s.padding = padY + 'px ' + padX + 'px';
    s.maxWidth = maxWidth + 'px';
    s.borderRadius = radius + 'px';
    s.background = colors ? S.rgba(p, 7) : th.bg;
    s.color = colors ? S.rgba(p, 11) : th.fg;
    s.border = colors ? 'none' : th.border;
    s.boxShadow = th.shadow;
    s.backdropFilter = colors ? 'none' : 'blur(20px)';

    var rich = HEAPF32[p + 16] > 0.5;
    var title = titlePtr ? UTF8ToString(titlePtr) : '';
    var text = UTF8ToString(textPtr);
    if (rich) {
      S.fillRich(S.titleEl, title, fontSize);
      S.fillRich(S.textEl, text, fontSize);
    } else {
      S.titleEl.textContent = title;
      S.textEl.textContent = text;
    }
    S.titleEl.style.display = title ? 'block' : 'none';

    // Measure at the top-left so the available width doesn't limit wrapping.
    var wasVisible = s.display !== 'none' && s.visibility !== 'hidden';
    var oldLeft = s.left, oldTop = s.top;
    s.visibility = 'hidden';
    s.display = 'block';
    s.left = '0px';
    s.top = '0px';
    var r = d.getBoundingClientRect();
    S.width = Math.ceil(r.width);
    S.height = Math.ceil(r.height);
    if (wasVisible) {
      s.left = oldLeft;
      s.top = oldTop;
      s.visibility = 'visible';
    }
    return 1;
  },

  NativeTooltips_DivSize__deps: ['$NativeTooltipsState'],
  NativeTooltips_DivSize: function (index) {
    return index === 0 ? NativeTooltipsState.width : NativeTooltipsState.height;
  },

  // Snapshot: canvas rect (x, y, w, h) and viewport size (w, h) in CSS pixels.
  NativeTooltips_DivSnapshot__deps: ['$NativeTooltipsState'],
  NativeTooltips_DivSnapshot: function () {
    var canvas = NativeTooltipsState.findCanvas();
    if (!canvas) return 0;
    var r = canvas.getBoundingClientRect();
    NativeTooltipsState.geom = [r.left, r.top, r.width, r.height,
      window.innerWidth || document.documentElement.clientWidth,
      window.innerHeight || document.documentElement.clientHeight];
    return 1;
  },

  NativeTooltips_DivGeometry__deps: ['$NativeTooltipsState'],
  NativeTooltips_DivGeometry: function (index) {
    return NativeTooltipsState.geom[index] || 0;
  },

  NativeTooltips_DivShowAt__deps: ['$NativeTooltipsState'],
  NativeTooltips_DivShowAt: function (x, y) {
    var d = NativeTooltipsState.div;
    if (!d) return;
    var s = d.style;
    s.left = Math.round(x) + 'px';
    s.top = Math.round(y) + 'px';
    s.display = 'block';
    s.visibility = 'visible';
    // next frame: fade in (like the OS)
    requestAnimationFrame(function () { s.opacity = '1'; });
  },

  NativeTooltips_DivHide__deps: ['$NativeTooltipsState'],
  NativeTooltips_DivHide: function () {
    var d = NativeTooltipsState.div;
    if (!d) return;
    d.style.opacity = '0';
    d.style.display = 'none';
  }
};

autoAddDeps(NativeTooltipsLib, '$NativeTooltipsState');
mergeInto(LibraryManager.library, NativeTooltipsLib);
