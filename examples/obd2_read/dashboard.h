#pragma once

constexpr char kDashboard[] PROGMEM = R"HTML(<!doctype html>
<html lang="zh-CN">
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>T-2CAN | OBD-II</title>
<style>
  :root{color-scheme:dark;background:#141918;color:#edf1ef;font:16px system-ui,sans-serif}
  body{margin:0}main{box-sizing:border-box;max-width:50rem;margin:auto;padding:1.5rem 1rem}
  header{display:flex;align-items:center;justify-content:space-between;gap:1rem;flex-wrap:wrap;border-top:3px solid #8ad8ad;padding-block:1rem}
  h1{margin:0;font-size:1.35rem}#status{margin:0;color:#b7c9bf}
  #status[data-state="ok"]{color:#8ad8ad}
  #status[data-state="partial"],#status[data-state="no_data"],#status[data-state="offline"]{color:#ffcb87}
  .bus{display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:.5rem;margin:0;padding:.65rem 0;border-top:1px solid #405048;color:#b7c9bf}
  dl{margin:0;display:grid;grid-template-columns:repeat(3,minmax(0,1fr));border-bottom:1px solid #405048}
  .metric{min-width:0;border-top:1px solid #405048;padding:.8rem .75rem;--line:#8ad8ad}
  .metric:nth-child(2n){--line:#ffcb87}.metric:nth-child(3n){--line:#99c8ff}
  dt{color:#b7c9bf}dd{margin:.25rem 0 0;font-variant-numeric:tabular-nums}
  .trend{display:block;box-sizing:border-box;width:100%;height:32px;margin-top:.5rem;color:var(--line);border-bottom:1px solid #405048}
  .trend path{fill:none;stroke:currentColor;stroke-width:1.5;vector-effect:non-scaling-stroke}
  .trend circle{fill:currentColor}
  @media(max-width:620px){dl{grid-template-columns:repeat(2,minmax(0,1fr))}}
  @media(max-width:380px){.metric{padding-inline:.25rem}header{gap:.5rem}}
  strong{font-size:1.6rem}small{color:#b7c9bf}
  .actions{display:flex;align-items:center;gap:.75rem;flex-wrap:wrap;margin-top:1rem}
  #read{font:inherit;font-weight:600;min-height:2.75rem;min-width:7rem;padding:.45rem .85rem;border:0;border-radius:6px;color:#102419;background:#8ad8ad;cursor:pointer}
  #read:disabled{color:#b7c9bf;background:#293b33;cursor:default}
  #read-feedback{color:#ffcb87}
  footer{display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:.75rem;padding-top:1rem}
  #log-status{color:#b7c9bf}
  footer a{color:#8ad8ad;text-underline-offset:.2em;padding:.5rem 0}
</style>
<main>
  <header><h1>T-2CAN / OBD-II</h1><p id="status" role="status">&#36830;&#25509;&#20013;</p></header>
  <p class="bus"><span>CAN B</span><span id="bus-status" role="status">&#8212;</span></p>
  <dl>
    <div class="metric" data-metric="rpm" data-digits="2"><dt>&#21457;&#21160;&#26426;&#36716;&#36895;</dt><dd><strong id="rpm">&#8212;</strong> <small>rpm</small></dd></div>
    <div class="metric" data-metric="coolant"><dt>&#20919;&#21364;&#28082;&#28201;&#24230;</dt><dd><strong id="coolant">&#8212;</strong> <small>&deg;C</small></dd></div>
    <div class="metric" data-metric="speed"><dt>&#36710;&#36895;</dt><dd><strong id="speed">&#8212;</strong> <small>km/h</small></dd></div>
    <div class="metric" data-metric="load" data-digits="1"><dt>&#35745;&#31639;&#36127;&#33655;</dt><dd><strong id="load">&#8212;</strong> <small>%</small></dd></div>
    <div class="metric" data-metric="throttle" data-digits="1"><dt>&#33410;&#27668;&#38376;&#24320;&#24230;</dt><dd><strong id="throttle">&#8212;</strong> <small>%</small></dd></div>
    <div class="metric" data-metric="intake"><dt>&#36827;&#27668;&#28201;&#24230;</dt><dd><strong id="intake">&#8212;</strong> <small>&deg;C</small></dd></div>
  </dl>
  <div class="actions">
    <button id="read" type="button" disabled>&#35835;&#21462;&#19968;&#27425;</button>
    <span id="read-feedback" role="status"></span>
  </div>
  <footer>
    <span id="log-status" role="status"></span>
    <a id="download" href="/obd2-v2.csv" download="obd2-v2.csv" hidden><span aria-hidden="true">&#8595;</span> &#19979;&#36733; CSV</a>
    <a id="legacy" href="/obd2.csv" download="obd2.csv" hidden><span aria-hidden="true">&#8595;</span> &#26087;&#29256; CSV</a>
  </footer>
</main>
<script>
  const status = document.getElementById('status');
  const busStatus = document.getElementById('bus-status');
  const logStatus = document.getElementById('log-status');
  const download = document.getElementById('download');
  const legacy = document.getElementById('legacy');
  const readButton = document.getElementById('read');
  const readFeedback = document.getElementById('read-feedback');
  let reading = false;
  let cooldown = false;
  let generation = 0;
  let boot = '';
  let sample = 0;
  const maxSamples = 24;
  const ns = 'http://www.w3.org/2000/svg';
  const metrics = Array.from(document.querySelectorAll('[data-metric]'), node => {
    const chart = document.createElementNS(ns, 'svg');
    chart.setAttribute('class', 'trend');
    chart.setAttribute('aria-hidden', 'true');
    chart.setAttribute('viewBox', '0 0 120 32');
    chart.setAttribute('preserveAspectRatio', 'none');
    const path = document.createElementNS(ns, 'path');
    const point = document.createElementNS(ns, 'circle');
    point.setAttribute('r', '1.6');
    point.setAttribute('visibility', 'hidden');
    chart.append(path, point);
    node.querySelector('dd').append(chart);
    return {key: node.dataset.metric, digits: Number(node.dataset.digits || 0),
            value: node.querySelector('strong'), path, point, history: []};
  });
  const labels = {
    idle: '\u7b49\u5f85\u8bfb\u53d6', ok: '\u5df2\u8bfb\u53d6',
    partial: '\u90e8\u5206\u8bfb\u53d6', no_data: '\u8bfb\u53d6\u65e0\u7ed3\u679c',
    offline: '\u8fde\u63a5\u4e2d\u65ad'
  };
  const logLabels = {
    empty: '\u65e0\u4fdd\u5b58\u8bb0\u5f55', ready: '\u8bb0\u5f55\u5df2\u4fdd\u5b58',
    full: '\u8bb0\u5f55\u5df2\u6ee1', failed: '\u4fdd\u5b58\u5931\u8d25',
    unavailable: '\u5b58\u50a8\u4e0d\u53ef\u7528'
  };
  const missing = '\u2014';
  function updateReadButton() {
    const online = status.dataset.state && status.dataset.state !== 'offline';
    readButton.disabled = reading || cooldown || !online;
    readButton.textContent = reading ? '\u8bfb\u53d6\u4e2d' : '\u8bfb\u53d6\u4e00\u6b21';
    const mode = reading ? '\u67e5\u8be2\u8bf7\u6c42\u4e2d' :
                 online ? '\u5f85\u673a / \u672a\u76d1\u542c' : '\u72b6\u6001\u672a\u77e5';
    if (busStatus.textContent !== mode) busStatus.textContent = mode;
  }
  function plot(metric) {
    const series = metric.history;
    const values = series.filter(Number.isFinite);
    const min = Math.min(...values);
    const max = Math.max(...values);
    let path = '';
    let previous = false;
    metric.point.setAttribute('visibility', 'hidden');
    series.forEach((value, i) => {
      if (!Number.isFinite(value)) {
        previous = false;
        return;
      }
      const x = series.length === 1 ? 118 : 2 + 116 * i / (series.length - 1);
      const y = min === max ? 16 : 29 - 26 * (value - min) / (max - min);
      path += (previous ? 'L' : 'M') + x.toFixed(2) + ' ' + y.toFixed(2);
      previous = true;
      if (i === series.length - 1) {
        metric.point.setAttribute('cx', x);
        metric.point.setAttribute('cy', y);
        metric.point.setAttribute('visibility', 'visible');
      }
    });
    metric.path.setAttribute('d', path);
  }
  function recordTrend(data) {
    if (!data.boot) return;
    if (data.boot !== boot) {
      boot = data.boot;
      sample = 0;
      for (const metric of metrics) {
        metric.history.length = 0;
        plot(metric);
      }
    }
    // Polling can return the same snapshot many times.
    if (!Number.isInteger(data.sample) || data.sample <= sample) return;
    sample = data.sample;
    for (const metric of metrics) {
      metric.history.push(Number.isFinite(data[metric.key]) ? data[metric.key] : null);
      if (metric.history.length > maxSamples) metric.history.shift();
      plot(metric);
    }
  }
  function show(data) {
    status.dataset.state = data.state;
    const label = labels[data.state] || labels.offline;
    if (status.textContent !== label) status.textContent = label;
    for (const metric of metrics) {
      const value = data[metric.key];
      metric.value.textContent = Number.isFinite(value) ? value.toFixed(metric.digits) : missing;
    }
    recordTrend(data);
    const logLabel = logLabels[data.log] || '';
    if (logStatus.textContent !== logLabel) logStatus.textContent = logLabel;
    download.hidden = data.has_log !== true;
    legacy.hidden = data.has_legacy !== true;
    updateReadButton();
  }
  async function refresh() {
    if (reading) {
      setTimeout(refresh, 1000);
      return;
    }
    const before = generation;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 2500);
    try {
      const result = await fetch('/api/data', {cache: 'no-store', signal: controller.signal});
      if (!result.ok) throw new Error(result.status);
      const data = await result.json();
      if (before === generation) show(data);
    } catch (_) {
      if (before === generation) show({state: 'offline'});
    } finally {
      clearTimeout(timeout);
      setTimeout(refresh, 1000);
    }
  }
  async function readOnce() {
    if (readButton.disabled) return;
    ++generation;
    reading = true;
    readFeedback.textContent = '';
    updateReadButton();
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 5000);
    try {
      const result = await fetch('/api/read', {
        method: 'POST', headers: {'X-OBD-Read': '1'}, cache: 'no-store', signal: controller.signal
      });
      if (result.status === 429) {
        readFeedback.textContent = '\u8bfb\u53d6\u95f4\u9694\u4e0d\u8db3';
      } else if (result.ok) {
        show(await result.json());
      } else {
        throw new Error(result.status);
      }
    } catch (_) {
      readFeedback.textContent = '\u672a\u6536\u5230\u8bfb\u53d6\u7ed3\u679c';
    } finally {
      clearTimeout(timeout);
      reading = false;
      cooldown = true;
      updateReadButton();
      setTimeout(() => {
        cooldown = false;
        readFeedback.textContent = '';
        updateReadButton();
      }, 2000);
    }
  }
  readButton.addEventListener('click', readOnce);
  refresh();
</script>
</html>)HTML";
