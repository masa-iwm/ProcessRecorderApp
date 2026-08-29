using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace ProcessRecorderApp.Components;

/// <summary>索引に載っている録画 1 件。</summary>
/// <param name="RelativePath">配信 root からの相対パス（区切りは <c>/</c>）。</param>
/// <param name="Length">走査した時点の長さ。</param>
/// <param name="LastWriteTimeUtc">更新時刻（UTC）。</param>
/// <param name="InProgress">録画中（<c>filesink</c> が書き込みで握っている）。</param>
/// <param name="Fragmented">fragmented MP4（<see cref="Fmp4Probe.IsFragmented"/>）。</param>
/// <param name="StartTimeUtc">
/// 録画開始時刻（UTC）。sidecar があればその値、無ければファイル名から推定した値。
/// </param>
/// <param name="Recorder">録画したレコーダーの名前。推定できなければ空文字。</param>
/// <param name="Trigger">
/// 開始理由（<see cref="RecordingSidecar.Trigger"/>）。<b>sidecar からしか来ない</b>ので、
/// 録画中と sidecar の無いものは <see langword="null"/>。
/// </param>
/// <param name="DurationMs">
/// 尺（ミリ秒）。sidecar があればその値、無ければ <c>moov</c> の <c>mvhd</c> から読んだ値。
/// fragmented と録画中は <see langword="null"/>（総尺は <see cref="Fmp4FragmentIndex"/> が持つ）。
/// </param>
/// <param name="Width">映像の幅（sidecar にあるときだけ）。</param>
/// <param name="Height">映像の高さ（同上）。</param>
/// <param name="HasThumbnail"><c>&lt;録画ファイル名&gt;.png</c> が在る。</param>
public sealed record RecordingEntry(
    string RelativePath,
    long Length,
    DateTime LastWriteTimeUtc,
    bool InProgress,
    bool Fragmented,
    DateTime StartTimeUtc,
    string Recorder,
    string? Trigger,
    long? DurationMs,
    int? Width,
    int? Height,
    bool HasThumbnail);

/// <summary>索引の差分の種類。</summary>
public enum RecordingIndexChangeKind
{
    /// <summary>今まで無かったファイルが現れた。</summary>
    Added,

    /// <summary>録画中だったものが録画中でなくなった。</summary>
    Completed,

    /// <summary>ファイルが消えた。</summary>
    Removed,

    /// <summary>
    /// 長さ・更新時刻・sidecar・サムネイルのいずれかが変わった。
    /// <b>録画中どうしのときは長さ・更新時刻の変化では出ない</b>（<see cref="RecordingIndex.Diff"/>）。
    /// </summary>
    Updated,
}

/// <summary>索引の差分 1 件。</summary>
/// <param name="Kind">差分の種類。</param>
/// <param name="RelativePath">対象の相対パス。</param>
public sealed record RecordingIndexChange(RecordingIndexChangeKind Kind, string RelativePath);

/// <summary>日付ごとの件数（<c>recording-days</c> の 1 行）。</summary>
/// <param name="Date"><c>yyyy-MM-dd</c>。</param>
/// <param name="Count">その日に始まった録画の件数。</param>
public sealed record RecordingDayCount(string Date, int Count);

/// <summary>
/// 保存先の録画をメモリに持つ索引。
///
/// <para>
/// <b>要求ごとにフォルダーを走査しない。</b> 一覧は <see cref="FileSystemWatcher"/> の通知で
/// 作り直し、要求は最後に完成した不変リスト（<see cref="Snapshot"/>）を読むだけにする
/// ── 走査は 1 件ごとにファイルを開く（録画中の判定・fragmented 判定・尺の読み取り）ので、
/// 件数が増えるほど要求のたびに重くなる。
/// </para>
/// <para>
/// <b>作り直しは全再構築で、読んだ結果は <c>(パス, 長さ, 更新時刻)</c> で覚えておく。</b>
/// 差分更新にすると通知の取りこぼし（<see cref="FileSystemWatcher"/> のバッファ溢れ）が
/// そのまま恒久的なずれになる。全再構築なら次の通知で必ず追いつく。
/// <b>録画中のものは覚えず、作り直しのたびに開いて読み直す</b>
/// ── 書き込みで握られている間はディレクトリ エントリが更新されず、キーが固まるため。
/// </para>
/// <para>
/// <b>録画中の項目が一覧に在る間は、通知を待たずに <see cref="DebounceMilliseconds"/> ごとに
/// 作り直す。</b> 同じ理由（ディレクトリ エントリが更新されない）で、録画中の
/// <see cref="FileSystemWatcher"/> の通知は作成の 1 回きりしか来ない ── 通知だけに頼ると、
/// その 1 回で読んだ値（<c>mp4mux</c> が <c>moov</c> を書く前なら
/// <see cref="RecordingEntry.Fragmented"/> は <see langword="false"/>、長さは 0）が録画の
/// 終わりまで動かない。録画が終われば張り直しも止まる。
/// </para>
/// <para>
/// <b><see cref="Snapshot"/> と <see cref="Rebind"/> はどのスレッドからでも呼べる。</b>
/// <see cref="Changed"/> はスレッドプールのスレッドで、ロックを持たずに発火する。
/// </para>
/// </summary>
public sealed partial class RecordingIndex : IDisposable
{
    /// <summary>
    /// 通知を畳み込む間隔。<b>通知が続いても、最大この間隔で 1 回は作り直す。</b>
    /// 通知が止むまで待つ形（毎回タイマを張り直す形）にはしない ── 録画中の
    /// <c>filesink</c> は <c>buffer-mode=unbuffered</c> で毎バッファ書くので、
    /// 数十 ms ごとに通知が来続けて一覧が永久に更新されない。
    /// </summary>
    public const int DebounceMilliseconds = 500;

