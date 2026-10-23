$(function () {
  'use strict';

  var POLL_INTERVAL = 2000;     // short polling: ask every 2 seconds
  var RECONNECT_DELAY = 3000;   // ndjson / websocket: wait before reconnecting
  var enc = new TextEncoder();

  var HINTS = {
    polling: 'The page asks "anything new?" every 2 seconds. Most answers are empty.',
    longpolling: 'The page asks once and the server holds the request open until there is news (max 25s).',
    sse: 'One long-lived GET. The server pushes events; the browser reconnects and resumes by itself.',
    ndjson: 'One long-lived GET. The server writes one JSON object per line; the page reads the body as it arrives.',
    websocket: 'One upgraded two-way connection. The server pushes, and this page also sends messages over it.'
  };

  var lastId = 0;    // highest message id shown (used to resume and to skip duplicates)
  var stats = null;  // client-side numbers for the current session
  var current = null; // the running transport: { stop(), send?(text) }

  function byteLen(s) { return enc.encode(s).length; }
  function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }
  function setState(s) { if (stats) stats.state = s; }

  function newStats() {
    return { state: 'Connecting...', requests: 0, reconnects: 0, messages: 0, bytes: 0,
             latencyLast: null, latencySum: 0, latencyN: 0, connectStart: Date.now() };
  }

  // Called by every transport for each message. Skips ones already shown.
  function receive(m) {
    if (m.id <= lastId) return;
    lastId = m.id;
    stats.messages++;

    var created = Date.parse(m.createdAt);
    if (created >= stats.connectStart) { // live message, not replayed history
      var delay = Math.max(0, Date.now() - created);
      stats.latencyLast = delay;
      stats.latencySum += delay;
      stats.latencyN++;
    }

    var time = new Date(m.createdAt).toLocaleTimeString();
    // .text() escapes the content, so user input can't inject HTML
    $('<li>').append($('<small>').text(time), $('<span>').text(m.text)).prependTo('#messages');
  }

  // ---------------------------------------------------------------- transports
  // Each one connects, calls receive() for messages, updates stats, and returns { stop }.

  var transports = {

    polling: function () {
      var alive = true, timer = null, xhr = null;
      function tick() {
        stats.requests++;
        xhr = $.ajax({ url: '/messages', data: { after: lastId }, dataType: 'json' })
          .done(function (data, _s, x) {
            setState('Polling every ' + POLL_INTERVAL / 1000 + 's');
            stats.bytes += byteLen(x.responseText);
            data.forEach(receive);
          })
          .fail(function (_x, status) {
            if (status === 'abort') return;
            stats.reconnects++;
            setState('Error - retrying');
          })
          .always(function () { if (alive) timer = setTimeout(tick, POLL_INTERVAL); });
      }
      tick();
      return { stop: function () { alive = false; clearTimeout(timer); if (xhr) xhr.abort(); } };
    },

    longpolling: function () {
      var alive = true, timer = null, xhr = null;
      function tick() {
        stats.requests++;
        setState('Waiting (request held open)');
        xhr = $.ajax({ url: '/longpoll', data: { after: lastId }, dataType: 'json', timeout: 35000 })
          .done(function (data, _s, x) {
            stats.bytes += byteLen(x.responseText);
            data.forEach(receive);
            if (alive) tick(); // ask again straight away
          })
          .fail(function (_x, status) {
            if (status === 'abort') return;
            stats.reconnects++;
            setState('Error - retrying');
            if (alive) timer = setTimeout(tick, RECONNECT_DELAY);
          });
      }
      tick();
      return { stop: function () { alive = false; clearTimeout(timer); if (xhr) xhr.abort(); } };
    },

    sse: function () {
      // EventSource does reconnecting and Last-Event-ID resume for us.
      var es = new EventSource('/stream');
      stats.requests = 1;
      es.onopen = function () { setState('Connected (streaming)'); };
      es.onerror = function () {
        stats.reconnects++;
        stats.requests++; // the browser opens a new request to reconnect
        setState('Reconnecting...');
      };
      es.addEventListener('message', function (e) {
        stats.bytes += byteLen(e.data);
        receive(JSON.parse(e.data));
      });
      return { stop: function () { es.close(); } };
    },

    ndjson: function () {
      var alive = true, ctrl = new AbortController();

      // fetch gives us the body as a stream; we split it into lines ourselves.
      async function run() {
        while (alive) {
          try {
            stats.requests++;
            var res = await fetch('/stream/ndjson?after=' + lastId, { signal: ctrl.signal });
            if (!res.ok) throw new Error('HTTP ' + res.status);
            setState('Connected (streaming)');

            var reader = res.body.getReader();
            var decoder = new TextDecoder();
            var buffer = '';
            while (true) {
              var chunk = await reader.read();
              if (chunk.done) break;
              stats.bytes += chunk.value.byteLength;
              buffer += decoder.decode(chunk.value, { stream: true });
              var lines = buffer.split('\n');
              buffer = lines.pop(); // last piece may be an incomplete line
              lines.forEach(function (line) { if (line.trim()) receive(JSON.parse(line)); });
            }
          } catch (e) {
            if (!alive) return;
          }
          if (!alive) return;
          stats.reconnects++;
          setState('Reconnecting...');
          await sleep(RECONNECT_DELAY);
        }
      }
      run();
      return { stop: function () { alive = false; ctrl.abort(); } };
    },

    websocket: function () {
      var alive = true, ws = null, timer = null;
      var scheme = location.protocol === 'https:' ? 'wss' : 'ws';

      function connect() {
        if (!alive) return;
        stats.requests++; // the upgrade handshake is one HTTP request
        setState('Connecting...');
        ws = new WebSocket(scheme + '://' + location.host + '/ws?after=' + lastId);
        ws.onopen = function () { setState('Connected (two-way)'); };
        ws.onmessage = function (e) {
          stats.bytes += byteLen(e.data);
          receive(JSON.parse(e.data));
        };
        ws.onclose = function () {
          if (!alive) return;
          stats.reconnects++;
          setState('Reconnecting...');
          timer = setTimeout(connect, RECONNECT_DELAY);
        };
      }
      connect();

      return {
        stop: function () { alive = false; clearTimeout(timer); if (ws) ws.close(); },
        // sending goes up the same socket instead of a separate POST
        send: function (text) {
          if (!ws || ws.readyState !== WebSocket.OPEN) return false;
          ws.send(JSON.stringify({ text: text }));
          return true;
        }
      };
    }
  };

  // ---------------------------------------------------------------- switching
  function start(name) {
    if (current) current.stop();
    $('#messages').empty();
    lastId = 0;
    stats = newStats();
    current = transports[name]();
    $('#hint').text(HINTS[name]);
    $('#sendvia').text(current.send ? 'Messages are sent over the WebSocket.' : 'Messages are sent with POST /messages.');
    renderServerStats(lastServerStats);
  }

  $('#transport').on('change', function () { start(this.value); });

  // ---------------------------------------------------------------- sending
  $('#form').on('submit', function (e) {
    e.preventDefault();
    var $text = $('#text');
    var text = $.trim($text.val());
    if (!text) return;

    $('#error').text('');
    if (current && current.send && current.send(text)) {
      $text.val('').focus();
      return;
    }
    $.ajax({
      url: '/messages', method: 'POST', contentType: 'application/json',
      data: JSON.stringify({ text: text })
    }).done(function () {
      $text.val('').focus();
    }).fail(function (xhr) {
      $('#error').text('Failed to send (' + xhr.status + ')');
    });
  });

  // ---------------------------------------------------------------- reset
  // Deletes all messages and zeroes the server stats. This page then reconnects from scratch,
  // which also zeroes its own numbers. Other tabs notice through /stats (see pollServerStats).
  var resetVersion = null;

  $('#reset').on('click', function () {
    var $btn = $(this).prop('disabled', true);
    $('#error').text('');
    $.ajax({ url: '/reset', method: 'POST', dataType: 'json' })
      .done(function (r) {
        resetVersion = r.resetVersion;
        start($('#transport').val());
        pollServerStats();
      })
      .fail(function (xhr) { $('#error').text('Reset failed (' + xhr.status + ')'); })
      .always(function () { $btn.prop('disabled', false); });
  });

  // ---------------------------------------------------------------- stats panels
  function fmtMs(n) { return n === null ? '-' : Math.round(n) + ' ms'; }
  function fmtBytes(n) { return n < 1024 ? n + ' B' : (n / 1024).toFixed(1) + ' KB'; }

  function renderClientStats() {
    if (!stats) return;
    var live = /^(Connected|Polling|Waiting)/.test(stats.state);
    $('#s-state').text(stats.state).toggleClass('live', live);
    $('#s-up').text(Math.round((Date.now() - stats.connectStart) / 1000) + ' s');
    $('#s-req').text(stats.requests);
    $('#s-recon').text(stats.reconnects);
    $('#s-msgs').text(stats.messages);
    $('#s-bytes').text(fmtBytes(stats.bytes));
    $('#s-last').text(fmtMs(stats.latencyLast));
    $('#s-avg').text(stats.latencyN ? fmtMs(stats.latencySum / stats.latencyN) : '-');
  }

  var NAMES = {
    polling: 'Short polling', longpolling: 'Long polling', sse: 'SSE',
    ndjson: 'HTTP streaming', websocket: 'WebSocket'
  };
  var lastServerStats = [];

  function renderServerStats(rows) {
    var selected = $('#transport').val();
    var $body = $('#server-stats').empty();
    rows.forEach(function (r) {
      $('<tr>').toggleClass('current', r.transport === selected).append(
        $('<td>').text(NAMES[r.transport] || r.transport),
        $('<td class="num">').text(r.active),
        $('<td class="num">').text(r.totalConnections),
        $('<td class="num">').text(r.messagesSent)
      ).appendTo($body);
    });
  }

  function pollServerStats() {
    $.getJSON('/stats').done(function (r) {
      // Someone pressed Reset in another tab: start fresh here too.
      if (resetVersion !== null && r.resetVersion !== resetVersion) start($('#transport').val());
      resetVersion = r.resetVersion;

      lastServerStats = r.transports;
      renderServerStats(r.transports);
    });
  }

  setInterval(renderClientStats, 500);
  setInterval(pollServerStats, 2000);
  pollServerStats();

  start($('#transport').val());
});
