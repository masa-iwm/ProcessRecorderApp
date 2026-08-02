// Log 画面のターミナル。C# 側（LogTerminalView）とのブリッジ。
//
// 約束ごと:
//   - メッセージは U+001F 区切りの文字列。JSON を使わないのは、
//     ログ本文をエスケープせずそのまま最後のフィールドに載せられるため。
//   - 'y'（ready）を返すまで C# は 1 バイトも送ってこない。
//     初期サイズ 80x24 のまま書き込むと、長い行が誤った桁で折り返されるため。
//   - 書き込みの ack（'a'）が唯一のフロー制御。C# は ack を待ってから次を送る。
//     ここを返さないとログが止まるので、write のコールバックからは必ず返すこと。
'use strict';

(function () {
  const US = '\u001F';
  const post = (s) => window.chrome.webview.postMessage(s);

  let term = null;
  let fit = null;
  let webgl = null;
  let follow = true;
  let renderer = 'dom';
  let ready = false;

  function parseInit(message) {
    // i US scrollback US follow US fg US bg US c0 .. c15
    const f = message.split(US);
    const colors = f.slice(5, 21);
    return {
      scrollback: parseInt(f[1], 10),
      follow: f[2] === '1',
      theme: {
        background: f[4],
        foreground: f[3],
        cursor: f[3],
        black: colors[0], red: colors[1], green: colors[2], yellow: colors[3],
        blue: colors[4], magenta: colors[5], cyan: colors[6], white: colors[7],
        brightBlack: colors[8], brightRed: colors[9], brightGreen: colors[10],
        brightYellow: colors[11], brightBlue: colors[12], brightMagenta: colors[13],
        brightCyan: colors[14], brightWhite: colors[15],
        selectionBackground: '#4DFFFFFF',
      },
    };
  }

  function debounce(fn, ms) {
    let handle = 0;
    return function () {
      window.clearTimeout(handle);
      handle = window.setTimeout(fn, ms);
    };
  }

  function safeFit() {
    try { fit.fit(); } catch (e) { /* 幅 0 のときなど。次の機会に合わせ直す */ }
  }

  function init(options) {
    follow = options.follow;

    term = new Terminal({
      fontFamily: 'Consolas, Cascadia Mono, monospace',
      fontSize: 12,
      scrollback: options.scrollback,
      theme: options.theme,
      // 入力は受けない（表示専用）。カーソルも出さない
      disableStdin: true,
      cursorBlink: false,
      cursorInactiveStyle: 'none',
      // 生の \r\n をそのまま解釈させる。true にすると \n が \r\n 扱いになり二重に効く
      convertEol: false,
      smoothScrollDuration: 0,
      allowProposedApi: false,
      rightClickSelectsWord: false,
    });

    fit = new FitAddon.FitAddon();
    term.loadAddon(fit);

    // open() が先。要素が付く前に WebGL アドオンを載せると activate() が
    // onWillOpen へ遅延され、下の try/catch を素通りしてフォールバックが成立しない
    term.open(document.getElementById('term'));

    try {
      webgl = new WebglAddon.WebglAddon();
      term.loadAddon(webgl);          // WebGL2 が使えないとここで同期に throw する
      renderer = 'webgl';
      webgl.onContextLoss(() => {
        // コンテキストを失ったら組み込みの DOM レンダラーへ落ちる（描画は続く）
        try { webgl.dispose(); } catch (e) { /* 破棄済み */ }
        webgl = null;
        renderer = 'dom';
      });
    } catch (e) {
      try { if (webgl) { webgl.dispose(); } } catch (e2) { /* 未 activate */ }
      webgl = null;
      renderer = 'dom';
    }

    safeFit();
    new ResizeObserver(debounce(safeFit, 50)).observe(document.body);
    window.addEventListener('resize', safeFit);
    watchDevicePixelRatio();

    // Ctrl+C は選択範囲を C# へ返すだけ。クリップボードへ入れるのは C# 側
    // （navigator.clipboard の可否に依存させない）
    term.attachCustomKeyEventHandler((ev) => {
      if (ev.type === 'keydown' && ev.ctrlKey && !ev.altKey && !ev.shiftKey &&
          ev.code === 'KeyC' && term.hasSelection()) {
        post('c' + US + term.getSelection());
        return false;
      }
      return true;
    });

    // 利用者が上へスクロールしたら追従を止める。最下部へ戻したら再開する
    term.onScroll(() => {
      const buffer = term.buffer.active;
      const atBottom = buffer.viewportY >= buffer.baseY;
      if (atBottom !== follow) {
        follow = atBottom;
        post('s' + US + (follow ? '1' : '0'));
      }
    });

    for (const type of ['dragover', 'drop', 'dragenter']) {
      window.addEventListener(type, (e) => { e.preventDefault(); e.stopPropagation(); }, true);
    }

    ready = true;
    post('y' + US + renderer);
  }

  function watchDevicePixelRatio() {
    // DPI が変わるとセルの寸法が変わる。matchMedia は変化のたびに作り直す必要がある
    const attach = () => {
      const query = window.matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`);
      const handler = () => { query.removeEventListener('change', handler); safeFit(); attach(); };
      query.addEventListener('change', handler);
    };
    attach();
  }

  window.chrome.webview.addEventListener('message', (ev) => {
    const message = ev.data;
    const tag = message[0];

    if (tag === 'i') {
      if (!ready) { init(parseInit(message)); }
      return;
    }
    if (!ready) { return; }

    if (tag === 'w') {
      const p1 = message.indexOf(US, 2);
      const p2 = message.indexOf(US, p1 + 1);
      const id = message.slice(2, p1);
      const flags = parseInt(message.slice(p1 + 1, p2), 10);
      const data = message.slice(p2 + 1);
      // Clear は書き込みと同じメッセージに載っている。別経路で送ると順序が保証されず、
      // 消去が直後の書き込みを追い越して新しい行を消す
      if (flags & 1) { term.reset(); }
      term.write(data, () => {
        if (follow) { term.scrollToBottom(); }
        post('a' + US + id);
      });
      return;
    }
    if (tag === 'f') {
      follow = message.slice(2) === '1';
      if (follow) { term.scrollToBottom(); }
      return;
    }
    if (tag === 'n') {
      term.options.scrollback = parseInt(message.slice(2), 10);
      return;
    }
  });

  window.onerror = (message) => {
    try { post('e' + US + String(message)); } catch (e) { /* ブリッジごと死んでいる */ }
  };
})();