    /// <summary><see cref="FileSystemWatcher"/> が溢れた（<c>Error</c>）ときに作り直すまでの間。</summary>
    public const int WatcherRetryMilliseconds = 30_000;

    /// <summary>
    /// sidecar が無いときにファイル名から開始時刻とレコーダー名を読む形。
    /// <c>{Time:yyyyMMdd_HHmmss}_{Name}</c>（常時録画は <c>_c00001</c> の連番が付く）。
    /// </summary>
    [GeneratedRegex(@"^(\d{8})_(\d{6})_(.+?)(_c\d+)?\.mp4$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilenamePattern();

    private readonly object _gate = new();
    private readonly Timer _debounce;
    private readonly Timer _watcherRetry;

    /// <summary>
    /// 走査を始めた回数。<b>走査をロックの外で行うための世代番号。</b>
    /// 読み書きはすべて <see cref="_gate"/> の下。
    /// </summary>
    private long _generation;

    /// <summary>
    /// 今の <see cref="_snapshot"/> を作った走査の世代。<b>「後から始まった」ではなく
    /// 「後から完了した」走査が在るときだけ捨てる</b> ── 前者で捨てると、通知が
    /// 続いていて走査時間が通知の間隔より長い間、一覧が一度も更新されない。
    /// </summary>
    private long _appliedGeneration;

    /// <summary>デバウンスのタイマを張ってある。張り直さないための印（<see cref="_gate"/> の下）。</summary>
    private bool _rebuildArmed;

    /// <summary>作り直しの実行中（<see cref="_gate"/> の下）。</summary>
    private bool _rebuilding;

    /// <summary>作り直しの実行中に通知が来た。終わったらもう 1 回だけ張る（<see cref="_gate"/> の下）。</summary>
    private bool _rebuildPending;

    /// <summary>読み終えたファイルの中身（<c>(パス, 長さ, 更新時刻)</c> をキーに覚えておく）。</summary>
    private Dictionary<string, FileFacts> _facts = new(StringComparer.Ordinal);

    private FileSystemWatcher? _watcher;
    private IReadOnlyList<RecordingEntry> _snapshot = [];
    private bool _disposed;

    /// <summary>
    /// <see cref="Rebind"/> の走査に入る直前に呼ぶ。<b>L1 が「構築の最中」という窓を
    /// 作るための口</b>（<see cref="Rebuild"/> と同じ用途で、製品の経路では誰も入れない）。
    /// </summary>
    internal Action? BuildStarting;

    /// <summary>
    /// 今見ているフォルダー。<b>ロックの外から読む</b> ── <see cref="Rebind"/> は要求のたびに
    /// 呼ばれるので、作り直しの最中でも「同じ root なら何もしない」を待たずに返せること。
    /// </summary>
    private volatile string _root = string.Empty;

    /// <summary>
    /// 走査が 1 度でも完成した（<see cref="_snapshot"/> が「作った結果」である）。
    /// <b>速い経路の条件。ロックの外から読む。</b>
    /// </summary>
    private volatile bool _built;

    /// <summary>
    /// <paramref name="root"/> を見る索引を作る。構築時の走査は<b>同期で</b>行う
    /// ── 直後の要求が空の一覧を受け取らないため。
    /// </summary>
    public RecordingIndex(string root)
    {
        _debounce = new Timer(static state => ((RecordingIndex)state!).OnDebounceElapsed(), this, Timeout.Infinite, Timeout.Infinite);
        _watcherRetry = new Timer(static state => ((RecordingIndex)state!).OnWatcherRetryElapsed(), this, Timeout.Infinite, Timeout.Infinite);

        Rebind(root);
    }

    /// <summary>今見ているフォルダー。</summary>
    public string Root => _root;

    /// <summary>
    /// 差分の通知。<b>スレッドプールのスレッドで発火する</b>ので、購読側は自分で直列化すること。
    /// 作り直しが 1 回でまとまるので、1 つのファイルの複数の変化が 1 件に畳まれうる。
    /// </summary>
    public event Action<RecordingIndexChange>? Changed;

    /// <summary>
    /// 最後に完成した一覧。並びは <see cref="RecordingEntry.StartTimeUtc"/> の降順、
    /// 同時刻は相対パスの序数昇順。<b>不変</b>なので、呼び出し側はそのまま持ち回してよい。
    /// </summary>
    public IReadOnlyList<RecordingEntry> Snapshot() => Volatile.Read(ref _snapshot);

    /// <summary>
    /// 見るフォルダーを差し替える。同じなら何もしない。違えば watcher を作り直し、
    /// <b>同期で</b>走査し直す（差し替え直後の要求に古い root の一覧を返さないため）。
    /// このときの差分は通知しない ── 中身の変化ではなく、見る対象の変化だから。
    ///
    /// <para>
    /// <b>同じ root でも、watcher がまだ無くて root が現れていれば張り直す。</b>
    /// 保存先は初回の録画のときに作られるので、設定した直後は root が存在せず
    /// watcher を張れない ── ここで拾い直さないと、以後の変化が永久に届かない。
    /// </para>
    /// </summary>
    public void Rebind(string root)
    {
        string normalized = Normalize(root);

        // 速い経路。要求のたびに呼ばれるので、作り直しのロックを待たせない。
        // watcher が張れているならフォルダーの存在は確かめない（要求ごとの I/O を避ける）。
        //
        // **最初の走査が完成するまでは通らない（_built）。** _root と watcher は走査より先に
        // 決まるので、これが無いと、初回の走査を抱えたロックの下で進んでいる間に来た要求が
        // 空の Snapshot を受け取る。ここでロックを待てば、待った側は完成した一覧を読む。
        // 走査が一度も完成しない root（存在しない・権限が無い）では要求ごとにロックを取るが、
        // その間は誰も長く保持しないので待たされない。
        if (_built
            && string.Equals(_root, normalized, StringComparison.OrdinalIgnoreCase)
            && (Volatile.Read(ref _watcher) is not null || !Directory.Exists(normalized)))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
                return;

            bool sameRoot = string.Equals(_root, normalized, StringComparison.OrdinalIgnoreCase);
            if (sameRoot && _watcher is not null)
                return;

            _root = normalized;

            // **走査より先に見張り始める。** 逆にすると、走査してから見張るまでの間に
            // 作られたファイルを次の通知まで取りこぼす。
            CreateWatcher(normalized);

            // 同じ root で見張れないまま（存在しない・権限が無い）なら走査もしない
            // ── ここで走査すると、要求のたびに全走査することになる。
            if (sameRoot && _watcher is null)
                return;

            BuildStarting?.Invoke();

            _facts = new Dictionary<string, FileFacts>(StringComparer.Ordinal);
            _snapshot = Build(normalized, _facts);

            // 同期で作ったので、この世代が「適用済み」になる。走査中だった古い root の
            // 結果は root の照合で、同じ root の古い結果は世代の比較で落ちる。
            _appliedGeneration = ++_generation;

            // 以後は速い経路を通してよい。**立てるのはここだけ** ── 走査せずに返る経路
            // （見張れない root）で立てると、初回の走査の最中がまた素通りになる。
            _built = true;

            // **ここで作った一覧に録画中の項目が在れば、自分で作り直しを張る。**
            // 索引が録画の最中に初めて構築されると、作成の通知は既に過ぎていて
            // 書き込みで握られている間はディレクトリ エントリも更新されない
            // ── 自発的な張り直しが無いと fragmented・長さ・録画中の別が
            // 録画の終わりまで固まる。以後の張り直しは OnDebounceElapsed が継ぐ。
            // ロックは再入できるので、ここから呼んでよい。
            if (HasInProgress(_snapshot))
                ScheduleRebuild();
        }
    }

    /// <summary>
    /// 走査し直して差分を通知する。<b>L1 が watcher を待たずに検査するための口</b>でもある。
    /// </summary>
    internal void Rebuild()
    {
        string root;
        Dictionary<string, FileFacts> carried;
        long generation;

        lock (_gate)
        {
            if (_disposed)
                return;

            root = _root;
            carried = _facts;
            generation = ++_generation;
        }

        // **走査はロックの外で行う。** sidecar の無い録画は 1 件ごとに開くので、
        // 古い録画が溜まると秒単位になる ── その間ロックを持つと watcher の
        // コールバック（ScheduleRebuild）が塞がれ、通知バッファが溢れて
        // WatcherRetryMilliseconds ぶん止まる。
        // carried は公開したあと書き換えないので、外から読んでよい。
        var facts = new Dictionary<string, FileFacts>(StringComparer.Ordinal);
        IReadOnlyList<RecordingEntry> current = Build(root, facts, carried);

        IReadOnlyList<RecordingIndexChange> changes;

        lock (_gate)
        {
            if (_disposed)
                return;

            // 見ているフォルダーが差し替わっていれば、この結果は別の木のもの。読んだ中身も使えない。
            if (!string.Equals(root, _root, StringComparison.OrdinalIgnoreCase))
                return;

            // 自分より後に**完了した**走査が在れば、こちらの結果は古い。
            // 読んだ中身だけは引き継ぐ ── キーが (パス, 長さ, 更新時刻, sidecar の更新時刻)
            // なので、同じ root なら混ぜても食い違わない。
            if (generation <= _appliedGeneration)
            {
                var merged = new Dictionary<string, FileFacts>(_facts.Count + facts.Count, StringComparer.Ordinal);
                foreach (var pair in _facts)
                    merged[pair.Key] = pair.Value;
                foreach (var pair in facts)
                    merged[pair.Key] = pair.Value;

                // 公開済みの辞書は書き換えない（走査中の Build が carried として読んでいる）。
                _facts = merged;
                return;
            }

            changes = Diff(_snapshot, current);
            _facts = facts;
            _snapshot = current;
            _appliedGeneration = generation;
        }

        var changed = Changed;
        if (changed is null)
            return;

        foreach (var change in changes)
            changed(change);
    }

    /// <summary>
    /// 前後の一覧を突き合わせて差分を作る。どちらも相対パスで整列している必要は無い。
    ///
    /// <para>
    /// <b>旧・新ともに録画中の項目は、長さと更新時刻の変化だけでは
    /// <see cref="RecordingIndexChangeKind.Updated"/> を出さない。</b> 録画中は作り直しの
    /// たびに開いて読み直すので長さが毎回伸び、そのままだと録画のあいだじゅう
    /// <see cref="DebounceMilliseconds"/> 間隔で通知が出続ける（購読側は合図として一覧を
    /// 引き直すので、そのぶん無駄な要求になる）。<see cref="Snapshot"/> の
    /// <see cref="RecordingEntry.Length"/> は最新のままなので、一覧を引けば伸びた長さは見える。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<RecordingIndexChange> Diff(
        IReadOnlyList<RecordingEntry> previous, IReadOnlyList<RecordingEntry> current)
    {
        var before = new Dictionary<string, RecordingEntry>(previous.Count, StringComparer.Ordinal);
        foreach (var entry in previous)
            before[entry.RelativePath] = entry;

        var changes = new List<RecordingIndexChange>();

        foreach (var entry in current)
        {
            if (!before.Remove(entry.RelativePath, out RecordingEntry? old))
            {
                changes.Add(new RecordingIndexChange(RecordingIndexChangeKind.Added, entry.RelativePath));
                continue;
            }

            if (old.InProgress && !entry.InProgress)
            {
                changes.Add(new RecordingIndexChange(RecordingIndexChangeKind.Completed, entry.RelativePath));
            }
            else if (old.InProgress && entry.InProgress
                ? WithoutGrowth(old) != WithoutGrowth(entry)
                : old != entry)
            {
                changes.Add(new RecordingIndexChange(RecordingIndexChangeKind.Updated, entry.RelativePath));
            }
        }

        foreach (string path in before.Keys)
            changes.Add(new RecordingIndexChange(RecordingIndexChangeKind.Removed, path));

        return changes;
    }

