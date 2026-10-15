/*
 * TvStation admin page.
 *
 * Loaded by the Jellyfin dashboard via <script src="configurationpage?name=TvStationJs">.
 * The script may execute more than once while navigating; all handlers are attached once per
 * page element (guarded by a flag) and data is (re)loaded on every 'pageshow'.
 *
 * All API payloads use PascalCase property names, string enums, dash-less GUIDs and ISO 8601
 * UTC dates, matching Jellyfin's System.Text.Json configuration.
 */
(function () {
  'use strict';

  // The dashboard may keep an earlier instance of this view cached in the DOM; the one that was
  // just injected (and whose <script> is running now) is the last match.
  const pages = document.querySelectorAll('#TvStationPage');
  const page = pages.length ? pages[pages.length - 1] : null;
  if (!page) {
    return;
  }

  // ------------------------------------------------------------------------------------------
  // Constants
  // ------------------------------------------------------------------------------------------

  const ZERO_GUID = '00000000000000000000000000000000';
  const DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
  const DAY_SHORT = { Monday: 'Mon', Tuesday: 'Tue', Wednesday: 'Wed', Thursday: 'Thu', Friday: 'Fri', Saturday: 'Sat', Sunday: 'Sun' };
  const KIND_LABEL = {
    Movie: 'Movie', Series: 'Series', Season: 'Season', Episode: 'Episode', BoxSet: 'Collection',
    Playlist: 'Playlist', Genre: 'Genre', Tag: 'Tag', Library: 'Library', Video: 'Video'
  };
  const MODE_LABEL = { Pool: 'Pool', Slots: 'Slots', Lineup: 'Lineup' };
  const MODE_HELP = {
    Pool: 'The pool is expanded to every playable item, ordered, and played in continuous passes starting at the anchor; each pass is re-shuffled. Add time blocks to take over at fixed local times, for example a movie every evening from 18:00; the pool carries on where it left off when a block ends.',
    Slots: 'Weekly recurring blocks play their own content at fixed local times. Whenever no block is active the filler pool plays.',
    Lineup: 'The items play in exactly this order from the anchor and loop forever. Use it for a fixed marathon or a hand-built evening.'
  };
  const BLOCKS_HELP = {
    Pool: 'Optional. A block takes over the station at a fixed local time on the selected days and plays its own content; the pool plays whenever no block is on. Blocks are evaluated in the station\'s time zone and are not subject to the daytime rules.',
    Slots: 'Each block plays its own content at a fixed local time on the selected days. Blocks are evaluated in the station\'s time zone.'
  };
  const EXCLUSION_TEXT = {
    NoRuntime: 'No runtime in metadata',
    NoPath: 'No file path',
    NotFound: 'Item no longer exists',
    NotVideo: 'Not a video item',
    UserExcluded: 'Excluded on this station',
    TagExcluded: 'Excluded by tag',
    TooShort: 'Shorter than the station minimum'
  };
  const RESOLUTIONS = [['1920x1080', '1920 × 1080 (1080p)'], ['1280x720', '1280 × 720 (720p)'], ['854x480', '854 × 480 (480p)'], ['custom', 'Custom']];
  const ENCODERS = [['libx264', 'libx264 (software)'], ['h264_nvenc', 'h264_nvenc (NVIDIA)'], ['h264_qsv', 'h264_qsv (Intel Quick Sync)'], ['h264_vaapi', 'h264_vaapi (VA-API)'], ['custom', 'Custom…']];
  const STATUS_INTERVAL_MS = 5000;

  // ------------------------------------------------------------------------------------------
  // State
  // ------------------------------------------------------------------------------------------

  const state = {
    view: 'list',
    stations: [],
    timeZones: null,
    draft: null,          // Station being edited (mutated in place by components)
    detail: null,         // StationDetailDto for existing stations
    runtimes: {},         // itemId -> seconds (for lineup totals)
    expandedBlocks: {},   // block client key -> bool
    blockSeq: 0,
    stationEncoding: null,
    settingsEncoding: null,
    statusTimer: null,
    openLogs: {},         // session UniqueId -> bool
    deleteTimer: null,
    settings: null,       // GlobalSettings as last loaded in the settings view
    suggestions: [],
    blockTemplates: []
  };

  // ------------------------------------------------------------------------------------------
  // DOM helpers
  // ------------------------------------------------------------------------------------------

  function $(selector, root) {
    return (root || page).querySelector(selector);
  }

  function $all(selector, root) {
    return Array.prototype.slice.call((root || page).querySelectorAll(selector));
  }

  function appendChildren(node, children) {
    if (children === null || children === undefined || children === false) {
      return;
    }
    if (Array.isArray(children)) {
      children.forEach(c => appendChildren(node, c));
      return;
    }
    if (children instanceof Node) {
      node.appendChild(children);
      return;
    }
    node.appendChild(document.createTextNode(String(children)));
  }

  /** Creates an element. Text goes through textContent; `html` is only ever used with static templates. */
  function el(tag, attrs, children) {
    const node = document.createElement(tag);
    if (attrs) {
      Object.keys(attrs).forEach(key => {
        const value = attrs[key];
        if (value === null || value === undefined || value === false) {
          return;
        }
        if (key === 'className') {
          node.className = value;
        } else if (key === 'text') {
          node.textContent = value;
        } else if (key === 'html') {
          node.innerHTML = value;
        } else if (key === 'style' && typeof value === 'object') {
          Object.assign(node.style, value);
        } else if (key.indexOf('on') === 0 && typeof value === 'function') {
          node.addEventListener(key.slice(2).toLowerCase(), value);
        } else {
          node.setAttribute(key, value === true ? '' : value);
        }
      });
    }
    appendChildren(node, children);
    return node;
  }

  function esc(value) {
    return String(value === null || value === undefined ? '' : value)
      .replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  function clear(node) {
    while (node.firstChild) {
      node.removeChild(node.firstChild);
    }
    return node;
  }

  function show(node, visible) {
    if (node) {
      node.classList.toggle('tvs-hidden', !visible);
    }
  }

  function val(selector, root) {
    const node = $(selector, root);
    return node ? node.value : '';
  }

  function setVal(selector, value, root) {
    const node = $(selector, root);
    if (node) {
      node.value = value === null || value === undefined ? '' : value;
    }
  }

  function checked(selector, root) {
    const node = $(selector, root);
    return !!(node && node.checked);
  }

  function setChecked(selector, value, root) {
    const node = $(selector, root);
    if (node) {
      node.checked = !!value;
    }
  }

  function iconButton(icon, title, onClick, extraClass) {
    const button = el('button', {
      type: 'button',
      is: 'paper-icon-button-light',
      className: 'paper-icon-button-light' + (extraClass ? ' ' + extraClass : ''),
      title: title,
      'aria-label': title,
      onClick: onClick
    });
    button.appendChild(el('span', { className: 'material-icons ' + icon, 'aria-hidden': 'true' }));
    return button;
  }

  function textButton(text, onClick, extraClass) {
    return el('button', {
      type: 'button',
      is: 'emby-button',
      className: 'raised emby-button' + (extraClass ? ' ' + extraClass : ''),
      onClick: onClick
    }, el('span', { text: text }));
  }

  function badge(text, cls) {
    return el('span', { className: 'tvs-badge ' + (cls || ''), text: text });
  }

  function renderErrorBox(box, messages) {
    clear(box);
    if (!messages || !messages.length) {
      show(box, false);
      return;
    }
    box.appendChild(el('strong', { text: messages.length === 1 ? 'There was a problem:' : 'There were problems:' }));
    box.appendChild(el('ul', null, messages.map(m => el('li', { text: m }))));
    show(box, true);
    if (typeof box.scrollIntoView === 'function') {
      box.scrollIntoView({ block: 'nearest' });
    }
  }

  function debounce(fn, ms) {
    let timer = null;
    return function () {
      const args = arguments;
      clearTimeout(timer);
      timer = setTimeout(() => fn.apply(null, args), ms);
    };
  }

  // ------------------------------------------------------------------------------------------
  // Static form-control templates (no user data; used so emby custom elements upgrade)
  // ------------------------------------------------------------------------------------------

  function inputHtml(id, label, type, attrs, description) {
    let attrText = '';
    Object.keys(attrs || {}).forEach(k => {
      attrText += ' ' + k + '="' + esc(attrs[k]) + '"';
    });
    return '<div class="inputContainer">' +
      '<input is="emby-input" id="' + id + '" label="' + esc(label) + '" type="' + type + '" autocomplete="off"' + attrText + '>' +
      (description ? '<div class="fieldDescription">' + esc(description) + '</div>' : '') +
      '</div>';
  }

  function selectHtml(id, label, options, description) {
    return '<div class="selectContainer">' +
      '<label class="selectLabel" for="' + id + '">' + esc(label) + '</label>' +
      '<select is="emby-select" class="emby-select-withcolor emby-select" id="' + id + '">' +
      options.map(o => '<option value="' + esc(o[0]) + '">' + esc(o[1]) + '</option>').join('') +
      '</select>' +
      (description ? '<div class="fieldDescription">' + esc(description) + '</div>' : '') +
      '</div>';
  }

  // ------------------------------------------------------------------------------------------
  // Formatting helpers
  // ------------------------------------------------------------------------------------------

  function pad2(n) {
    return (n < 10 ? '0' : '') + n;
  }

  /** h:mm, with a day prefix once it passes 24 hours. */
  function fmtDur(totalSeconds) {
    if (totalSeconds === null || totalSeconds === undefined || !isFinite(totalSeconds) || totalSeconds < 0) {
      return '–';
    }
    let s = Math.round(totalSeconds);
    const days = Math.floor(s / 86400);
    s -= days * 86400;
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const core = h + ':' + pad2(m);
    return days > 0 ? days + 'd ' + core : core;
  }

  /** h:mm:ss for offsets. */
  function fmtClock(totalSeconds) {
    if (totalSeconds === null || totalSeconds === undefined || !isFinite(totalSeconds)) {
      return '';
    }
    const s = Math.max(0, Math.round(totalSeconds));
    return Math.floor(s / 3600) + ':' + pad2(Math.floor((s % 3600) / 60)) + ':' + pad2(s % 60);
  }

  /** Parses a .NET TimeSpan string ("d.hh:mm:ss.fffffff" or "hh:mm:ss") to seconds. */
  function parseTimeSpan(text) {
    if (!text || typeof text !== 'string') {
      return null;
    }
    let str = text;
    const negative = str.charAt(0) === '-';
    if (negative) {
      str = str.slice(1);
    }
    let days = 0;
    const dot = str.indexOf('.');
    const colon = str.indexOf(':');
    if (dot > -1 && dot < colon) {
      days = parseInt(str.slice(0, dot), 10) || 0;
      str = str.slice(dot + 1);
    }
    const parts = str.split(':');
    if (parts.length < 2) {
      return null;
    }
    const seconds = days * 86400 + (parseInt(parts[0], 10) || 0) * 3600 + (parseInt(parts[1], 10) || 0) * 60 + (parts[2] ? parseFloat(parts[2]) || 0 : 0);
    return negative ? -seconds : seconds;
  }

  function fmtTime(iso) {
    const d = new Date(iso);
    return isNaN(d.getTime()) ? '' : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  function fmtDateTime(iso) {
    const d = new Date(iso);
    return isNaN(d.getTime()) ? '' : d.toLocaleString([], { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  }

  function fmtDayHeading(date) {
    return date.toLocaleDateString([], { weekday: 'long', month: 'short', day: 'numeric' });
  }

  function fmtBytes(bytes) {
    const b = Number(bytes) || 0;
    if (b < 1024 * 1024) {
      return (b / 1024).toFixed(0) + ' KB';
    }
    if (b < 1024 * 1024 * 1024) {
      return (b / 1048576).toFixed(1) + ' MB';
    }
    return (b / 1073741824).toFixed(2) + ' GB';
  }

  function isZeroDate(iso) {
    return !iso || String(iso).indexOf('0001-01-01') === 0;
  }

  function toLocalInputValue(iso) {
    if (isZeroDate(iso)) {
      return '';
    }
    const d = new Date(iso);
    if (isNaN(d.getTime())) {
      return '';
    }
    return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()) + 'T' + pad2(d.getHours()) + ':' + pad2(d.getMinutes());
  }

  function fromLocalInputValue(value) {
    if (!value) {
      return null;
    }
    const d = new Date(value);
    return isNaN(d.getTime()) ? null : d.toISOString();
  }

  function parseHm(text) {
    const m = /^(\d{1,2}):(\d{2})/.exec(String(text || ''));
    if (!m) {
      return null;
    }
    const h = parseInt(m[1], 10);
    const min = parseInt(m[2], 10);
    if (h > 23 || min > 59) {
      return null;
    }
    return h * 60 + min;
  }

  function entryTitle(entry) {
    if (!entry) {
      return '';
    }
    if (entry.Kind === 'Slate') {
      return entry.Title || 'Station Break';
    }
    if (entry.Kind === 'Bumper') {
      return 'Bumper · ' + (entry.Title || '');
    }
    const parts = [entry.Title || ''];
    if (entry.IsSeries) {
      if (entry.SeasonNumber !== null && entry.SeasonNumber !== undefined && entry.EpisodeNumber !== null && entry.EpisodeNumber !== undefined) {
        parts.push('S' + pad2(entry.SeasonNumber) + 'E' + pad2(entry.EpisodeNumber));
      }
      if (entry.EpisodeTitle) {
        parts.push(entry.EpisodeTitle);
      }
    }
    return parts.filter(Boolean).join(' · ');
  }

  function itemDisplayName(item) {
    if (item.Kind === 'Episode' && item.SeriesName) {
      let se = '';
      if (item.SeasonNumber !== null && item.SeasonNumber !== undefined && item.EpisodeNumber !== null && item.EpisodeNumber !== undefined) {
        se = ' S' + pad2(item.SeasonNumber) + 'E' + pad2(item.EpisodeNumber);
      }
      return item.SeriesName + se + ' – ' + item.Name;
    }
    if (item.Kind === 'Season' && item.SeriesName) {
      return item.SeriesName + ' – ' + item.Name;
    }
    return item.Name || '(untitled)';
  }

  function itemMeta(item) {
    const bits = [];
    if (item.Kind === 'Episode' || item.Kind === 'Season') {
      if (item.SeriesName) {
        bits.push(item.SeriesName);
      }
      if (item.Kind === 'Episode' && item.SeasonNumber !== null && item.SeasonNumber !== undefined) {
        bits.push('Season ' + item.SeasonNumber + (item.EpisodeNumber !== null && item.EpisodeNumber !== undefined ? ', episode ' + item.EpisodeNumber : ''));
      }
    }
    if (item.Year) {
      bits.push(String(item.Year));
    }
    if (item.RuntimeTicks) {
      bits.push(fmtDur(item.RuntimeTicks / 1e7));
    }
    if (item.LibraryName) {
      bits.push(item.LibraryName);
    }
    return bits.join(' · ');
  }

  function imageUrl(item) {
    if (!item || !item.HasImage) {
      return null;
    }
    try {
      if (typeof ApiClient.getImageUrl === 'function') {
        return ApiClient.getImageUrl(item.Id, { type: 'Primary', maxHeight: 120 });
      }
    } catch (e) {
      /* fall through */
    }
    return ApiClient.getUrl('Items/' + item.Id + '/Images/Primary', { maxHeight: 120 });
  }

  function logoUrl(stationId) {
    return ApiClient.getUrl('TvStation/Stations/' + stationId + '/Logo', { v: Date.now() });
  }

  function guessTimeZone() {
    try {
      return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
    } catch (e) {
      return 'UTC';
    }
  }

  // ------------------------------------------------------------------------------------------
  // API helpers
  // ------------------------------------------------------------------------------------------

  function api(method, path, body, query, expectEmpty) {
    const options = {
      type: method,
      url: ApiClient.getUrl('TvStation/' + path, query || undefined)
    };
    if (body !== undefined && body !== null) {
      options.data = JSON.stringify(body);
      options.contentType = 'application/json';
    }
    if (!expectEmpty) {
      options.dataType = 'json';
    }
    return ApiClient.ajax(options);
  }

  function httpMessage(status) {
    if (status === 401 || status === 403) {
      return 'Not authorized. You must be signed in as an administrator.';
    }
    if (status === 404) {
      return 'Not found. It may have been deleted in the meantime.';
    }
    if (status === 503) {
      return 'The service is unavailable.';
    }
    return status ? 'The request failed (HTTP ' + status + ').' : 'The request failed.';
  }

  /** Normalizes any rejection into a list of human-readable messages (400s carry { Errors: [...] }). */
  function readErrors(err) {
    if (!err) {
      return Promise.resolve(['Unknown error.']);
    }
    if (typeof err === 'string') {
      return Promise.resolve([err]);
    }
    if (err instanceof Error) {
      return Promise.resolve([err.message || 'Unknown error.']);
    }
    if (typeof err.json === 'function') {
      const status = err.status;
      return err.json().then(json => {
        const list = json && (json.Errors || json.errors);
        if (Array.isArray(list) && list.length) {
          return list.map(String);
        }
        if (json && json.errors && typeof json.errors === 'object') {
          // ASP.NET model-binding ValidationProblemDetails
          const flat = [];
          Object.keys(json.errors).forEach(k => (json.errors[k] || []).forEach(m => flat.push((k ? k + ': ' : '') + m)));
          if (flat.length) {
            return flat;
          }
        }
        const single = json && (json.title || json.Message || json.message);
        return [single ? String(single) : httpMessage(status)];
      }).catch(() => [httpMessage(status)]);
    }
    return Promise.resolve([String(err)]);
  }

  function toast(text) {
    try {
      Dashboard.alert(text);
    } catch (e) {
      /* ignore */
    }
  }

  function alertErrors(err, title) {
    readErrors(err).then(messages => {
      try {
        Dashboard.alert({ title: title || 'Error', message: messages.join('\n') });
      } catch (e) {
        /* ignore */
      }
    });
  }

  function loading(on) {
    try {
      if (on) {
        Dashboard.showLoadingMsg();
      } else {
        Dashboard.hideLoadingMsg();
      }
    } catch (e) {
      /* ignore */
    }
  }

  // ------------------------------------------------------------------------------------------
  // Views
  // ------------------------------------------------------------------------------------------

  const VIEW_IDS = { list: '#tvsViewList', editor: '#tvsViewEditor', settings: '#tvsViewSettings', status: '#tvsViewStatus', suggest: '#tvsViewSuggest' };

  function showView(name) {
    if (state.view === 'status' && name !== 'status') {
      stopStatusTimer();
    }
    state.view = name;
    Object.keys(VIEW_IDS).forEach(v => show($(VIEW_IDS[v]), v === name));
    try {
      window.scrollTo(0, 0);
    } catch (e) {
      /* ignore */
    }
  }

  // ------------------------------------------------------------------------------------------
  // Station list
  // ------------------------------------------------------------------------------------------


  const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

  /** "a, b ,c" -> ['a', 'b', 'c'] without blanks or case-insensitive duplicates. */
  function parseTagList(text) {
    const out = [];
    const seen = {};
    String(text || '').split(',').forEach(t => {
      const tag = t.trim();
      const key = tag.toLowerCase();
      if (tag && !seen[key]) {
        seen[key] = true;
        out.push(tag);
      }
    });
    return out;
  }

  const SEASON_PRESETS = {
    halloween: { Label: 'Halloween', StartMonth: 10, StartDay: 1, EndMonth: 10, EndDay: 31 },
    thanksgiving: { Label: 'Thanksgiving', StartMonth: 11, StartDay: 15, EndMonth: 11, EndDay: 30 },
    christmas: { Label: 'Christmas', StartMonth: 12, StartDay: 1, EndMonth: 1, EndDay: 6 },
    holidays: { Label: 'Holiday season', StartMonth: 11, StartDay: 20, EndMonth: 1, EndDay: 6 },
    newyear: { Label: 'New Year', StartMonth: 12, StartDay: 26, EndMonth: 1, EndDay: 7 },
    valentines: { Label: "Valentine's", StartMonth: 2, StartDay: 1, EndMonth: 2, EndDay: 14 },
    stpatricks: { Label: "St. Patrick's", StartMonth: 3, StartDay: 10, EndMonth: 3, EndDay: 17 },
    easter: { Label: 'Spring', StartMonth: 3, StartDay: 20, EndMonth: 4, EndDay: 30 },
    july4: { Label: 'Independence Day', StartMonth: 6, StartDay: 28, EndMonth: 7, EndDay: 7 },
    summer: { Label: 'Summer', StartMonth: 6, StartDay: 1, EndMonth: 8, EndDay: 31 },
    winter: { Label: 'Winter', StartMonth: 12, StartDay: 1, EndMonth: 2, EndDay: 28 }
  };

  /** Grid minutes from the padding dropdown; "by length" aligns the anchor to the half hour. */
  function paddingGridMinutes() {
    const v = val('#tvsPadding');
    return v === 'length' ? 30 : (parseInt(v, 10) || 0);
  }

  /** Now, rounded down to the padding grid so programs line up with the clock. */
  function nowOnGrid(gridMinutes) {
    const d = new Date();
    d.setSeconds(0, 0);
    const g = parseInt(gridMinutes, 10) || 0;
    if (g > 0) {
      d.setMinutes(Math.floor(d.getMinutes() / g) * g);
    }
    return d;
  }

  /** Re-anchors a saved station so its schedule restarts from now. */
  function reanchorStation(s) {
    api('GET', 'Stations/' + s.Id).then(detail => {
      const station = detail.Station;
      station.AnchorUtc = nowOnGrid(station.PaddingGridMinutes).toISOString();
      return api('PUT', 'Stations/' + s.Id, station);
    }).then(() => {
      toast('“' + s.Name + '” now starts from this moment.');
      return loadStations();
    }).catch(err => alertErrors(err, 'Could not re-anchor'));
  }

  function renderDaypartRows() {
    const host = clear($('#tvsDaypartRows'));
    const rules = state.draft.Pool.DaypartRules;
    if (!rules.length) {
      host.appendChild(el('div', { className: 'tvs-secondary', text: 'No time-of-day limits.' }));
      return;
    }
    rules.forEach((r, index) => {
      function timeInput(key) {
        const input = el('input', { type: 'time', className: 'emby-input' });
        input.value = r[key] || '00:00';
        input.addEventListener('change', () => { r[key] = input.value || '00:00'; });
        return input;
      }
      const rating = el('select', { className: 'emby-select' }, RATING_CHOICES.slice(1).map(c => el('option', { value: c[0], text: c[1] })));
      rating.value = RATING_ALIAS[r.MaxRating] || r.MaxRating || 'PG';
      rating.addEventListener('change', () => { r.MaxRating = rating.value; });
      const unrated = el('input', { type: 'checkbox', className: 'emby-checkbox' });
      unrated.checked = !!r.AllowUnrated;
      unrated.addEventListener('change', () => { r.AllowUnrated = unrated.checked; });
      host.appendChild(el('div', { className: 'tvs-season-row' }, [
        el('span', { text: 'from' }), timeInput('StartLocal'),
        el('span', { text: 'to' }), timeInput('EndLocal'),
        el('span', { text: 'at most' }), rating,
        el('label', { className: 'f row' }, [unrated, el('span', { text: 'allow unrated' })]),
        el('button', { type: 'button', title: 'Remove', text: '×', onClick: () => { rules.splice(index, 1); renderDaypartRows(); } })
      ]));
    });
  }

  function renderSeasonRows() {
    const host = clear($('#tvsSeasonRows'));
    const windows = state.draft.ActiveWindows;
    if (!windows.length) {
      host.appendChild(el('div', { className: 'tvs-secondary', text: 'Listed all year.' }));
      return;
    }
    windows.forEach((w, index) => {
      function monthSelect(key) {
        const sel = el('select', { className: 'emby-select' }, MONTHS.map((m, i) => el('option', { value: String(i + 1), text: m })));
        sel.value = String(w[key] || 1);
        sel.addEventListener('change', () => { w[key] = parseInt(sel.value, 10); });
        return sel;
      }
      function daySelect(key) {
        const sel = el('select', { className: 'emby-select' });
        for (let d = 1; d <= 31; d++) {
          sel.appendChild(el('option', { value: String(d), text: String(d) }));
        }
        sel.value = String(w[key] || 1);
        sel.addEventListener('change', () => { w[key] = parseInt(sel.value, 10); });
        return sel;
      }
      const label = el('input', { type: 'text', className: 'emby-input', placeholder: 'Label (optional)' });
      label.value = w.Label || '';
      label.addEventListener('change', () => { w.Label = label.value.trim(); });
      host.appendChild(el('div', { className: 'tvs-season-row' }, [
        label,
        el('span', { text: 'from' }), monthSelect('StartMonth'), daySelect('StartDay'),
        el('span', { text: 'to' }), monthSelect('EndMonth'), daySelect('EndDay'),
        el('button', { type: 'button', title: 'Remove', text: '×', onClick: () => { windows.splice(index, 1); renderSeasonRows(); } })
      ]));
    });
  }

  function exportStations() {
    api('GET', 'Stations/Export').then(list => {
      const stamp = new Date().toISOString().slice(0, 10);
      const blob = new Blob([JSON.stringify(list, null, 2)], { type: 'application/json' });
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob);
      a.download = 'tvstation-stations-' + stamp + '.json';
      document.body.appendChild(a);
      a.click();
      setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
      toast('Exported ' + list.length + ' station' + (list.length === 1 ? '' : 's') + '.');
    }).catch(err => alertErrors(err, 'Export failed'));
  }

  function importStationsFromFile(ev) {
    const file = ev.target.files && ev.target.files[0];
    if (!file) {
      return;
    }
    const reader = new FileReader();
    reader.onload = () => {
      let parsed;
      try {
        parsed = JSON.parse(reader.result);
      } catch (e) {
        toast('That file is not valid JSON.');
        return;
      }
      const list = Array.isArray(parsed) ? parsed : [parsed];
      loading(true);
      api('POST', 'Stations/Import', list).then(res => {
        const parts = [];
        if (res.Created.length) { parts.push('created ' + res.Created.length); }
        if (res.Updated.length) { parts.push('updated ' + res.Updated.length); }
        if (res.Errors.length) { parts.push(res.Errors.length + ' failed'); }
        const resolved = res.Resolved || [];
        const unmatched = res.Unmatched || [];
        if (resolved.length) { parts.push(resolved.length + ' source' + (resolved.length === 1 ? '' : 's') + ' matched by name'); }
        if (unmatched.length) { parts.push(unmatched.length + ' not found'); }
        toast('Import: ' + (parts.join(', ') || 'nothing to do') + '.');
        if (res.Errors.length || unmatched.length) {
          renderErrorBox($('#tvsListError'), res.Errors.concat(unmatched.map(u => 'Not found, dropped: ' + u)));
        }
        return loadStations();
      }).catch(err => alertErrors(err, 'Import failed')).then(() => loading(false));
    };
    reader.readAsText(file);
  }

  function loadStations() {
    loading(true);
    return api('GET', 'Stations').then(list => {
      state.stations = Array.isArray(list) ? list : [];
      renderErrorBox($('#tvsListError'), null);
      renderStationList();
    }).catch(err => {
      readErrors(err).then(messages => renderErrorBox($('#tvsListError'), messages));
    }).then(() => loading(false));
  }

  function channelSort(a, b) {
    const na = parseFloat(a.ChannelNumber);
    const nb = parseFloat(b.ChannelNumber);
    if (!isNaN(na) && !isNaN(nb) && na !== nb) {
      return na - nb;
    }
    return String(a.ChannelNumber).localeCompare(String(b.ChannelNumber)) || String(a.Name).localeCompare(String(b.Name));
  }

  function renderStationList() {
    const list = clear($('#tvsStationList'));
    show($('#tvsStationEmpty'), state.stations.length === 0);
    state.stations.slice().sort(channelSort).forEach(s => list.appendChild(stationRow(s)));
  }

  function stationRow(s) {
    const row = el('div', { className: 'listItem tvs-station' });

    if (s.HasLogo) {
      row.appendChild(el('img', { className: 'tvs-logo', src: logoUrl(s.Id), alt: '' }));
    } else {
      row.appendChild(el('div', { className: 'tvs-chnum', text: s.ChannelNumber || '–' }));
    }

    const body = el('div', { className: 'listItemBody three-line' });
    const title = el('div', { className: 'listItemBodyText' }, [
      el('span', { className: 'tvs-secondary', text: 'Ch ' + (s.ChannelNumber || '?') + '  ' }),
      el('span', { text: s.Name || '(unnamed)' }),
      badge(s.Enabled ? 'Enabled' : 'Disabled', s.Enabled ? 'on' : 'off'),
      s.Seasonal ? badge(s.InSeason ? 'In season' + (s.NextSeasonChange ? ' until ' + s.NextSeasonChange : '') : 'Off season' + (s.NextSeasonChange ? ' until ' + s.NextSeasonChange : ''), s.InSeason ? 'on' : 'warn') : null,
      s.Streaming ? badge('Streaming', 'live') : null
    ]);
    body.appendChild(title);

    const meta = [MODE_LABEL[s.Mode] || s.Mode, s.ItemCount + (s.ItemCount === 1 ? ' item' : ' items'), 'rev ' + s.Revision];
    const metaLine = el('div', { className: 'listItemBodyText secondary' }, meta.join(' · '));
    if (s.ExcludedCount > 0) {
      metaLine.appendChild(badge(s.ExcludedCount + ' excluded', 'warn'));
    }
    body.appendChild(metaLine);

    let nowText;
    if (s.NowPlaying) {
      nowText = 'Now: ' + entryTitle(s.NowPlaying);
      if (s.NowPlaying.OffsetSeconds !== null && s.NowPlaying.OffsetSeconds !== undefined) {
        nowText += ' (' + fmtClock(s.NowPlaying.OffsetSeconds) + ' in)';
      }
    } else {
      nowText = s.Enabled ? 'Now: nothing scheduled – add content or recompile' : 'Now: off air (disabled)';
    }
    body.appendChild(el('div', { className: 'listItemBodyText secondary tvs-ellipsis', text: nowText }));
    row.appendChild(body);

    const actions = el('div', { className: 'tvs-station-actions' });
    renderStationActions(actions, s, false);
    row.appendChild(actions);
    return row;
  }

  function renderStationActions(container, s, armed) {
    clear(container);
    if (armed) {
      container.appendChild(textButton('Confirm delete?', () => deleteStation(s), 'button-cancel'));
      container.appendChild(iconButton('close', 'Keep station', () => renderStationActions(container, s, false)));
      clearTimeout(state.deleteTimer);
      state.deleteTimer = setTimeout(() => {
        if (container.isConnected) {
          renderStationActions(container, s, false);
        }
      }, 6000);
      return;
    }
    container.appendChild(iconButton('refresh', 'Restart stream', () => restartStream(s)));
    container.appendChild(iconButton('restart_alt', 'Start schedule from now', () => reanchorStation(s)));
    container.appendChild(iconButton('edit', 'Edit', () => openEditor(s.Id)));
    container.appendChild(iconButton('delete', 'Delete', () => renderStationActions(container, s, true)));
  }

  function deleteStation(s) {
    loading(true);
    api('DELETE', 'Stations/' + s.Id, undefined, undefined, true).then(() => {
      toast('Deleted “' + s.Name + '”.');
      return loadStations();
    }).catch(err => alertErrors(err, 'Could not delete station')).then(() => loading(false));
  }

  function restartStream(s) {
    api('POST', 'Stations/' + s.Id + '/Stream/Restart', undefined, undefined, true)
      .then(() => toast('Stream restart requested for “' + s.Name + '”.'))
      .catch(err => alertErrors(err, 'Could not restart stream'));
  }

  function refreshGuide() {
    loading(true);
    api('POST', 'Guide/Refresh', undefined, undefined, true)
      .then(() => toast('Live TV guide refresh requested.'))
      .catch(err => alertErrors(err, 'Could not refresh guide'))
      .then(() => loading(false));
  }

  // ------------------------------------------------------------------------------------------
  // Station editor
  // ------------------------------------------------------------------------------------------

  function loadTimeZones() {
    if (state.timeZones) {
      return Promise.resolve(state.timeZones);
    }
    return api('GET', 'TimeZones').then(list => {
      state.timeZones = Array.isArray(list) ? list : [];
      return state.timeZones;
    }).catch(() => {
      state.timeZones = ['UTC'];
      return state.timeZones;
    });
  }

  function nextChannelNumber() {
    let max = 0;
    state.stations.forEach(s => {
      const n = parseInt(s.ChannelNumber, 10);
      if (!isNaN(n) && n > max) {
        max = n;
      }
    });
    return String(max + 1);
  }

  function newDraft() {
    return {
      Id: ZERO_GUID,
      Name: '',
      ChannelNumber: nextChannelNumber(),
      CallSign: null,
      Enabled: true,
      LogoFileName: null,
      LogoOverlay: { Enabled: false, Corner: 'TopRight', Opacity: 70, HeightPercent: 8 },
      TimeZoneId: guessTimeZone(),
      AnchorUtc: null,
      Revision: 1,
      PaddingGridMinutes: 0,
      PaddingByLength: false,
      Mode: 'Pool',
      Pool: { Sources: [], Order: 'Shuffle', AvoidSameSeriesBackToBack: true, MarathonMin: 1, MarathonMax: 1, DaypartRules: [] },
      Slots: { Blocks: [], Filler: { Sources: [], Order: 'Shuffle' } },
      Lineup: { Items: [] },
      Encoding: null,
      AutoRecompileNightly: true,
      ActiveWindows: [],
      ExcludedItemIds: [],
      ExcludedTags: [],
      UseGlobalExcludedTags: true,
      MinRuntimeMinutes: 0,
      Bumpers: { Enabled: false, Sources: [], BreakMinutes: 0, MaxClipMinutes: 5 },
      CardText: null
    };
  }

  function normalizeStation(station) {
    const s = station || {};
    s.Pool = s.Pool || {};
    s.Pool.Sources = s.Pool.Sources || [];
    s.Pool.Order = s.Pool.Order || 'Shuffle';
    if (s.Pool.AvoidSameSeriesBackToBack === undefined) {
      s.Pool.AvoidSameSeriesBackToBack = true;
    }
    s.Pool.MarathonMin = Math.max(1, parseInt(s.Pool.MarathonMin, 10) || 1);
    s.Pool.MarathonMax = Math.max(s.Pool.MarathonMin, parseInt(s.Pool.MarathonMax, 10) || 1);
    s.Pool.DaypartRules = Array.isArray(s.Pool.DaypartRules) ? s.Pool.DaypartRules : [];
    s.Pool.Sources.forEach(normalizeSource);
    s.Slots = s.Slots || {};
    s.Slots.Blocks = s.Slots.Blocks || [];
    s.Slots.Blocks.forEach(b => {
      b.Days = b.Days || [];
      b.Sources = b.Sources || [];
      b.Sources.forEach(normalizeSource);
      b.Order = b.Order || 'Sequential';
      b.Overflow = b.Overflow || 'FillerRemainder';
      b.StartLocal = b.StartLocal || '20:00';
      if (b.DurationMinutes === undefined || b.DurationMinutes === null) {
        b.DurationMinutes = 60;
      }
      b._key = 'b' + (++state.blockSeq);
    });
    s.Slots.Filler = s.Slots.Filler || {};
    s.Slots.Filler.Sources = s.Slots.Filler.Sources || [];
    s.Slots.Filler.Sources.forEach(normalizeSource);
    s.Slots.Filler.Order = s.Slots.Filler.Order || 'Shuffle';
    s.Lineup = s.Lineup || {};
    s.Lineup.Items = s.Lineup.Items || [];
    s.Mode = s.Mode || 'Pool';
    s.ActiveWindows = Array.isArray(s.ActiveWindows) ? s.ActiveWindows : [];
    s.ExcludedItemIds = Array.isArray(s.ExcludedItemIds) ? s.ExcludedItemIds : [];
    s.ExcludedTags = Array.isArray(s.ExcludedTags) ? s.ExcludedTags : [];
    s.PaddingByLength = !!s.PaddingByLength;
    s.UseGlobalExcludedTags = s.UseGlobalExcludedTags !== false;
    s.MinRuntimeMinutes = Math.max(0, parseInt(s.MinRuntimeMinutes, 10) || 0);
    s.Bumpers = s.Bumpers || {};
    s.Bumpers.Enabled = !!s.Bumpers.Enabled;
    s.Bumpers.Sources = Array.isArray(s.Bumpers.Sources) ? s.Bumpers.Sources : [];
    s.Bumpers.Sources.forEach(normalizeSource);
    s.Bumpers.BreakMinutes = Math.max(0, parseInt(s.Bumpers.BreakMinutes, 10) || 0);
    s.Bumpers.MaxClipMinutes = Math.max(1, parseInt(s.Bumpers.MaxClipMinutes, 10) || 5);
    s.CardText = s.CardText || null;
    s.LogoOverlay = s.LogoOverlay || {};
    s.LogoOverlay.Enabled = !!s.LogoOverlay.Enabled;
    s.LogoOverlay.Corner = s.LogoOverlay.Corner || 'TopRight';
    s.LogoOverlay.Opacity = Math.min(100, Math.max(10, parseInt(s.LogoOverlay.Opacity, 10) || 70));
    s.LogoOverlay.HeightPercent = Math.min(25, Math.max(3, parseInt(s.LogoOverlay.HeightPercent, 10) || 8));
    return s;
  }

  function normalizeSource(src) {
    src.Weight = Math.min(10, Math.max(1, parseInt(src.Weight, 10) || 1));
    src.ItemKinds = Array.isArray(src.ItemKinds) ? src.ItemKinds : [];
    if (src.SeasonFrom !== undefined && src.SeasonFrom !== null && !(src.SeasonFrom > 0)) { src.SeasonFrom = null; }
    if (src.SeasonTo !== undefined && src.SeasonTo !== null && !(src.SeasonTo > 0)) { src.SeasonTo = null; }
    src.ExcludeSpecials = !!src.ExcludeSpecials;
    src.MaxRating = src.MaxRating || null;
    src.Ratings = Array.isArray(src.Ratings) ? src.Ratings : [];
    src.AllowUnrated = !!src.AllowUnrated;
    if (src.YearFrom !== undefined && src.YearFrom !== null && !(src.YearFrom > 0)) { src.YearFrom = null; }
    if (src.YearTo !== undefined && src.YearTo !== null && !(src.YearTo > 0)) { src.YearTo = null; }
    return src;
  }

  const RATING_CHOICES = [
    ['', 'Any rating'], ['TV-Y', 'TV-Y'], ['TV-Y7', 'TV-Y7'], ['G', 'G / TV-G'], ['PG', 'PG / TV-PG'],
    ['PG-13', 'PG-13 / TV-14'], ['R', 'R / TV-MA'], ['NC-17', 'NC-17']
  ];
  const RATING_ALIAS = { 'TV-G': 'G', 'TV-PG': 'PG', 'TV-14': 'PG-13', 'TV-MA': 'R', 'TV-Y7-FV': 'TV-Y7' };

  /** True for source kinds that expand to a list the rating / year filters apply to. */
  function hasTuningFilters(src) {
    return src.Kind !== 'Movie' && src.Kind !== 'Episode';
  }

  /** True when a container source should offer the movies / shows filter. */
  function hasKindFilter(src) {
    return src.Kind === 'Genre' || src.Kind === 'Tag' || src.Kind === 'Playlist' || src.Kind === 'BoxSet' || src.Kind === 'Library';
  }

  /** 'Both' | 'Movie' | 'Episode' from a source's ItemKinds list. */
  function kindFilterValue(src) {
    const kinds = src.ItemKinds || [];
    const movies = kinds.length === 0 || kinds.indexOf('Movie') >= 0;
    const episodes = kinds.length === 0 || kinds.indexOf('Episode') >= 0;
    return movies && episodes ? 'Both' : (movies ? 'Movie' : 'Episode');
  }

  /** Short summary of a source's tuning for its chip, or '' when it is untouched. */
  function sourceBadge(src) {
    const parts = [];
    if (src.Kind === 'Series') {
      if (src.SeasonFrom || src.SeasonTo) {
        parts.push('S' + (src.SeasonFrom || 1) + (src.SeasonTo ? '–S' + src.SeasonTo : '+'));
      }
      if (src.ExcludeSpecials) {
        parts.push('no specials');
      }
    } else if (hasKindFilter(src)) {
      const k = kindFilterValue(src);
      if (k === 'Movie') { parts.push('movies only'); }
      if (k === 'Episode') { parts.push('shows only'); }
    }
    if (src.MaxRating) {
      parts.push('≤ ' + src.MaxRating + (src.AllowUnrated ? ' +NR' : ''));
    }
    if (src.YearFrom || src.YearTo) {
      parts.push((src.YearFrom || '…') + '–' + (src.YearTo || '…'));
    }
    if (src.Weight > 1) {
      parts.push('×' + src.Weight);
    }
    return parts.join(' · ');
  }

  function openEditor(id) {
    loading(true);
    const detailPromise = id ? api('GET', 'Stations/' + id) : Promise.resolve(null);
    Promise.all([loadTimeZones(), detailPromise]).then(results => {
      const detail = results[1];
      state.detail = detail;
      state.draft = detail ? normalizeStation(detail.Station) : newDraft();
      state.runtimes = {};
      state.expandedBlocks = {};
      renderEditor();
      showView('editor');
    }).catch(err => alertErrors(err, 'Could not open station')).then(() => loading(false));
  }

  function isNewDraft() {
    return !state.draft || !state.draft.Id || state.draft.Id === ZERO_GUID;
  }

  function renderEditor() {
    const d = state.draft;
    const isNew = isNewDraft();

    $('#tvsEditorTitle').textContent = isNew ? 'New station' : 'Edit station – ' + (d.Name || '');
    renderErrorBox($('#tvsEditorErrors'), null);

    setVal('#tvsName', d.Name);
    setVal('#tvsChannel', d.ChannelNumber);
    setVal('#tvsCallSign', d.CallSign || '');
    setChecked('#tvsEnabled', d.Enabled !== false);
    setChecked('#tvsNightly', d.AutoRecompileNightly !== false);
    setVal('#tvsCardText', d.CardText || '');
    setVal('#tvsExcludedTags', d.ExcludedTags.join(', '));
    setChecked('#tvsUseGlobalTags', d.UseGlobalExcludedTags !== false);
    setVal('#tvsMinRuntime', d.MinRuntimeMinutes || 0);
    loadBlockTemplates();
    setChecked('#tvsBumpersEnabled', !!d.Bumpers.Enabled);
    setVal('#tvsBumperBreak', d.Bumpers.BreakMinutes || 0);
    setVal('#tvsBumperMax', d.Bumpers.MaxClipMinutes || 5);
    show($('#tvsBumperOptions'), !!d.Bumpers.Enabled);
    createPicker({
      container: $('#tvsBumperPicker'),
      mode: 'sources',
      getSources: () => state.draft.Bumpers.Sources,
      onAdd: source => state.draft.Bumpers.Sources.push(source),
      onRemove: index => state.draft.Bumpers.Sources.splice(index, 1)
    });
    renderSeasonRows();
    state.content = null;
    clear($('#tvsContent'));
    $('#tvsContentSummary').textContent = '';
    setVal('#tvsPadding', d.PaddingByLength ? 'length' : String(d.PaddingGridMinutes || 0));
    setVal('#tvsMode', d.Mode);
    $('#tvsModeHelp').textContent = MODE_HELP[d.Mode] || '';

    // Time zone
    const tzSelect = clear($('#tvsTimeZone'));
    const zones = state.timeZones.slice();
    if (d.TimeZoneId && zones.indexOf(d.TimeZoneId) < 0) {
      zones.unshift(d.TimeZoneId);
    }
    zones.forEach(z => tzSelect.appendChild(el('option', { value: z, text: z })));
    if (zones.indexOf(d.TimeZoneId) < 0) {
      d.TimeZoneId = zones.indexOf('UTC') >= 0 ? 'UTC' : (zones[0] || 'UTC');
    }
    tzSelect.value = d.TimeZoneId;

    // Anchor
    setVal('#tvsAnchor', toLocalInputValue(d.AnchorUtc));
    updateAnchorHint();

    // Logo
    renderLogo();
    setChecked('#tvsOverlayEnabled', !!d.LogoOverlay.Enabled);
    setVal('#tvsOverlayCorner', d.LogoOverlay.Corner);
    setVal('#tvsOverlayOpacity', d.LogoOverlay.Opacity);
    setVal('#tvsOverlaySize', d.LogoOverlay.HeightPercent);
    show($('#tvsOverlayOptions'), !!d.LogoOverlay.Enabled);

    // Mode panels
    setVal('#tvsPoolOrder', d.Pool.Order);
    setChecked('#tvsPoolAvoid', d.Pool.AvoidSameSeriesBackToBack !== false);
    setVal('#tvsMarathonMin', d.Pool.MarathonMin || 1);
    setVal('#tvsMarathonMax', d.Pool.MarathonMax || 1);
    renderDaypartRows();
    setVal('#tvsFillerOrder', d.Slots.Filler.Order);
    updateModePanels();

    createPicker({
      container: $('#tvsPoolPicker'),
      mode: 'sources',
      getSources: () => state.draft.Pool.Sources,
      onAdd: source => state.draft.Pool.Sources.push(source),
      onRemove: index => state.draft.Pool.Sources.splice(index, 1)
    });
    createPicker({
      container: $('#tvsFillerPicker'),
      mode: 'sources',
      getSources: () => state.draft.Slots.Filler.Sources,
      onAdd: source => state.draft.Slots.Filler.Sources.push(source),
      onRemove: index => state.draft.Slots.Filler.Sources.splice(index, 1)
    });
    createPicker({
      container: $('#tvsLineupPicker'),
      mode: 'items',
      onAddItems: addLineupItems
    });

    renderBlocks();
    renderLineup();
    hydrateLineupRuntimes();

    // Cycle info (Pool)
    const cycleSeconds = state.detail && state.detail.CycleDuration ? parseTimeSpan(state.detail.CycleDuration) : null;
    $('#tvsPoolCycle').textContent = cycleSeconds !== null && cycleSeconds !== undefined && d.Mode === 'Pool'
      ? 'Compiled cycle length: ' + fmtDur(cycleSeconds) + ' across ' + (state.detail.Compiled ? state.detail.Compiled.ItemCount : 0) + ' items (including padding).'
      : '';

    // Encoding
    if (!state.stationEncoding) {
      state.stationEncoding = createEncodingForm($('#tvsStationEncoding'), 'tvsStEnc');
    }
    const hasEncoding = !!d.Encoding;
    setChecked('#tvsEncodingEnabled', hasEncoding);
    state.stationEncoding.set(d.Encoding || defaultEncoding());
    show($('#tvsStationEncoding'), hasEncoding);

    renderExcluded();
    clear($('#tvsPreview'));

    show($('#tvsBtnRecompile'), !isNew);
  }

  function updateAnchorHint() {
    const value = val('#tvsAnchor');
    const hint = $('#tvsAnchorHint');
    if (!value) {
      hint.textContent = isNewDraft()
        ? 'Not set – the anchor will be the moment the station is saved.'
        : 'Cleared – the anchor will be reset to now when you save.';
    } else {
      const iso = fromLocalInputValue(value);
      hint.textContent = iso ? 'UTC: ' + iso.replace('.000Z', 'Z') : '';
    }
  }

  function updateModePanels() {
    const mode = val('#tvsMode') || 'Pool';
    const changed = state.draft.Mode !== mode;
    state.draft.Mode = mode;
    $('#tvsModeHelp').textContent = MODE_HELP[mode] || '';
    $('#tvsBlocksHelp').textContent = BLOCKS_HELP[mode] || '';
    show($('#tvsPanelPool'), mode === 'Pool');
    show($('#tvsPanelBlocks'), mode === 'Pool' || mode === 'Slots');
    show($('#tvsPanelSlots'), mode === 'Slots');
    show($('#tvsPanelLineup'), mode === 'Lineup');
    if (changed) {
      renderBlocks(); // block wording depends on the mode
    }
  }

  /** What plays when a block has no content or hands time back: the pool in Pool mode, the filler otherwise. */
  function betweenBlocksName() {
    return state.draft.Mode === 'Pool' ? 'the pool' : 'filler';
  }

  function renderLogo() {
    const isNew = isNewDraft();
    const img = $('#tvsLogoImg');
    const none = $('#tvsLogoNone');
    const file = $('#tvsLogoFile');
    const hint = $('#tvsLogoHint');
    file.value = '';
    file.disabled = isNew;
    const hasLogo = !isNew && !!state.draft.LogoFileName;
    if (hasLogo) {
      img.src = logoUrl(state.draft.Id);
    } else {
      img.removeAttribute('src');
    }
    show(img, hasLogo);
    show(none, !hasLogo);
    none.textContent = isNew ? 'Save the station first, then upload a logo.' : 'No logo uploaded.';
    hint.textContent = 'PNG, JPEG, WebP or GIF up to 5 MB. Shown in the guide and on station-break slates.';
  }

  function uploadLogo(file) {
    if (!file || isNewDraft()) {
      return;
    }
    const id = state.draft.Id;
    const hint = $('#tvsLogoHint');
    hint.textContent = 'Uploading…';
    const headers = { 'Content-Type': file.type || 'application/octet-stream' };
    try {
      headers.Authorization = 'MediaBrowser Token="' + ApiClient.accessToken() + '"';
    } catch (e) {
      /* ignore */
    }
    fetch(ApiClient.getUrl('TvStation/Stations/' + id + '/Logo'), { method: 'POST', body: file, headers: headers })
      .then(response => {
        if (!response.ok) {
          return Promise.reject(response);
        }
        state.draft.LogoFileName = file.name || 'logo';
        renderLogo();
        toast('Logo uploaded.');
      })
      .catch(err => {
        renderLogo();
        alertErrors(err, 'Logo upload failed');
      });
  }

  // ---- Reading the form back into the draft -----------------------------------------------

  function readDraft() {
    const d = state.draft;
    d.Name = val('#tvsName').trim();
    d.ChannelNumber = val('#tvsChannel').trim();
    d.CallSign = val('#tvsCallSign').trim() || null;
    d.Enabled = checked('#tvsEnabled');
    d.TimeZoneId = val('#tvsTimeZone');
    d.AnchorUtc = fromLocalInputValue(val('#tvsAnchor'));
    d.PaddingByLength = val('#tvsPadding') === 'length';
    d.PaddingGridMinutes = paddingGridMinutes();
    d.AutoRecompileNightly = checked('#tvsNightly');
    d.CardText = val('#tvsCardText').trim() || null;
    d.LogoOverlay.Enabled = checked('#tvsOverlayEnabled');
    d.LogoOverlay.Corner = val('#tvsOverlayCorner') || 'TopRight';
    d.LogoOverlay.Opacity = Math.min(100, Math.max(10, parseInt(val('#tvsOverlayOpacity'), 10) || 70));
    d.LogoOverlay.HeightPercent = Math.min(25, Math.max(3, parseInt(val('#tvsOverlaySize'), 10) || 8));
    d.ExcludedTags = parseTagList(val('#tvsExcludedTags'));
    d.UseGlobalExcludedTags = checked('#tvsUseGlobalTags');
    d.MinRuntimeMinutes = Math.max(0, parseInt(val('#tvsMinRuntime'), 10) || 0);
    d.Bumpers.Enabled = checked('#tvsBumpersEnabled');
    d.Bumpers.BreakMinutes = Math.max(0, parseInt(val('#tvsBumperBreak'), 10) || 0);
    d.Bumpers.MaxClipMinutes = Math.max(1, parseInt(val('#tvsBumperMax'), 10) || 5);
    d.Mode = val('#tvsMode') || 'Pool';
    d.Pool.Order = val('#tvsPoolOrder') || 'Shuffle';
    d.Pool.AvoidSameSeriesBackToBack = checked('#tvsPoolAvoid');
    d.Pool.MarathonMin = Math.max(1, parseInt(val('#tvsMarathonMin'), 10) || 1);
    d.Pool.MarathonMax = Math.max(d.Pool.MarathonMin, parseInt(val('#tvsMarathonMax'), 10) || 1);
    d.Slots.Filler.Order = val('#tvsFillerOrder') || 'Shuffle';
    d.Encoding = checked('#tvsEncodingEnabled') && state.stationEncoding ? state.stationEncoding.get() : null;
    return toPayload(d);
  }

  /** Deep copies the draft and strips client-only fields / nulls the server cannot bind. */
  function toPayload(d) {
    const p = JSON.parse(JSON.stringify(d, (key, value) => (key.charAt(0) === '_' ? undefined : value)));
    if (!p.Id) {
      p.Id = ZERO_GUID;
    }
    if (!p.AnchorUtc) {
      delete p.AnchorUtc; // server picks "now"
    }
    ['CreatedUtc', 'UpdatedUtc'].forEach(k => {
      if (!p[k]) {
        delete p[k];
      }
    });
    (p.Slots.Blocks || []).forEach(b => {
      if (!b.Id || b.Id === ZERO_GUID) {
        delete b.Id; // server assigns
      }
      b.DurationMinutes = parseInt(b.DurationMinutes, 10) || 0;
    });
    return p;
  }

  function clientValidate(p) {
    const errors = [];
    if (!p.Name) {
      errors.push('Name is required.');
    }
    if (!p.ChannelNumber) {
      errors.push('Channel number is required.');
    }
    if (p.Mode === 'Pool' && !p.Pool.Sources.length) {
      errors.push('Add at least one content source to the pool.');
    }
    if (p.Mode === 'Lineup' && !p.Lineup.Items.length) {
      errors.push('Add at least one item to the lineup.');
    }
    if (p.Mode === 'Slots' && !p.Slots.Blocks.length && !p.Slots.Filler.Sources.length) {
      errors.push('Add at least one time block or some filler content.');
    }
    if (p.Mode === 'Pool' || p.Mode === 'Slots') {
      p.Slots.Blocks.forEach((b, i) => {
        const label = b.Name ? '“' + b.Name + '”' : 'Block ' + (i + 1);
        if (!b.Days || !b.Days.length) {
          errors.push(label + ' has no days selected.');
        }
        if (parseHm(b.StartLocal) === null) {
          errors.push(label + ' has an invalid start time.');
        }
        if (!(b.DurationMinutes > 0)) {
          errors.push(label + ' needs a duration greater than 0 minutes.');
        }
        if (!b.Sources || !b.Sources.length) {
          errors.push(label + ' has no content.');
        }
      });
    }
    return errors;
  }

  // ---- Save / recompile / preview ---------------------------------------------------------

  function saveStation(refreshGuideAfter) {
    const payload = readDraft();
    const errors = clientValidate(payload);
    const box = $('#tvsEditorErrors');
    if (errors.length) {
      renderErrorBox(box, errors);
      return;
    }
    renderErrorBox(box, null);
    loading(true);
    const request = payload.Id === ZERO_GUID
      ? api('POST', 'Stations', payload)
      : api('PUT', 'Stations/' + payload.Id, payload);

    request.then(saved => {
      const name = (saved && saved.Name) || payload.Name;
      let after = Promise.resolve();
      if (refreshGuideAfter) {
        after = api('POST', 'Guide/Refresh', undefined, undefined, true)
          .then(() => toast('Saved “' + name + '” and requested a guide refresh.'))
          .catch(err => alertErrors(err, 'Station saved, but the guide refresh failed'));
      } else {
        toast('Saved “' + name + '”.');
      }
      return after.then(() => {
        showView('list');
        return loadStations();
      });
    }).catch(err => {
      readErrors(err).then(messages => renderErrorBox(box, messages));
    }).then(() => loading(false));
  }

  function recompileStation() {
    if (isNewDraft()) {
      return;
    }
    const id = state.draft.Id;
    loading(true);
    api('POST', 'Stations/' + id + '/Recompile').then(result => {
      if (state.detail) {
        state.detail.Compiled = result;
      }
      if (result && result.Revision) {
        state.draft.Revision = result.Revision;
      }
      renderExcluded();
      const excluded = result && result.Excluded ? result.Excluded.length : 0;
      toast('Recompiled: ' + (result ? result.ItemCount : 0) + ' items, ' + excluded + ' excluded.' +
        (result && result.ContentChanged ? ' Content changed – revision ' + result.Revision + '.' : ' Content unchanged.'));
    }).catch(err => alertErrors(err, 'Recompile failed')).then(() => loading(false));
  }

  function renderExcluded() {
    const list = clear($('#tvsExcludedList'));
    const info = $('#tvsExcludedInfo');
    const compiled = state.detail && state.detail.Compiled;
    if (isNewDraft()) {
      info.textContent = 'The station has not been compiled yet. Save it to see which items (if any) are skipped.';
      return;
    }
    if (!compiled) {
      info.textContent = 'No compiled content yet. Use “Recompile content” to expand the sources.';
      return;
    }
    const excluded = compiled.Excluded || [];
    if (!excluded.length) {
      info.textContent = 'All ' + compiled.ItemCount + ' items compiled successfully (revision ' + compiled.Revision + ', ' + fmtDateTime(compiled.CompiledUtc) + ').';
      return;
    }
    info.textContent = excluded.length + ' item' + (excluded.length === 1 ? ' is' : 's are') +
      ' skipped. Refresh metadata for these items in Jellyfin (or fix their files) and recompile; until then they never air. Compiled ' + fmtDateTime(compiled.CompiledUtc) + '.';
    excluded.forEach(x => {
      list.appendChild(el('div', { className: 'listItem' }, [
        el('span', { className: 'material-icons listItemIcon warning', 'aria-hidden': 'true' }),
        el('div', { className: 'listItemBody two-line' }, [
          el('div', { className: 'listItemBodyText', text: x.Name || x.ItemId }),
          el('div', { className: 'listItemBodyText secondary', text: EXCLUSION_TEXT[x.Reason] || x.Reason })
        ])
      ]));
    });
  }

  function loadContent() {
    const payload = readDraft();
    const container = $('#tvsContent');
    clear(container).appendChild(el('div', { className: 'tvs-info', text: 'Expanding sources…' }));
    api('POST', 'Schedule/PreviewItems', payload, { limit: 5000 }).then(res => {
      state.content = res;
      renderContent();
    }).catch(err => {
      readErrors(err).then(messages => {
        clear(container);
        const warn = el('div', { className: 'tvs-warn' }, [el('strong', { text: 'Could not load content: ' })]);
        warn.appendChild(el('ul', null, messages.map(m => el('li', { text: m }))));
        container.appendChild(warn);
      });
    });
  }

  function isExcluded(id) {
    return state.draft.ExcludedItemIds.indexOf(id) >= 0;
  }

  function setExcluded(id, excluded) {
    const list = state.draft.ExcludedItemIds;
    const idx = list.indexOf(id);
    if (excluded && idx < 0) {
      list.push(id);
    } else if (!excluded && idx >= 0) {
      list.splice(idx, 1);
    }
  }

  function renderContent() {
    const container = $('#tvsContent');
    const summary = $('#tvsContentSummary');
    const res = state.content;
    if (!res) {
      return;
    }
    clear(container);
    const q = (val('#tvsContentFilter') || '').trim().toLowerCase();
    const rows = res.Items.filter(r => {
      if (!q) {
        return true;
      }
      const hay = [r.Title, r.EpisodeTitle, r.Rating, r.Source, String(r.Year || ''), (r.Genres || []).join(' ')].join(' ').toLowerCase();
      return hay.indexOf(q) >= 0;
    });
    const excludedNow = res.Items.filter(r => isExcluded(r.ItemId)).length;
    const sources = Object.keys(res.BySource || {}).map(k => k + ' ' + res.BySource[k]).join(' · ');
    summary.textContent = res.Total + ' item' + (res.Total === 1 ? '' : 's') + ' · ' + res.UniqueHours + ' h of unique content'
      + (res.CycleHours !== null && res.CycleHours !== undefined ? ' · ' + res.CycleHours + ' h per cycle' : '')
      + (excludedNow ? ' · ' + excludedNow + ' excluded' : '') + (sources ? ' · ' + sources : '')
      + (res.Total > res.Items.length ? ' · showing first ' + res.Items.length : '');
    if (!rows.length) {
      container.appendChild(el('div', { className: 'tvs-info', text: res.Items.length ? 'No items match the filter.' : 'This station expands to no items. Check its sources and filters.' }));
      return;
    }
    const table = el('table', { className: 'tvs-table' });
    table.appendChild(el('thead', null, el('tr', null, [
      el('th', { text: 'Title' }), el('th', { text: 'Year' }), el('th', { text: 'Rating' }), el('th', { text: 'Length' }),
      el('th', { text: 'Genres' }), el('th', { text: 'Source' }), el('th', { text: '' })
    ])));
    const tbody = el('tbody');
    rows.forEach(r => {
      const excluded = isExcluded(r.ItemId) || !!r.ExcludedByRule;
      const title = r.IsSeries
        ? r.Title + (r.SeasonNumber || r.EpisodeNumber ? ' S' + (r.SeasonNumber || 0) + 'E' + (r.EpisodeNumber || 0) : '') + (r.EpisodeTitle ? ' – ' + r.EpisodeTitle : '')
        : r.Title;
      const btn = r.ExcludedByRule
        ? el('span', { className: 'tvs-secondary', text: 'by tag', title: 'Dropped by an excluded tag (station or Settings). Remove the tag rule to include it.' })
        : el('button', { type: 'button', text: excluded ? 'Include' : 'Exclude', onClick: () => {
          setExcluded(r.ItemId, !excluded);
          renderContent();
        } });
      tbody.appendChild(el('tr', { className: excluded ? 'excluded' : '' }, [
        el('td', { text: title }),
        el('td', { className: 'nowrap', text: r.Year || '' }),
        el('td', { className: 'nowrap', text: r.Rating || '' }),
        el('td', { className: 'nowrap', text: r.Minutes + ' min' }),
        el('td', { text: (r.Genres || []).slice(0, 3).join(', ') }),
        el('td', { className: 'nowrap', text: r.Source }),
        el('td', { className: 'actions nowrap' }, btn)
      ]));
    });
    table.appendChild(tbody);
    container.appendChild(table);
  }

  function previewSchedule(hours) {
    const payload = readDraft();
    const container = $('#tvsPreview');
    clear(container).appendChild(el('div', { className: 'tvs-info', text: 'Building preview…' }));
    api('POST', 'Schedule/Preview', payload, { hours: hours }).then(entries => {
      renderSchedule(container, Array.isArray(entries) ? entries : [], hours);
    }).catch(err => {
      readErrors(err).then(messages => {
        clear(container);
        const warn = el('div', { className: 'tvs-warn' }, [el('strong', { text: 'Preview failed: ' })]);
        warn.appendChild(el('ul', null, messages.map(m => el('li', { text: m }))));
        container.appendChild(warn);
      });
    });
  }

  function renderSchedule(container, entries, hours) {
    clear(container);
    if (!entries.length) {
      container.appendChild(el('div', { className: 'tvs-info', text: 'Nothing is scheduled in the next ' + hours + ' hours. Check that the station has content.' }));
      return;
    }
    const now = Date.now();
    const table = el('table', { className: 'tvs-table' });
    table.appendChild(el('thead', null, el('tr', null, [
      el('th', { text: 'Start' }), el('th', { text: 'End' }), el('th', { text: 'Length' }),
      el('th', { text: 'Program' }), el('th', { text: 'Source' })
    ])));
    const tbody = el('tbody');
    let lastDay = null;
    let programs = 0;
    let slates = 0;
    entries.forEach(e => {
      const start = new Date(e.StartUtc);
      const end = new Date(e.EndUtc);
      const dayKey = start.toDateString();
      if (dayKey !== lastDay) {
        lastDay = dayKey;
        tbody.appendChild(el('tr', { className: 'dayhead' }, el('td', { colspan: 5, text: fmtDayHeading(start) })));
      }
      const isSlate = e.Kind === 'Slate';
      const isBumper = e.Kind === 'Bumper';
      if (isSlate) {
        slates++;
      } else if (!isBumper) {
        programs++;
      }
      const classes = [];
      if (isSlate) {
        classes.push('slate');
      }
      if (isBumper) {
        classes.push('bumper');
      }
      if (start.getTime() <= now && now < end.getTime()) {
        classes.push('now');
      }
      const programCell = el('td', null, el('span', { text: entryTitle(e) }));
      if (e.IsTruncated) {
        programCell.appendChild(badge('cut off', 'warn'));
      }
      if (classes.indexOf('now') >= 0) {
        programCell.appendChild(badge('On air', 'live'));
      }
      tbody.appendChild(el('tr', { className: classes.join(' ') }, [
        el('td', { className: 'nowrap', text: fmtTime(start) }),
        el('td', { className: 'nowrap', text: fmtTime(end) }),
        el('td', { className: 'nowrap', text: fmtDur((end - start) / 1000) }),
        programCell,
        el('td', { className: 'tvs-secondary', text: e.SourceLabel || '' })
      ]));
    });
    table.appendChild(tbody);
    container.appendChild(el('div', { className: 'tvs-info', text: programs + ' program' + (programs === 1 ? '' : 's') + (slates ? ', ' + slates + ' station break' + (slates === 1 ? '' : 's') : '') + ' in the next ' + hours + ' hours.' }));
    container.appendChild(table);
  }

  // ------------------------------------------------------------------------------------------
  // Slots: blocks
  // ------------------------------------------------------------------------------------------

  function newBlock() {
    return {
      _key: 'b' + (++state.blockSeq),
      Name: '',
      Days: ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'],
      StartLocal: '20:00',
      DurationMinutes: 60,
      Sources: [],
      Order: 'Sequential',
      Overflow: 'FillerRemainder'
    };
  }

  function blockSummary(b) {
    const days = DAYS.filter(d => (b.Days || []).indexOf(d) >= 0);
    let dayText;
    if (days.length === 7) {
      dayText = 'Every day';
    } else if (days.length === 5 && days.indexOf('Saturday') < 0 && days.indexOf('Sunday') < 0) {
      dayText = 'Weekdays';
    } else if (days.length === 2 && days.indexOf('Saturday') >= 0 && days.indexOf('Sunday') >= 0) {
      dayText = 'Weekends';
    } else if (!days.length) {
      dayText = 'No days';
    } else {
      dayText = days.map(d => DAY_SHORT[d]).join(', ');
    }
    const startMin = parseHm(b.StartLocal);
    const dur = parseInt(b.DurationMinutes, 10) || 0;
    let timeText = b.StartLocal || '??:??';
    if (startMin !== null && dur > 0) {
      const endMin = (startMin + dur) % 1440;
      timeText += '–' + pad2(Math.floor(endMin / 60)) + ':' + pad2(endMin % 60) + (startMin + dur >= 1440 ? ' (+1d)' : '');
    }
    const sources = (b.Sources || []).length;
    return dayText + ' · ' + timeText + ' · ' + fmtDur(dur * 60) + ' · ' + sources + ' source' + (sources === 1 ? '' : 's');
  }

  function renderBlocks() {
    const wrap = clear($('#tvsBlocks'));
    const blocks = state.draft.Slots.Blocks;
    show($('#tvsBlocksEmpty'), blocks.length === 0);
    blocks.forEach((b, i) => wrap.appendChild(blockCard(b, i)));
    renderBlockWarnings();
  }

  function blockCard(b, index) {
    const blocks = state.draft.Slots.Blocks;
    const open = !!state.expandedBlocks[b._key];
    const card = el('div', { className: 'tvs-card' + (open ? ' open' : '') });

    const titleBox = el('div', { className: 'title' }, [
      el('div', { className: 'name tvs-ellipsis', text: b.Name || 'Untitled block' }),
      el('div', { className: 'tvs-secondary tvs-ellipsis', text: blockSummary(b) })
    ]);
    const head = el('div', { className: 'tvs-card-head' }, [
      el('span', { className: 'material-icons expand_more', 'aria-hidden': 'true' }),
      titleBox
    ]);
    head.addEventListener('click', () => {
      state.expandedBlocks[b._key] = !state.expandedBlocks[b._key];
      renderBlocks();
    });
    const stop = fn => ev => {
      ev.stopPropagation();
      fn();
    };
    head.appendChild(iconButton('arrow_upward', 'Move up', stop(() => {
      if (index > 0) {
        blocks.splice(index - 1, 0, blocks.splice(index, 1)[0]);
        renderBlocks();
      }
    })));
    head.appendChild(iconButton('arrow_downward', 'Move down', stop(() => {
      if (index < blocks.length - 1) {
        blocks.splice(index + 1, 0, blocks.splice(index, 1)[0]);
        renderBlocks();
      }
    })));
    head.appendChild(iconButton('bookmark', 'Save as template (under the block name)', stop(() => saveBlockTemplate(b))));
    head.appendChild(iconButton('delete', 'Remove block', stop(() => {
      blocks.splice(index, 1);
      delete state.expandedBlocks[b._key];
      renderBlocks();
    })));
    card.appendChild(head);

    if (!open) {
      return card;
    }

    const p = 'tvsBlk' + index + '_';
    const body = el('div', { className: 'tvs-card-body' });
    body.innerHTML =
      '<div class="tvs-grid2">' +
        inputHtml(p + 'name', 'Block name', 'text', {}, 'Shown as the source label in the guide preview.') +
        inputHtml(p + 'start', 'Start time (station local)', 'time', { step: 60 }) +
        inputHtml(p + 'dur', 'Duration (minutes)', 'number', { min: 1, max: 1440, step: 1 }) +
        selectHtml(p + 'order', 'Play order', [['Sequential', 'Sequential – continue where the last occurrence left off'], ['Shuffle', 'Shuffle – re-rolled every occurrence'], ['ShuffleEpisodesInOrder', 'Shuffle shows, episodes in order – re-rolled every occurrence']]) +
        selectHtml(p + 'overflow', 'When the next item does not fit', [['FillerRemainder', 'Hand the remainder to ' + betweenBlocksName()], ['TruncateLast', 'Play it anyway and cut it off at the block end']]) +
      '</div>' +
      '<div class="tvs-days-label">Days</div>' +
      '<div class="tvs-days">' +
        DAYS.map(d => '<label class="tvs-day"><input type="checkbox" data-day="' + d + '"><span>' + DAY_SHORT[d] + '</span></label>').join('') +
      '</div>' +
      '<div class="tvs-days-label">Content</div>' +
      '<div class="tvs-block-picker"></div>';

    const q = id => body.querySelector('#' + p + id);
    q('name').value = b.Name || '';
    q('start').value = b.StartLocal || '';
    q('dur').value = b.DurationMinutes;
    q('order').value = b.Order || 'Sequential';
    q('overflow').value = b.Overflow || 'FillerRemainder';

    const refreshHead = () => {
      titleBox.firstChild.textContent = b.Name || 'Untitled block';
      titleBox.lastChild.textContent = blockSummary(b);
      renderBlockWarnings();
    };
    q('name').addEventListener('input', () => { b.Name = q('name').value; refreshHead(); });
    q('start').addEventListener('change', () => { b.StartLocal = q('start').value; refreshHead(); });
    q('dur').addEventListener('input', () => { b.DurationMinutes = parseInt(q('dur').value, 10) || 0; refreshHead(); });
    q('order').addEventListener('change', () => { b.Order = q('order').value; });
    q('overflow').addEventListener('change', () => { b.Overflow = q('overflow').value; });

    $all('input[data-day]', body).forEach(cb => {
      const day = cb.getAttribute('data-day');
      cb.checked = (b.Days || []).indexOf(day) >= 0;
      cb.parentNode.classList.toggle('active', cb.checked);
      cb.addEventListener('change', () => {
        const set = new Set(b.Days || []);
        if (cb.checked) {
          set.add(day);
        } else {
          set.delete(day);
        }
        b.Days = DAYS.filter(d => set.has(d));
        cb.parentNode.classList.toggle('active', cb.checked);
        refreshHead();
      });
    });

    createPicker({
      container: body.querySelector('.tvs-block-picker'),
      mode: 'sources',
      getSources: () => b.Sources,
      onAdd: source => { b.Sources.push(source); refreshHead(); },
      onRemove: i => { b.Sources.splice(i, 1); refreshHead(); }
    });

    card.appendChild(body);
    return card;
  }

  /** Finds blocks whose weekly occurrences overlap (wrapping across midnight and the week end). */
  function computeBlockOverlaps(blocks) {
    const WEEK = 7 * 1440;
    const occurrences = [];
    blocks.forEach((b, bi) => {
      const start = parseHm(b.StartLocal);
      const dur = parseInt(b.DurationMinutes, 10) || 0;
      if (start === null || dur <= 0) {
        return;
      }
      (b.Days || []).forEach(day => {
        const di = DAYS.indexOf(day);
        if (di >= 0) {
          occurrences.push({ bi: bi, day: day, start: di * 1440 + start, end: di * 1440 + start + dur });
        }
      });
    });
    const seen = {};
    const warnings = [];
    for (let i = 0; i < occurrences.length; i++) {
      for (let j = i + 1; j < occurrences.length; j++) {
        const a = occurrences[i];
        const c = occurrences[j];
        if (a.bi === c.bi) {
          continue;
        }
        let overlap = false;
        for (let shift = -WEEK; shift <= WEEK && !overlap; shift += WEEK) {
          overlap = a.start < c.end + shift && c.start + shift < a.end;
        }
        if (!overlap) {
          continue;
        }
        const key = Math.min(a.bi, c.bi) + ':' + Math.max(a.bi, c.bi) + ':' + a.day;
        if (seen[key]) {
          continue;
        }
        seen[key] = true;
        const nameA = blocks[a.bi].Name || 'Block ' + (a.bi + 1);
        const nameC = blocks[c.bi].Name || 'Block ' + (c.bi + 1);
        warnings.push('“' + nameA + '” and “' + nameC + '” overlap on ' + a.day + '. The earlier block in the list wins; the other is cut.');
      }
    }
    return warnings;
  }

  function renderBlockWarnings() {
    const box = clear($('#tvsBlockWarnings'));
    const blocks = state.draft.Slots.Blocks;
    const warnings = computeBlockOverlaps(blocks);
    blocks.forEach((b, i) => {
      const label = b.Name ? '“' + b.Name + '”' : 'Block ' + (i + 1);
      if (!(b.Days || []).length) {
        warnings.push(label + ' has no days selected and will never air.');
      }
      if (!(b.Sources || []).length) {
        warnings.push(label + ' has no content and will play ' + betweenBlocksName() + ' instead.');
      }
    });
    if (!warnings.length) {
      return;
    }
    box.appendChild(el('div', { className: 'tvs-warn' }, el('ul', null, warnings.map(w => el('li', { text: w })))));
  }

  // ------------------------------------------------------------------------------------------
  // Block templates
  // ------------------------------------------------------------------------------------------

  function loadBlockTemplates() {
    return api('GET', 'BlockTemplates').then(list => {
      state.blockTemplates = Array.isArray(list) ? list : [];
      renderBlockTemplateSelect();
    }).catch(() => {
      state.blockTemplates = [];
      renderBlockTemplateSelect();
    });
  }

  function renderBlockTemplateSelect() {
    const select = $('#tvsBlockTemplateSelect');
    if (!select) {
      return;
    }
    clear(select).appendChild(el('option', { value: '', text: state.blockTemplates.length ? 'Blank block' : 'Blank block (no templates saved)' }));
    state.blockTemplates.forEach(t => select.appendChild(el('option', { value: t.Id, text: 'Template: ' + t.Name + ' – ' + blockSummary(t) })));
  }

  function blockFromTemplate(t) {
    const block = {
      _key: 'b' + (++state.blockSeq),
      Name: t.Name || '',
      Days: (t.Days || []).slice(),
      StartLocal: t.StartLocal || '20:00',
      DurationMinutes: parseInt(t.DurationMinutes, 10) || 60,
      Sources: JSON.parse(JSON.stringify(t.Sources || [])),
      Order: t.Order || 'Sequential',
      Overflow: t.Overflow || 'FillerRemainder'
    };
    block.Sources.forEach(normalizeSource);
    return block;
  }

  function saveBlockTemplate(b) {
    const name = (b.Name || '').trim();
    if (!name) {
      toast('Give the block a name first; the template is saved under it.');
      return;
    }
    const payload = toPayload({ Slots: { Blocks: [b] } }).Slots.Blocks[0];
    delete payload.Id;
    api('POST', 'BlockTemplates', payload).then(list => {
      state.blockTemplates = Array.isArray(list) ? list : [];
      renderBlockTemplateSelect();
      toast('Template “' + name + '” saved.');
    }).catch(err => alertErrors(err, 'Could not save template'));
  }

  function renderSettingsTemplates(templates) {
    const list = clear($('#tvsTemplateList'));
    show($('#tvsTemplateEmpty'), !templates.length);
    templates.forEach(t => {
      const row = el('div', { className: 'listItem' }, [
        el('span', { className: 'material-icons listItemIcon bookmark', 'aria-hidden': 'true' }),
        el('div', { className: 'listItemBody two-line' }, [
          el('div', { className: 'listItemBodyText', text: t.Name }),
          el('div', { className: 'listItemBodyText secondary', text: blockSummary(t) + ' · ' + (t.Order || 'Sequential') })
        ])
      ]);
      row.appendChild(iconButton('delete', 'Delete template', () => {
        api('DELETE', 'BlockTemplates/' + t.Id, null, null, true).then(() => {
          state.settings.BlockTemplates = (state.settings.BlockTemplates || []).filter(x => x.Id !== t.Id);
          renderSettingsTemplates(state.settings.BlockTemplates);
          toast('Template deleted.');
        }).catch(err => alertErrors(err, 'Could not delete template'));
      }));
      list.appendChild(row);
    });
  }

  // ------------------------------------------------------------------------------------------
  // Suggestions
  // ------------------------------------------------------------------------------------------

  function openSuggestions(refresh) {
    loading(true);
    api('GET', 'Suggestions', null, suggestQuery(refresh)).then(list => {
      state.suggestions = Array.isArray(list) ? list : [];
      renderSuggestions();
      showView('suggest');
    }).catch(err => alertErrors(err, 'Could not build suggestions')).then(() => loading(false));
  }

  /** Query for the suggestions endpoint: optional re-scan and the lowered thresholds for small libraries. */
  function suggestQuery(refresh) {
    const q = {};
    if (refresh) { q.refresh = true; }
    if (checked('#tvsSuggestSmall')) { q.minHours = 1; q.minEpisodes = 5; }
    return q;
  }

  function renderSuggestions() {
    const box = clear($('#tvsSuggestList'));
    renderErrorBox($('#tvsSuggestError'), null);
    const list = state.suggestions;
    show($('#tvsSuggestEmpty'), list.length === 0);
    const categories = [];
    list.forEach(s => {
      if (categories.indexOf(s.Category) < 0) {
        categories.push(s.Category);
      }
    });
    categories.forEach(cat => {
      box.appendChild(el('h3', { className: 'sectionTitle', text: cat }));
      list.filter(s => s.Category === cat).forEach(s => {
        const cb = el('input', { type: 'checkbox', 'data-key': s.Key });
        cb.disabled = !!s.Exists;
        const row = el('div', { className: 'listItem' }, [
          el('label', { className: 'tvs-suggest-pick' }, cb),
          el('div', { className: 'listItemBody two-line' }, [
            el('div', { className: 'listItemBodyText' }, [
              el('span', { text: s.Name }),
              badge(MODE_LABEL[s.Station.Mode] || s.Station.Mode, 'on'),
              s.Exists ? badge('Already exists', 'off') : null
            ]),
            el('div', { className: 'listItemBodyText secondary', text: s.Hours + ' h · ' + s.Description })
          ])
        ]);
        box.appendChild(row);
      });
    });
    updateSuggestCount();
  }

  function selectedSuggestionKeys() {
    return $all('#tvsSuggestList input[type=checkbox]:checked').map(c => c.getAttribute('data-key'));
  }

  function updateSuggestCount() {
    const n = selectedSuggestionKeys().length;
    $('#tvsBtnSuggestCreate').disabled = n === 0;
    $('#tvsSuggestCount').textContent = n ? n + ' selected' : '';
  }

  function createSelectedSuggestions() {
    const keys = selectedSuggestionKeys();
    if (!keys.length) {
      return;
    }
    loading(true);
    const q = suggestQuery(false);
    api('POST', 'Suggestions/Create', { Keys: keys, TimeZoneId: guessTimeZone(), MinHours: q.minHours || null, MinEpisodes: q.minEpisodes || null }).then(res => {
      const parts = [];
      if (res.Created.length) { parts.push('created ' + res.Created.length); }
      if (res.Errors.length) { parts.push(res.Errors.length + ' failed'); }
      toast('Suggestions: ' + (parts.join(', ') || 'nothing to do') + '.');
      return loadStations().then(() => api('GET', 'Suggestions', null, suggestQuery(false))).then(list => {
        state.suggestions = Array.isArray(list) ? list : [];
        renderSuggestions();
        renderErrorBox($('#tvsSuggestError'), res.Errors.length ? res.Errors : null);
      });
    }).catch(err => alertErrors(err, 'Could not create stations')).then(() => loading(false));
  }

  // ------------------------------------------------------------------------------------------
  // Lineup
  // ------------------------------------------------------------------------------------------

  function addLineupItems(items) {
    items.forEach(item => {
      if (item.RuntimeTicks) {
        state.runtimes[item.Id] = item.RuntimeTicks / 1e7;
      } else if (state.runtimes[item.Id] === undefined) {
        state.runtimes[item.Id] = null;
      }
      state.draft.Lineup.Items.push({ ItemId: item.Id, DisplayName: itemDisplayName(item) });
    });
    renderLineup();
  }

  function renderLineup() {
    const list = clear($('#tvsLineupList'));
    const items = state.draft.Lineup.Items;
    show($('#tvsLineupEmpty'), items.length === 0);
    let total = 0;
    let unknown = 0;
    items.forEach((item, index) => {
      const seconds = state.runtimes[item.ItemId];
      if (typeof seconds === 'number') {
        total += seconds;
      } else {
        unknown++;
      }
      const row = el('div', { className: 'listItem' }, [
        el('div', { className: 'tvs-index', text: String(index + 1) }),
        el('div', { className: 'listItemBody two-line' }, [
          el('div', { className: 'listItemBodyText tvs-ellipsis', text: item.DisplayName || item.ItemId }),
          el('div', { className: 'listItemBodyText secondary', text: typeof seconds === 'number' ? fmtDur(seconds) : (seconds === undefined ? 'Loading runtime…' : 'Runtime unknown') })
        ]),
        iconButton('arrow_upward', 'Move up', () => {
          if (index > 0) {
            items.splice(index - 1, 0, items.splice(index, 1)[0]);
            renderLineup();
          }
        }),
        iconButton('arrow_downward', 'Move down', () => {
          if (index < items.length - 1) {
            items.splice(index + 1, 0, items.splice(index, 1)[0]);
            renderLineup();
          }
        }),
        iconButton('delete', 'Remove', () => {
          items.splice(index, 1);
          renderLineup();
        })
      ]);
      list.appendChild(row);
    });

    const totalBox = $('#tvsLineupTotal');
    if (!items.length) {
      totalBox.textContent = '';
      return;
    }
    let text = items.length + ' item' + (items.length === 1 ? '' : 's') + ' · loop length ' + fmtDur(total);
    if (paddingGridMinutes() > 0) {
      text += ' before padding';
    }
    if (unknown > 0) {
      text += ' (' + unknown + ' with unknown runtime not included)';
    }
    const cycleSeconds = state.detail && state.detail.CycleDuration && state.draft.Mode === 'Lineup' ? parseTimeSpan(state.detail.CycleDuration) : null;
    if (cycleSeconds !== null && cycleSeconds !== undefined) {
      text += '. Compiled loop length (saved version, with padding): ' + fmtDur(cycleSeconds) + '.';
    }
    totalBox.textContent = text;
  }

  /** Fills runtimes for lineup items loaded from the server, using Jellyfin's item endpoint. */
  function hydrateLineupRuntimes() {
    const items = state.draft.Lineup.Items;
    const missing = Array.from(new Set(items.map(i => i.ItemId).filter(id => id && state.runtimes[id] === undefined)));
    if (!missing.length) {
      return;
    }
    if (typeof ApiClient.getItem !== 'function') {
      missing.forEach(id => { state.runtimes[id] = null; });
      renderLineup();
      return;
    }
    const draft = state.draft;
    const userId = ApiClient.getCurrentUserId();
    let cursor = 0;
    const next = () => {
      if (cursor >= missing.length) {
        if (state.draft === draft) {
          renderLineup();
        }
        return Promise.resolve();
      }
      const batch = missing.slice(cursor, cursor + 8);
      cursor += 8;
      return Promise.all(batch.map(id =>
        ApiClient.getItem(userId, id)
          .then(item => { state.runtimes[id] = item && item.RunTimeTicks ? item.RunTimeTicks / 1e7 : null; })
          .catch(() => { state.runtimes[id] = null; })
      )).then(next);
    };
    next();
  }

  // ------------------------------------------------------------------------------------------
  // Content picker (reusable)
  // ------------------------------------------------------------------------------------------

  const pickerCache = { genres: null, tags: null, libraries: null };

  function loadTags() {
    if (pickerCache.tags) {
      return Promise.resolve(pickerCache.tags);
    }
    return api('GET', 'Library/Tags').then(list => {
      pickerCache.tags = Array.isArray(list) ? list : [];
      return pickerCache.tags;
    });
  }

  function loadGenres() {
    if (pickerCache.genres) {
      return Promise.resolve(pickerCache.genres);
    }
    return api('GET', 'Library/Genres').then(list => {
      pickerCache.genres = Array.isArray(list) ? list : [];
      return pickerCache.genres;
    });
  }

  function loadLibraries() {
    if (pickerCache.libraries) {
      return Promise.resolve(pickerCache.libraries);
    }
    return api('GET', 'Library/Libraries').then(list => {
      pickerCache.libraries = Array.isArray(list) ? list : [];
      return pickerCache.libraries;
    });
  }

  function mapSourceKind(itemKind) {
    return itemKind === 'Video' ? 'Movie' : itemKind;
  }

  function sourceKey(source) {
    return source.Kind + ':' + (source.Kind === 'Genre' ? String(source.Genre || '').toLowerCase() : source.Kind === 'Tag' ? String(source.Tag || '').toLowerCase() : (source.ItemId || ''));
  }

  /** Expands a picked library item to its leaf playables (movies / episodes), depth-limited. */
  function expandToLeaves(item, depth) {
    const kind = item.Kind;
    if (kind === 'Movie' || kind === 'Episode' || kind === 'Video') {
      return Promise.resolve([item]);
    }
    if (depth > 3) {
      return Promise.resolve([]);
    }
    return api('GET', 'Library/Items/' + item.Id + '/Children').then(children => {
      const list = Array.isArray(children) ? children : [];
      return Promise.all(list.map(c => expandToLeaves(c, depth + 1))).then(groups => [].concat.apply([], groups));
    });
  }

  /**
   * opts.container  – element to render into (cleared)
   * opts.mode       – 'sources' (produces ContentSource via onAdd/getSources/onRemove)
   *                   or 'items' (produces leaf LibraryItemDto[] via onAddItems)
   */
  function createPicker(opts) {
    const container = clear(opts.container);
    const mode = opts.mode || 'sources';
    const itemKinds = mode === 'items'
      ? ['Movie', 'Series', 'Season', 'Episode', 'BoxSet', 'Playlist']
      : ['Movie', 'Series', 'BoxSet', 'Playlist'];
    const filter = { kinds: itemKinds.slice(), special: null };
    let lastResults = [];
    let searched = false;
    let requestSeq = 0;

    container.classList.add('tvs-picker');
    const chips = el('div', { className: 'tvs-chips tvs-selected' });
    const kindRow = el('div', { className: 'tvs-chips tvs-kinds' });
    const input = el('input', {
      type: 'text', className: 'emby-input', autocomplete: 'off',
      placeholder: mode === 'items' ? 'Search movies, series, seasons, episodes, collections, playlists…' : 'Search the library…'
    });
    const inputWrap = el('div', { className: 'inputContainer' }, input);
    const status = el('div', { className: 'tvs-info' });
    const results = el('div', { className: 'tvs-results tvs-hidden' });

    if (mode === 'sources') {
      container.appendChild(chips);
    }
    container.appendChild(kindRow);
    container.appendChild(inputWrap);
    container.appendChild(status);
    container.appendChild(results);

    function kindChip(label, active, onClick) {
      const chip = el('span', { className: 'tvs-chip toggle' + (active ? ' active' : ''), text: label, role: 'button', tabindex: '0' });
      chip.addEventListener('click', onClick);
      chip.addEventListener('keydown', ev => {
        if (ev.key === 'Enter' || ev.key === ' ') {
          ev.preventDefault();
          onClick();
        }
      });
      return chip;
    }

    function renderKinds() {
      clear(kindRow);
      itemKinds.forEach(kind => {
        const active = filter.special === null && filter.kinds.indexOf(kind) >= 0;
        kindRow.appendChild(kindChip(KIND_LABEL[kind], active, () => {
          if (filter.special) {
            filter.special = null;
            filter.kinds = [kind];
          } else if (filter.kinds.indexOf(kind) >= 0) {
            filter.kinds = filter.kinds.filter(k => k !== kind);
            if (!filter.kinds.length) {
              filter.kinds = itemKinds.slice();
            }
          } else {
            filter.kinds.push(kind);
          }
          renderKinds();
          search();
        }));
      });
      if (mode === 'sources') {
        ['Genre', 'Tag', 'Library'].forEach(special => {
          kindRow.appendChild(kindChip(KIND_LABEL[special], filter.special === special, () => {
            filter.special = filter.special === special ? null : special;
            if (filter.special === null) {
              filter.kinds = itemKinds.slice();
            }
            input.placeholder = filter.special === 'Genre' ? 'Filter genres…' : filter.special === 'Tag' ? 'Filter tags…' : filter.special === 'Library' ? 'Filter libraries…' : 'Search the library…';
            renderKinds();
            search();
          }));
        });
      }
    }

    let editing = -1;

    function renderChips() {
      if (mode !== 'sources') {
        return;
      }
      clear(chips);
      const sources = opts.getSources() || [];
      if (!sources.length) {
        editing = -1;
        chips.appendChild(el('span', { className: 'tvs-secondary', text: 'No content selected yet. Search below and press Add.' }));
        return;
      }
      if (editing >= sources.length) {
        editing = -1;
      }
      sources.forEach((source, index) => {
        normalizeSource(source);
        const badge = sourceBadge(source);
        const children = [
          el('span', { className: 'kind', text: KIND_LABEL[source.Kind] || source.Kind }),
          el('span', { className: 'label', text: source.DisplayName || source.Genre || source.Tag || source.ItemId || '?' })
        ];
        if (badge) {
          children.push(el('span', { className: 'badge', text: badge }));
        }
        children.push(el('button', { type: 'button', title: 'Tune this source', 'aria-label': 'Tune', text: '✎', onClick: () => {
          editing = editing === index ? -1 : index;
          renderChips();
        } }));
        children.push(el('button', { type: 'button', title: 'Remove', 'aria-label': 'Remove', text: '×', onClick: () => {
          opts.onRemove(index);
          editing = -1;
          renderChips();
          renderResults(lastResults);
        } }));
        const chip = el('span', { className: 'tvs-chip' + (editing === index ? ' editing' : '') }, children);
        chips.appendChild(chip);
        if (editing === index) {
          chips.appendChild(renderSourceEditor(source));
        }
      });
    }

    /** Inline editor for one source: season range / specials, movies-shows filter, weight. */
    function renderSourceEditor(source) {
      const box = el('div', { className: 'tvs-source-editor' });
      const name = source.DisplayName || source.Genre || source.Tag || '';
      box.appendChild(el('div', { className: 'title', text: 'Tune “' + name + '”' }));

      function numberField(label, value, onChange) {
        const input = el('input', { type: 'number', min: '1', max: '9999', step: '1', className: 'emby-input', placeholder: 'any' });
        input.value = value || '';
        input.addEventListener('change', () => {
          const n = parseInt(input.value, 10);
          onChange(n > 0 ? n : null);
          renderChips();
        });
        return el('label', { className: 'f' }, [el('span', { text: label }), input]);
      }

      if (source.Kind === 'Series') {
        box.appendChild(numberField('From season', source.SeasonFrom, n => { source.SeasonFrom = n; }));
        box.appendChild(numberField('To season', source.SeasonTo, n => { source.SeasonTo = n; }));
        const specials = el('input', { type: 'checkbox', className: 'emby-checkbox' });
        specials.checked = !!source.ExcludeSpecials;
        specials.addEventListener('change', () => { source.ExcludeSpecials = specials.checked; renderChips(); });
        box.appendChild(el('label', { className: 'f row' }, [specials, el('span', { text: 'Skip specials (season 0)' })]));
      }

      if (hasKindFilter(source)) {
        const select = el('select', { className: 'emby-select' }, [
          el('option', { value: 'Both', text: 'Movies and shows' }),
          el('option', { value: 'Movie', text: 'Movies only' }),
          el('option', { value: 'Episode', text: 'Shows only' })
        ]);
        select.value = kindFilterValue(source);
        select.addEventListener('change', () => {
          source.ItemKinds = select.value === 'Both' ? [] : [select.value];
          renderChips();
        });
        box.appendChild(el('label', { className: 'f' }, [el('span', { text: 'Content' }), select]));
      }

      if (hasTuningFilters(source)) {
        const rating = el('select', { className: 'emby-select' }, RATING_CHOICES.map(c => el('option', { value: c[0], text: c[1] })));
        rating.value = RATING_ALIAS[source.MaxRating] || source.MaxRating || '';
        const unrated = el('input', { type: 'checkbox', className: 'emby-checkbox' });
        unrated.checked = !!source.AllowUnrated;
        const unratedLabel = el('label', { className: 'f row' }, [unrated, el('span', { text: 'Allow unrated' })]);
        unratedLabel.hidden = !source.MaxRating;
        rating.addEventListener('change', () => {
          source.MaxRating = rating.value || null;
          unratedLabel.hidden = !source.MaxRating;
          renderChips();
        });
        unrated.addEventListener('change', () => { source.AllowUnrated = unrated.checked; renderChips(); });
        box.appendChild(el('label', { className: 'f' }, [el('span', { text: 'Max rating' }), rating]));
        box.appendChild(unratedLabel);
        box.appendChild(numberField('From year', source.YearFrom, n => { source.YearFrom = n; }));
        box.appendChild(numberField('To year', source.YearTo, n => { source.YearTo = n; }));
      }

      const weight = el('select', { className: 'emby-select', title: 'How often this source appears compared with the others' });
      for (let w = 1; w <= 10; w++) {
        weight.appendChild(el('option', { value: String(w), text: w === 1 ? '×1 (normal)' : '×' + w }));
      }
      weight.value = String(source.Weight || 1);
      weight.addEventListener('change', () => { source.Weight = parseInt(weight.value, 10) || 1; renderChips(); });
      box.appendChild(el('label', { className: 'f' }, [el('span', { text: 'Weight' }), weight]));

      box.appendChild(el('button', { type: 'button', className: 'raised button-submit emby-button', text: 'Done', onClick: () => { editing = -1; renderChips(); } }));
      return box;
    }

    function isSelected(candidate) {
      if (mode !== 'sources') {
        return false;
      }
      const key = sourceKey(candidate);
      return (opts.getSources() || []).some(s => sourceKey(s) === key);
    }

    function search() {
      const seq = ++requestSeq;
      const q = input.value.trim();
      const qLower = q.toLowerCase();
      searched = true;
      status.textContent = 'Searching…';
      let promise;
      if (filter.special === 'Genre') {
        promise = loadGenres().then(genres => genres
          .filter(g => !qLower || String(g.Name).toLowerCase().indexOf(qLower) >= 0)
          .map(g => ({ special: 'Genre', Id: 'genre:' + g.Name, Name: g.Name, Kind: 'Genre', meta: g.MovieCount + ' movie' + (g.MovieCount === 1 ? '' : 's') + ' · ' + g.EpisodeCount + ' episode' + (g.EpisodeCount === 1 ? '' : 's') })));
      } else if (filter.special === 'Tag') {
        promise = loadTags().then(tags => tags
          .filter(t => !qLower || String(t.Name).toLowerCase().indexOf(qLower) >= 0)
          .map(t => ({ special: 'Tag', Id: 'tag:' + t.Name, Name: t.Name, Kind: 'Tag', meta: t.MovieCount + ' movie' + (t.MovieCount === 1 ? '' : 's') + ' · ' + t.SeriesCount + ' series' })));
      } else if (filter.special === 'Library') {
        promise = loadLibraries().then(libs => libs
          .filter(l => !qLower || String(l.Name).toLowerCase().indexOf(qLower) >= 0)
          .map(l => ({ special: 'Library', Id: l.Id, Name: l.Name, Kind: 'Library', meta: l.CollectionType ? 'Type: ' + l.CollectionType : 'Mixed content' })));
      } else {
        promise = api('GET', 'Library/Search', undefined, { q: q, kinds: filter.kinds.join(','), limit: 50 })
          .then(list => Array.isArray(list) ? list : []);
      }
      promise.then(list => {
        if (seq !== requestSeq) {
          return;
        }
        lastResults = list;
        renderResults(list);
      }).catch(err => {
        if (seq !== requestSeq) {
          return;
        }
        readErrors(err).then(messages => {
          status.textContent = 'Search failed: ' + messages.join(' ');
          show(results, false);
        });
      });
    }

    const debouncedSearch = debounce(search, 300);

    function renderResults(list) {
      clear(results);
      show(results, true);
      if (!list.length) {
        status.textContent = '';
        results.appendChild(el('div', { className: 'tvs-msg', text: searched ? 'No matches.' : '' }));
        return;
      }
      status.textContent = list.length + ' result' + (list.length === 1 ? '' : 's') + (list.length >= 50 && !filter.special ? ' (showing the first 50 – refine your search)' : '');
      list.forEach(item => {
        const row = el('div', { className: 'tvs-result' });
        const url = item.special ? null : imageUrl(item);
        if (url) {
          row.appendChild(el('img', { className: 'tvs-thumb', src: url, alt: '', loading: 'lazy' }));
        } else {
          row.appendChild(el('div', { className: 'tvs-thumb placeholder' }, el('span', { className: 'material-icons ' + thumbIcon(item.Kind), 'aria-hidden': 'true' })));
        }
        const body = el('div', { className: 'body' }, [
          el('div', { className: 'tvs-ellipsis' }, [
            el('span', { text: item.special ? item.Name : itemDisplayName(item) }),
            badge(KIND_LABEL[item.Kind] || item.Kind, 'kind')
          ]),
          el('div', { className: 'tvs-secondary tvs-ellipsis', text: item.special ? item.meta : itemMeta(item) })
        ]);
        row.appendChild(body);
        const selected = isSelected(toSource(item));
        const addButton = textButton(selected ? 'Added' : 'Add', () => add(item, addButton));
        addButton.disabled = selected;
        row.appendChild(addButton);
        results.appendChild(row);
      });
    }

    function thumbIcon(kind) {
      switch (kind) {
        case 'Movie': case 'Video': return 'movie';
        case 'Series': case 'Season': case 'Episode': return 'tv';
        case 'BoxSet': return 'collections';
        case 'Playlist': return 'playlist_play';
        case 'Genre': return 'theater_comedy';
        case 'Tag': return 'label';
        case 'Library': return 'folder';
        default: return 'videocam';
      }
    }

    function toSource(item) {
      if (item.special === 'Genre') {
        return { Kind: 'Genre', Genre: item.Name, ItemKinds: [], Weight: 1, DisplayName: item.Name };
      }
      if (item.special === 'Tag') {
        return { Kind: 'Tag', Tag: item.Name, ItemKinds: [], Weight: 1, DisplayName: item.Name };
      }
      if (item.special === 'Library') {
        return { Kind: 'Library', ItemId: item.Id, ItemKinds: [], Weight: 1, DisplayName: item.Name };
      }
      return { Kind: mapSourceKind(item.Kind), ItemId: item.Id, ItemKinds: [], Weight: 1, DisplayName: itemDisplayName(item) };
    }

    function add(item, button) {
      if (mode === 'sources') {
        const source = toSource(item);
        if (isSelected(source)) {
          return;
        }
        opts.onAdd(source);
        renderChips();
        renderResults(lastResults);
        return;
      }
      button.disabled = true;
      button.firstChild.textContent = 'Adding…';
      expandToLeaves(item, 0).then(leaves => {
        if (!leaves.length) {
          toast('“' + item.Name + '” contains no playable items.');
        } else {
          opts.onAddItems(leaves);
          toast('Added ' + leaves.length + ' item' + (leaves.length === 1 ? '' : 's') + ' to the lineup.');
        }
      }).catch(err => alertErrors(err, 'Could not add items')).then(() => {
        button.disabled = false;
        button.firstChild.textContent = 'Add';
      });
    }

    input.addEventListener('input', debouncedSearch);
    input.addEventListener('focus', () => {
      if (!searched) {
        search();
      }
    });
    input.addEventListener('keydown', ev => {
      if (ev.key === 'Enter') {
        ev.preventDefault();
        search();
      }
    });

    renderKinds();
    renderChips();
    return { refresh: () => { renderChips(); renderResults(lastResults); } };
  }

  // ------------------------------------------------------------------------------------------
  // Encoding profile form (shared by Settings and the per-station override)
  // ------------------------------------------------------------------------------------------

  function defaultEncoding() {
    return {
      Width: 1280, Height: 720, Fps: 30, VideoBitrateKbps: 3500, AudioBitrateKbps: 160,
      VideoEncoder: 'libx264', Preset: 'veryfast', H264Profile: 'high', H264Level: '4.1', GopSeconds: 2,
      ExtraInputArgs: '', ExtraOutputArgs: '', AudioLanguagePreference: 'eng'
    };
  }

  function createEncodingForm(container, prefix) {
    const p = prefix + '_';
    container.innerHTML =
      '<div class="tvs-grid2">' +
        selectHtml(p + 'res', 'Resolution', RESOLUTIONS, 'Every program is scaled (letterboxed) to this size.') +
        '<div class="tvs-grid2">' +
          inputHtml(p + 'width', 'Width', 'number', { min: 16, max: 7680, step: 2 }) +
          inputHtml(p + 'height', 'Height', 'number', { min: 16, max: 4320, step: 2 }) +
        '</div>' +
        inputHtml(p + 'fps', 'Frame rate (fps)', 'number', { min: 1, max: 120, step: 1 }, 'Constant output frame rate, typically 30 or 60.') +
        inputHtml(p + 'gop', 'Keyframe interval (seconds)', 'number', { min: 1, max: 30, step: 1 }, 'GOP length. Shorter makes channel changes faster at a small bitrate cost.') +
        inputHtml(p + 'vbr', 'Video bitrate (kbps)', 'number', { min: 100, max: 100000, step: 50 }, 'Target bitrate for the video stream.') +
        inputHtml(p + 'abr', 'Audio bitrate (kbps)', 'number', { min: 32, max: 1024, step: 8 }, 'AAC audio bitrate.') +
        selectHtml(p + 'enc', 'Video encoder', ENCODERS, 'Hardware encoders need the matching drivers and may need extra input arguments (e.g. -hwaccel).') +
        inputHtml(p + 'encCustom', 'Custom encoder name', 'text', {}, 'Exact ffmpeg encoder name, e.g. h264_amf or libx265.') +
        inputHtml(p + 'preset', 'Preset', 'text', {}, 'Encoder preset, e.g. veryfast (libx264) or p4 (nvenc).') +
        inputHtml(p + 'profile', 'H.264 profile', 'text', {}, 'baseline, main or high.') +
        inputHtml(p + 'level', 'H.264 level', 'text', {}, 'For example 4.1.') +
        inputHtml(p + 'lang', 'Preferred audio language', 'text', { maxlength: 3 }, 'ISO 639-2 code (eng, spa, jpn…) used to pick the audio track when compiling.') +
      '</div>' +
      inputHtml(p + 'inArgs', 'Extra input arguments', 'text', {}, 'Inserted before the input, e.g. -hwaccel cuda -hwaccel_output_format cuda.') +
      inputHtml(p + 'outArgs', 'Extra output arguments', 'text', {}, 'Appended after the output options.');

    const q = id => container.querySelector('#' + p + id);
    const encoderNames = ENCODERS.map(e => e[0]).filter(e => e !== 'custom');

    function syncResolutionSelect() {
      const key = q('width').value + 'x' + q('height').value;
      q('res').value = RESOLUTIONS.some(r => r[0] === key) ? key : 'custom';
    }

    function syncEncoderVisibility() {
      show(q('encCustom').parentNode, q('enc').value === 'custom');
    }

    q('res').addEventListener('change', () => {
      const value = q('res').value;
      if (value !== 'custom') {
        const parts = value.split('x');
        q('width').value = parts[0];
        q('height').value = parts[1];
      }
    });
    q('width').addEventListener('input', syncResolutionSelect);
    q('height').addEventListener('input', syncResolutionSelect);
    q('enc').addEventListener('change', syncEncoderVisibility);

    return {
      set(profile) {
        const e = Object.assign(defaultEncoding(), profile || {});
        q('width').value = e.Width;
        q('height').value = e.Height;
        syncResolutionSelect();
        q('fps').value = e.Fps;
        q('gop').value = e.GopSeconds;
        q('vbr').value = e.VideoBitrateKbps;
        q('abr').value = e.AudioBitrateKbps;
        if (encoderNames.indexOf(e.VideoEncoder) >= 0) {
          q('enc').value = e.VideoEncoder;
          q('encCustom').value = '';
        } else {
          q('enc').value = 'custom';
          q('encCustom').value = e.VideoEncoder || '';
        }
        syncEncoderVisibility();
        q('preset').value = e.Preset || '';
        q('profile').value = e.H264Profile || '';
        q('level').value = e.H264Level || '';
        q('lang').value = e.AudioLanguagePreference || '';
        q('inArgs').value = e.ExtraInputArgs || '';
        q('outArgs').value = e.ExtraOutputArgs || '';
      },
      get() {
        const encoder = q('enc').value === 'custom' ? q('encCustom').value.trim() : q('enc').value;
        return {
          Width: parseInt(q('width').value, 10) || 0,
          Height: parseInt(q('height').value, 10) || 0,
          Fps: parseInt(q('fps').value, 10) || 0,
          VideoBitrateKbps: parseInt(q('vbr').value, 10) || 0,
          AudioBitrateKbps: parseInt(q('abr').value, 10) || 0,
          VideoEncoder: encoder,
          Preset: q('preset').value.trim(),
          H264Profile: q('profile').value.trim(),
          H264Level: q('level').value.trim(),
          GopSeconds: parseInt(q('gop').value, 10) || 0,
          ExtraInputArgs: q('inArgs').value.trim(),
          ExtraOutputArgs: q('outArgs').value.trim(),
          AudioLanguagePreference: q('lang').value.trim()
        };
      }
    };
  }

  // ------------------------------------------------------------------------------------------
  // Settings
  // ------------------------------------------------------------------------------------------

  function openSettings() {
    loading(true);
    api('GET', 'Settings').then(settings => {
      state.settings = settings || {};
      renderSettings(state.settings);
      showView('settings');
    }).catch(err => alertErrors(err, 'Could not load settings')).then(() => loading(false));
  }

  function renderSettings(s) {
    renderErrorBox($('#tvsSettingsErrors'), null);
    if (!state.settingsEncoding) {
      state.settingsEncoding = createEncodingForm($('#tvsSettingsEncoding'), 'tvsGlEnc');
    }
    state.settingsEncoding.set(s.DefaultEncoding || defaultEncoding());
    setVal('#tvsLinger', s.LingerSeconds !== undefined ? s.LingerSeconds : 30);
    setVal('#tvsMaxSessions', s.MaxConcurrentSessions !== undefined ? s.MaxConcurrentSessions : 4);
    setVal('#tvsFfmpegPath', s.FfmpegPathOverride || '');
    setVal('#tvsMaxGuideDays', s.MaxGuideDays !== undefined ? s.MaxGuideDays : 21);
    const color = /^#[0-9a-f]{6}$/i.test(s.SlateBackgroundColor || '') ? s.SlateBackgroundColor : '#101010';
    setVal('#tvsSlateColor', color);
    $('#tvsSlateColorText').textContent = color;
    setChecked('#tvsShowText', s.ShowTextOnSlate !== false);
    setChecked('#tvsShowLogo', s.ShowLogoOnSlate !== false);
    setVal('#tvsGlobalExcludedTags', Array.isArray(s.ExcludedTags) ? s.ExcludedTags.join(', ') : '');
    renderSettingsTemplates(Array.isArray(s.BlockTemplates) ? s.BlockTemplates : []);
  }

  function saveSettings() {
    const payload = {
      DefaultEncoding: state.settingsEncoding.get(),
      LingerSeconds: parseInt(val('#tvsLinger'), 10),
      MaxGuideDays: parseInt(val('#tvsMaxGuideDays'), 10),
      FfmpegPathOverride: val('#tvsFfmpegPath').trim() || null,
      MaxConcurrentSessions: parseInt(val('#tvsMaxSessions'), 10),
      SlateBackgroundColor: val('#tvsSlateColor') || '#101010',
      ShowTextOnSlate: checked('#tvsShowText'),
      ShowLogoOnSlate: checked('#tvsShowLogo'),
      ExcludedTags: parseTagList(val('#tvsGlobalExcludedTags')),
      BlockTemplates: (state.settings && state.settings.BlockTemplates) || []
    };
    const errors = [];
    if (isNaN(payload.LingerSeconds)) {
      errors.push('Linger seconds must be a number.');
    }
    if (isNaN(payload.MaxGuideDays)) {
      errors.push('Max guide days must be a number.');
    }
    if (isNaN(payload.MaxConcurrentSessions)) {
      errors.push('Max concurrent sessions must be a number.');
    }
    const box = $('#tvsSettingsErrors');
    if (errors.length) {
      renderErrorBox(box, errors);
      return;
    }
    renderErrorBox(box, null);
    loading(true);
    api('PUT', 'Settings', payload).then(() => {
      toast('Settings saved.');
      showView('list');
      return loadStations();
    }).catch(err => {
      readErrors(err).then(messages => renderErrorBox(box, messages));
    }).then(() => loading(false));
  }

  // ------------------------------------------------------------------------------------------
  // Status
  // ------------------------------------------------------------------------------------------

  function openStatus() {
    showView('status');
    loadStatus(true);
    startStatusTimer();
  }

  function startStatusTimer() {
    stopStatusTimer();
    state.statusTimer = setInterval(() => {
      if (state.view !== 'status' || !page.isConnected) {
        stopStatusTimer();
        return;
      }
      if (document.hidden) {
        return;
      }
      loadStatus(false);
    }, STATUS_INTERVAL_MS);
  }

  function stopStatusTimer() {
    if (state.statusTimer) {
      clearInterval(state.statusTimer);
      state.statusTimer = null;
    }
  }

  function loadStatus(showSpinner) {
    if (showSpinner) {
      loading(true);
    }
    return api('GET', 'Status').then(status => {
      renderErrorBox($('#tvsStatusError'), null);
      renderStatus(status || {});
      $('#tvsStatusUpdated').textContent = 'Updated ' + new Date().toLocaleTimeString();
    }).catch(err => {
      readErrors(err).then(messages => renderErrorBox($('#tvsStatusError'), messages));
    }).then(() => {
      if (showSpinner) {
        loading(false);
      }
    });
  }

  function renderStatus(s) {
    const info = clear($('#tvsStatusInfo'));
    const kv = (label, valueNode) => {
      info.appendChild(el('dt', { text: label }));
      info.appendChild(el('dd', null, valueNode));
    };
    kv('ffmpeg', [
      el('span', { text: s.FfmpegPath || '(not set)' }),
      ' ',
      s.FfmpegExists
        ? el('span', { className: 'tvs-ok', text: '✓ found' })
        : el('span', { className: 'tvs-bad', text: '✗ not found – streaming will fail until this is fixed in Settings or Jellyfin\'s playback settings' })
    ]);
    kv('Data path', s.DataPath || '');
    kv('Plugin version', s.Version || '');
    kv('Server time (UTC)', s.ServerTimeUtc ? s.ServerTimeUtc + ' (' + fmtDateTime(s.ServerTimeUtc) + ' local)' : '');

    const wrap = clear($('#tvsSessions'));
    const sessions = Array.isArray(s.Sessions) ? s.Sessions : [];
    if (!sessions.length) {
      wrap.appendChild(el('div', { className: 'tvs-empty', text: 'No stations are streaming right now. Sessions appear here while a client is tuned in (plus the linger period).' }));
      return;
    }
    const table = el('table', { className: 'tvs-table' });
    table.appendChild(el('thead', null, el('tr', null, [
      el('th', { text: 'Station' }), el('th', { text: 'Viewers' }), el('th', { text: 'Readers' }), el('th', { text: 'Started' }),
      el('th', { text: 'Streamed' }), el('th', { text: 'PID' }), el('th', { text: 'Now playing' }), el('th', { text: '' })
    ])));
    const tbody = el('tbody');
    sessions.forEach(session => {
      const current = session.Current;
      const nowCell = el('td');
      if (current) {
        nowCell.appendChild(el('div', { text: entryTitle(current) }));
        if (current.OffsetSeconds !== null && current.OffsetSeconds !== undefined) {
          const total = (new Date(current.EndUtc) - new Date(current.StartUtc)) / 1000;
          nowCell.appendChild(el('div', { className: 'tvs-secondary', text: fmtClock(current.OffsetSeconds) + ' / ' + fmtClock(total) }));
        }
      } else {
        nowCell.appendChild(el('span', { className: 'tvs-secondary', text: '–' }));
      }

      const logOpen = !!state.openLogs[session.UniqueId];
      const logCount = (session.RecentLog || []).length;
      const actions = el('td', { className: 'nowrap' }, [
        iconButton('refresh', 'Restart encoder', () => {
          api('POST', 'Stations/' + session.StationId + '/Stream/Restart', undefined, undefined, true)
            .then(() => { toast('Restart requested.'); return loadStatus(false); })
            .catch(err => alertErrors(err, 'Could not restart'));
        }),
        iconButton('stop', 'Stop encoder', () => {
          api('POST', 'Stations/' + session.StationId + '/Stream/Stop', undefined, undefined, true)
            .then(() => { toast('Stop requested.'); return loadStatus(false); })
            .catch(err => alertErrors(err, 'Could not stop'));
        }),
        iconButton('expand_more', (logOpen ? 'Hide' : 'Show') + ' log (' + logCount + ')', () => {
          state.openLogs[session.UniqueId] = !logOpen;
          renderStatus(s);
        })
      ]);

      tbody.appendChild(el('tr', null, [
        el('td', null, [el('div', { text: session.StationName || session.StationId }), el('div', { className: 'tvs-secondary', text: String(session.UniqueId || '').slice(0, 8) })]),
        el('td', { text: String(session.Consumers) }),
        el('td', { text: String(session.Readers) }),
        el('td', { className: 'nowrap', text: fmtDateTime(session.StartedUtc) }),
        el('td', { className: 'nowrap', text: fmtBytes(session.BytesStreamed) }),
        el('td', { text: session.EncoderPid !== null && session.EncoderPid !== undefined ? String(session.EncoderPid) : '–' }),
        nowCell,
        actions
      ]));

      if (logOpen) {
        const logText = logCount ? (session.RecentLog || []).join('\n') : '(no recent log lines)';
        tbody.appendChild(el('tr', { className: 'logrow' }, el('td', { colspan: 8 }, el('pre', { className: 'tvs-log', text: logText }))));
      }
    });
    table.appendChild(tbody);
    wrap.appendChild(table);
  }

  // ------------------------------------------------------------------------------------------
  // Wiring
  // ------------------------------------------------------------------------------------------

  function on(selector, event, handler) {
    const node = $(selector);
    if (node) {
      node.addEventListener(event, handler);
    }
  }

  function wireHandlers() {
    // List
    on('#tvsBtnNew', 'click', () => openEditor(null));
    on('#tvsBumpersEnabled', 'change', () => show($('#tvsBumperOptions'), checked('#tvsBumpersEnabled')));
    on('#tvsBtnContent', 'click', loadContent);
    on('#tvsContentFilter', 'input', () => renderContent());
    on('#tvsBtnAddDaypart', 'click', () => {
      state.draft.Pool.DaypartRules.push({ StartLocal: '06:00', EndLocal: '20:00', MaxRating: 'PG', AllowUnrated: false });
      renderDaypartRows();
    });
    on('#tvsBtnAnchorNow', 'click', () => {
      setVal('#tvsAnchor', toLocalInputValue(nowOnGrid(paddingGridMinutes()).toISOString()));
      updateAnchorHint();
    });
    on('#tvsSeasonPreset', 'change', () => {
      const key = val('#tvsSeasonPreset');
      if (key && SEASON_PRESETS[key]) {
        state.draft.ActiveWindows.push(Object.assign({}, SEASON_PRESETS[key]));
        renderSeasonRows();
      }
      setVal('#tvsSeasonPreset', '');
    });
    on('#tvsBtnAddSeason', 'click', () => {
      state.draft.ActiveWindows.push({ Label: '', StartMonth: 12, StartDay: 1, EndMonth: 12, EndDay: 31 });
      renderSeasonRows();
    });
    on('#tvsBtnExport', 'click', exportStations);
    on('#tvsBtnImport', 'click', () => { $('#tvsImportFile').value = ''; $('#tvsImportFile').click(); });
    on('#tvsImportFile', 'change', importStationsFromFile);
    on('#tvsBtnRefreshGuide', 'click', refreshGuide);
    on('#tvsBtnSettings', 'click', openSettings);
    on('#tvsBtnStatus', 'click', openStatus);

    // Editor
    const backToList = () => {
      showView('list');
      loadStations();
    };
    on('#tvsBtnEditorBack', 'click', backToList);
    on('#tvsBtnCancel', 'click', backToList);
    on('#tvsBtnSave', 'click', () => saveStation(false));
    on('#tvsBtnSaveRefresh', 'click', () => saveStation(true));
    on('#tvsBtnRecompile', 'click', recompileStation);
    on('#tvsMode', 'change', updateModePanels);
    on('#tvsAnchor', 'change', updateAnchorHint);
    on('#tvsAnchor', 'input', updateAnchorHint);
    on('#tvsBtnResetAnchor', 'click', () => {
      setVal('#tvsAnchor', '');
      state.draft.AnchorUtc = null;
      updateAnchorHint();
    });
    on('#tvsPadding', 'change', () => {
      if (state.draft) {
        renderLineup();
      }
    });
    on('#tvsName', 'input', () => {
      if (state.draft && !isNewDraft()) {
        $('#tvsEditorTitle').textContent = 'Edit station – ' + val('#tvsName');
      }
    });
    on('#tvsEncodingEnabled', 'change', () => show($('#tvsStationEncoding'), checked('#tvsEncodingEnabled')));
    on('#tvsOverlayEnabled', 'change', () => show($('#tvsOverlayOptions'), checked('#tvsOverlayEnabled')));
    on('#tvsBtnAddBlock', 'click', () => {
      const templateId = val('#tvsBlockTemplateSelect');
      const template = state.blockTemplates.find(t => t.Id === templateId);
      const block = template ? blockFromTemplate(template) : newBlock();
      state.draft.Slots.Blocks.push(block);
      state.expandedBlocks[block._key] = true;
      setVal('#tvsBlockTemplateSelect', '');
      renderBlocks();
    });
    on('#tvsBtnSuggest', 'click', () => openSuggestions(false));
    on('#tvsBtnSuggestRefresh', 'click', () => openSuggestions(true));
    on('#tvsSuggestSmall', 'change', () => openSuggestions(false));
    on('#tvsBtnSuggestBack', 'click', () => showView('list'));
    on('#tvsBtnSuggestCreate', 'click', createSelectedSuggestions);
    on('#tvsSuggestList', 'change', updateSuggestCount);
    on('#tvsLogoFile', 'change', ev => {
      const file = ev.target.files && ev.target.files[0];
      if (file) {
        uploadLogo(file);
      }
    });
    $all('.tvs-preview-btn').forEach(button => {
      button.addEventListener('click', () => previewSchedule(parseInt(button.getAttribute('data-hours'), 10) || 24));
    });

    // Settings
    on('#tvsBtnSettingsBack', 'click', backToList);
    on('#tvsBtnSettingsCancel', 'click', backToList);
    on('#tvsBtnSettingsSave', 'click', saveSettings);
    on('#tvsSlateColor', 'input', () => {
      $('#tvsSlateColorText').textContent = val('#tvsSlateColor');
    });

    // Status
    on('#tvsBtnStatusBack', 'click', backToList);
    on('#tvsBtnStatusRefresh', 'click', () => loadStatus(true));
  }

  if (!page.tvsInitialized) {
    page.tvsInitialized = true;
    wireHandlers();

    page.addEventListener('pageshow', () => {
      // Always start on the list when the dashboard navigates here.
      showView('list');
      loadStations();
    });

    const onHide = () => {
      stopStatusTimer();
      clearTimeout(state.deleteTimer);
    };
    page.addEventListener('pagehide', onHide);
    page.addEventListener('viewhide', onHide);
    page.addEventListener('viewdestroy', onHide);
  }
})();
