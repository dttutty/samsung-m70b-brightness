(function () {
  'use strict';

  // Replace this example address with the Windows PC's LAN IPv4 address
  // before packaging. Never commit a personal LAN address.
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
  var capabilityCache = null;
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

  function sendError(id, code, message, setting) {
    var payload = { op: 'error', id: id || null, code: code, message: message };
    if (setting) payload.setting = setting;
    send(payload);
  }

  function readBacklight() {
    return webapis.avinfo.getBacklight();
  }

  function writeBacklight(value) {
    webapis.avinfo.setBacklight(value);
  }

  function audioAvailable() {
    return !!(window.tizen && tizen.tvaudiocontrol);
  }

  function settingDefinitions() {
    var definitions = [{
      id: 'backlight',
      type: 'integer',
      min: MIN_BACKLIGHT,
      max: MAX_BACKLIGHT,
      read: readBacklight,
      write: writeBacklight
    }];

    if (audioAvailable() && typeof tizen.tvaudiocontrol.getVolume === 'function' &&
        typeof tizen.tvaudiocontrol.setVolume === 'function') {
      definitions.push({
        id: 'volume',
        type: 'integer',
        min: 0,
        max: 100,
        read: function () { return tizen.tvaudiocontrol.getVolume(); },
        write: function (value) { tizen.tvaudiocontrol.setVolume(value); }
      });
    }

    if (audioAvailable() && typeof tizen.tvaudiocontrol.isMute === 'function' &&
        typeof tizen.tvaudiocontrol.setMute === 'function') {
      definitions.push({
        id: 'mute',
        type: 'boolean',
        read: function () { return tizen.tvaudiocontrol.isMute(); },
        write: function (value) { tizen.tvaudiocontrol.setMute(value); }
      });
    }
    return definitions;
  }

  function findDefinition(id) {
    var definitions = settingDefinitions();
    var index;
    for (index = 0; index < definitions.length; index += 1) {
      if (definitions[index].id === id) return definitions[index];
    }
    return null;
  }

  function validateValue(definition, value) {
    if (definition.type === 'integer') {
      if (typeof value !== 'number' || !isFinite(value) || Math.round(value) !== value) {
        throw new Error('value must be a finite integer');
      }
      if (value < definition.min || value > definition.max) {
        throw new Error('value is outside the supported range');
      }
      return value;
    }
    if (definition.type === 'boolean') {
      if (typeof value !== 'boolean') throw new Error('value must be boolean');
      return value;
    }
    throw new Error('unsupported setting type');
  }

  function capabilityFor(definition) {
    var capability = { id: definition.id, type: definition.type, readable: false, writable: false };
    var value;
    if (typeof definition.min === 'number') capability.min = definition.min;
    if (typeof definition.max === 'number') capability.max = definition.max;
    if (definition.type === 'integer') capability.step = 1;
    try {
      value = validateValue(definition, definition.read());
      capability.readable = true;
      capability.value = value;
    } catch (readError) {
      capability.error = errorText(readError);
      return capability;
    }
    capability.writable = typeof definition.write === 'function';
    return capability;
  }

  function getCapabilities() {
    var definitions;
    var capabilities;
    var index;
    if (capabilityCache !== null) return capabilityCache;
    definitions = settingDefinitions();
    capabilities = [];
    for (index = 0; index < definitions.length; index += 1) {
      capabilities.push(capabilityFor(definitions[index]));
    }
    capabilityCache = capabilities;
    return capabilityCache;
  }

  function sendCapabilities(id) {
    send({
      op: 'capabilities', id: id || null, protocol: 2, bridgeVersion: '2.0.3',
      settings: getCapabilities(), actions: []
    });
  }

  function sendSettingState(id, settingId) {
    var definition = findDefinition(settingId);
    if (!definition) {
      sendError(id, 'unknown_setting', 'unsupported setting', settingId);
      return;
    }
    try {
      send({ op: 'setting_state', id: id || null, setting: settingId, value: definition.read() });
    } catch (error) {
      sendError(id, 'read_failed', errorText(error), settingId);
    }
  }

  function sendSettingsState(id) {
    var definitions = settingDefinitions();
    var values = {};
    var index;
    for (index = 0; index < definitions.length; index += 1) {
      try { values[definitions[index].id] = definitions[index].read(); } catch (ignore) {}
    }
    send({ op: 'settings_state', id: id || null, values: values });
  }

  function applySetting(id, settingId, value) {
    var definition = findDefinition(settingId);
    if (!definition) {
      sendError(id, 'unknown_setting', 'unsupported setting', settingId);
      return;
    }
    try {
      value = validateValue(definition, value);
      definition.write(value);
      send({ op: 'setting_ack', id: id || null, setting: settingId, value: definition.read() });
    } catch (error) {
      sendError(id, 'write_failed', errorText(error), settingId);
    }
  }

  function sendState(id) {
    try {
      send({ op: 'state', id: id || null, connected: true, value: readBacklight(),
        min: MIN_BACKLIGHT, max: MAX_BACKLIGHT });
    } catch (error) {
      sendError(id, 'read_failed', errorText(error), 'backlight');
    }
  }

  function applyPendingBacklight() {
    var value;
    var id;
    applyTimer = null;
    if (pendingValue === null) return;
    value = pendingValue;
    id = pendingId;
    pendingValue = null;
    pendingId = null;
    try {
      writeBacklight(value);
      send({ op: 'ack', id: id || null, value: readBacklight() });
    } catch (error) {
      sendError(id, 'write_failed', errorText(error), 'backlight');
    }
  }

  function queueBacklight(value, id) {
    if (typeof value !== 'number' || !isFinite(value)) {
      sendError(id, 'invalid_value', 'value must be a finite number', 'backlight');
      return;
    }
    pendingValue = Math.max(MIN_BACKLIGHT, Math.min(MAX_BACKLIGHT, Math.round(value)));
    pendingId = id || null;
    if (applyTimer === null) applyTimer = setTimeout(applyPendingBacklight, 80);
  }

  function closeApp() {
    clearTimeout(reconnectTimer);
    clearTimeout(videoWindowTimer);
    clearInterval(connectionWatchdog);
    if (applyTimer !== null) clearTimeout(applyTimer);
    if (socket) try { socket.close(); } catch (ignore) {}
    try { tizen.application.getCurrentApplication().exit(); }
    catch (error) { window.close(); }
  }

  function handleMessage(event) {
    var message;
    try { message = JSON.parse(event.data); }
    catch (error) { sendError(null, 'invalid_json', 'invalid JSON'); return; }
    if (!message || typeof message !== 'object') {
      sendError(null, 'invalid_message', 'message must be an object');
    } else if (message.op === 'get') {
      sendState(message.id);
    } else if (message.op === 'set') {
      queueBacklight(message.value, message.id);
    } else if (message.op === 'capabilities') {
      sendCapabilities(message.id);
    } else if (message.op === 'get_setting') {
      sendSettingState(message.id, message.setting);
    } else if (message.op === 'get_settings') {
      sendSettingsState(message.id);
    } else if (message.op === 'set_setting') {
      applySetting(message.id, message.setting, message.value);
    } else if (message.op === 'ping') {
      send({ op: 'pong', id: message.id || null });
    } else if (message.op === 'exit') {
      send({ op: 'ack', id: message.id || null, value: readBacklight() });
      setTimeout(closeApp, 100);
    } else {
      sendError(message.id, 'unsupported_operation', 'unsupported operation');
    }
  }

  function connectToPc() {
    clearTimeout(reconnectTimer);
    if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) return;
    try {
      socket = new WebSocket(PC_SOCKET);
      socket.onopen = function () {
        send({ op: 'hello', role: 'tv', model: 'Samsung Tizen', protocol: 1,
          protocolMax: 2, features: ['settings-v2'] });
        sendState();
      };
      socket.onmessage = handleMessage;
      socket.onerror = function () { try { socket.close(); } catch (ignore) {} };
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
      ['0%', '0%', '100%', '100%'], 'MAIN', 'FRONT'
    );
  }

  function start() {
    if (!window.tizen || !tizen.tvwindow) {
      setStatus('此设备不支持 TVWindow API。');
      return;
    }
    if (!window.webapis || !webapis.avinfo || typeof webapis.avinfo.getBacklight !== 'function' ||
        typeof webapis.avinfo.setBacklight !== 'function') {
      setStatus('此设备没有可用的 AVInfo 背光接口。');
      return;
    }
    showWindow();
  }

  document.getElementById('close').addEventListener('click', closeApp);
  document.addEventListener('keydown', function (event) {
    if (event.keyCode === 10009 || event.key === 'Escape') closeApp();
  });
  start();
}());