    /// <summary>
    /// 「書き足されただけ」を無視して比べるための形。<b>比較の中だけで使う</b>
    /// ── <see cref="RecordingEntry"/> の形は変えず、<see cref="Snapshot"/> には
    /// 最新の長さ・更新時刻を載せたままにする。
    /// </summary>
    private static RecordingEntry WithoutGrowth(RecordingEntry entry)
        => entry with { Length = 0, LastWriteTimeUtc = default };

    public void Dispose()
    {
        FileSystemWatcher? watcher;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _debounce.Dispose();
        _watcherRetry.Dispose();
    }

    /// <summary>
    /// 1 回の走査。<paramref name="carried"/> があれば <c>(パス, 長さ, 更新時刻)</c> が
    /// 一致するものを読み直さず引き継ぐ。
    /// </summary>
    private static IReadOnlyList<RecordingEntry> Build(
        string root, Dictionary<string, FileFacts> facts, Dictionary<string, FileFacts>? carried = null)
    {
        var candidates = RecordingFiles.WalkFiles(root);

        // sidecar とサムネイルは走査 1 回ぶんの結果から引く（同じ木を 3 回歩かない）。
        var companions = new Dictionary<string, FileInfo>(candidates.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            string name = candidate.File.Name;
            if (name.EndsWith(RecordingSidecar.Extension, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(RecordingSidecar.ThumbnailExtension, StringComparison.OrdinalIgnoreCase))
            {
                companions[candidate.File.FullName] = candidate.File;
            }
        }

        var entries = new List<RecordingEntry>();

        foreach (var candidate in candidates)
        {
            var file = candidate.File;
            if (!string.Equals(file.Extension, RecordingCleanup.Extension, StringComparison.OrdinalIgnoreCase))
                continue;

            string sidecarPath = RecordingSidecar.PathFor(file.FullName);
            bool hasSidecar = companions.TryGetValue(sidecarPath, out FileInfo? sidecarFile);
            bool hasThumbnail = companions.ContainsKey(RecordingSidecar.ThumbnailPathFor(file.FullName));

            // sidecar が在る＝排出が終わっている。開いて確かめる必要は無い
            // （録画中の判定はファイルを 1 つずつ開くので、件数ぶんの費用になる）。
            bool inProgress = false;
            if (!hasSidecar && !RecordingFiles.TryProbeInProgress(file.FullName, out inProgress))
            {
                // 列挙してから開くまでの間に消えた・権限が無い。
                // RecordingFiles.Enumerate と同じく一覧から落とす
                // （載せると、次の走査で消えて幻の Added/Removed になる）。
                continue;
            }

            long length = file.Length;
            DateTime lastWriteTimeUtc = file.LastWriteTimeUtc;
            FileFacts? cached;

            if (inProgress)
            {
                // **録画中は覚えない（キーが一致しても持ち越さない）。** 書き込みで握られている
                // 間、ディレクトリ エントリ（FileInfo の長さ・更新時刻）は更新されないので
                // キーが固まる ── 覚えると、最初の走査で読んだ値（mp4mux が moov を書く前なら
                // fragmented=false・長さ 0）が録画の終わりまで反転しない。読み直す費用は
                // 録画中の本数（通常 1〜数件）× 作り直しごとに 1 回開くぶん。
                if (!TryReadFacts(file, null, inProgress: true, ref length, ref lastWriteTimeUtc, out cached))
                    continue;
            }
            else
            {
                // 確定済み（sidecar 在り、または閉じている）。長さ・更新時刻は
                // ディレクトリ エントリの値で正しいので、そのままキーにして読み直さない。
                string key = MakeKey(file.FullName, length, lastWriteTimeUtc, sidecarFile);
                if (carried is null || !carried.TryGetValue(key, out cached))
                {
                    // 落としたものは facts に入らないので、次の走査でも必ず読み直される。
                    if (!TryReadFacts(
                            file, hasSidecar ? sidecarPath : null, inProgress: false,
                            ref length, ref lastWriteTimeUtc, out cached))
                    {
                        continue;
                    }
                }

                facts[key] = cached;
            }

            entries.Add(new RecordingEntry(
                candidate.RelativePath,
                length,
                lastWriteTimeUtc,
                inProgress,
                cached.Fragmented,
                cached.StartTimeUtc,
                cached.Recorder,
                cached.Trigger,
                cached.DurationMs,
                cached.Width,
                cached.Height,
                hasThumbnail));
        }

        entries.Sort(static (a, b) =>
        {
            int byTime = b.StartTimeUtc.CompareTo(a.StartTimeUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.RelativePath, b.RelativePath);
        });

        return entries;
    }

    /// <summary>
    /// ファイルを読んで決まるぶん（sidecar・fragmented・尺・開始時刻）を作る。
    /// sidecar が無いときのフォールバックはファイル名 → <c>mvhd</c> の尺 → 更新時刻の順で、
    /// <b>録画中だけは最後を作成時刻にする</b>（更新時刻は伸びるたびに動くため）。
    /// 消えていた・権限が無いものは <see langword="false"/>（一覧から落とす）。
    ///
    /// <para>
    /// <paramref name="length"/> と <paramref name="lastWriteTimeUtc"/> は
    /// <b>録画中のときだけ</b>、開いたハンドルから読んだ値で置き換える。確定済みでも
    /// 置き換えると、キャッシュ経路（<see cref="FileInfo"/> の値）と読み直し経路で
    /// 同じファイルの値が揺れて、幻の <see cref="RecordingIndexChangeKind.Updated"/> が出る。
    /// </para>
    /// </summary>
    private static bool TryReadFacts(
        FileInfo file, string? sidecarPath, bool inProgress,
        ref long length, ref DateTime lastWriteTimeUtc, [NotNullWhen(true)] out FileFacts? facts)
    {
        facts = null;
        RecordingSidecar? sidecar = sidecarPath is null ? null : RecordingSidecar.TryRead(sidecarPath);

        bool fragmented = false;
        long? durationMs = sidecar?.DurationMs;

        // 録画中の開始時刻のフォールバック。開けなかった回は null のまま（更新時刻へ倒す）。
        DateTime? creationTimeUtc = null;

        if (!inProgress)
        {
            try
            {
                using var stream = new FileStream(
                    file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                fragmented = Fmp4Probe.IsFragmented(RecordingFiles.ReadHeader(stream));

                // fragmented の総尺は mvhd に無い（duration 0）。一覧では出さない。
                if (durationMs is null && !fragmented && Fmp4Probe.TryReadMovieDuration(stream, out long read))
                    durationMs = read;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                          or UnauthorizedAccessException)
            {
                // IOException より先に受けること（消えたものは IOException の派生）。
                // RecordingFiles.Enumerate と同じ除外規則。
                return false;
            }
            catch (IOException)
            {
                // 開けはするが読めないものは「fragmented ではない・尺は不明」に畳む。
            }
        }
        else
        {
            // 録画中。**1 回開くだけで fragmented と実サイズをまとめて取る**
            // ── ディレクトリ エントリと違い、ハンドル経由の長さ（GetFileSizeEx）は
            // 書き込み中でも今の値を返す。共有は TryProbeInProgress より緩くする
            // （filesink が握ったままのファイルを読むため）。
            try
            {
                using var stream = new FileStream(
                    file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                fragmented = Fmp4Probe.IsFragmented(RecordingFiles.ReadHeader(stream));
                length = stream.Length;
                lastWriteTimeUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
                creationTimeUtc = File.GetCreationTimeUtc(stream.SafeFileHandle);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                          or UnauthorizedAccessException)
            {
                // IOException より先に受けること（消えたものは IOException の派生）。
                return false;
            }
            catch (IOException)
            {
                // 開けはするが読めない・排他で握られている。「fragmented ではない」に畳み、
                // 長さと更新時刻はディレクトリ エントリの値のままにする。
            }
        }

        var match = FilenamePattern().Match(file.Name);
        string recorder = sidecar?.Recorder ?? (match.Success ? match.Groups[3].Value : string.Empty);

        DateTime startTimeUtc;
        if (sidecar is not null)
            startTimeUtc = sidecar.StartTime.UtcDateTime;
        else if (match.Success && TryParseFilenameTime(match, out DateTime fromName))
            startTimeUtc = fromName;
        else if (durationMs is long known)
            startTimeUtc = lastWriteTimeUtc.AddMilliseconds(-known);
        else if (creationTimeUtc is DateTime created)
            // 録画中はここに来る（尺は sidecar からしか来ず、その sidecar が無い経路）。
            // **更新時刻は使えない。** 開いたハンドルから読む値は書き込みのたびに動くので、
            // 作り直しごとに開始時刻がずれて Updated が流れ続ける。作成時刻は動かない。
            startTimeUtc = created;
        else
            startTimeUtc = lastWriteTimeUtc;

        facts = new FileFacts(
            fragmented, durationMs, startTimeUtc, recorder, sidecar?.Trigger, sidecar?.Width, sidecar?.Height);
        return true;
    }

    /// <summary>ファイル名の <c>yyyyMMdd_HHmmss</c> を<b>ローカル時刻として</b>読む（録画時に付ける形）。</summary>
    private static bool TryParseFilenameTime(Match match, out DateTime utc)
    {
        utc = default;

        if (!DateTime.TryParseExact(
                match.Groups[1].Value + match.Groups[2].Value, "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            return false;
        }

        utc = DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
        return true;
    }

    private static string MakeKey(string path, long length, DateTime lastWriteUtc, FileInfo? sidecar)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{path}|{length}|{lastWriteUtc.Ticks}|{sidecar?.LastWriteTimeUtc.Ticks ?? 0}");

    private static string Normalize(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return string.Empty;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>watcher を作り直す。<b><c>_gate</c> を保持したまま呼ぶこと。</b></summary>
    private void CreateWatcher(string root)
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        if (root.Length == 0 || !Directory.Exists(root))
            return;

        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Filters.Add("*" + RecordingCleanup.Extension);
            watcher.Filters.Add("*" + RecordingCleanup.Extension + RecordingSidecar.Extension);
            watcher.Filters.Add("*" + RecordingCleanup.Extension + RecordingSidecar.ThumbnailExtension);

            watcher.Created += OnWatcherEvent;
            watcher.Changed += OnWatcherEvent;
            watcher.Deleted += OnWatcherEvent;
            watcher.Renamed += OnWatcherEvent;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 見張れないだけで一覧は作れている。次の Rebind でもう一度試す。
            ActivityLog.Warn("recording-index.watch fail", $"dir='{root}' {ex.Message}");
        }
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e) => ScheduleRebuild();

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // 通知が溢れた（内部バッファ超過）。watcher は止まっているので作り直すしかない。
        ActivityLog.Warn("recording-index.watch error", e.GetException().Message);

        lock (_gate)
        {
            if (_disposed)
                return;

            try { _watcherRetry.Change(WatcherRetryMilliseconds, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// 通知 1 件を畳み込む。<b>張ってあるタイマは張り直さない</b>
    /// ── 張り直すと「通知が止むまで待つ」形になり、書き込みが続いている間
    /// （録画中）は一度も発火しない。作り直しの実行中に来たぶんは
    /// <see cref="_rebuildPending"/> に畳み、終わってからもう 1 回だけ張る。
    /// </summary>
    private void ScheduleRebuild()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            if (_rebuilding)
            {
                _rebuildPending = true;
                return;
            }

            if (_rebuildArmed)
                return;

            try
            {
                _debounce.Change(DebounceMilliseconds, Timeout.Infinite);
                _rebuildArmed = true;
            }
            catch (ObjectDisposedException) { }
        }
    }

    private void OnDebounceElapsed()
    {
        lock (_gate)
        {
            _rebuildArmed = false;

            if (_disposed)
                return;

            // 直接呼ぶ経路（watcher の張り直し）とタイマが重なりうる。重ねて走らせず、
            // 「来ていた」ことだけ残す ── 走査は重いので、並行させると余計に遅れる。
            if (_rebuilding)
            {
                _rebuildPending = true;
                return;
            }

            _rebuilding = true;
        }

        try
        {
            Rebuild();
        }
        catch (Exception ex)
        {
            // 作り直しの失敗でスレッドプールのスレッドを落とさない（プロセスごと落ちる）。
            ActivityLog.Warn("recording-index.rebuild fail", ex.Message);
        }
        finally
        {
            bool again;
            lock (_gate)
            {
                _rebuilding = false;

                // **録画中の項目が残っていれば、通知が来ていなくても張り直す。**
                // 書き込みで握られている間はディレクトリ エントリが更新されないので、
                // watcher の通知は録画 1 本につき作成の 1 回きりしか来ない ── その 1 回が
                // mp4mux の書く ftyp+moov より前に走ると、fragmented・長さ・録画中の別が
                // 録画の終わりまで固まる（Rebind は速い経路で返るので一覧を引き直しても
                // 直らない）。録画が終われば次の作り直しが InProgress=false を見て、
                // ここで張り直さなくなる。
                again = (_rebuildPending || HasInProgress(_snapshot)) && !_disposed;
                _rebuildPending = false;
            }

            if (again)
                ScheduleRebuild();
        }
    }

    /// <summary>録画中の項目が 1 件でも在る。</summary>
    private static bool HasInProgress(IReadOnlyList<RecordingEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.InProgress)
                return true;
        }

        return false;
    }

    private void OnWatcherRetryElapsed()
    {
        string root;
        lock (_gate)
        {
            if (_disposed)
                return;

            root = _root;
            CreateWatcher(root);
        }

        OnDebounceElapsed();
    }

    /// <summary>ファイルを開いて分かるぶん。<c>(パス, 長さ, 更新時刻)</c> ごとに覚えておく。</summary>
    private sealed record FileFacts(
        bool Fragmented, long? DurationMs, DateTime StartTimeUtc, string Recorder, string? Trigger,
        int? Width, int? Height);
}

/// <summary>
/// 索引の一覧に対する絞り込み・並び・集計。
///
/// <para>
/// <b>純関数で、ファイルを触らない。</b> API の問い合わせ規則をここに置くことで、
/// HTTP を立てずに L1 で固定できる。
/// </para>
/// </summary>
public static class RecordingQuery
{
    /// <summary><c>limit</c> の下限。</summary>
    public const int MinLimit = 1;

    /// <summary><c>limit</c> の上限。</summary>
    public const int MaxLimit = 1000;

    /// <summary>固定オフセットとして受け付ける形（<c>±hh:mm</c>）。</summary>
    private static readonly TimeSpan MaxUtcOffset = TimeSpan.FromHours(14);

    /// <summary>
    /// <paramref name="from"/>（含む）／<paramref name="to"/>（含まない）を
    /// <see cref="RecordingEntry.StartTimeUtc"/> に、<paramref name="recorder"/> を
    /// 完全一致で適用する。並びは入力のまま（索引が既に整列している）。
    /// </summary>
    public static IReadOnlyList<RecordingEntry> Filter(
        IReadOnlyList<RecordingEntry> entries, DateTimeOffset? from, DateTimeOffset? to, string? recorder)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (from is null && to is null && string.IsNullOrEmpty(recorder))
            return entries;

        var filtered = new List<RecordingEntry>();

        foreach (var entry in entries)
        {
            var start = new DateTimeOffset(DateTime.SpecifyKind(entry.StartTimeUtc, DateTimeKind.Utc));

            if (from is DateTimeOffset lower && start < lower)
                continue;
            if (to is DateTimeOffset upper && upper <= start)
                continue;
            if (!string.IsNullOrEmpty(recorder) && !string.Equals(entry.Recorder, recorder, StringComparison.Ordinal))
                continue;

            filtered.Add(entry);
        }

        return filtered;
    }

    /// <summary>
    /// <paramref name="offset"/> 件飛ばして <paramref name="limit"/> 件返す。
    /// <paramref name="limit"/> が <see langword="null"/> なら残り全部（従来の無指定と同じ）。
    /// 範囲の外は空になるだけで、例外にはしない。
    /// </summary>
    public static IReadOnlyList<RecordingEntry> Page(
        IReadOnlyList<RecordingEntry> entries, int? limit, int offset)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (offset < 0)
            offset = 0;

        if (entries.Count <= offset)
            return [];

        int take = limit is int value ? Math.Min(value, entries.Count - offset) : entries.Count - offset;
        if (take <= 0)
            return [];

        var page = new List<RecordingEntry>(take);
        for (int i = 0; i < take; i++)
            page.Add(entries[offset + i]);

        return page;
    }

