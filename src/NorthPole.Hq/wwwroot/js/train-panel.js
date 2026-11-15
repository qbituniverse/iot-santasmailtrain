// The mail train panel, shared by the elves' workshop (/elves) and the train driver's page (/driver): the hub's link and
// battery, Connect and Disconnect, and the driver's Forward, Reverse, Stop, speed and lights. Each page passes its own
// API, and the panel's buttons say what they do with data-cmd (a train command) or data-drive ("forward" or "reverse",
// at the slider's speed).
(() => {
  // The signal box publishes every 10 s and on every change; a state older than this means it has gone quiet.
  const QuietAfterMs = 30000;
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const hms = ms => {
    const t = Math.max(0, Math.floor(ms / 1000));
    return `${Math.floor(t / 3600)}:${String(Math.floor(t / 60) % 60).padStart(2, '0')}:${String(t % 60).padStart(2, '0')}`;
  };
  const ago = ms => { const t = Math.max(0, Math.round(ms / 1000)); return t < 90 ? `${t} s` : `${Math.round(t / 60)} min`; };

  // A connection timer and an "updated N s ago", measured on the Pi's clock at fetch time and counted on from there, so
  // the phone's clock doesn't matter. The ids are looked up each second, since the panels redraw them.
  function clock() {
    let c = null;
    const tick = () => {
      if (!c) return;
      const since = performance.now() - c.fetched;
      const up = document.getElementById(c.uptimeId), heard = document.getElementById(c.heardId);
      if (c.connected != null && up) up.textContent = hms(c.connected + since);
      if (heard) {
        const age = c.heard + since;
        heard.textContent = ago(age);
        heard.parentElement.classList.toggle('stale', age > QuietAfterMs);
      }
    };
    setInterval(tick, 1000);
    return { set(next) { c = next ? { fetched: performance.now(), ...next } : null; tick(); } };
  }

  // api(path, options) fetches under the page's API base; onCommand runs after a button's command; onError gets failures.
  function trainPanel({ root, api, onCommand = () => {}, onError = e => console.error(e) }) {
    const $ = id => document.getElementById(id);
    const timer = clock();
    const hasTestRun = !!root.querySelector('[data-cmd="TestRun"]');
    let state = null;

    function render(t) {
      const s = t.state;
      const now = Date.parse(t.now);
      const quiet = s && now - Date.parse(s.at) > QuietAfterMs;
      renderDriver(t.mqttConnected && !quiet ? s : null);
      timer.set(null);
      if (!t.mqttConnected) { $('trainStatus').innerHTML = '<span class="pill bad">MQTT offline</span>'; return; }
      if (!s) { $('trainStatus').innerHTML = '<span class="pill bad">Signal box not seen yet</span>'; return; }
      const awake = s.hubConnected && !quiet;
      const low = s.batteryPercent != null && s.batteryPercent < s.lowBatteryPercent;
      const battery = (s.batteryPercent == null ? '?' : s.batteryPercent + '%') + (s.batteryVolts == null ? '' : ` · ${s.batteryVolts.toFixed(1)} V`);
      const doing = s.busy ? 'On a run' : s.speed > 0 ? `Forward at ${s.speed}` : s.speed < 0 ? `Reverse at ${-s.speed}` : 'Waiting';
      const link = quiet ? '<span class="pill bad">Signal box quiet</span>'
        : awake ? '<span class="pill ok">Connected <span id="uptime"></span></span>'
        : s.listening ? '<span class="pill">Listening for the train</span>'
        : s.asleep ? '<span class="pill">Asleep</span>'
        : '<span class="pill bad">Disconnected</span>';
      const sleepsIn = awake && s.sleepAt ? Math.max(1, Math.ceil((Date.parse(s.sleepAt) - now) / 60000)) : null;
      const hint = quiet ? ''
        : awake ? (sleepsIn ? `Sleeps in about ${sleepsIn} min if nothing happens, to save its batteries.` : '')
        // A flashing hub is already waiting to be found; another press of its button would switch it off.
        : s.listening ? 'If the light on the train\'s hub isn\'t flashing, press its green button.'
        : s.asleep ? `It went to sleep after ${s.idleMinutes} min with nothing happening, to save its batteries. ` +
          'Press Connect to wake it, and the hub\'s green button if its light is off.'
        : 'Press Connect to look for the train again, and the hub\'s green button if its light isn\'t flashing.';
      $('trainStatus').innerHTML =
        `${link} <span class="pill ${low ? 'bad' : ''}">Battery ${esc(battery)}</span> <span class="pill">${esc(doing)}</span>` +
        (hint ? `<div class="muted" style="margin-top:6px">${hint}</div>` : '') +
        (low && awake ? `<div class="muted" style="margin-top:6px">Below ${s.lowBatteryPercent}% the train turns letters away ` +
          `(Rudolph carries them)${hasTestRun ? ' and Test run won\'t start' : ''}.</div>` : '') +
        `<div class="muted" style="margin-top:6px">Train: ${esc(s.trainMode)} · Track: ${esc(s.tracksideMode)} · ` +
        `<span>updated <span id="heard"></span> ago</span></div>`;
      timer.set({
        uptimeId: 'uptime',
        heardId: 'heard',
        heard: now - Date.parse(s.at),
        connected: awake && s.connectedSince ? now - Date.parse(s.connectedSince) : null,
      });
    }

    // The driver: only between mail runs, and only once the hub is awake. Stop always works.
    function renderDriver(s) {
      state = s;
      const slider = $('speed');
      const awake = !!s?.hubConnected;
      const canDrive = awake && !s.busy;
      const max = s?.maxSpeed || 70; // an older signal box doesn't say: its default cap
      slider.max = max;
      $('speedMax').textContent = max;
      if (s?.speed && !s.busy && document.activeElement !== slider) slider.value = Math.abs(s.speed);
      if (+slider.value > max) slider.value = max;
      $('speedOut').textContent = slider.value;
      slider.disabled = !canDrive;
      root.querySelectorAll('[data-drive]').forEach(b => b.disabled = !canDrive);
      root.querySelectorAll('.driver [data-cmd^="Lights"]').forEach(b => b.disabled = !awake);
      $('disconnect').disabled = !awake;
      $('connect').disabled = !s || awake;
      $('driverNote').textContent = !s ? 'The signal box isn\'t answering.'
        : !awake ? 'Connect the train first.'
        : s.busy ? 'On a mail run: the driver takes over once it\'s back.' : '';
    }

    async function send(command, query = '') {
      await api('/train/' + command + query, { method: 'POST' });
      setTimeout(onCommand, 700);
    }

    root.addEventListener('click', async e => {
      const b = e.target.closest('button');
      if (!b || !root.contains(b)) return;
      try {
        if (b.dataset.cmd) await send(b.dataset.cmd);
        else if (b.dataset.drive) {
          const speed = +$('speed').value;
          if (speed === 0) $('driverNote').textContent = 'Set a speed above 0 first.';
          else await send('Drive', '?speed=' + (b.dataset.drive === 'reverse' ? -speed : speed));
        }
      } catch (err) { onError(err); }
    });

    // While the train moves, letting go of the slider changes its speed the way it's going; standing still, Forward or
    // Reverse sends it.
    $('speed').addEventListener('input', e => { $('speedOut').textContent = e.target.value; });
    $('speed').addEventListener('change', async e => {
      if (!state?.speed || state.busy) return;
      try { await send('Drive', '?speed=' + Math.sign(state.speed) * +e.target.value); } catch (err) { onError(err); }
    });

    return { render };
  }

  window.NorthPole = { clock, trainPanel };
})();
