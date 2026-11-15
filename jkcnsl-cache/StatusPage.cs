namespace jkcnsl_cache;

internal static class StatusPage
{
    public const string Html = """
        <!DOCTYPE html>
        <html lang="ja">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>jkcnsl-cache</title>
        <style>
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body { background: #1a1a2e; color: #e0e0e0; font-family: monospace; padding: 16px; }
        h1 { font-size: 1.1rem; color: #7eb8f7; margin-bottom: 4px; }
        #uptime { font-size: 0.8rem; color: #888; margin-bottom: 12px; }
        .tabs { display: flex; gap: 6px; margin: 8px 0 12px; border-bottom: 1px solid #2a2a4a; }
        .tab-btn { appearance: none; border: 1px solid #2a2a4a; border-bottom: none; background: #111126;
                   color: #aaa; padding: 7px 12px; border-radius: 4px 4px 0 0; cursor: pointer; font: inherit; }
        .tab-btn.active { background: #16213e; color: #7eb8f7; }
        .tab-panel[hidden] { display: none; }
        table { width: 100%; border-collapse: collapse; font-size: 0.85rem; }
        th { background: #16213e; color: #7eb8f7; padding: 6px 10px; text-align: right; white-space: nowrap; }
        th:first-child, th:nth-child(2), th:nth-child(3), th:nth-child(4) { text-align: left; }
        td { padding: 5px 10px; border-bottom: 1px solid #2a2a4a; text-align: right; vertical-align: top; }
        td:first-child, td:nth-child(2), td:nth-child(3), td:nth-child(4) { text-align: left; }
        tr:hover td { background: #1e2a4a; }
        .on  { color: #4caf50; }
        .off { color: #555; }
        .force { color: #ffd54f; font-weight: bold; }
        .type-official   { color: #7eb8f7; }
        .type-unofficial { color: #ce93d8; }
        .type-refuge     { color: #80cbc4; }
        .type-local      { color: #a5d6a7; }
        .type-nichan     { color: #f48fb1; }
        .ntable { margin-bottom: 12px; }
        .ntable th, .ntable td { text-align: left; white-space: nowrap; }
        .ntable td.wrap { white-space: normal; }
        .ntable th.n, .ntable td.n { text-align: right; }
        .ntable a { color: #f48fb1; text-decoration: none; }
        .ntable a:hover { text-decoration: underline; }
        .thread { display: block; font-size: 0.78rem; line-height: 1.5; }
        .thread .meta { color: #888; }
        .thread.done { color: #666; }
        .err  { color: #ef5350; }
        .warn { color: #ffb74d; }
        .muted { color: #666; }
        #nichan-settings { font-size: 0.75rem; color: #888; margin-bottom: 10px; }
        .sub { font-size: 0.75rem; color: #888; display: block; margin-top: 3px; }
        .sub-on        { color: #81c784; }
        .sub-off       { color: #555; }
        .sub-scheduled { color: #ffb74d; }
        .sub-fallback  { color: #ffd54f; }
        h2 { font-size: 0.9rem; color: #7eb8f7; margin: 16px 0 4px; }
        #log { height: 220px; overflow-y: scroll; background: #0d0d1e;
               border: 1px solid #2a2a4a; padding: 6px 8px; font-size: 0.78rem; }
        #log div { white-space: pre-wrap; word-break: break-all; line-height: 1.4; }
        .li { color: #aaa; }
        .lw { color: #ffd54f; }
        .le { color: #ef5350; }
        .sub a { color: inherit; text-decoration: none; }
        .sub a:hover { text-decoration: underline; }
        a.nico-link { color: #81c784; text-decoration: none; }
        a.nico-link:hover { text-decoration: underline; }
        .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 8px; margin-bottom: 12px; }
        .card { background: #111126; border: 1px solid #2a2a4a; padding: 10px; border-radius: 4px; }
        .card-label { color: #888; font-size: 0.75rem; margin-bottom: 4px; }
        .card-value { color: #e8eefc; font-size: 1.2rem; font-weight: bold; }
        .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 12px; }
        .grid-vertical { display: grid; grid-template-columns: 1fr; gap: 12px; }
        .chart { background: #0d0d1e; border: 1px solid #2a2a4a; border-radius: 4px; padding: 8px; }
        .chart-title { color: #7eb8f7; font-size: 0.82rem; margin-bottom: 6px; }
        canvas { display: block; width: 100%; height: 180px; }
        #footer { font-size: 0.75rem; color: #555; margin-top: 8px; }
        </style>
        </head>
        <body>
        <h1>jkcnsl-cache</h1>
        <div id="uptime"></div>
        <div class="tabs">
          <button class="tab-btn active" data-tab="channels">チャンネル</button>
          <button class="tab-btn" data-tab="clients">クライアント</button>
          <button class="tab-btn" data-tab="system">システム</button>
          <button class="tab-btn" data-tab="nichan">5ch</button>
        </div>
        <section id="tab-channels" class="tab-panel">
        <table>
          <thead><tr>
            <th>ch</th><th>チャンネル</th><th>種別</th><th>接続先</th>
            <th>状態</th><th>勢い</th><th>視聴者</th><th>累計</th><th>最終No</th>
          </tr></thead>
          <tbody id="body"></tbody>
        </table>
        <div id="footer"></div>
        <h2>ログ <span style="font-size:.75rem;color:#555">直近200件 ● 自動スクロール</span></h2>
        <div id="log"></div>
        </section>
        <section id="tab-clients" class="tab-panel" hidden>
          <div class="cards">
            <div class="card"><div class="card-label">総接続</div><div class="card-value" id="m-total-connections">-</div></div>
            <div class="card"><div class="card-label">comment接続</div><div class="card-value" id="m-comment-connections">-</div></div>
            <div class="card"><div class="card-label">watch接続</div><div class="card-value" id="m-watch-connections">-</div></div>
            <div class="card"><div class="card-label">投稿/5秒</div><div class="card-value" id="m-posts">-</div></div>
            <div class="card"><div class="card-label">投稿累計</div><div class="card-value" id="m-posts-total">-</div></div>
            <div class="card"><div class="card-label">配信/5秒</div><div class="card-value" id="m-delivered">-</div></div>
            <div class="card"><div class="card-label">拒否/5秒</div><div class="card-value" id="m-rejects">-</div></div>
          </div>
          <div class="grid">
            <div class="chart"><div class="chart-title">接続数</div><canvas id="chart-connections" width="640" height="180"></canvas></div>
            <div class="chart"><div class="chart-title">投稿・配信</div><canvas id="chart-flow" width="640" height="180"></canvas></div>
            <div class="chart"><div class="chart-title">拒否・エラー</div><canvas id="chart-errors" width="640" height="180"></canvas></div>
          </div>
        </section>
        <section id="tab-system" class="tab-panel" hidden>
          <div class="cards">
            <div class="card"><div class="card-label">Process CPU</div><div class="card-value" id="m-cpu">-</div></div>
            <div class="card"><div class="card-label">Working Set</div><div class="card-value" id="m-working-set">-</div></div>
            <div class="card"><div class="card-label">Managed Memory</div><div class="card-value" id="m-managed-memory">-</div></div>
            <div class="card"><div class="card-label">GC / 5秒</div><div class="card-value" id="m-gc">-</div></div>
          </div>
          <div class="grid-vertical">
            <div class="chart"><div class="chart-title">CPU 使用率</div><canvas id="chart-cpu" width="960" height="180"></canvas></div>
            <div class="chart"><div class="chart-title">メモリ使用量</div><canvas id="chart-memory" width="960" height="180"></canvas></div>
            <div class="chart"><div class="chart-title">GC</div><canvas id="chart-gc" width="640" height="180"></canvas></div>
          </div>
          <h2>コメントストレージ</h2>
          <div class="cards">
            <div class="card"><div class="card-label">キュー深度</div><div class="card-value" id="m-store-queue">-</div></div>
            <div class="card"><div class="card-label">今回保存数</div><div class="card-value" id="m-store-inserted">-</div></div>
            <div class="card"><div class="card-label">DB サイズ</div><div class="card-value" id="m-store-size">-</div></div>
            <div class="card"><div class="card-label">最終書込</div><div class="card-value" id="m-store-last">-</div></div>
          </div>
        </section>
        <section id="tab-nichan" class="tab-panel" hidden>
          <div id="nichan-settings"></div>
          <div class="cards">
            <div class="card"><div class="card-label">取得中のソース</div><div class="card-value" id="n-active">-</div></div>
            <div class="card"><div class="card-label">リクエスト/5秒</div><div class="card-value" id="n-req">-</div></div>
            <div class="card"><div class="card-label">リクエスト累計</div><div class="card-value" id="n-req-total">-</div></div>
            <div class="card"><div class="card-label">304（更新なし）率</div><div class="card-value" id="n-304">-</div></div>
            <div class="card"><div class="card-label">エラー累計</div><div class="card-value" id="n-err">-</div></div>
            <div class="card"><div class="card-label">429/503 累計</div><div class="card-value" id="n-rate">-</div></div>
            <div class="card"><div class="card-label">停止中のホスト</div><div class="card-value" id="n-blocked">-</div></div>
            <div class="card"><div class="card-label">受信量累計</div><div class="card-value" id="n-bytes">-</div></div>
          </div>
          <div class="grid">
            <div class="chart"><div class="chart-title">リクエスト（5秒ごと）</div><canvas id="chart-nichan-req" width="640" height="180"></canvas></div>
            <div class="chart"><div class="chart-title">エラー・制限（5秒ごと）</div><canvas id="chart-nichan-err" width="640" height="180"></canvas></div>
          </div>
          <h2>ソース</h2>
          <table class="ntable">
            <thead><tr><th>ソース</th><th>状態</th><th class="n">受信者</th><th class="n">勢い</th><th class="n">配信累計</th><th class="n">配信待ち</th><th>追跡中のスレ</th></tr></thead>
            <tbody id="nichan-channels"></tbody>
          </table>
          <h2>ホスト</h2>
          <table class="ntable">
            <thead><tr><th>ホスト</th><th class="n">リクエスト</th><th class="n">304</th><th class="n">エラー</th><th class="n">429/503</th><th class="n">見送り</th><th class="n">受信量</th><th>最終応答</th><th>状態</th></tr></thead>
            <tbody id="nichan-hosts"></tbody>
          </table>
          <h2>板（subject.txt）</h2>
          <table class="ntable">
            <thead><tr><th>板</th><th class="n">スレ数</th><th>最終取得</th><th>移転先</th></tr></thead>
            <tbody id="nichan-boards"></tbody>
          </table>
        </section>
        <script>
        function fmtUptime(sec) {
          const d = Math.floor(sec / 86400);
          const h = Math.floor((sec % 86400) / 3600);
          const m = Math.floor((sec % 3600) / 60);
          const s = sec % 60;
          return (d > 0 ? d + 'd ' : '') + String(h).padStart(2,'0') + ':' +
                 String(m).padStart(2,'0') + ':' + String(s).padStart(2,'0');
        }
        function esc(s) {
          return String(s ?? '').replace(/[&<>"']/g, c => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
          }[c]));
        }
        document.querySelectorAll('.tab-btn').forEach(btn => {
          btn.addEventListener('click', () => {
            document.querySelectorAll('.tab-btn').forEach(b => b.classList.toggle('active', b === btn));
            document.querySelectorAll('.tab-panel').forEach(panel => {
              panel.hidden = panel.id !== 'tab-' + btn.dataset.tab;
            });
            if (btn.dataset.tab === 'nichan') refreshNichan();
            else if (btn.dataset.tab !== 'channels') refreshMetrics();
          });
        });
        function sourceTypeClass(t) {
          return t === 'official' ? 'type-official'
               : t === 'unofficial' ? 'type-unofficial'
               : t === 'refuge' ? 'type-refuge'
               : t === 'local' ? 'type-local'
               : t === 'nichan' ? 'type-nichan'
               : '';
        }
        function fmtTarget(t) {
          if (!t) return '';
          if (t.startsWith('wss://') || t.startsWith('ws://'))
            return esc(t.replace(/^wss?:\/\//, '').replace(/\/.*$/, ''));
          if (t.startsWith('https://'))
            return `<a class="nico-link" href="${esc(t)}" target="_blank" rel="noreferrer">${esc(t.replace(/^https:\/\//, '').replace(/\/$/, ''))}</a>`;
          if (t.match(/^(?:lv|ch)\d+$/))
            return `<a class="nico-link" href="https://live.nicovideo.jp/watch/${esc(t)}" target="_blank">${esc(t)}</a>`;
          return esc(t);
        }
        function sourceHtml(source) {
          const fallback = source.status === 'fallbackLocal';
          const cls = fallback ? 'sub-fallback' : source.running ? 'sub-on' : source.isReserved ? 'sub-scheduled' : 'sub-off';
          const dot = fallback ? '●' : source.running ? '●' : source.isReserved ? '◇' : '○';
          const label = `<span class="${sourceTypeClass(source.sourceType)}">${esc(source.label)}</span>`;
          let status = fallback ? ' ローカル待避中'
            : source.statusText ? ' ' + esc(source.statusText)
            : '';
          const target = source.currentTarget ? ' ' + fmtTarget(source.currentTarget) : '';
          if (source.isReserved) {
            let timeStr = '時刻不明';
            if (source.scheduledStartUtc) {
              const jst = new Date(new Date(source.scheduledStartUtc).getTime() + 9 * 3600 * 1000);
              const mm = String(jst.getUTCMonth() + 1).padStart(2, '0');
              const dd = String(jst.getUTCDate()).padStart(2, '0');
              const hh = String(jst.getUTCHours()).padStart(2, '0');
              const mn = String(jst.getUTCMinutes()).padStart(2, '0');
              timeStr = `${mm}/${dd} ${hh}:${mn}`;
            }
            if (fallback) status += ` ${timeStr}開始予定`;
            return `<span class="sub ${cls}">${dot} ${label}${status}${target}${fallback ? '' : ' ' + timeStr + '開始予定'}</span>`;
          }
          return `<span class="sub ${cls}">${dot} ${label}${status}${target}</span>`;
        }
        function sourcesHtml(sources) {
          const configured = Array.isArray(sources) ? sources.filter(s => s.configured) : [];
          return configured.length ? configured.map(sourceHtml).join('') : '<span class="sub sub-off">未設定</span>';
        }
        async function refresh() {
          try {
            const data = await (await fetch('/api/status')).json();
            document.getElementById('uptime').textContent = '稼働時間: ' + fmtUptime(data.uptimeSec);
            document.getElementById('body').innerHTML = data.channels.map(ch => {
              return `<tr>
                <td>${esc(ch.id)}</td>
                <td>${esc(ch.name)}</td>
                <td>${ch.bs ? 'BS' : '地上波'}</td>
                <td>${sourcesHtml(ch.sources)}</td>
                <td class="${ch.running ? 'on' : 'off'}">${ch.running ? '●' : '○'}</td>
                <td class="force">${esc(ch.force)}</td>
                <td>${esc(ch.viewers)}</td>
                <td>${ch.totalComments?.toLocaleString() ?? '-'}</td>
                <td>${ch.lastResNo > 0 ? ch.lastResNo.toLocaleString() : '-'}</td>
              </tr>`;
            }).join('');
            document.getElementById('footer').textContent = '更新: ' + new Date().toLocaleTimeString();
          } catch(e) {
            document.getElementById('footer').textContent = 'エラー: ' + e.message;
          }
        }
        refresh();
        setInterval(refresh, 3000);

        function num(v) {
          return Number(v ?? 0).toLocaleString();
        }
        function setText(id, text) {
          const el = document.getElementById(id);
          if (el) el.textContent = text;
        }
        function drawLine(canvas, series, defs, independentScale = false) {
          if (!canvas) return;
          const ctx = canvas.getContext('2d');
          const w = canvas.width, h = canvas.height;
          ctx.clearRect(0, 0, w, h);
          ctx.fillStyle = '#0d0d1e';
          ctx.fillRect(0, 0, w, h);
          ctx.strokeStyle = '#20203c';
          ctx.lineWidth = 1;
          for (let i = 1; i <= 3; i++) {
            const y = Math.round((h - 24) * i / 4) + 8;
            ctx.beginPath(); ctx.moveTo(32, y); ctx.lineTo(w - 8, y); ctx.stroke();
          }
          const values = [];
          defs.forEach(d => series.forEach(p => values.push(Number(p[d.key] ?? 0))));
          const max = Math.max(1, ...values);
          ctx.fillStyle = '#65718a';
          ctx.font = '11px monospace';
          ctx.fillText(independentScale ? 'auto' : String(Math.ceil(max)), 4, 16);
          defs.forEach(d => {
            const lineMax = independentScale
              ? Math.max(1, ...series.map(p => Number(p[d.key] ?? 0)))
              : max;
            ctx.strokeStyle = d.color;
            ctx.lineWidth = 2;
            ctx.beginPath();
            series.forEach((p, i) => {
              const x = 32 + (w - 44) * (series.length <= 1 ? 1 : i / (series.length - 1));
              const y = h - 14 - (Number(p[d.key] ?? 0) / lineMax) * (h - 28);
              if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
            });
            ctx.stroke();
          });
          let lx = 36;
          defs.forEach(d => {
            ctx.fillStyle = d.color;
            ctx.fillRect(lx, h - 10, 8, 3);
            ctx.fillStyle = '#9aa8c5';
            ctx.fillText(d.label, lx + 12, h - 6);
            lx += d.label.length * 8 + 32;
          });
        }
        function drawBars(canvas, series, defs) {
          if (!canvas) return;
          const ctx = canvas.getContext('2d');
          const w = canvas.width, h = canvas.height;
          ctx.clearRect(0, 0, w, h);
          ctx.fillStyle = '#0d0d1e';
          ctx.fillRect(0, 0, w, h);
          const values = [];
          defs.forEach(d => series.forEach(p => values.push(Number(p[d.key] ?? 0))));
          const max = Math.max(1, ...values);
          const plotX = 32, plotW = w - 44, plotH = h - 28;
          const groupW = plotW / Math.max(1, series.length);
          series.forEach((p, i) => {
            defs.forEach((d, j) => {
              const value = Number(p[d.key] ?? 0);
              const barW = Math.max(1, groupW / defs.length - 1);
              const x = plotX + i * groupW + j * barW;
              const bh = value / max * plotH;
              ctx.fillStyle = d.color;
              ctx.fillRect(x, h - 14 - bh, barW, bh);
            });
          });
          ctx.fillStyle = '#65718a';
          ctx.font = '11px monospace';
          ctx.fillText(String(Math.ceil(max)), 4, 16);
          let lx = 36;
          defs.forEach(d => {
            ctx.fillStyle = d.color;
            ctx.fillRect(lx, h - 10, 8, 3);
            ctx.fillStyle = '#9aa8c5';
            ctx.fillText(d.label, lx + 12, h - 6);
            lx += d.label.length * 8 + 32;
          });
        }
        async function refreshMetrics() {
          try {
            const data = await (await fetch('/api/admin/metrics')).json();
            const now = data.now ?? {};
            const totals = data.totals ?? {};
            const series = data.series ?? [];
            setText('m-total-connections', num(now.totalConnections));
            setText('m-comment-connections', num(now.commentConnections));
            setText('m-watch-connections', num(now.watchConnections));
            setText('m-posts', num(now.posts));
            setText('m-posts-total', num(totals.posts));
            setText('m-delivered', num(now.delivered));
            setText('m-rejects', num(now.rejects));
            setText('m-cpu', Number(now.processCpuPercent ?? 0).toFixed(1) + '%');
            setText('m-working-set', Number(now.workingSetMB ?? 0).toFixed(1) + ' MB');
            setText('m-managed-memory', Number(now.managedMemoryMB ?? 0).toFixed(1) + ' MB');
            setText('m-gc', num((now.gen0Collections ?? 0) + (now.gen1Collections ?? 0) + (now.gen2Collections ?? 0)));
            const st = data.storage ?? {};
            setText('m-store-queue', num(st.queueDepth ?? 0));
            setText('m-store-inserted', num(st.sessionInserted ?? 0));
            setText('m-store-size', st.dbSizeMB != null ? Number(st.dbSizeMB).toFixed(1) + ' MB' : '-');
            setText('m-store-last', st.lastInsertAt ? new Date(st.lastInsertAt).toLocaleTimeString() : '-');
            drawLine(document.getElementById('chart-connections'), series, [
              { key: 'commentConnections', label: 'comment', color: '#7eb8f7' },
              { key: 'watchConnections', label: 'watch', color: '#80cbc4' },
            ]);
            drawLine(document.getElementById('chart-flow'), series, [
              { key: 'posts', label: 'post', color: '#ffd54f' },
              { key: 'delivered', label: 'deliver', color: '#81c784' },
            ], true);
            drawBars(document.getElementById('chart-errors'), series, [
              { key: 'rejects', label: 'reject', color: '#ffb74d' },
              { key: 'postFailed', label: 'postFail', color: '#ef5350' },
              { key: 'queueDrops', label: 'drop', color: '#ce93d8' },
            ]);
            drawLine(document.getElementById('chart-cpu'), series, [
              { key: 'processCpuPercent', label: 'cpu%', color: '#7eb8f7' },
            ]);
            drawLine(document.getElementById('chart-memory'), series, [
              { key: 'workingSetMB', label: 'memMB', color: '#f093b7' },
              { key: 'managedMemoryMB', label: 'heapMB', color: '#84d39f' },
            ]);
            drawBars(document.getElementById('chart-gc'), series, [
              { key: 'gen0Collections', label: 'gen0', color: '#81c784' },
              { key: 'gen1Collections', label: 'gen1', color: '#ffb74d' },
              { key: 'gen2Collections', label: 'gen2', color: '#ef5350' },
            ]);
          } catch { }
        }
        refreshMetrics();
        setInterval(refreshMetrics, 5000);

        function fmtBytes(b) {
          b = Number(b ?? 0);
          if (b >= 1024 * 1024 * 1024) return (b / 1024 / 1024 / 1024).toFixed(2) + ' GB';
          if (b >= 1024 * 1024) return (b / 1024 / 1024).toFixed(1) + ' MB';
          if (b >= 1024) return (b / 1024).toFixed(1) + ' KB';
          return b + ' B';
        }
        function fmtAgo(iso) {
          if (!iso) return '-';
          const sec = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
          if (sec < 60) return sec + '秒前';
          if (sec < 3600) return Math.floor(sec / 60) + '分前';
          return Math.floor(sec / 3600) + '時間前';
        }
        function fmtTime(iso) {
          return iso ? new Date(iso).toLocaleTimeString() : '-';
        }
        const nichanStatusLabel = {
          running: ['取得中', 'on'], idle: ['停止', 'muted'], noThread: ['対応スレなし', 'warn'],
          blocked: ['アクセス停止中', 'err'], error: ['エラー', 'err'], disabled: ['無効', 'muted'],
        };
        function nichanThreadHtml(t) {
          const cls = t.finished || t.dropping ? 'thread done' : 'thread';
          const tag = (t.programMatch ? ' [番組一致]' : '') + (t.finished ? ' [終了]' : t.dropping ? ' [選択外]' : '');
          const err = t.error
            ? ` <span class="err">${esc(t.error)}${t.retryAt ? '（' + esc(fmtTime(t.retryAt)) + ' に再試行）' : ''}</span>`
            : '';
          return `<span class="${cls}"><a href="${esc(t.url)}" target="_blank" rel="noreferrer">${esc(t.title)}</a>` +
            ` <span class="meta">(${num(t.resCount)}) 取得 ${esc(fmtAgo(t.lastFetchAt))} / 新着 ${esc(fmtAgo(t.lastNewResAt))} / 配信対象 ${num(t.queued)}${esc(tag)}</span>${err}</span>`;
        }
        async function refreshNichan() {
          try {
            const data = await (await fetch('/api/admin/nichan')).json();
            const totals = data.totals ?? {};
            const series = (data.series ?? []).map(p => ({ time: p.time, ...(p.counters ?? {}) }));
            // 最後の枠は集計途中なので、1 つ前の枠を「直近 5 秒」とする
            const last = series.length > 1 ? series[series.length - 2] : (series[0] ?? {});
            const o = data.options ?? {};
            document.getElementById('nichan-settings').textContent = data.enabled
              ? `有効 / スレ一覧 ${o.subjectIntervalSec}秒ごと / dat ${o.datIntervalSec}秒ごと / 1ソース最大 ${o.maxThreadsPerChannel}スレ / 配信遅延 ${o.playbackDelaySec}秒 / 同一ホスト間隔 ${o.hostMinIntervalMs}ms / 保存 ${o.storeComments ? 'する' : 'しない'}`
              : '無効（CacheServer:Nichan:Enabled が false）';
            const channels = data.channels ?? [];
            const hosts = data.hosts ?? [];
            setText('n-active', `${channels.filter(c => c.running).length} / ${channels.length}`);
            setText('n-req', num(last.requests));
            setText('n-req-total', num(totals.requests));
            setText('n-304', totals.requests ? Math.round(totals.notModified / totals.requests * 100) + '%' : '-');
            setText('n-err', num(totals.errors));
            setText('n-rate', num(totals.rateLimited));
            setText('n-blocked', num(hosts.filter(h => h.blockedUntil).length));
            setText('n-bytes', fmtBytes(totals.bytes));
            drawLine(document.getElementById('chart-nichan-req'), series, [
              { key: 'requests', label: 'request', color: '#f48fb1' },
              { key: 'notModified', label: '304', color: '#80cbc4' },
            ]);
            drawBars(document.getElementById('chart-nichan-err'), series, [
              { key: 'errors', label: 'error', color: '#ef5350' },
              { key: 'rateLimited', label: '429/503', color: '#ffb74d' },
              { key: 'skippedBlocked', label: 'skip', color: '#ce93d8' },
            ]);
            document.getElementById('nichan-channels').innerHTML = channels.length ? channels.map(c => {
              const [label, cls] = nichanStatusLabel[c.status] ?? [c.status, ''];
              const threads = (c.threads ?? []).map(nichanThreadHtml).join('');
              const note = !threads && c.statusText ? `<span class="thread muted">${esc(c.statusText)}</span>` : '';
              const program = c.running
                ? `<span class="thread muted">番組: ${c.programTitle ? esc(c.programTitle) : '（番組表なし）'}${c.programKeys?.length ? ' ／ 照合: ' + esc(c.programKeys.join(', ')) : ''}</span>`
                : '';
              return `<tr>
                <td class="type-nichan">${esc(c.key)}</td>
                <td class="${cls}">${esc(label)}</td>
                <td class="n">${num(c.clients)}</td>
                <td class="n force">${num(c.force)}</td>
                <td class="n">${num(c.delivered)}</td>
                <td class="n">${num(c.pending)}</td>
                <td class="wrap">${program}${threads}${note}</td>
              </tr>`;
            }).join('') : '<tr><td colspan="7" class="muted">5ch ソースは設定されていません</td></tr>';
            document.getElementById('nichan-hosts').innerHTML = hosts.length ? hosts.map(h => {
              const c = h.counters ?? {};
              const state = h.blockedUntil
                ? `<span class="err">${esc(fmtTime(h.blockedUntil))} まで停止</span>`
                : '<span class="on">正常</span>';
              return `<tr>
                <td>${esc(h.host)}</td>
                <td class="n">${num(c.requests)}</td>
                <td class="n">${num(c.notModified)}</td>
                <td class="n${c.errors ? ' err' : ''}">${num(c.errors)}</td>
                <td class="n${c.rateLimited ? ' warn' : ''}">${num(c.rateLimited)}</td>
                <td class="n">${num(c.skippedBlocked)}</td>
                <td class="n">${fmtBytes(c.bytes)}</td>
                <td>${h.lastStatus ? esc(h.lastStatus) : '<span class="err">通信失敗</span>'} ${esc(fmtAgo(h.lastRequestAt))}</td>
                <td>${state}</td>
              </tr>`;
            }).join('') : '<tr><td colspan="9" class="muted">まだアクセスしていません</td></tr>';
            const boards = data.boards ?? [];
            document.getElementById('nichan-boards').innerHTML = boards.length ? boards.map(b => `<tr>
                <td><a href="${esc(b.board)}" target="_blank" rel="noreferrer">${esc(b.board)}</a></td>
                <td class="n">${num(b.threads)}</td>
                <td>${esc(fmtAgo(b.fetchedAt))}</td>
                <td>${b.movedTo ? esc(b.movedTo) : '<span class="muted">-</span>'}</td>
              </tr>`).join('') : '<tr><td colspan="4" class="muted">まだ取得していません</td></tr>';
          } catch { }
        }
        setInterval(() => {
          if (!document.getElementById('tab-nichan').hidden) refreshNichan();
        }, 5000);

        // ログ SSE
        const logEl = document.getElementById('log');
        let autoScroll = true;
        logEl.addEventListener('scroll', () => {
          autoScroll = logEl.scrollHeight - logEl.scrollTop - logEl.clientHeight < 16;
        });
        function appendLog(text) {
          const d = document.createElement('div');
          d.className = text.includes(' [WARN]') ? 'lw' : text.includes(' [ERROR]') ? 'le' : 'li';
          d.textContent = text;
          logEl.appendChild(d);
          while (logEl.childElementCount > 200) logEl.removeChild(logEl.firstChild);
          if (autoScroll) logEl.scrollTop = logEl.scrollHeight;
        }
        const es = new EventSource('/api/logs');
        es.onmessage = e => appendLog(JSON.parse(e.data));
        es.onerror = () => {};
        </script>
        </body>
        </html>
        """;
}
