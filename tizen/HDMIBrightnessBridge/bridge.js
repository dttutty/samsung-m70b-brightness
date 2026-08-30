(function () {
  'use strict';

  // Replace this address with the LAN IPv4 address of the Windows PC before
  // packaging the Tizen application.
  var PC_SOCKET = 'ws://192.168.1.100:8765/m70b';
  var MIN_BACKLIGHT = 0;
  var MAX_BACKLIGHT = 50;
  var socket = null;
  var reconnectTimer = null;
  var connectionWatchdog = null;
  var videoWindowTimer = null;
  var pendingValue = null;
  var pendingId = null;
  var applyTimer = null;
  var statusElement = document.getElementById('status');

  function setStatus(text) {
    statusElement.textContent = text;
    console.log('[HdmiBrightnessBridge] ' + text);
  }

  function errorText(error) {
    if (!error) return 'unknown error';
    return (error.name ? error.name + ': ' : '') + (error.message || String(error));
  }

  function send(payload) {
    if (socket && socket.readyState === WebSocket.OPEN) {
      socket.send(JSON.stringify(payload));
    }
  }

  function readBacklight() {
    return webapis.avinfo.getBacklight();
  }

  function sendState(id) {
    try {
      send({
        op: 'state',
        id: id || null,
        connected: true,
        value: readBacklight(),
        min: MIN_BACKLIGHT,
        max: MAX_BACKLIGHT
      });
    } catch (error) {
      send({ op: 'error', id: id || null, message: errorText(error) });
    }
  }

  function applyPendingBacklight() {
    applyTimer = null;
    if (pendingValue === null) return;

    var value = pendingValue;
    var id = pendingId;
    pendingValue = null;
    pendingId = null;

    try {
      webapis.avinfo.setBacklight(value);
      send({ op: 'ack', id: id || null, value: readBacklight() });
    } catch (error) {
      send({ op: 'error', id: id || null, message: errorText(error) });
    }
  }

  function queueBacklight(value, id) {
    if (typeof value !== 'number' || !isFinite(value)) {
      send({ op: 'error', id: id || null, message: 'value must be a finite number' });
      return;
    }

    pendingValue = Math.max(MIN_BACKLIGHT, Math.min(MAX_BACKLIGHT, Math.round(value)));
    pendingId = id || null;
    if (applyTimer === null) applyTimer = setTimeout(applyPendingBacklight, 80);
  }

  function handleMessage(event) {
    var message;
    try {
      message = JSON.parse(event.data);
    } catch (error) {
      send({ op: 'error', message: 'invalid JSON' });
      return;
    }

    if (message.op === 'get') {
      sendState(message.id);
    } else if (message.op === 'set') {
      queueBacklight(message.value, message.id);
    } else if (message.op === 'ping') {
      send({ op: 'pong', id: message.id || null });
    } else if (message.op === 'exit') {
      send({ op: 'ack', id: message.id || null, value: readBacklight() });
      setTimeout(closeApp, 100);
    } else {
      send({ op: 'error', id: message.id || null, message: 'unsupported operation' });
    }
  }

  function connectToPc() {
    clearTimeout(reconnectTimer);
    if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) {
      return;
    }
    try {
      socket = new WebSocket(PC_SOCKET);
      socket.onopen = function () {
        send({ op: 'hello', role: 'tv', model: 'Samsung Tizen', protocol: 1 });
        sendState();
      };
      socket.onmessage = handleMessage;
      socket.onerror = function () {
        try { socket.close(); } catch (ignore) {}
      };
      socket.onclose = function () {
        socket = null;
        reconnectTimer = setTimeout(connectToPc, 2000);
      };
    } catch (error) {
      reconnectTimer = setTimeout(connectToPc, 2000);
    }
  }

  function showWindow() {
    clearTimeout(videoWindowTimer);
    videoWindowTimer = setTimeout(function () {
      setStatus('M70B 没有响应 TVWindow.show；请按返回键退出。');
      document.getElementById('close').focus();
    }, 8000);
    tizen.tvwindow.show(
      function () {
        clearTimeout(videoWindowTimer);
        setStatus('HDMI 画面已显示；等待电脑连接。');
        connectToPc();
        connectionWatchdog = setInterval(connectToPc, 2000);
      },
      function (error) {
        clearTimeout(videoWindowTimer);
        setStatus('无法显示 HDMI：' + errorText(error));
        document.getElementById('close').focus();
      },
      ['0%', '0%', '100%', '100%'],
      'MAIN',
      'FRONT'
    );
  }

  function start() {
    if (!window.tizen || !tizen.tvwindow) {
      setStatus('此设备不支持 TVWindow API。');
      return;
    }
    if (!window.webapis || !webapis.avinfo || typeof webapis.avinfo.setBacklight !== 'function') {
      setStatus('此设备没有可用的 AVInfo 背光接口。');
      return;
    }
    try {
      if (!tizen.systeminfo.getCapability('http://tizen.org/feature/tv.pip')) {
        setStatus('M70B 报告不支持 TVWindow/PiP。');
        document.getElementById('close').focus();
        return;
      }
    } catch (ignore) {}

    // Preserve the source already selected by the user. Calling setSource() on
    // the M70B can stall without invoking either callback.
    showWindow();
  }

  function closeApp() {
    clearTimeout(reconnectTimer);
    clearTimeout(videoWindowTimer);
    clearInterval(connectionWatchdog);
    if (applyTimer !== null) clearTimeout(applyTimer);
    if (socket) {
      try { socket.close(); } catch (ignore) {}
    }
    try { tizen.application.getCurrentApplication().exit(); }
    catch (error) { window.close(); }
  }

  document.getElementById('close').addEventListener('click', closeApp);
  document.addEventListener('keydown', function (event) {
    if (event.keyCode === 10009 || event.key === 'Escape') closeApp();
  });

  start();
}());
