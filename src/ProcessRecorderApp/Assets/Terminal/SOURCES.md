# Assets/Terminal/vendor の出所

Log 画面のターミナル表示に使う xterm.js。**上流の配布物を改変せずに置いている。**

`licenses/third-party/` の台帳（`SOURCES.tsv` / `COMPONENTS.tsv`）には**載せない**
── あちらは同梱 GStreamer ランタイム専用で、`ThirdPartyLicenseTests` が
ディスクと `SOURCES.tsv` を、`release.yml` が `COMPONENTS.tsv` と発行物の
`runtimes/win-x64` を双方向で突き合わせる。xterm.js は**同梱・非同梱の両方**の
配布物に入るので、あの台帳に載せると必ず赤になる。ここが正本である。

改行は `.gitattributes` の `src/ProcessRecorderApp/Assets/Terminal/vendor/** -text` で
変換を止めている。止めないとチェックアウトのたびに CRLF へ書き換えられ、
下の SHA256 が合わなくなる。

## ファイル

| ファイル | パッケージ | 版 | ライセンス | SHA256 |
|---|---|---|---|---|
| `xterm.js` | `@xterm/xterm` | 6.0.0 | MIT | `14903579ff54664cd72f8e8699e6961a6272c21863ec1c3b118cdc8af5d4a972` |
| `xterm.css` | `@xterm/xterm` | 6.0.0 | MIT | `854a7c0fb70e8b1a083c16797ab827299fb18744f5ad34f227b48337e33293c6` |
| `LICENSE-xterm.txt` | `@xterm/xterm` | 6.0.0 | MIT | `b569f629d00f2626a8100df2a1798210535621e42164dfd426a6fe5aac7b0ccd` |
| `addon-webgl.js` | `@xterm/addon-webgl` | 0.19.0 | MIT | `b85f8d4b3e9756bebb757e3fe47134d70f03ea3d6b187624426d2e2b65dec06c` |
| `LICENSE-xterm-addon-webgl.txt` | `@xterm/addon-webgl` | 0.19.0 | MIT | `21b975c39532001d431dc3b2c29fbff691d83f370833a66b54698bda290fde0e` |
| `addon-fit.js` | `@xterm/addon-fit` | 0.11.0 | MIT | `ba3ea256ce0620a0992a197d6c9baea64823fc93d8da07a9e366ca9943c18527` |
| `LICENSE-xterm-addon-fit.txt` | `@xterm/addon-fit` | 0.11.0 | MIT | `e256f01188af527e4d06d21d06fbf785ae9c50d4b328bf03cbe0ba7f0aa4228f` |

ライセンス全文は 3 つとも**内容が異なる**（著作権表示が違う）ので 1 つにまとめない。

## 取得元

```
https://cdn.jsdelivr.net/npm/@xterm/xterm@6.0.0/lib/xterm.js
https://cdn.jsdelivr.net/npm/@xterm/xterm@6.0.0/css/xterm.css
https://cdn.jsdelivr.net/npm/@xterm/xterm@6.0.0/LICENSE
https://cdn.jsdelivr.net/npm/@xterm/addon-webgl@0.19.0/lib/addon-webgl.js
https://cdn.jsdelivr.net/npm/@xterm/addon-webgl@0.19.0/LICENSE
https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.11.0/lib/addon-fit.js
https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.11.0/LICENSE
```

## 版を選んだ根拠

- **canvas レンダラーは xterm.js 6 で削除された**ので、レンダラーは WebGL → DOM の 2 段。
- アドオンの npm メタデータは `peerDependencies: "@xterm/xterm": "^5.0.0"` のままだが、
  この 2 つは xterm.js リポジトリの master と同じ版番号であり、v6 世代のものである。
- `.js.map` / `.mjs` / `typings` は同梱しない（`.map` だけで 3.6MB あり、
  DevTools は無効にしているので参照されない）。
- UMD を選んだのでバンドラーが要らない。グローバル名は
  `Terminal` / `FitAddon.FitAddon` / `WebglAddon.WebglAddon`。

## 更新するとき

1. 上の URL の版を差し替えて取り直す。
2. `sha256sum` を取り直して上の表を更新する。
3. [`THIRD-PARTY-NOTICES.md`](../../../../THIRD-PARTY-NOTICES.md) の版表記も同時に直す。
4. 4 つの発行プロファイルで `Assets\Terminal\` が発行物に入ることを確かめる。
