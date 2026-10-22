// NativeTooltips — WebGL.
//
// Native mode: browsers show a native tooltip for the HTML "title" attribute. We set it on the
// <canvas> when a UI element is hovered and remove it on exit — the delay, look and position come
// from the browser/OS (Chrome, Firefox, Safari, Edge).
//
// Custom bubble: a <div> styled like the browser's own tooltip (Chromium-based browsers on Windows/Linux
// draw it themselves, Edge and Opera GX in their colours, Firefox on Windows its own) or like the OS tooltip (macOS, Windows 11 or a neutral dark
// bubble); light/dark via prefers-color-scheme. Used when a TooltipStyle needs it, for Tooltip.Show() and
// for touch long-press (browsers have no touch tooltips). Placement is computed in C#.

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

    env: null,

    // Which browser and OS the page runs in. Opera GX can only be told apart from Opera through
    // navigator.userAgentData.brands (secure contexts only: https or localhost) — see github.com/opera-gaming/gx-detect.
    environment: function () {
      if (NativeTooltipsState.env) return NativeTooltipsState.env;
      var ua = navigator.userAgent || '';
      var all = ua + ' ' + (navigator.platform || '');
      var uad = navigator.userAgentData;
      var brands = (uad && uad.brands) || [];
      var brand = function (name) {
        for (var i = 0; i < brands.length; i++)
          if (brands[i] && String(brands[i].brand).indexOf(name) >= 0) return true;
        return false;
      };

      var os = /iPhone|iPad|iPod/i.test(all) ? 'ios'
             : /Android/i.test(all) ? 'android'
             : /Mac/i.test(all) ? (navigator.maxTouchPoints > 1 ? 'ios' : 'mac') // iPadOS reports "Macintosh"
             : /Win/i.test(all) ? 'windows'
             : /Linux|X11|CrOS/i.test(all) ? 'linux' : 'other';

      var browser, name;
      if (brand('Opera GX'))                                     { browser = 'operagx';  name = 'Opera GX'; }
      else if (brand('Opera') || /\bOPR\//.test(ua))             { browser = 'chromium'; name = 'Opera'; }
      else if (brand('Microsoft Edge') || /\bEdg\//.test(ua))    { browser = 'edge';     name = 'Edge'; }
      else if (/\bFirefox\//.test(ua))                           { browser = 'firefox';  name = 'Firefox'; }
      else if (brand('Brave') || (navigator.brave && true))      { browser = 'chromium'; name = 'Brave'; }
      else if (/\bVivaldi\//.test(ua))                           { browser = 'chromium'; name = 'Vivaldi'; }
      else if (brand('Google Chrome'))                           { browser = 'chromium'; name = 'Chrome'; }
      else if (/\b(Chrome|Chromium|CriOS)\//.test(ua))           { browser = 'chromium'; name = 'Chromium'; }
      else if (/\bSafari\//.test(ua))                            { browser = 'safari';   name = 'Safari'; }
      else                                                       { browser = 'other';    name = 'Browser'; }

      NativeTooltipsState.env = { os: os, browser: browser, name: name };
      return NativeTooltipsState.env;
    },

    // Theme key for the bubble. useBrowser = TooltipSettings.WebGLBubbleTheme == Browser.
    // "?ntTheme=chromium|edge|operagx|firefox|windows|mac|gtk" in the page URL forces one (handy for comparing).
    themeKey: function (useBrowser) {
      try {
        var forced = /[?&]ntTheme=([a-z]+)/i.exec(window.location.search || '');
        if (forced) return forced[1].toLowerCase();
      } catch (e) {}
      var env = NativeTooltipsState.environment();
      var osTheme = env.os === 'mac' ? 'mac' : env.os === 'windows' ? 'windows' : 'gtk';
      if (!useBrowser) return osTheme;
      // On macOS every browser (Chromium included) shows the AppKit tooltip; Firefox draws an OS-like one
      // everywhere. Chromium-based browsers on Windows / Linux draw their own (views::TooltipAura).
      if (env.os === 'mac' || env.browser === 'safari') return osTheme;
      if (env.browser === 'firefox') return env.os === 'windows' ? 'firefox' : osTheme;
      if (env.os !== 'windows' && env.os !== 'linux') return osTheme;
      if (env.browser === 'operagx' || env.browser === 'edge' || env.browser === 'chromium') return env.browser;
      return osTheme;
    },

    // Defaults per look: font, size px, padding [top, right, bottom, left] px, line height, radius px,
    // max width px (default 320), colours, border, shadow, backdrop blur.
    theme: function (key) {
      var dark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
      var env = NativeTooltipsState.environment();
      var uiFont = env.os === 'windows' ? '"Segoe UI", sans-serif' : 'system-ui, "Segoe UI", Roboto, sans-serif';
      // TooltipAura wraps at min(800 px, half the screen width).
      var chromiumMax = Math.min(800, Math.round(((window.screen && window.screen.width) || 1600) / 2));
      switch (key) {
        case 'mac':
          return {
            font: '-apple-system, BlinkMacSystemFont, "Helvetica Neue", sans-serif', size: 11, pad: [4, 7, 4, 7],
            lineHeight: '1.3', radius: 5,
            bg: dark ? 'rgba(48,48,50,0.96)' : 'rgba(246,246,246,0.97)', fg: dark ? '#fff' : '#000',
            border: dark ? '0.5px solid rgba(255,255,255,0.15)' : '0.5px solid rgba(0,0,0,0.15)',
            shadow: '0 3px 10px rgba(0,0,0,0.22)', blur: true
          };
        case 'windows':
          return {
            font: '"Segoe UI Variable Text", "Segoe UI", sans-serif', size: 12, pad: [5, 8, 5, 8],
            lineHeight: '1.3', radius: 4,
            bg: dark ? '#2b2b2b' : '#ffffff', fg: dark ? '#ffffff' : '#000000',
            border: dark ? '1px solid #454545' : '1px solid rgba(0,0,0,0.12)',
            shadow: '0 2px 8px rgba(0,0,0,0.20)', blur: false
          };
        case 'edge':
          // Same views::TooltipAura as Chromium, in Edge's colours — measured from Edge on Windows: identical in
          // light and dark mode.
          return {
            font: uiFont, size: 12, pad: [3, 7, 4, 7], lineHeight: 'normal', radius: 0, maxWidth: chromiumMax,
            bg: '#333333', fg: '#d7deef', border: '1px solid #d7deef', shadow: 'none', blur: false
          };
        case 'firefox':
          // Firefox on Windows draws its own tooltip — measured from Firefox in light and dark mode. Its
          // InfoBackground / InfoText system colours are NOT the tooltip colours (#1c1b22 / yellow #ffffe1),
          // so they can't be read from CSS.
          return {
            font: uiFont, size: 12, pad: [2, 3, 2, 3], lineHeight: 'normal', radius: 4, maxWidth: 480,
            bg: dark ? '#2b2a33' : '#f9f9fb', fg: dark ? '#fbfbfe' : '#000000',
            border: dark ? '1px solid #f9f9fb' : '1px solid #67676c', shadow: 'none', blur: false
          };
        case 'chromium':
          // views::TooltipViewAura: insets 4/8/5/8 including a 1 px border drawn in the text colour,
          // square corners, colours from ui::kColorTooltipBackground / kColorTooltipForeground.
          return {
            font: uiFont, size: env.os === 'windows' ? 12 : 13, pad: [3, 7, 4, 7], lineHeight: 'normal', radius: 0,
            maxWidth: chromiumMax,
            bg: dark ? '#202124' : '#ffffff', fg: dark ? '#cecfd2' : '#222222',
            border: dark ? '1px solid #cecfd2' : '1px solid #222222',
            shadow: 'none', blur: false
          };
        case 'operagx':
          // Measured from an Opera GX screenshot on Windows. The tooltip follows the GX theme, which a page can't read,
          // so other GX colour schemes look different.
          return {
            font: uiFont, size: 12, pad: [7, 5, 10, 5], lineHeight: 'normal', radius: 2, maxWidth: chromiumMax,
            bg: '#181e39', fg: '#a9b2db', border: '1px solid #303c73', shadow: 'none', blur: false
          };
        default: // 'gtk' and anything else: Adwaita-like dark bubble
          return {
            font: 'system-ui, Roboto, "Segoe UI", sans-serif', size: 13, pad: [6, 10, 6, 10],
            lineHeight: '1.3', radius: 6,
            bg: 'rgba(32,32,34,0.92)', fg: '#ffffff', border: 'none', shadow: '0 2px 8px rgba(0,0,0,0.25)', blur: true
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

  // "Opera GX, Windows" — returned as a malloc'd UTF-8 string (freed by the C# marshaller).
  NativeTooltips_Environment__deps: ['$NativeTooltipsState'],
  NativeTooltips_Environment: function () {
    var env = NativeTooltipsState.environment();
    var os = { windows: 'Windows', mac: 'macOS', linux: 'Linux', android: 'Android', ios: 'iOS' }[env.os] || 'unknown OS';
    var str = env.name + ', ' + os;
    var size = lengthBytesUTF8(str) + 1;
    var buffer = _malloc(size);
    stringToUTF8(str, buffer, size);
    return buffer;
  },

  // ---------------------------------------------------------------- custom bubble
  // params (float32): 0 fontSize, 1 fontScale, 2 bold, 3 padX, 4 padY, 5 maxWidth, 6 overrideColors,
  //                   7-10 background rgba, 11-14 text rgba, 15 cornerRadius, 16 richText,
  //                   17 theme (0 = browser, 1 = operating system)

  NativeTooltips_DivPrepare__deps: ['$NativeTooltipsState'],
  NativeTooltips_DivPrepare: function (textPtr, titlePtr, paramsPtr, count) {
    if (count < 17) return 0;
    var S = NativeTooltipsState;
    var d = S.ensureDiv();
    var p = paramsPtr >> 2;
    var th = S.theme(S.themeKey(count < 18 || HEAPF32[p + 17] < 0.5));

    var fontSize = HEAPF32[p] > 0 ? HEAPF32[p] : th.size * Math.max(0.1, HEAPF32[p + 1]);
    var pad = th.pad.slice();
    if (HEAPF32[p + 3] >= 0) pad[1] = pad[3] = HEAPF32[p + 3];
    if (HEAPF32[p + 4] >= 0) pad[0] = pad[2] = HEAPF32[p + 4];
    var maxWidth = HEAPF32[p + 5] > 0 ? HEAPF32[p + 5] : (th.maxWidth || 320);
    var colors = HEAPF32[p + 6] > 0.5;
    var radius = HEAPF32[p + 15] >= 0 ? HEAPF32[p + 15] : th.radius;

    var s = d.style;
    s.fontFamily = th.font;
    s.fontSize = fontSize + 'px';
    s.lineHeight = th.lineHeight;
    s.fontWeight = HEAPF32[p + 2] > 0.5 ? 'bold' : 'normal';
    s.padding = pad[0] + 'px ' + pad[1] + 'px ' + pad[2] + 'px ' + pad[3] + 'px';
    s.maxWidth = maxWidth + 'px';
    s.borderRadius = radius + 'px';
    s.background = colors ? S.rgba(p, 7) : th.bg;
    s.color = colors ? S.rgba(p, 11) : th.fg;
    s.border = colors ? 'none' : th.border;
    s.boxShadow = th.shadow;
    s.backdropFilter = colors || !th.blur ? 'none' : 'blur(20px)';

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
