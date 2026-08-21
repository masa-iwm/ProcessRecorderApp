import io
import subprocess

body = subprocess.run(
    ['gh', 'release', 'view', 'v0.3.1', '--json', 'body', '-q', '.body'],
    capture_output=True, text=True, encoding='utf-8', check=True).stdout

pairs = [
    # --- English: lead ---
    ("""instead of the positional index, so unplugging some other monitor no longer makes it capture the
wrong screen. Nothing else changes: recording behaviour, the `activity.log` format and the CLI
exit codes are the same as in v0.3.0.""",
     """instead of the positional index, so unplugging some other monitor no longer makes it capture the
wrong screen. A disconnect that the source reports only as end-of-stream — which is what Windows
Graphics Capture does — now triggers the automatic recovery as well. Recording behaviour, the
`activity.log` format and the CLI exit codes are the same as in v0.3.0."""),

    # --- English: new bullet ---
    ("""- **A monitor that is not connected fails loudly**""",
     """- **A disconnect that only ends the stream is now recovered from** — automatic recovery was
  started by an error on the bus, and `capture-api=wgc` does not raise one: disconnecting the
  display ended the stream cleanly, so nothing was retried and the recorder stayed dark until it
  was reconfigured by hand. End-of-stream on the capture pipeline now schedules a recovery too.
  It does so only for sources recognised as devices — for a camera or a screen capture, the stream
  ending can only mean the device went away, whereas a finite test pattern or a file ends on
  purpose and rebuilding it would loop forever.
- **A monitor that is not connected fails loudly**"""),

    # --- Japanese: lead ---
    ("""パスで指定できるようにしました。別のモニタを抜いても、撮る画面が入れ替わりません。
ほかは変わっていません ── 録画の挙動・`activity.log` の形式・CLI の終了コードは v0.3.0 と同じです。""",
     """パスで指定できるようにしました。別のモニタを抜いても、撮る画面が入れ替わりません。
あわせて、ソースが**エラーを出さず EOS だけで終わる**切断（Windows Graphics Capture が
これにあたります）でも自動復帰が動くようにしました。録画の挙動・`activity.log` の形式・
CLI の終了コードは v0.3.0 と同じです。"""),

    # --- Japanese: new bullet ---
    ("""- **指定したモニタが繋がっていなければ、はっきり失敗します**""",
     """- **EOS だけで終わる切断からも復帰するようにしました** — 自動復帰はバスのエラーで
  起動していましたが、`capture-api=wgc` はディスプレイを切断してもエラーを出さず、
  ストリームが綺麗に終わるだけでした。そのため何も再試行されず、手で設定し直すまで
  レコーダーは止まったままでした。キャプチャ側の EOS でも復帰を予約するようにしています。
  対象は**デバイスと認識できるソースだけ**です ── カメラや画面キャプチャの EOS は
  デバイスが去った以外にありえませんが、有限のテストパターンやファイルの EOS は正常終了で、
  作り直すと無限に回り続けるためです。
- **指定したモニタが繋がっていなければ、はっきり失敗します**"""),
]

for old, new in pairs:
    assert old in body, old[:60]
    body = body.replace(old, new, 1)

io.open('C:/temp/v031b-notes.md', 'w', encoding='utf-8', newline='\n').write(body)
print('ok')