    /// <summary>
    /// <paramref name="timeZone"/> のローカル日付ごとに数える。並びは日付の昇順。
    /// </summary>
    public static IReadOnlyList<RecordingDayCount> CountDays(
        IReadOnlyList<RecordingEntry> entries, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(timeZone);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var utc = DateTime.SpecifyKind(entry.StartTimeUtc, DateTimeKind.Utc);
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);
            string date = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            counts[date] = counts.TryGetValue(date, out int existing) ? existing + 1 : 1;
        }

        var days = new List<RecordingDayCount>(counts.Count);
        foreach (var pair in counts)
            days.Add(new RecordingDayCount(pair.Key, pair.Value));

        days.Sort(static (a, b) => string.CompareOrdinal(a.Date, b.Date));
        return days;
    }

    /// <summary>
    /// <c>tz</c> を解決する。空なら UTC、<c>±hh:mm</c> は固定オフセット、
    /// それ以外は <b>Windows のタイムゾーン ID</b>（<c>InvariantGlobalization=true</c> なので
    /// IANA の ID は解決できない）。解決できなければ <see langword="false"/>。
    /// </summary>
    public static bool TryResolveTimeZone(string? tz, [NotNullWhen(true)] out TimeZoneInfo? timeZone)
    {
        if (string.IsNullOrWhiteSpace(tz))
        {
            timeZone = TimeZoneInfo.Utc;
            return true;
        }

        timeZone = null;

        if (TryParseUtcOffset(tz, out TimeSpan offset))
        {
            if (offset == TimeSpan.Zero)
            {
                timeZone = TimeZoneInfo.Utc;
                return true;
            }

            try
            {
                timeZone = TimeZoneInfo.CreateCustomTimeZone(tz, offset, tz, tz);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(tz);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException
                                      or ArgumentException or SecurityException)
        {
            return false;
        }
    }

    /// <summary><c>±hh:mm</c> だけを受ける（<c>+9</c> や <c>09:00</c> は受けない）。</summary>
    private static bool TryParseUtcOffset(string tz, out TimeSpan offset)
    {
        offset = default;

        if (tz.Length != 6 || (tz[0] != '+' && tz[0] != '-') || tz[3] != ':')
            return false;

        if (!int.TryParse(tz.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(tz.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
            || 59 < minutes)
        {
            return false;
        }

        var parsed = new TimeSpan(hours, minutes, 0);
        if (MaxUtcOffset < parsed)
            return false;

        offset = tz[0] == '-' ? -parsed : parsed;
        return true;
    }
}
