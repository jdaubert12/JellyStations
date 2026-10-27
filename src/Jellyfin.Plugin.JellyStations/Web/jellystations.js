/*
 * JellyStations admin page.
 *
 * Loaded by the Jellyfin dashboard via <script src="configurationpage?name=JellyStationsJs">.
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
  const pages = document.querySelectorAll('#JellyStationsPage');
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
  const WEEK_LABELS = { 1: '1st', 2: '2nd', 3: '3rd', 4: '4th', 5: 'Last' };
  const RECURRENCES = ['Weekly', 'EveryNWeeks', 'WeeksOfMonth', 'Dates'];
  const KIND_LABEL = {
    Movie: 'Movie', Series: 'Series', Season: 'Season', Episode: 'Episode', BoxSet: 'Collection',
    Playlist: 'Playlist', Genre: 'Genre', Tag: 'Tag', Library: 'Library', Video: 'Video',
    Album: 'Album', Artist: 'Artist', MusicGenre: 'Music genre', Audio: 'Track', Folder: 'Folder'
  };
  const MODE_LABEL = { Pool: 'Shuffle', Slots: 'Weekly schedule', Lineup: 'Fixed list', Weather: 'Weather channel' };
  const MODE_HELP = {
    Pool: 'Everything you add plays around the clock, reshuffled each time the channel has been through it all. Example: a comedy channel that never repeats a film twice in a row. Time blocks are optional and take over at fixed times, say a film every evening from 18:00; the shuffle carries on where it left off afterwards.',
    Slots: 'Blocks play their own content at fixed times on chosen days, like cartoons on weekday mornings and a film on Friday night. Whenever no block is on, the filler plays.',
    Lineup: 'Exactly the items you pick, in your order, looping forever. Good for a fixed marathon or a hand-built evening.',
    Weather: 'Current conditions, the next twelve hours and the week ahead for a few places, drawn by the plugin as full-screen graphics with a live clock, cycling over music. Forecasts come from Open-Meteo, a free service; only each place\'s coordinates are sent.'
  };
  /** Starting points for "New station": each fills in the schedule settings; the person adds the content. */
  const PRESETS = [
    { key: 'shuffle', icon: 'shuffle', name: 'Shuffle a collection', desc: 'Pick any shows and movies; they play around the clock, reshuffled each round, never the same show or film twice in a row.', next: 'Next: add shows and movies.', apply: () => {} },
    { key: 'kids', icon: 'child_care', name: 'Kids channel', desc: 'Cartoons and family shows all day, rated G or below. Each show plays two or three episodes in order before switching.', next: 'Next: add the shows. Whatever you add is limited to G unless you change it.', sourceDefaults: { MaxRating: 'G', AllowUnrated: false },
      apply: d => { d.Pool.Order = 'ShuffleEpisodesInOrder'; d.Pool.MarathonMin = 2; d.Pool.MarathonMax = 3; d.PaddingByLength = true; d.PaddingGridMinutes = 0; } },
    { key: 'movies', icon: 'movie', name: 'Movie night', desc: 'Films in two-hour slots, shuffled, and a film rests at least eight hours before it plays again. Nothing above PG-13 before 8 pm.', next: 'Next: add a genre, a collection or single films.', sourceDefaults: { ItemKinds: ['Movie'] },
      apply: d => { d.PaddingByLength = true; d.PaddingGridMinutes = 0; d.Pool.CooldownHours = 8; d.Pool.DaypartRules = [{ StartLocal: '06:00', EndLocal: '20:00', MaxRating: 'PG-13', AllowUnrated: true }]; } },
    { key: 'reruns', icon: 'tv', name: 'Comfort reruns', desc: 'A few favourite shows in half-hour and hour slots. A random show comes up, always on its next episode.', next: 'Next: add the shows.', sourceDefaults: { ItemKinds: ['Episode'] },
      apply: d => { d.Pool.Order = 'ShuffleEpisodesInOrder'; d.PaddingByLength = true; d.PaddingGridMinutes = 0; } },
    { key: 'holiday', icon: 'ac_unit', name: 'Holiday channel', desc: 'Seasonal films and specials. The channel only appears in the guide from late November to early January.', next: 'Next: add the holiday titles. The excluded tags from Settings are off here so holiday-tagged items stay in.',
      apply: d => { d.PaddingByLength = true; d.PaddingGridMinutes = 0; d.ActiveWindows = [Object.assign({}, SEASON_PRESETS.holidays)]; d.UseGlobalExcludedTags = false; } },
    { key: 'marathon', icon: 'playlist_play', name: 'One show, start to finish', desc: 'One series from the first episode in order, around the clock, then again from the start.', next: 'Next: add the show.',
      apply: d => { d.Pool.Order = 'Sequential'; d.Pool.AvoidSameSeriesBackToBack = false; d.PaddingByLength = true; d.PaddingGridMinutes = 0; } },
    { key: 'weekly', icon: 'event', name: 'Weekly schedule', desc: 'Blocks at fixed times each week, say cartoons on weekday mornings and a film on Friday night, with filler in between.', next: 'Next: add blocks and the filler.',
      apply: d => { d.Mode = 'Slots'; d.PaddingGridMinutes = 15; } },
    { key: 'list', icon: 'format_list_numbered', name: 'Play a fixed list', desc: 'Exactly the items you pick, in your order, looping forever.', next: 'Next: add items in order.',
      apply: d => { d.Mode = 'Lineup'; } },
    { key: 'weather', icon: 'wb_sunny', name: 'Weather channel', desc: 'Local forecasts for a few places, like the cable weather channel: current conditions, the hours ahead and the week, over music, with a live clock.', next: 'Next: add places and some music.',
      apply: d => { d.Mode = 'Weather'; d.Name = d.Name || 'Weather'; } }
  ];
  const BLOCKS_HELP = {
    Pool: 'Optional. A block takes over at a fixed time on the days you pick and plays its own content; the shuffle carries on whenever no block is on. Times are in the station\'s time zone; family hours do not apply inside a block.',
    Slots: 'Each block plays its own content at a fixed time on the days you pick (station time zone). The filler plays whenever no block is on.'
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
  const ENCODERS = [['libx264', 'libx264 (software)'], ['h264_nvenc', 'h264_nvenc (NVIDIA)'], ['h264_qsv', 'h264_qsv (Intel Quick Sync)'], ['h264_vaapi', 'h264_vaapi (VA-API)'], ['libx265', 'libx265 (software, HEVC)'], ['hevc_nvenc', 'hevc_nvenc (NVIDIA, HEVC)'], ['hevc_qsv', 'hevc_qsv (Intel Quick Sync, HEVC)'], ['hevc_vaapi', 'hevc_vaapi (VA-API, HEVC)'], ['custom', 'Custom…']];
  const OUTPUT_RANGES = [['sdr', 'SDR (BT.709, 8-bit)'], ['hdr10', 'HDR10 (BT.2020 PQ, 10-bit HEVC)']];
  const HW_DECODERS = [['auto', 'Follow Jellyfin'], ['', 'None (software)'], ['qsv', 'Intel Quick Sync (qsv)'], ['vaapi', 'VA-API'], ['cuda', 'NVIDIA (cuda)'], ['videotoolbox', 'Apple VideoToolbox'], ['d3d11va', 'Windows (d3d11va)']];
  const STATUS_INTERVAL_MS = 5000;
  const LIST_INTERVAL_MS = 60000;

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
    coverage: null,       // CoverageDto for the "Not on a channel" page
    openLogs: {},         // session UniqueId -> bool
    deleteTimer: null,
    listTimer: null,
    guidedStart: false,   // true once an empty list has sent the person to the suggestions
    presetName: null,     // name of the preset a new draft started from
    editorStep: '1',      // '1' | '2' | '3' | 'all'
    pendingLogo: null,    // generated logo (Blob) for a new station, uploaded right after the first save
    logoLook: 0,          // how many times "Another look" was pressed for this draft; part of the logo seed
    sourceDefaults: null, // filters the preset applies to every source added to the pool
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

  function badge(text, cls, title) {
    return el('span', { className: 'tvs-badge ' + (cls || ''), text: text, title: title || null });
  }

  /** "48 min" / "2 h 05 min" for time left in a program. */
  function fmtLeft(totalSeconds) {
    if (totalSeconds === null || totalSeconds === undefined || !isFinite(totalSeconds)) {
      return '';
    }
    const m = Math.max(0, Math.round(totalSeconds / 60));
    if (m < 1) {
      return 'under a minute';
    }
    if (m < 60) {
      return m + ' min';
    }
    return Math.floor(m / 60) + ' h ' + pad2(m % 60) + ' min';
  }

  function fmtHours(hours) {
    const n = Number(hours) || 0;
    return (n % 1 === 0 ? String(n) : n.toFixed(1)) + ' h';
  }

  /** Tooltip for the excluded badge: one line per reason. */
  function excludedTitle(health) {
    const byReason = health && health.ExcludedByReason ? health.ExcludedByReason : {};
    const lines = Object.keys(byReason).map(k => byReason[k] + ' × ' + (EXCLUSION_TEXT[k] || k));
    return lines.length ? lines.join('\n') : null;
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
      const role = entry.SourceLabel === 'Pre-roll' || entry.SourceLabel === 'Post-roll' ? entry.SourceLabel : 'Bumper';
      return role + ' · ' + (entry.Title || '');
    }
    if (entry.Kind === 'Weather') {
      return entry.Title || 'Local weather';
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
    return ApiClient.getUrl('JellyStations/Stations/' + stationId + '/Logo', { v: Date.now() });
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
      url: ApiClient.getUrl('JellyStations/' + path, query || undefined)
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

  const VIEW_IDS = { list: '#tvsViewList', editor: '#tvsViewEditor', settings: '#tvsViewSettings', status: '#tvsViewStatus', suggest: '#tvsViewSuggest', presets: '#tvsViewPresets', coverage: '#tvsViewCoverage' };

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
      host.appendChild(el('div', { className: 'tvs-secondary', text: 'No family hours: anything may play at any time.' }));
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
      a.download = 'jellystations-stations-' + stamp + '.json';
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
      startListTimer();
      if (!state.stations.length && !state.guidedStart) {
        // First visit with nothing set up: lead with what the library could support.
        state.guidedStart = true;
        openSuggestions(false);
      }
    }).catch(err => {
      readErrors(err).then(messages => renderErrorBox($('#tvsListError'), messages));
    }).then(() => loading(false));
  }

  /** Keeps "Now", "Next" and the time left on the station rows current while the list is on screen. */
  function startListTimer() {
    if (state.listTimer) {
      return;
    }
    state.listTimer = setInterval(() => {
      if (!page.isConnected) {
        clearInterval(state.listTimer);
        state.listTimer = null;
        return;
      }
      if (state.view !== 'list' || document.hidden) {
        return;
      }
      api('GET', 'Stations').then(list => {
        if (state.view !== 'list') {
          return;
        }
        state.stations = Array.isArray(list) ? list : [];
        renderStationList();
      }).catch(() => { /* the next manual load reports errors */ });
    }, LIST_INTERVAL_MS);
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

    const meta = s.Mode === 'Weather'
      ? [MODE_LABEL.Weather, s.LocationCount + (s.LocationCount === 1 ? ' place' : ' places'), s.MusicCount + (s.MusicCount === 1 ? ' track' : ' tracks')]
      : [MODE_LABEL[s.Mode] || s.Mode, s.ItemCount + (s.ItemCount === 1 ? ' item' : ' items')];
    const metaLine = el('div', { className: 'listItemBodyText secondary' }, meta.join(' · '));
    const health = s.Health || null;
    const warn = (text, title) => {
      const b = badge(text, 'warn tvs-clickable', (title ? title + '\n' : '') + 'Click to open the station.');
      b.addEventListener('click', () => openEditor(s.Id));
      return b;
    };
    if (s.ExcludedCount > 0) {
      metaLine.appendChild(warn(s.ExcludedCount + ' excluded', excludedTitle(health)));
    }
    if (health) {
      if (s.Mode !== 'Weather' && s.ItemCount > 0 && health.UniqueHours < 24) {
        metaLine.appendChild(warn(fmtHours(health.UniqueHours) + ' unique', 'Under a day of unique content: programs repeat within 24 hours. Add more shows or films.'));
      }
      const empty = Array.isArray(health.EmptySources) ? health.EmptySources : [];
      if (empty.length) {
        metaLine.appendChild(warn(empty.length + (empty.length === 1 ? ' empty source' : ' empty sources'), empty.join('\n')));
      }
    }
    body.appendChild(metaLine);

    let nowText;
    if (s.NowPlaying) {
      const np = s.NowPlaying;
      nowText = 'Now: ' + entryTitle(np);
      if (np.OffsetSeconds !== null && np.OffsetSeconds !== undefined) {
        const total = (new Date(np.EndUtc) - new Date(np.StartUtc)) / 1000;
        const left = isFinite(total) ? fmtLeft(total - np.OffsetSeconds) : '';
        nowText += ' (' + fmtClock(np.OffsetSeconds) + ' in' + (left ? ', ' + left + ' left' : '') + ')';
      }
    } else {
      nowText = s.Enabled ? 'Now: nothing scheduled – add content or recompile' : 'Now: off air (disabled)';
    }
    body.appendChild(el('div', { className: 'listItemBodyText secondary tvs-ellipsis', text: nowText, title: nowText }));

    const next = s.Enabled && Array.isArray(s.Next) ? s.Next : [];
    if (next.length) {
      const nextText = 'Next: ' + next.map((e, i) => (i ? 'then ' : '') + entryTitle(e) + ' at ' + fmtTime(e.StartUtc)).join(' · ');
      body.appendChild(el('div', { className: 'listItemBodyText secondary tvs-ellipsis', text: nextText, title: nextText }));
    }

    if (s.LastError) {
      const when = s.LastErrorUtc ? fmtDateTime(s.LastErrorUtc) + ' · ' : '';
      body.appendChild(el('div', { className: 'listItemBodyText tvs-fault' }, [
        el('span', { className: 'material-icons warning', 'aria-hidden': 'true' }),
        el('span', { className: 'tvs-ellipsis', text: when + s.LastError, title: s.LastError }),
        iconButton('close', 'Dismiss this error', () => {
          api('POST', 'Stations/' + s.Id + '/Stream/ClearError', undefined, undefined, true)
            .then(() => loadStations())
            .catch(err => alertErrors(err, 'Could not dismiss the error'));
        }, 'tvs-fault-dismiss')
      ]));
    }
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

  function recompileAll() {
    loading(true);
    api('POST', 'Stations/RecompileAll').then(result => {
      const rows = result && Array.isArray(result.Stations) ? result.Stations : [];
      const changed = rows.filter(r => r.ContentChanged && !r.Error);
      const failed = rows.filter(r => r.Error);
      const name = r => (r.ChannelNumber ? r.ChannelNumber + ' ' : '') + r.Name;
      const lines = ['Refreshed ' + rows.length + ' station' + (rows.length === 1 ? '' : 's') + '.'];
      if (changed.length) {
        lines.push(changed.length + ' changed content and the guide is refreshing: ' + changed.map(name).join(', ') + '.');
      } else {
        lines.push('No station changed content.');
      }
      if (failed.length) {
        lines.push(failed.length + ' failed: ' + failed.map(r => name(r) + ' (' + r.Error + ')').join('; ') + '.');
      }
      toast({ title: 'Refresh content', message: lines.join('\n') });
      return loadStations();
    }).catch(err => alertErrors(err, 'Refresh content failed')).then(() => loading(false));
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
      UpNextCard: { Enabled: false, Corner: 'BottomRight', Animation: 'Fade', LeadTimesSeconds: [30], ShowSeconds: 10, HeightPercent: 14, Opacity: 85, ShowThumbnail: true, ShowTimes: true, TwentyFourHourClock: false },
      TimeZoneId: guessTimeZone(),
      AnchorUtc: null,
      Revision: 1,
      PaddingGridMinutes: 0,
      PaddingByLength: false,
      Mode: 'Pool',
      Pool: { Sources: [], Order: 'Shuffle', AvoidSameSeriesBackToBack: true, CooldownHours: 0, MarathonMin: 1, MarathonMax: 1, DaypartRules: [] },
      Slots: { Blocks: [], Filler: { Sources: [], Order: 'Shuffle' } },
      Lineup: { Items: [] },
      Weather: defaultWeather(),
      Encoding: null,
      AutoRecompileNightly: true,
      ActiveWindows: [],
      ExcludedItemIds: [],
      ExcludedTags: [],
      UseGlobalExcludedTags: true,
      MinRuntimeMinutes: 0,
      Bumpers: { Enabled: false, Sources: [], BreakMinutes: 0, MaxClipMinutes: 5, PreRoll: { Enabled: false, Sources: [] }, PostRoll: { Enabled: false, Sources: [] } },
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
    s.Pool.CooldownHours = Math.min(168, Math.max(0, parseInt(s.Pool.CooldownHours, 10) || 0));
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
      b.RemainderMayOverrun = !!b.RemainderMayOverrun;
      b.MaxStartDelayMinutes = Math.min(240, Math.max(0, parseInt(b.MaxStartDelayMinutes, 10) || 0));
      b.Kind = b.Kind === 'Weather' ? 'Weather' : 'Content';
      b.RepeatEveryMinutes = Math.min(1440, Math.max(0, parseInt(b.RepeatEveryMinutes, 10) || 0));
      b.RepeatUntilLocal = parseHm(b.RepeatUntilLocal) === null ? '' : b.RepeatUntilLocal;
      normalizeRecurrence(b);
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
    s.Weather = normalizeWeather(s.Weather);
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
    ['PreRoll', 'PostRoll'].forEach(key => {
      const roll = s.Bumpers[key] = s.Bumpers[key] || {};
      roll.Enabled = !!roll.Enabled;
      roll.Sources = Array.isArray(roll.Sources) ? roll.Sources : [];
      roll.Sources.forEach(normalizeSource);
    });
    const wb = s.Bumpers.Weather = s.Bumpers.Weather || {};
    wb.Enabled = !!wb.Enabled;
    wb.Minutes = Math.min(10, Math.max(1, parseInt(wb.Minutes, 10) || 2));
    wb.Position = wb.Position === 'AfterProgram' ? 'AfterProgram' : 'BeforeProgram';
    s.CardText = s.CardText || null;
    s.LogoOverlay = s.LogoOverlay || {};
    s.LogoOverlay.Enabled = !!s.LogoOverlay.Enabled;
    s.LogoOverlay.Corner = s.LogoOverlay.Corner || 'TopRight';
    s.LogoOverlay.Opacity = Math.min(100, Math.max(10, parseInt(s.LogoOverlay.Opacity, 10) || 70));
    s.LogoOverlay.HeightPercent = Math.min(25, Math.max(3, parseInt(s.LogoOverlay.HeightPercent, 10) || 8));
    const card = s.UpNextCard = s.UpNextCard || {};
    card.Enabled = !!card.Enabled;
    card.Corner = card.Corner || 'BottomRight';
    card.Animation = ['None', 'Fade', 'Slide', 'FadeAndSlide'].indexOf(card.Animation) >= 0 ? card.Animation : 'Fade';
    card.LeadTimesSeconds = normalizeLeadTimes(card.LeadTimesSeconds);
    card.ShowSeconds = Math.min(Math.min(60, Math.min.apply(null, card.LeadTimesSeconds)), Math.max(3, parseInt(card.ShowSeconds, 10) || 10));
    card.HeightPercent = Math.min(30, Math.max(8, parseInt(card.HeightPercent, 10) || 14));
    card.Opacity = Math.min(100, Math.max(30, parseInt(card.Opacity, 10) || 85));
    card.ShowThumbnail = card.ShowThumbnail !== false;
    card.ShowTimes = card.ShowTimes !== false;
    card.TwentyFourHourClock = !!card.TwentyFourHourClock;
    return s;
  }

  // Up next lead times: 5 s to 6 h each, distinct, largest first, at most eight; never empty.
  function normalizeLeadTimes(list) {
    const seen = {};
    const out = (Array.isArray(list) ? list : [])
      .map(v => parseInt(v, 10))
      .filter(v => !isNaN(v))
      .map(v => Math.min(21600, Math.max(5, v)))
      .filter(v => (seen[v] ? false : (seen[v] = true)))
      .sort((a, b) => b - a)
      .slice(0, 8);
    return out.length ? out : [30];
  }

  // "1h, 30m, 5m, 30s" or "1h30m" or plain seconds -> seconds.
  function parseLeadTimes(text) {
    const out = [];
    String(text || '').split(/[,;\n]+/).forEach(part => {
      const p = part.trim().toLowerCase();
      if (!p) { return; }
      if (/^\d+$/.test(p)) { out.push(parseInt(p, 10)); return; }
      let total = 0, matched = false;
      p.replace(/(\d+(?:\.\d+)?)\s*(h|hr|hrs|hour|hours|m|min|mins|minute|minutes|s|sec|secs|second|seconds)\b/g, (m, n, u) => {
        matched = true;
        const v = parseFloat(n);
        total += u[0] === 'h' ? v * 3600 : u[0] === 'm' ? v * 60 : v;
        return '';
      });
      if (matched) { out.push(Math.round(total)); }
    });
    return normalizeLeadTimes(out);
  }

  function formatLeadTime(seconds) {
    const h = Math.floor(seconds / 3600), m = Math.floor((seconds % 3600) / 60), s = seconds % 60;
    const parts = [];
    if (h) { parts.push(h + 'h'); }
    if (m) { parts.push(m + 'm'); }
    if (s || !parts.length) { parts.push(s + 's'); }
    return parts.join(' ');
  }

  function formatLeadTimes(list) {
    return normalizeLeadTimes(list).map(formatLeadTime).join(', ');
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
    src.AudioLanguage = (src.AudioLanguage || '').trim().toLowerCase() || null;
    src.SubtitleLanguage = (src.SubtitleLanguage || '').trim().toLowerCase() || null;
    src.SubtitlesOnlyIfAudioMissing = !!src.SubtitlesOnlyIfAudioMissing;
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
    if (src.AudioLanguage) {
      parts.push('audio ' + src.AudioLanguage);
    }
    if (src.SubtitleLanguage) {
      parts.push('subs ' + src.SubtitleLanguage + (src.SubtitlesOnlyIfAudioMissing ? ' if needed' : ''));
    }
    if (src.Weight > 1) {
      parts.push('×' + src.Weight);
    }
    return parts.join(' · ');
  }

  /** Common three-letter language codes offered when the source has no files to read them from. */
  const COMMON_LANGUAGES = [['eng', 'English'], ['jpn', 'Japanese'], ['spa', 'Spanish'], ['fra', 'French'], ['deu', 'German'], ['ita', 'Italian'], ['por', 'Portuguese'], ['kor', 'Korean'], ['zho', 'Chinese'], ['rus', 'Russian'], ['hin', 'Hindi'], ['ara', 'Arabic']];

  /**
   * A select of languages for a source's audio or subtitle track: the first option is the "none / default" choice,
   * the rest come from the item's files (Library/Items/{id}/Languages) or, when there are none, a common list;
   * a value already saved but not in the list is kept as its own option.
   */
  function languageSelect(current, noneText, onChange) {
    const select = el('select', { className: 'emby-select' }, [el('option', { value: '', text: noneText })]);
    let known = [];
    function fill(list) {
      known = list;
      while (select.options.length > 1) { select.remove(1); }
      list.forEach(l => select.appendChild(el('option', { value: l.Code, text: l.Name + ' (' + l.Code + ')' + (l.Files ? ' · ' + l.Files + ' file' + (l.Files === 1 ? '' : 's') : '') })));
      if (current && !list.some(l => l.Code === current)) {
        select.appendChild(el('option', { value: current, text: current }));
      }
      select.value = current || '';
    }
    fill(COMMON_LANGUAGES.map(c => ({ Code: c[0], Name: c[1], Files: 0 })));
    select.addEventListener('change', () => { current = select.value || null; onChange(current); });
    return { select: select, fill: fill, known: () => known };
  }

  /** The "New station" page: one card per preset. */
  function openPresets() {
    const box = clear($('#tvsPresetList'));
    PRESETS.forEach(p => {
      box.appendChild(el('button', { type: 'button', className: 'tvs-preset', onClick: () => openEditor(null, p) }, [
        el('div', { className: 'tvs-preset-head' }, [el('span', { className: 'material-icons ' + p.icon, 'aria-hidden': 'true' }), el('span', { text: p.name })]),
        el('div', { className: 'tvs-preset-desc', text: p.desc }),
        el('div', { className: 'tvs-preset-next', text: p.next })
      ]));
    });
    showView('presets');
  }

  /** Filters a preset wants on every source added to the pool (rating cap, movies or episodes only). */
  function applySourceDefaults(source) {
    const d = state.sourceDefaults;
    if (!d || !hasTuningFilters(source)) {
      return;
    }
    if (d.MaxRating !== undefined) {
      source.MaxRating = d.MaxRating;
      source.AllowUnrated = !!d.AllowUnrated;
    }
    if (d.ItemKinds && hasKindFilter(source)) {
      source.ItemKinds = d.ItemKinds.slice();
    }
  }

  function openEditor(id, preset) {
    loading(true);
    const detailPromise = id ? api('GET', 'Stations/' + id) : Promise.resolve(null);
    Promise.all([loadTimeZones(), detailPromise]).then(results => {
      const detail = results[1];
      state.detail = detail;
      state.draft = detail ? normalizeStation(detail.Station) : newDraft();
      state.presetName = null;
      state.sourceDefaults = null;
      if (!detail && preset) {
        preset.apply(state.draft);
        state.presetName = preset.name;
        state.sourceDefaults = preset.sourceDefaults || null;
      }
      state.runtimes = {};
      state.expandedBlocks = {};
      state.pendingLogo = null;
      state.logoLook = 0;
      renderEditor();
      showStep(rememberedEditorView() === 'all' ? 'all' : '1');
      showView('editor');
    }).catch(err => alertErrors(err, 'Could not open station')).then(() => loading(false));
  }

  function isNewDraft() {
    return !state.draft || !state.draft.Id || state.draft.Id === ZERO_GUID;
  }

  function renderEditor() {
    const d = state.draft;
    const isNew = isNewDraft();

    $('#tvsEditorTitle').textContent = isNew ? 'New station' + (state.presetName ? ': ' + state.presetName : '') : 'Edit station – ' + (d.Name || '');
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
    setChecked('#tvsPreRollEnabled', !!d.Bumpers.PreRoll.Enabled);
    setChecked('#tvsPostRollEnabled', !!d.Bumpers.PostRoll.Enabled);
    show($('#tvsPreRollPicker'), !!d.Bumpers.PreRoll.Enabled);
    show($('#tvsPostRollPicker'), !!d.Bumpers.PostRoll.Enabled);
    setChecked('#tvsBumperWeatherEnabled', !!d.Bumpers.Weather.Enabled);
    setVal('#tvsBumperWeatherMinutes', d.Bumpers.Weather.Minutes || 2);
    setVal('#tvsBumperWeatherPosition', d.Bumpers.Weather.Position || 'BeforeProgram');
    show($('#tvsBumperWeatherOptions'), !!d.Bumpers.Weather.Enabled);
    createPicker({
      container: $('#tvsPreRollPicker'),
      mode: 'sources',
      getSources: () => state.draft.Bumpers.PreRoll.Sources,
      onAdd: source => state.draft.Bumpers.PreRoll.Sources.push(source),
      onRemove: index => state.draft.Bumpers.PreRoll.Sources.splice(index, 1)
    });
    createPicker({
      container: $('#tvsPostRollPicker'),
      mode: 'sources',
      getSources: () => state.draft.Bumpers.PostRoll.Sources,
      onAdd: source => state.draft.Bumpers.PostRoll.Sources.push(source),
      onRemove: index => state.draft.Bumpers.PostRoll.Sources.splice(index, 1)
    });
    renderSeasonRows();
    renderWeatherPanel();
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
    delete $('#tvsLogoColor').dataset.picked;
    setChecked('#tvsLogoAutoColor', autoLogoColor());
    suggestLogoColor();
    renderLogo();
    setChecked('#tvsOverlayEnabled', !!d.LogoOverlay.Enabled);
    setVal('#tvsOverlayCorner', d.LogoOverlay.Corner);
    setVal('#tvsOverlayOpacity', d.LogoOverlay.Opacity);
    setVal('#tvsOverlaySize', d.LogoOverlay.HeightPercent);
    show($('#tvsOverlayOptions'), !!d.LogoOverlay.Enabled);
    setChecked('#tvsUpNextEnabled', !!d.UpNextCard.Enabled);
    setVal('#tvsUpNextCorner', d.UpNextCard.Corner);
    setVal('#tvsUpNextAnimation', d.UpNextCard.Animation || 'Fade');
    setVal('#tvsUpNextBefore', formatLeadTimes(d.UpNextCard.LeadTimesSeconds));
    setVal('#tvsUpNextShow', d.UpNextCard.ShowSeconds);
    setVal('#tvsUpNextSize', d.UpNextCard.HeightPercent);
    setVal('#tvsUpNextOpacity', d.UpNextCard.Opacity);
    setChecked('#tvsUpNextThumb', d.UpNextCard.ShowThumbnail);
    setChecked('#tvsUpNextTimes', d.UpNextCard.ShowTimes);
    setChecked('#tvsUpNextClock24', d.UpNextCard.TwentyFourHourClock);
    show($('#tvsUpNextOptions'), !!d.UpNextCard.Enabled);

    // Mode panels
    setVal('#tvsPoolOrder', d.Pool.Order);
    setChecked('#tvsPoolAvoid', d.Pool.AvoidSameSeriesBackToBack !== false);
    setVal('#tvsPoolCooldown', d.Pool.CooldownHours || 0);
    setVal('#tvsMarathonMin', d.Pool.MarathonMin || 1);
    setVal('#tvsMarathonMax', d.Pool.MarathonMax || 1);
    renderDaypartRows();
    setVal('#tvsFillerOrder', d.Slots.Filler.Order);
    updateModePanels();

    createPicker({
      container: $('#tvsPoolPicker'),
      mode: 'sources',
      getSources: () => state.draft.Pool.Sources,
      onAdd: source => { applySourceDefaults(source); state.draft.Pool.Sources.push(source); },
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
      ? 'One full round of this channel: ' + fmtDur(cycleSeconds) + ' across ' + (state.detail.Compiled ? state.detail.Compiled.ItemCount : 0) + ' items, including padding.'
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
    show($('#tvsPanelFamily'), mode === 'Pool');
    show($('#tvsPanelBlocks'), mode === 'Pool' || mode === 'Slots');
    show($('#tvsPanelSlots'), mode === 'Slots');
    show($('#tvsPanelLineup'), mode === 'Lineup');
    $('#tvsViewEditor').classList.toggle('tvs-weather-mode', mode === 'Weather');
    updateWeatherPanel();
    if (changed) {
      renderBlocks(); // block wording depends on the mode
    }
  }

  /** True when the draft as it stands in the form puts the weather on the air: a weather channel, a weather block or weather in the breaks. */
  function usesWeatherLive() {
    const mode = val('#tvsMode') || 'Pool';
    if (mode === 'Weather') {
      return true;
    }
    if (checked('#tvsBumpersEnabled') && checked('#tvsBumperWeatherEnabled')) {
      return true;
    }
    return (mode === 'Pool' || mode === 'Slots') && (state.draft.Slots.Blocks || []).some(b => b.Kind === 'Weather');
  }

  function usesWeatherPayload(p) {
    if (p.Mode === 'Weather') {
      return true;
    }
    if (p.Bumpers && p.Bumpers.Enabled && p.Bumpers.Weather && p.Bumpers.Weather.Enabled) {
      return true;
    }
    return (p.Mode === 'Pool' || p.Mode === 'Slots') && (p.Slots.Blocks || []).some(b => b.Kind === 'Weather');
  }

  /** Shows the Weather section when the station needs it; outside Weather mode the channel-only settings are hidden. */
  function updateWeatherPanel() {
    const mode = val('#tvsMode') || 'Pool';
    show($('#tvsPanelWeather'), usesWeatherLive());
    $('#tvsViewEditor').classList.toggle('tvs-weather-aux', mode !== 'Weather');
    $all('#tvsPanelWeather [data-weather-aux]').forEach(e => show(e, mode !== 'Weather'));
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
      img.src = logoUrl(state.draft.Id) + '&v=' + Date.now();
    } else if (state.pendingLogo) {
      img.src = URL.createObjectURL(state.pendingLogo);
    } else {
      img.removeAttribute('src');
    }
    show(img, hasLogo || !!state.pendingLogo);
    show(none, !hasLogo && !state.pendingLogo);
    none.textContent = isNew ? 'No logo yet: make one from the name below, or save the station first to upload your own.' : 'No logo uploaded.';
    hint.textContent = state.pendingLogo && isNew
      ? 'The generated logo is uploaded when you save.'
      : 'PNG, JPEG, WebP or GIF up to 5 MB. Shown in the guide and on station-break slates.';
  }

  // ---- Three steps -------------------------------------------------------------------------

  const STEP_TITLES = { 1: 'Name and look', 2: 'What plays', 3: 'When' };

  function rememberedEditorView() {
    try {
      return window.localStorage.getItem('tvsEditorView') || '';
    } catch (e) {
      return '';
    }
  }

  function rememberEditorView(view) {
    try {
      window.localStorage.setItem('tvsEditorView', view);
    } catch (e) {
      /* private mode or blocked storage: fine */
    }
  }

  /** Shows one step of the editor ('1', '2', '3') or everything ('all'); sections carry data-steps. */
  function showStep(step) {
    state.editorStep = step;
    rememberEditorView(step === 'all' ? 'all' : 'steps');
    $all('#tvsViewEditor [data-steps]').forEach(node => {
      const steps = (node.getAttribute('data-steps') || '').split(' ');
      node.classList.toggle('tvs-step-hidden', step !== 'all' && steps.indexOf(step) < 0);
    });
    $all('#tvsSteps .tvs-step').forEach(b => b.classList.toggle('active', b.getAttribute('data-step') === step));
    renderStepNav();
  }

  function renderStepNav() {
    const nav = clear($('#tvsStepNav'));
    const step = state.editorStep;
    if (step === 'all') {
      nav.appendChild(el('span', { className: 'tvs-secondary', text: 'Every setting on one page.' }));
      nav.appendChild(textButton('Back to the three steps', () => showStep('1')));
      return;
    }
    const n = parseInt(step, 10);
    const hints = {
      1: 'Give the channel a name and number. A logo is optional and can be made from the name.',
      2: 'Add the shows and films, and say how they play.',
      3: 'Family hours, time blocks and the dates the channel is listed. Then save.'
    };
    nav.appendChild(el('span', { className: 'tvs-secondary', text: 'Step ' + n + ' of 3: ' + hints[n] }));
    if (n > 1) {
      nav.appendChild(textButton('Back: ' + STEP_TITLES[n - 1], () => showStep(String(n - 1))));
    }
    if (n < 3) {
      nav.appendChild(textButton('Next: ' + STEP_TITLES[n + 1], () => showStep(String(n + 1)), 'button-submit'));
    } else {
      nav.appendChild(textButton('Save', () => saveStation(false), 'button-submit'));
    }
  }

  /** The first step that holds a field a validation message refers to, so the person lands where the fix is. */
  function stepForErrors(errors) {
    const text = errors.join(' ');
    if (/Name is required|Channel number/.test(text)) {
      return '1';
    }
    if (/block/i.test(text)) {
      return '3';
    }
    return '2';
  }

  // ---- Generated logo ----------------------------------------------------------------------

  function colorForName(name) {
    let h = 0;
    for (let i = 0; i < name.length; i++) {
      h = (h * 31 + name.charCodeAt(i)) >>> 0;
    }
    return hslToHex(h % 360, 55, 38);
  }

  /** A color drawn from the station seed: any hue, kept mid-saturated and dark enough for white text. */
  function colorForSeed(seedText) {
    const rnd = seededRandom(hashText(seedText));
    return hslToHex(Math.floor(rnd() * 360), 45 + Math.floor(rnd() * 35), 28 + Math.floor(rnd() * 20));
  }

  function autoLogoColor() {
    try {
      return window.localStorage.getItem('tvsLogoAutoColor') === '1';
    } catch (e) {
      return false;
    }
  }

  function rememberAutoLogoColor(on) {
    try {
      window.localStorage.setItem('tvsLogoAutoColor', on ? '1' : '0');
    } catch (e) {
      /* ignore */
    }
  }

  /** Puts the suggested color in the picker: from the whole seed when "Pick the color too" is on, else from the name until the person picks one. */
  function suggestLogoColor() {
    const auto = checked('#tvsLogoAutoColor');
    const picker = $('#tvsLogoColor');
    picker.disabled = auto;
    const name = val('#tvsName').trim() || 'TV';
    if (auto) {
      picker.value = colorForSeed(logoSeedText(name, val('#tvsChannel').trim()));
    } else if (!picker.dataset.picked) {
      picker.value = colorForName(name);
    }
  }

  function hslToHex(h, s, l) {
    s /= 100;
    l /= 100;
    const k = n => (n + h / 30) % 12;
    const a = s * Math.min(l, 1 - l);
    const f = n => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
    const hex = x => Math.round(255 * x).toString(16).padStart(2, '0');
    return '#' + hex(f(0)) + hex(f(8)) + hex(f(4));
  }

  function luminance(hex) {
    const n = parseInt(hex.slice(1), 16);
    return (0.299 * ((n >> 16) & 255) + 0.587 * ((n >> 8) & 255) + 0.114 * (n & 255)) / 255;
  }

  function hexToHsl(hex) {
    const n = parseInt(hex.slice(1), 16);
    const r = ((n >> 16) & 255) / 255;
    const g = ((n >> 8) & 255) / 255;
    const b = (n & 255) / 255;
    const max = Math.max(r, g, b);
    const min = Math.min(r, g, b);
    const l = (max + min) / 2;
    if (max === min) {
      return { h: 0, s: 0, l: l * 100 };
    }
    const d = max - min;
    const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    let h;
    if (max === r) {
      h = (g - b) / d + (g < b ? 6 : 0);
    } else if (max === g) {
      h = (b - r) / d + 2;
    } else {
      h = (r - g) / d + 4;
    }
    return { h: h * 60, s: s * 100, l: l * 100 };
  }

  /** FNV-1a over a string: a stable 32-bit seed. */
  function hashText(text) {
    let h = 2166136261;
    for (let i = 0; i < text.length; i++) {
      h ^= text.charCodeAt(i);
      h = Math.imul(h, 16777619) >>> 0;
    }
    return h >>> 0;
  }

  /** mulberry32: a small seeded random source, so the same seed draws the same card. */
  function seededRandom(seed) {
    let a = seed >>> 0;
    return () => {
      a = (a + 0x6D2B79F5) >>> 0;
      let t = a;
      t = Math.imul(t ^ (t >>> 15), t | 1);
      t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  /** What gives a station its look: the name, number and everything it plays, plus the "Another look" counter. */
  function logoSeedText(name, number) {
    const parts = [name, number];
    const d = state.draft;
    if (d) {
      const add = sources => (sources || []).forEach(s => parts.push(s.DisplayName || s.Genre || s.Tag || s.ItemId || s.Kind || ''));
      add(d.Pool && d.Pool.Sources);
      ((d.Slots && d.Slots.Blocks) || []).forEach(b => add(b.Sources));
      add(d.Slots && d.Slots.Filler && d.Slots.Filler.Sources);
      ((d.Lineup && d.Lineup.Items) || []).forEach(i => parts.push(i.DisplayName || i.ItemId || ''));
    }
    parts.push('look' + (state.logoLook || 0));
    return parts.join('|').toLowerCase();
  }

  /**
   * Paints the card's background from a seeded random source: one of several patterns in the base color and a
   * neighbouring hue. Overlays stay faint so the text reads on any of them.
   */
  function drawLogoBackground(ctx, color, fg, rnd) {
    const base = hexToHsl(color);
    const shift = (rnd() < 0.5 ? -1 : 1) * (25 + rnd() * 45);
    const partner = hslToHex((base.h + shift + 360) % 360, Math.min(100, base.s + 10), Math.max(12, Math.min(88, base.l + (rnd() < 0.5 ? -9 : 9))));
    const deep = hslToHex(base.h, base.s, Math.max(6, base.l - 14));
    const angle = rnd() * Math.PI * 2;
    const gradient = (from, to) => {
      const g = ctx.createLinearGradient(400 - Math.cos(angle) * 450, 225 - Math.sin(angle) * 450, 400 + Math.cos(angle) * 450, 225 + Math.sin(angle) * 450);
      g.addColorStop(0, from);
      g.addColorStop(1, to);
      return g;
    };
    const style = Math.floor(rnd() * 6);
    ctx.fillStyle = style === 0 ? gradient(color, partner) : color;
    ctx.fillRect(0, 0, 800, 450);
    ctx.save();
    if (style === 1) {
      // Diagonal stripes.
      const width = 30 + rnd() * 60;
      ctx.translate(400, 225);
      ctx.rotate(angle);
      ctx.fillStyle = partner;
      ctx.globalAlpha = 0.22;
      for (let x = -700; x < 700; x += width * 2) {
        ctx.fillRect(x, -700, width, 1400);
      }
    } else if (style === 2) {
      // A grid of dots.
      const gap = 36 + rnd() * 44;
      const radius = gap * (0.12 + rnd() * 0.16);
      const offsetX = rnd() * gap;
      const offsetY = rnd() * gap;
      ctx.fillStyle = fg;
      ctx.globalAlpha = 0.12;
      for (let y = offsetY - gap; y < 450 + gap; y += gap) {
        for (let x = offsetX - gap; x < 800 + gap; x += gap) {
          ctx.beginPath();
          ctx.arc(x, y, radius, 0, Math.PI * 2);
          ctx.fill();
        }
      }
    } else if (style === 3) {
      // Rings spreading from one corner.
      const cx = rnd() < 0.5 ? -40 : 840;
      const cy = rnd() < 0.5 ? -40 : 490;
      const gap = 40 + rnd() * 50;
      ctx.strokeStyle = partner;
      ctx.lineWidth = gap * 0.45;
      ctx.globalAlpha = 0.2;
      for (let r = gap; r < 1100; r += gap) {
        ctx.beginPath();
        ctx.arc(cx, cy, r, 0, Math.PI * 2);
        ctx.stroke();
      }
    } else if (style === 4) {
      // Two or three big tilted slabs.
      const count = 2 + Math.floor(rnd() * 2);
      ctx.fillStyle = partner;
      ctx.globalAlpha = 0.28;
      for (let i = 0; i < count; i++) {
        ctx.save();
        ctx.translate(rnd() * 800, rnd() * 450);
        ctx.rotate(angle + i * (0.5 + rnd()));
        ctx.fillRect(-900, -(40 + rnd() * 120) / 2, 1800, 40 + rnd() * 120);
        ctx.restore();
      }
    } else if (style === 5) {
      // Color bars along the bottom, like a test card.
      ctx.fillStyle = gradient(color, deep);
      ctx.fillRect(0, 0, 800, 450);
      const bars = 6 + Math.floor(rnd() * 3);
      const height = 22 + rnd() * 10; // stays under the channel number
      const width = 800 / bars;
      ctx.globalAlpha = 0.55;
      for (let i = 0; i < bars; i++) {
        ctx.fillStyle = hslToHex((base.h + i * (360 / bars)) % 360, 60, 48);
        ctx.fillRect(i * width, 450 - height, width + 1, height);
      }
    }
    ctx.restore();
    // The soft disc every card has, placed by the seed.
    ctx.fillStyle = fg;
    ctx.globalAlpha = 0.08;
    ctx.beginPath();
    ctx.arc(rnd() < 0.5 ? 120 + rnd() * 80 : 600 + rnd() * 80, rnd() < 0.5 ? 60 + rnd() * 60 : 340 + rnd() * 60, 200 + rnd() * 120, 0, Math.PI * 2);
    ctx.fill();
    ctx.globalAlpha = 1;
  }

  /** Draws an 800 x 450 card: the channel name (up to two lines) and number on a patterned background seeded from the station. Null when the browser cannot draw. */
  function drawLogo(name, number, color, seedText) {
    const canvas = document.createElement('canvas');
    canvas.width = 800;
    canvas.height = 450;
    const ctx = canvas.getContext && canvas.getContext('2d');
    if (!ctx) {
      return null;
    }
    const fg = luminance(color) > 0.6 ? '#111111' : '#ffffff';
    drawLogoBackground(ctx, color, fg, seededRandom(hashText(seedText || name || 'TV')));
    ctx.fillStyle = fg;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    const words = (name || 'TV').trim().split(/\s+/);
    let lines = [words.join(' ')];
    let size = 120;
    const fits = (text, px) => { ctx.font = 'bold ' + px + 'px sans-serif'; return ctx.measureText(text).width <= 700; };
    while (size > 40 && !lines.every(l => fits(l, size))) {
      if (lines.length === 1 && words.length > 1 && size < 96) {
        const mid = Math.ceil(words.length / 2);
        lines = [words.slice(0, mid).join(' '), words.slice(mid).join(' ')];
        size = 110;
      } else {
        size -= 6;
      }
    }
    ctx.font = 'bold ' + size + 'px sans-serif';
    ctx.shadowColor = 'rgba(0,0,0,.35)';
    ctx.shadowBlur = 12;
    const lineHeight = size * 1.15;
    const top = 225 - ((lines.length - 1) * lineHeight) / 2 - (number ? 16 : 0);
    lines.forEach((l, i) => ctx.fillText(l, 400, top + i * lineHeight));
    if (number) {
      ctx.shadowBlur = 0;
      ctx.globalAlpha = 0.85;
      ctx.font = '600 34px sans-serif';
      ctx.fillText('CH ' + number, 400, 400);
      ctx.globalAlpha = 1;
    }
    return canvas;
  }

  function generateLogo() {
    const name = val('#tvsName').trim() || 'TV';
    const number = val('#tvsChannel').trim();
    suggestLogoColor();
    const canvas = drawLogo(name, number, val('#tvsLogoColor') || colorForName(name), logoSeedText(name, number));
    if (!canvas) {
      toast('This browser cannot draw the logo; upload an image instead.');
      return;
    }
    canvas.toBlob(blob => {
      if (!blob) {
        return;
      }
      if (isNewDraft()) {
        state.pendingLogo = blob;
        renderLogo();
        return;
      }
      uploadLogoTo(state.draft.Id, blob, 'logo.png').then(() => {
        state.draft.LogoFileName = 'logo.png';
        renderLogo();
        toast('Logo saved.');
      }).catch(err => alertErrors(err, 'Could not save the logo'));
    }, 'image/png');
  }

  /** POSTs an image to a station's logo endpoint. */
  function uploadLogoTo(id, blob, name) {
    const headers = { 'Content-Type': blob.type || 'application/octet-stream' };
    try {
      headers.Authorization = 'MediaBrowser Token="' + ApiClient.accessToken() + '"';
    } catch (e) {
      /* ignore */
    }
    return fetch(ApiClient.getUrl('JellyStations/Stations/' + id + '/Logo'), { method: 'POST', body: blob, headers: headers })
      .then(response => (response.ok ? name : Promise.reject(response)));
  }

  function uploadLogo(file) {
    if (!file || isNewDraft()) {
      return;
    }
    const hint = $('#tvsLogoHint');
    hint.textContent = 'Uploading…';
    uploadLogoTo(state.draft.Id, file, file.name || 'logo')
      .then(name => {
        state.draft.LogoFileName = name;
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
    d.UpNextCard.Enabled = checked('#tvsUpNextEnabled');
    d.UpNextCard.Corner = val('#tvsUpNextCorner') || 'BottomRight';
    d.UpNextCard.Animation = val('#tvsUpNextAnimation') || 'Fade';
    d.UpNextCard.LeadTimesSeconds = parseLeadTimes(val('#tvsUpNextBefore'));
    d.UpNextCard.ShowSeconds = Math.min(Math.min(60, Math.min.apply(null, d.UpNextCard.LeadTimesSeconds)), Math.max(3, parseInt(val('#tvsUpNextShow'), 10) || 10));
    d.UpNextCard.HeightPercent = Math.min(30, Math.max(8, parseInt(val('#tvsUpNextSize'), 10) || 14));
    d.UpNextCard.Opacity = Math.min(100, Math.max(30, parseInt(val('#tvsUpNextOpacity'), 10) || 85));
    d.UpNextCard.ShowThumbnail = checked('#tvsUpNextThumb');
    d.UpNextCard.ShowTimes = checked('#tvsUpNextTimes');
    d.UpNextCard.TwentyFourHourClock = checked('#tvsUpNextClock24');
    d.ExcludedTags = parseTagList(val('#tvsExcludedTags'));
    d.UseGlobalExcludedTags = checked('#tvsUseGlobalTags');
    d.MinRuntimeMinutes = Math.max(0, parseInt(val('#tvsMinRuntime'), 10) || 0);
    d.Bumpers.Enabled = checked('#tvsBumpersEnabled');
    d.Bumpers.BreakMinutes = Math.max(0, parseInt(val('#tvsBumperBreak'), 10) || 0);
    d.Bumpers.MaxClipMinutes = Math.max(1, parseInt(val('#tvsBumperMax'), 10) || 5);
    d.Bumpers.PreRoll.Enabled = checked('#tvsPreRollEnabled');
    d.Bumpers.PostRoll.Enabled = checked('#tvsPostRollEnabled');
    d.Bumpers.Weather.Enabled = checked('#tvsBumperWeatherEnabled');
    d.Bumpers.Weather.Minutes = Math.min(10, Math.max(1, parseInt(val('#tvsBumperWeatherMinutes'), 10) || 2));
    d.Bumpers.Weather.Position = val('#tvsBumperWeatherPosition') || 'BeforeProgram';
    d.Mode = val('#tvsMode') || 'Pool';
    d.Pool.Order = val('#tvsPoolOrder') || 'Shuffle';
    d.Pool.AvoidSameSeriesBackToBack = checked('#tvsPoolAvoid');
    d.Pool.CooldownHours = Math.min(168, Math.max(0, parseInt(val('#tvsPoolCooldown'), 10) || 0));
    d.Pool.MarathonMin = Math.max(1, parseInt(val('#tvsMarathonMin'), 10) || 1);
    d.Pool.MarathonMax = Math.max(d.Pool.MarathonMin, parseInt(val('#tvsMarathonMax'), 10) || 1);
    d.Slots.Filler.Order = val('#tvsFillerOrder') || 'Shuffle';
    const w = d.Weather;
    w.Units = val('#tvsWeatherUnits') || 'Imperial';
    w.Theme = val('#tvsWeatherTheme') || 'Classic';
    w.ScreenSeconds = Math.min(60, Math.max(4, parseInt(val('#tvsWeatherScreenSeconds'), 10) || 10));
    w.GuideEntryMinutes = Math.min(240, Math.max(15, parseInt(val('#tvsWeatherEntryMinutes'), 10) || 60));
    w.OnTheEights = checked('#tvsWeatherEights');
    w.TwentyFourHourClock = checked('#tvsWeatherClock24');
    w.ShowTitleCards = checked('#tvsWeatherTitleCards');
    w.TitleCardSeconds = Math.min(10, Math.max(1, parseInt(val('#tvsWeatherTitleCardSeconds'), 10) || 3));
    w.ShowAlerts = checked('#tvsWeatherAlerts');
    w.ShowHourly = checked('#tvsWeatherHourly');
    w.ShowDaily = checked('#tvsWeatherDaily');
    w.ShowRadar = checked('#tvsWeatherRadar');
    w.RadarZoom = val('#tvsWeatherRadarZoom') || 'State';
    w.ShowForecastRadar = checked('#tvsWeatherForecastRadar');
    w.ForecastRadarHours = parseInt(val('#tvsWeatherForecastHours'), 10) || 3;
    w.ForecastRadarStepMinutes = parseInt(val('#tvsWeatherForecastStep'), 10) || 15;
    w.ShowTravelCities = checked('#tvsWeatherTravel');
    w.ShowLogo = checked('#tvsWeatherLogo');
    w.GuideTitle = val('#tvsWeatherGuideTitle').trim() || null;
    w.MusicEnabled = checked('#tvsWeatherMusic');
    w.MusicFolder = val('#tvsWeatherMusicFolder').trim() || null;
    w.MusicVolumePercent = Math.min(150, Math.max(20, parseInt(val('#tvsWeatherVolume'), 10) || 100));
    w.ShowMusicCredit = checked('#tvsWeatherCredit');
    w.NormalizeMusic = checked('#tvsWeatherNormalize');
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
    ((p.Weather && p.Weather.Locations) || []).forEach(l => {
      if (!l.Id || l.Id === ZERO_GUID) {
        delete l.Id; // server assigns
      }
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
    if (usesWeatherPayload(p) && !(p.Weather.Locations || []).some(weatherPlaceValid)) {
      errors.push(p.Mode === 'Weather' ? 'Add at least one place to the weather channel.' : 'Add at least one place in the Weather section for the weather block or breaks.');
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
        if (b.Kind !== 'Weather' && (!b.Sources || !b.Sources.length)) {
          errors.push(label + ' has no content.');
        }
        if (b.RepeatEveryMinutes && b.RepeatEveryMinutes < 5) {
          errors.push(label + ': repeat every 5 minutes or more, or 0 for once.');
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
      if (state.editorStep !== 'all') {
        showStep(stepForErrors(errors));
      }
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
      if (state.pendingLogo && saved && saved.Id) {
        const logo = state.pendingLogo;
        state.pendingLogo = null;
        after = uploadLogoTo(saved.Id, logo, 'logo.png').catch(err => alertErrors(err, 'Station saved, but the logo could not be uploaded'));
      }
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
      toast('Refreshed: ' + (result ? result.ItemCount : 0) + ' items, ' + excluded + ' excluded.' +
        (result && result.ContentChanged ? ' Content changed – revision ' + result.Revision + '.' : ' Content unchanged.'));
    }).catch(err => alertErrors(err, 'Refresh content failed')).then(() => loading(false));
  }

  function renderExcluded() {
    const list = clear($('#tvsExcludedList'));
    const info = $('#tvsExcludedInfo');
    const compiled = state.detail && state.detail.Compiled;
    if (isNewDraft()) {
      info.textContent = 'The station has not been scanned yet. Save it to see which items (if any) are skipped.';
      return;
    }
    if (!compiled) {
      info.textContent = 'No content scanned yet. Save the station, or press Refresh content.';
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
        : r.IsMusic ? r.Title + (r.EpisodeTitle ? ' – ' + r.EpisodeTitle : '') : r.Title;
      const btn = r.IsMusic
        ? el('span', { className: 'tvs-secondary', text: 'music' })
        : r.ExcludedByRule
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
      Overflow: 'FillerRemainder',
      RemainderMayOverrun: true,
      MaxStartDelayMinutes: 30,
      Kind: 'Content',
      RepeatEveryMinutes: 0,
      RepeatUntilLocal: '',
      Recurrence: 'Weekly',
      IntervalWeeks: 2,
      RecurrenceStart: '',
      WeeksOfMonth: [],
      Dates: []
    };
  }

  /** Clamps the recurrence fields of a block or template in place. */
  function normalizeRecurrence(b) {
    b.Recurrence = RECURRENCES.indexOf(b.Recurrence) >= 0 ? b.Recurrence : 'Weekly';
    b.IntervalWeeks = Math.min(52, Math.max(1, parseInt(b.IntervalWeeks, 10) || 2));
    b.RecurrenceStart = isIsoDate(b.RecurrenceStart) ? b.RecurrenceStart : '';
    b.WeeksOfMonth = (Array.isArray(b.WeeksOfMonth) ? b.WeeksOfMonth : []).map(w => parseInt(w, 10)).filter(w => w >= 1 && w <= 5).filter((w, i, a) => a.indexOf(w) === i).sort();
    b.Dates = (Array.isArray(b.Dates) ? b.Dates : []).filter(isIsoDate).filter((d, i, a) => a.indexOf(d) === i).sort();
    return b;
  }

  function isIsoDate(text) {
    return /^\d{4}-\d{2}-\d{2}$/.test(String(text || '')) && !isNaN(new Date(text + 'T00:00:00').getTime());
  }

  /** "Nov 14, 2026" from "2026-11-14", read as a plain calendar date. */
  function fmtIsoDate(iso) {
    const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(iso || ''));
    if (!m) {
      return iso || '';
    }
    return new Date(parseInt(m[1], 10), parseInt(m[2], 10) - 1, parseInt(m[3], 10)).toLocaleDateString([], { year: 'numeric', month: 'short', day: 'numeric' });
  }

  function todayIso() {
    const d = new Date();
    return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate());
  }

  /** The repeat rule of a block in a few words, or '' for plain weekly. */
  function recurrenceText(b) {
    switch (b.Recurrence) {
      case 'EveryNWeeks':
        return b.IntervalWeeks === 1 ? '' : 'every ' + b.IntervalWeeks + ' weeks';
      case 'WeeksOfMonth':
        return (b.WeeksOfMonth || []).length ? (b.WeeksOfMonth || []).map(w => WEEK_LABELS[w] || w).join(', ') + ' week of the month' : 'no weeks chosen';
      case 'Dates': {
        const dates = b.Dates || [];
        if (!dates.length) {
          return 'no dates';
        }
        return dates.length <= 3 ? dates.map(fmtIsoDate).join(', ') : dates.length + ' dates, ' + fmtIsoDate(dates[0]) + ' to ' + fmtIsoDate(dates[dates.length - 1]);
      }
      default:
        return '';
    }
  }

  function blockSummary(b) {
    const days = DAYS.filter(d => (b.Days || []).indexOf(d) >= 0);
    let dayText;
    if (b.Recurrence === 'Dates') {
      dayText = recurrenceText(b);
    } else if (days.length === 7) {
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
    if (b.Recurrence !== 'Dates' && recurrenceText(b)) {
      dayText += ', ' + recurrenceText(b);
    }
    const startMin = parseHm(b.StartLocal);
    const dur = parseInt(b.DurationMinutes, 10) || 0;
    let timeText = b.StartLocal || '??:??';
    if (startMin !== null && dur > 0) {
      const endMin = (startMin + dur) % 1440;
      timeText += '–' + pad2(Math.floor(endMin / 60)) + ':' + pad2(endMin % 60) + (startMin + dur >= 1440 ? ' (+1d)' : '');
    }
    if (b.RepeatEveryMinutes > 0) {
      timeText += ', every ' + fmtDur(b.RepeatEveryMinutes * 60) + ' until ' + (b.RepeatUntilLocal || 'midnight');
    }
    const sources = (b.Sources || []).length;
    const what = b.Kind === 'Weather' ? 'local weather' : sources + ' source' + (sources === 1 ? '' : 's');
    return dayText + ' · ' + timeText + ' · ' + fmtDur(dur * 60) + ' · ' + what;
  }

  /** Start minutes of a block on a day it occurs: its start, then every repeat until its limit or midnight. */
  function blockStarts(b) {
    const start = parseHm(b.StartLocal);
    if (start === null) {
      return [];
    }
    const starts = [start];
    const every = parseInt(b.RepeatEveryMinutes, 10) || 0;
    if (every >= 5) {
      const until = parseHm(b.RepeatUntilLocal);
      const limit = until !== null && until > start ? until : 1440;
      for (let t = start + every; t <= limit && t < 1440; t += every) {
        starts.push(t);
      }
    }
    return starts;
  }

  function renderBlocks() {
    const wrap = clear($('#tvsBlocks'));
    const blocks = state.draft.Slots.Blocks;
    show($('#tvsBlocksEmpty'), blocks.length === 0);
    blocks.forEach((b, i) => wrap.appendChild(blockCard(b, i)));
    renderBlockWarnings();
    renderWeekGrid();
  }

  // ---- Week grid: paint blocks onto seven days ----------------------------------------------

  const GRID_ROW_PX = 12; // one 30-minute row
  const GRID_ROWS = 48;
  const BLOCK_COLORS = ['#2f80ed', '#eb5757', '#27ae60', '#f2994a', '#9b51e0', '#1abc9c', '#e84393', '#c0a000'];

  /** Days a Dates-recurrence block falls on, for drawing. */
  function blockDays(b) {
    if (b.Recurrence !== 'Dates') {
      return b.Days || [];
    }
    return (b.Dates || []).map(d => {
      const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(d);
      return m ? DAYS[(new Date(parseInt(m[1], 10), parseInt(m[2], 10) - 1, parseInt(m[3], 10)).getDay() + 6) % 7] : null;
    }).filter((d, i, a) => d && a.indexOf(d) === i);
  }

  function renderWeekGrid() {
    const host = $('#tvsWeekGrid');
    if (!host || !state.draft) {
      return;
    }
    clear(host);
    const blocks = state.draft.Slots.Blocks;
    const grid = el('div', { className: 'tvs-week-grid' });
    grid.appendChild(el('div', { className: 'tvs-week-corner' }));
    DAYS.forEach(d => grid.appendChild(el('div', { className: 'tvs-week-day', text: DAY_SHORT[d] })));
    const times = el('div', { className: 'tvs-week-times' });
    for (let h = 0; h < 24; h += 2) {
      times.appendChild(el('div', { className: 'tvs-week-time', style: { top: (h * 2 * GRID_ROW_PX) + 'px' }, text: pad2(h) + ':00' }));
    }
    grid.appendChild(times);
    const cols = DAYS.map((d, di) => {
      const col = el('div', { className: 'tvs-week-col', 'data-day': String(di), style: { height: (GRID_ROWS * GRID_ROW_PX) + 'px' }, title: DAYS[di] });
      for (let r = 2; r < GRID_ROWS; r += 2) {
        col.appendChild(el('div', { className: 'tvs-week-hour' + (r % 12 === 0 ? ' major' : ''), style: { top: (r * GRID_ROW_PX) + 'px' } }));
      }
      grid.appendChild(col);
      return col;
    });

    blocks.forEach((b, bi) => {
      const start = parseHm(b.StartLocal);
      const dur = parseInt(b.DurationMinutes, 10) || 0;
      if (start === null || dur <= 0) {
        return;
      }
      blockDays(b).forEach(day => {
        const di = DAYS.indexOf(day);
        if (di < 0) {
          return;
        }
        blockStarts(b).forEach(s => {
          placeBlockRect(cols[di], s, Math.min(dur, 1440 - s), b, bi, false);
          if (s + dur > 1440) {
            placeBlockRect(cols[(di + 1) % 7], 0, Math.min(s + dur - 1440, 1440), b, bi, true);
          }
        });
      });
    });

    host.appendChild(grid);
    wireWeekPainting(grid, cols);
  }

  function placeBlockRect(col, startMin, durMin, b, bi, continuation) {
    const color = BLOCK_COLORS[bi % BLOCK_COLORS.length];
    const top = startMin / 30 * GRID_ROW_PX;
    const height = Math.max(GRID_ROW_PX, durMin / 30 * GRID_ROW_PX) - 1;
    const rect = el('div', {
      className: 'tvs-week-block' + (b.Recurrence !== 'Weekly' ? ' sometimes' : '') + (continuation ? ' cont' : ''),
      style: { top: top + 'px', height: height + 'px', background: color + '66', borderLeftColor: color },
      title: (b.Name || 'Untitled block') + '\n' + blockSummary(b),
      text: height >= 20 ? (b.Name || 'Untitled block') : ''
    });
    rect.addEventListener('click', ev => {
      ev.stopPropagation();
      openBlockCard(bi);
    });
    col.appendChild(rect);
  }

  /** Expands a block's card and scrolls it into view. */
  function openBlockCard(index) {
    const b = state.draft.Slots.Blocks[index];
    if (!b) {
      return;
    }
    state.expandedBlocks[b._key] = true;
    renderBlocks();
    const card = $('#tvsBlocks').children[index];
    if (card && typeof card.scrollIntoView === 'function') {
      card.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    }
  }

  function wireWeekPainting(grid, cols) {
    let paint = null;
    const rowAt = (col, clientY) => {
      const top = col.getBoundingClientRect().top;
      return Math.min(GRID_ROWS - 1, Math.max(0, Math.floor((clientY - top) / GRID_ROW_PX)));
    };
    const colAt = clientX => {
      let best = 0;
      cols.forEach((c, i) => {
        const r = c.getBoundingClientRect();
        if (clientX >= r.left) {
          best = i;
        }
      });
      return best;
    };
    const updateSelection = () => {
      const c0 = Math.min(paint.c0, paint.c1);
      const c1 = Math.max(paint.c0, paint.c1);
      const r0 = Math.min(paint.r0, paint.r1);
      const r1 = Math.max(paint.r0, paint.r1);
      const g = grid.getBoundingClientRect();
      const a = cols[c0].getBoundingClientRect();
      const z = cols[c1].getBoundingClientRect();
      Object.assign(paint.sel.style, {
        left: (a.left - g.left) + 'px',
        width: (z.right - a.left) + 'px',
        top: (a.top - g.top + r0 * GRID_ROW_PX) + 'px',
        height: ((r1 - r0 + 1) * GRID_ROW_PX) + 'px'
      });
    };
    grid.addEventListener('pointerdown', ev => {
      const col = ev.target.closest ? ev.target.closest('.tvs-week-col') : null;
      if (!col || (ev.target.closest && ev.target.closest('.tvs-week-block')) || ev.button) {
        return;
      }
      const c = parseInt(col.getAttribute('data-day'), 10);
      const r = rowAt(col, ev.clientY);
      paint = { c0: c, r0: r, c1: c, r1: r, moved: false, sel: el('div', { className: 'tvs-week-select' }) };
      grid.appendChild(paint.sel);
      if (grid.setPointerCapture && ev.pointerId !== undefined) {
        try { grid.setPointerCapture(ev.pointerId); } catch (e) { /* not a pointer event */ }
      }
      updateSelection();
      ev.preventDefault();
    });
    grid.addEventListener('pointermove', ev => {
      if (!paint) {
        return;
      }
      const c = colAt(ev.clientX);
      const r = rowAt(cols[c], ev.clientY);
      if (c !== paint.c1 || r !== paint.r1) {
        paint.moved = true;
      }
      paint.c1 = c;
      paint.r1 = r;
      updateSelection();
    });
    const finish = () => {
      if (!paint) {
        return;
      }
      const p = paint;
      paint = null;
      p.sel.remove();
      addBlockFromPaint(p);
    };
    grid.addEventListener('pointerup', finish);
    grid.addEventListener('pointercancel', () => {
      if (paint) {
        paint.sel.remove();
        paint = null;
      }
    });
  }

  /** A painted range becomes a block: a plain click gives one hour, a drag gives the rows and days covered. */
  function addBlockFromPaint(p) {
    const c0 = Math.min(p.c0, p.c1);
    const c1 = Math.max(p.c0, p.c1);
    const r0 = Math.min(p.r0, p.r1);
    let rows = Math.abs(p.r1 - p.r0) + 1;
    if (!p.moved) {
      rows = Math.min(2, GRID_ROWS - r0);
    }
    const templateId = val('#tvsBlockTemplateSelect');
    const template = state.blockTemplates.find(t => t.Id === templateId);
    const block = template ? blockFromTemplate(template) : newBlock();
    block.Days = DAYS.slice(c0, c1 + 1);
    block.StartLocal = pad2(Math.floor(r0 / 2)) + ':' + (r0 % 2 ? '30' : '00');
    block.DurationMinutes = rows * 30;
    if (block.Recurrence === 'Dates') {
      block.Recurrence = 'Weekly';
    }
    state.draft.Slots.Blocks.push(block);
    state.expandedBlocks[block._key] = true;
    setVal('#tvsBlockTemplateSelect', '');
    renderBlocks();
    openBlockCard(state.draft.Slots.Blocks.length - 1);
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
        inputHtml(p + 'name', 'Block name', 'text', {}, 'Shown as the source label in the guide preview; for a weather block, its guide title (empty = the Weather section\'s guide title).') +
        selectHtml(p + 'kind', 'What plays', [['Content', 'Programs from the sources below'], ['Weather', 'Local weather – the station\'s weather screens']], 'A weather block needs places in the Weather section, which appears while one exists.') +
        inputHtml(p + 'start', 'Start time (station local)', 'time', { step: 60 }) +
        inputHtml(p + 'dur', 'Duration (minutes)', 'number', { min: 1, max: 1440, step: 1 }) +
        inputHtml(p + 'every', 'Repeat every (minutes; 0 = once)', 'number', { min: 0, max: 1440, step: 5 }, 'Each repeat has the full duration. "Local weather at the top of the hour" is a 5-minute weather block at 06:00 repeating every 60 minutes until 23:00.') +
        inputHtml(p + 'until', 'Repeat until (station local; empty = midnight)', 'time', { step: 60 }) +
        inputHtml(p + 'delay', 'Start up to this many minutes late so the previous program can finish', 'number', { min: 0, max: 240, step: 5 }, '0 = start on time and cut the program. When the block starts late it keeps its full duration, unless another block follows.') +
      '</div>' +
      '<div class="tvs-grid2" data-kind="Content">' +
        selectHtml(p + 'order', 'Play order', [['Sequential', 'Sequential – continue where the last occurrence left off'], ['Shuffle', 'Shuffle – re-rolled every occurrence'], ['ShuffleEpisodesInOrder', 'Shuffle shows, episodes in order – re-rolled every occurrence']]) +
        selectHtml(p + 'overflow', 'When the next item does not fit', [['FillerRemainder', 'Hand the remainder to ' + betweenBlocksName()], ['TruncateLast', 'Play it anyway and cut it off at the block end']]) +
      '</div>' +
      '<label class="emby-checkbox-label tvs-block-overrun" data-kind="Content"><input type="checkbox" id="' + p + 'overrun"><span>When ' + betweenBlocksName() + ' takes the remainder, let its last program finish past the block end instead of cutting it (unless another block starts right then)</span></label>' +
      '<div class="tvs-grid2">' +
        selectHtml(p + 'rec', 'Repeats', [['Weekly', 'Every week'], ['EveryNWeeks', 'Every N weeks'], ['WeeksOfMonth', 'Certain weeks of the month'], ['Dates', 'Only on specific dates']], 'Weeks run Monday to Sunday, so a Saturday-Sunday weekend stays together. An earlier block in the list wins when two blocks coincide.') +
      '</div>' +
      '<div class="tvs-grid2" data-rec="EveryNWeeks">' +
        inputHtml(p + 'interval', 'Every N weeks', 'number', { min: 1, max: 52, step: 1 }) +
        inputHtml(p + 'recstart', 'In the week of', 'date', {}, 'Any date in a week the block occurs; the pattern repeats both ways from it.') +
      '</div>' +
      '<div data-rec="WeeksOfMonth">' +
        '<div class="tvs-days-label">Weeks of the month (a week belongs to the month of its Monday)</div>' +
        '<div class="tvs-days">' +
          [1, 2, 3, 4, 5].map(w => '<label class="tvs-day"><input type="checkbox" data-week="' + w + '"><span>' + WEEK_LABELS[w] + '</span></label>').join('') +
        '</div>' +
      '</div>' +
      '<div data-rec="Dates">' +
        '<div class="tvs-grid2">' +
          inputHtml(p + 'date', 'Add a date', 'date', {}, 'The block runs at the start time on each listed date. The day picker is ignored.') +
          '<div class="inputContainer"><button is="emby-button" type="button" id="' + p + 'addDate" class="raised emby-button"><span>Add date</span></button></div>' +
        '</div>' +
        '<div class="tvs-chips" id="' + p + 'dates"></div>' +
      '</div>' +
      '<div data-rec-days>' +
        '<div class="tvs-days-label">Days</div>' +
        '<div class="tvs-days">' +
          DAYS.map(d => '<label class="tvs-day"><input type="checkbox" data-day="' + d + '"><span>' + DAY_SHORT[d] + '</span></label>').join('') +
        '</div>' +
      '</div>' +
      '<div data-kind="Content">' +
        '<div class="tvs-days-label">Content</div>' +
        '<div class="tvs-block-picker"></div>' +
      '</div>';

    const q = id => body.querySelector('#' + p + id);
    q('name').value = b.Name || '';
    q('start').value = b.StartLocal || '';
    q('dur').value = b.DurationMinutes;
    q('kind').value = b.Kind === 'Weather' ? 'Weather' : 'Content';
    q('every').value = b.RepeatEveryMinutes || 0;
    q('until').value = b.RepeatUntilLocal || '';
    q('order').value = b.Order || 'Sequential';
    q('overflow').value = b.Overflow || 'FillerRemainder';
    q('overrun').checked = !!b.RemainderMayOverrun;
    q('delay').value = b.MaxStartDelayMinutes || 0;
    const showKindPanels = () => $all('[data-kind]', body).forEach(panel => show(panel, panel.getAttribute('data-kind') === (b.Kind || 'Content')));
    showKindPanels();
    q('kind').addEventListener('change', () => {
      b.Kind = q('kind').value === 'Weather' ? 'Weather' : 'Content';
      showKindPanels();
      refreshHead();
      updateWeatherPanel();
    });
    q('every').addEventListener('input', () => { b.RepeatEveryMinutes = Math.min(1440, Math.max(0, parseInt(q('every').value, 10) || 0)); refreshHead(); });
    q('until').addEventListener('change', () => { b.RepeatUntilLocal = parseHm(q('until').value) === null ? '' : q('until').value; refreshHead(); });
    normalizeRecurrence(b);
    q('rec').value = b.Recurrence;
    q('interval').value = b.IntervalWeeks;
    q('recstart').value = b.RecurrenceStart;

    const refreshHead = () => {
      titleBox.firstChild.textContent = b.Name || 'Untitled block';
      titleBox.lastChild.textContent = blockSummary(b);
      renderBlockWarnings();
      renderWeekGrid();
    };
    const showRecurrencePanels = () => {
      $all('[data-rec]', body).forEach(panel => show(panel, panel.getAttribute('data-rec') === b.Recurrence));
      show(body.querySelector('[data-rec-days]'), b.Recurrence !== 'Dates');
    };
    const renderDates = () => {
      const box = clear(q('dates'));
      if (!b.Dates.length) {
        box.appendChild(el('span', { className: 'tvs-secondary', text: 'No dates yet.' }));
        return;
      }
      b.Dates.forEach(d => {
        const chip = el('span', { className: 'tvs-chip' }, [el('span', { className: 'label', text: fmtIsoDate(d) })]);
        const remove = el('button', { type: 'button', title: 'Remove date', 'aria-label': 'Remove ' + fmtIsoDate(d), text: '×' });
        remove.addEventListener('click', () => {
          b.Dates = b.Dates.filter(x => x !== d);
          renderDates();
          refreshHead();
        });
        chip.appendChild(remove);
        box.appendChild(chip);
      });
    };
    showRecurrencePanels();
    renderDates();
    q('rec').addEventListener('change', () => {
      b.Recurrence = q('rec').value;
      if (b.Recurrence === 'EveryNWeeks' && !b.RecurrenceStart) {
        b.RecurrenceStart = todayIso();
        q('recstart').value = b.RecurrenceStart;
      }
      showRecurrencePanels();
      refreshHead();
    });
    q('interval').addEventListener('input', () => { b.IntervalWeeks = Math.min(52, Math.max(1, parseInt(q('interval').value, 10) || 1)); refreshHead(); });
    q('recstart').addEventListener('change', () => { b.RecurrenceStart = isIsoDate(q('recstart').value) ? q('recstart').value : ''; refreshHead(); });
    const addDate = () => {
      const d = q('date').value;
      if (!isIsoDate(d)) {
        toast('Pick a date first.');
        return;
      }
      if (b.Dates.indexOf(d) < 0) {
        b.Dates = b.Dates.concat([d]).sort();
      }
      q('date').value = '';
      renderDates();
      refreshHead();
    };
    q('addDate').addEventListener('click', addDate);
    q('date').addEventListener('keydown', ev => {
      if (ev.key === 'Enter') {
        ev.preventDefault();
        addDate();
      }
    });
    $all('input[data-week]', body).forEach(cb => {
      const week = parseInt(cb.getAttribute('data-week'), 10);
      cb.checked = b.WeeksOfMonth.indexOf(week) >= 0;
      cb.parentNode.classList.toggle('active', cb.checked);
      cb.addEventListener('change', () => {
        const set = new Set(b.WeeksOfMonth);
        if (cb.checked) {
          set.add(week);
        } else {
          set.delete(week);
        }
        b.WeeksOfMonth = [1, 2, 3, 4, 5].filter(w => set.has(w));
        cb.parentNode.classList.toggle('active', cb.checked);
        refreshHead();
      });
    });
    q('name').addEventListener('input', () => { b.Name = q('name').value; refreshHead(); });
    q('start').addEventListener('change', () => { b.StartLocal = q('start').value; refreshHead(); });
    q('dur').addEventListener('input', () => { b.DurationMinutes = parseInt(q('dur').value, 10) || 0; refreshHead(); });
    q('order').addEventListener('change', () => { b.Order = q('order').value; });
    q('overflow').addEventListener('change', () => { b.Overflow = q('overflow').value; });
    q('overrun').addEventListener('change', () => { b.RemainderMayOverrun = q('overrun').checked; });
    q('delay').addEventListener('input', () => { b.MaxStartDelayMinutes = Math.min(240, Math.max(0, parseInt(q('delay').value, 10) || 0)); });

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
      const days = b.Recurrence === 'Dates'
        ? (b.Dates || []).map(d => { const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(d); return m ? DAYS[(new Date(parseInt(m[1], 10), parseInt(m[2], 10) - 1, parseInt(m[3], 10)).getDay() + 6) % 7] : null; }).filter((d, i, a) => d && a.indexOf(d) === i)
        : (b.Days || []);
      days.forEach(day => {
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
        const sparse = (blocks[a.bi].Recurrence || 'Weekly') !== 'Weekly' || (blocks[c.bi].Recurrence || 'Weekly') !== 'Weekly';
        warnings.push('“' + nameA + '” and “' + nameC + '” overlap on ' + a.day + (sparse ? ' when both occur' : '') + '. The earlier block in the list wins; the other is cut.');
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
      if (b.Recurrence === 'Dates') {
        if (!(b.Dates || []).length) {
          warnings.push(label + ' has no dates and will never air.');
        }
      } else if (!(b.Days || []).length) {
        warnings.push(label + ' has no days selected and will never air.');
      }
      if (b.Recurrence === 'WeeksOfMonth' && !(b.WeeksOfMonth || []).length) {
        warnings.push(label + ' has no week of the month chosen and will never air.');
      }
      if (b.Kind !== 'Weather' && !(b.Sources || []).length) {
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
      Overflow: t.Overflow || 'FillerRemainder',
      RemainderMayOverrun: !!t.RemainderMayOverrun,
      MaxStartDelayMinutes: Math.min(240, Math.max(0, parseInt(t.MaxStartDelayMinutes, 10) || 0)),
      Recurrence: t.Recurrence,
      IntervalWeeks: t.IntervalWeeks,
      RecurrenceStart: t.RecurrenceStart,
      WeeksOfMonth: (t.WeeksOfMonth || []).slice(),
      Dates: (t.Dates || []).slice()
    };
    normalizeRecurrence(block);
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
      show($('#tvsSuggestWelcome'), state.stations.length === 0);
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
  // Not on a channel: library titles no station plays
  // ------------------------------------------------------------------------------------------

  function openCoverage(refresh) {
    loading(true);
    const q = { includeDisabled: checked('#tvsCoverageDisabled') };
    if (refresh) {
      q.refresh = true;
    }
    api('GET', 'Library/Coverage', null, q).then(c => {
      state.coverage = c;
      renderCoverage();
      showView('coverage');
    }).catch(err => alertErrors(err, 'Could not compare the library with the channels')).then(() => loading(false));
  }

  function plural(n, word) {
    return n + ' ' + word + (n === 1 ? '' : 's');
  }

  function renderCoverage() {
    const c = state.coverage;
    const box = clear($('#tvsCoverageList'));
    renderErrorBox($('#tvsCoverageError'), null);
    if (!c) {
      return;
    }
    const t = c.Totals || {};
    const summary = clear($('#tvsCoverageSummary'));
    const moviesOff = (t.Movies || 0) - (t.MoviesOnChannels || 0);
    const seriesOff = (c.Series || []).length;
    summary.appendChild(el('span', { text: plural(c.Channels || 0, 'channel') + ' counted. ' }));
    summary.appendChild(el('strong', { text: moviesOff + ' of ' + plural(t.Movies || 0, 'movie') }));
    summary.appendChild(el('span', { text: ' and ' }));
    summary.appendChild(el('strong', { text: seriesOff + ' of ' + plural(t.Series || 0, 'show') }));
    summary.appendChild(el('span', { text: ' are not on a channel' + (t.SeriesPartly ? ' (' + plural(t.SeriesPartly, 'show') + ' only partly)' : '') + ': ' + fmtHours(t.HoursNotOnChannels) + ' of ' + fmtHours(t.HoursTotal) + '.' }));

    const filter = val('#tvsCoverageFilter').trim().toLowerCase();
    const matches = parts => !filter || parts.join(' ').toLowerCase().indexOf(filter) >= 0;
    const series = (c.Series || []).filter(s => matches([s.Name, s.Year, s.Rating, (s.Genres || []).join(' '), s.LibraryName]));
    const movies = (c.Movies || []).filter(m => matches([m.Title, m.Year, m.Rating, (m.Genres || []).join(' '), m.LibraryName]));
    show($('#tvsCoverageEmpty'), (c.Movies || []).length === 0 && (c.Series || []).length === 0);
    if (series.length) {
      box.appendChild(coverageSection('Shows', series.length, series, true));
    }
    if (movies.length) {
      box.appendChild(coverageSection('Movies', movies.length, movies, false));
    }
    if (filter && !series.length && !movies.length && ((c.Movies || []).length || (c.Series || []).length)) {
      box.appendChild(el('div', { className: 'tvs-secondary', text: 'Nothing matches the filter.' }));
    }
    renderCoverageTargets();
    updateCoverageCount();
  }

  /** One table of movies or shows with a tick box per row and one in the header for the whole table. */
  function coverageSection(title, count, rows, isSeries) {
    const wrap = el('div', { className: 'tvs-tablewrap' });
    const all = el('input', { type: 'checkbox', title: 'Tick every ' + (isSeries ? 'show' : 'movie') + ' shown', 'aria-label': 'Select all' });
    const boxes = [];
    all.addEventListener('change', () => {
      boxes.forEach(b => { b.checked = all.checked; });
      updateCoverageCount();
    });
    wrap.appendChild(el('div', { className: 'tvs-coverage-head' }, [
      el('h3', { className: 'sectionTitle', text: title }),
      el('span', { className: 'tvs-secondary', text: plural(count, isSeries ? 'show' : 'movie') })
    ]));
    const table = el('table', { className: 'tvs-table' });
    table.appendChild(el('thead', null, el('tr', null, [
      el('th', { className: 'pick' }, all),
      el('th', { text: 'Title' }),
      el('th', { text: 'Year' }),
      el('th', { text: 'Rating' }),
      el('th', { text: isSeries ? 'Episodes' : 'Length' }),
      el('th', { text: 'Genres' }),
      el('th', { text: 'Library' })
    ])));
    const tbody = el('tbody');
    rows.forEach(r => {
      const name = isSeries ? r.Name : r.Title;
      const cb = el('input', { type: 'checkbox', 'data-kind': isSeries ? 'Series' : 'Movie', 'data-id': r.Id, 'data-name': name, 'aria-label': name });
      cb.addEventListener('change', () => {
        if (!cb.checked) {
          all.checked = false;
        }
        updateCoverageCount();
      });
      boxes.push(cb);
      const amount = isSeries
        ? [el('span', { text: plural(r.Episodes, 'episode') + ' · ' + fmtHours(r.Hours) }),
          r.EpisodesOnChannels ? badge(r.EpisodesOnChannels + ' already on a channel', 'warn', 'Some episodes of this show are on a channel; the rest are not. Adding the show again gives the channel every episode.') : null]
        : [el('span', { text: r.Minutes + ' min' })];
      tbody.appendChild(el('tr', null, [
        el('td', { className: 'pick' }, el('label', null, cb)),
        el('td', { text: name }),
        el('td', { className: 'nowrap', text: r.Year || '' }),
        el('td', { className: 'nowrap', text: r.Rating || '' }),
        el('td', { className: 'nowrap' }, amount),
        el('td', { text: (r.Genres || []).slice(0, 3).join(', ') }),
        el('td', { className: 'nowrap', text: r.LibraryName || '' })
      ]));
    });
    table.appendChild(tbody);
    wrap.appendChild(table);
    return wrap;
  }

  function renderCoverageTargets() {
    const select = $('#tvsCoverageTarget');
    const current = select.value;
    clear(select);
    select.appendChild(el('option', { value: '', text: state.stations.length ? 'Choose a channel…' : 'No channels yet' }));
    state.stations.forEach(s => select.appendChild(el('option', { value: s.Id, text: (s.ChannelNumber ? s.ChannelNumber + ' ' : '') + s.Name })));
    select.value = current;
    updateCoverageCount();
  }

  /** The ticked rows as content sources, in the shape the source picker produces. */
  function selectedCoverage() {
    return $all('#tvsCoverageList input[type=checkbox][data-id]:checked').map(cb => ({
      Kind: cb.getAttribute('data-kind'),
      ItemId: cb.getAttribute('data-id'),
      ItemKinds: [],
      Weight: 1,
      DisplayName: cb.getAttribute('data-name')
    }));
  }

  function updateCoverageCount() {
    const n = selectedCoverage().length;
    $('#tvsCoverageCount').textContent = n ? n + ' selected' : '';
    $('#tvsBtnCoverageNew').disabled = n === 0;
    $('#tvsBtnCoverageAdd').disabled = n === 0 || !val('#tvsCoverageTarget');
  }

  function newChannelFromCoverage() {
    const sources = selectedCoverage();
    if (!sources.length) {
      return;
    }
    openEditor(null, {
      name: null,
      apply: d => {
        d.Pool.Sources = sources;
        d.PaddingByLength = true;
        d.PaddingGridMinutes = 0;
      }
    });
    toast(plural(sources.length, 'title') + ' added; name the channel and save.');
  }

  /** Appends the ticked titles to an existing station: its pool, its filler (Slots) or its lineup (Lineup). */
  function addCoverageToStation() {
    const sources = selectedCoverage();
    const id = val('#tvsCoverageTarget');
    if (!sources.length || !id) {
      return;
    }
    loading(true);
    api('GET', 'Stations/' + id).then(detail => {
      const s = detail.Station;
      let added = 0;
      if (s.Mode === 'Lineup') {
        s.Lineup = s.Lineup || { Items: [] };
        s.Lineup.Items = s.Lineup.Items || [];
        sources.forEach(src => {
          if (!s.Lineup.Items.some(i => i.ItemId === src.ItemId)) {
            s.Lineup.Items.push({ ItemId: src.ItemId, DisplayName: src.DisplayName });
            added++;
          }
        });
      } else {
        const list = s.Mode === 'Slots' ? (s.Slots.Filler.Sources = s.Slots.Filler.Sources || []) : (s.Pool.Sources = s.Pool.Sources || []);
        const keys = list.map(sourceKey);
        sources.forEach(src => {
          if (keys.indexOf(sourceKey(src)) < 0) {
            list.push(src);
            added++;
          }
        });
      }
      if (!added) {
        toast('That channel already has every ticked title.');
        return null;
      }
      return api('PUT', 'Stations/' + id, s).then(() => {
        toast(plural(added, 'title') + ' added to ' + s.Name + '.');
        return loadStations().then(() => openCoverage(false));
      });
    }).catch(err => alertErrors(err, 'Could not add to the channel')).then(() => loading(false));
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
    const itemKinds = opts.kinds ? opts.kinds.slice() : mode === 'items'
      ? ['Movie', 'Series', 'Season', 'Episode', 'BoxSet', 'Playlist']
      : ['Movie', 'Series', 'BoxSet', 'Playlist'];
    const specials = opts.specials !== false && mode === 'sources';
    const tune = opts.tune !== false;
    const filter = { kinds: itemKinds.slice(), special: null };
    let lastResults = [];
    let searched = false;
    let requestSeq = 0;

    container.classList.add('tvs-picker');
    const chips = el('div', { className: 'tvs-chips tvs-selected' });
    const kindRow = el('div', { className: 'tvs-chips tvs-kinds' });
    const input = el('input', {
      type: 'text', className: 'emby-input', autocomplete: 'off',
      placeholder: opts.placeholder || (mode === 'items' ? 'Search movies, series, seasons, episodes, collections, playlists…' : 'Search the library…')
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
      if (specials) {
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
    let coverageSeq = 0;
    const coverageCache = {};

    /**
     * Asks the server which of this section's wider sources (genre, tag, series, collection, playlist, library) already
     * play the given movies/episodes, then calls back with { itemId: 'Genre “Comedy”' } for the covered ones.
     * Answers are cached per (sources, ids); a stale answer is dropped when the sources change meanwhile.
     */
    function checkCoverage(ids, cb) {
      if (mode !== 'sources' || !ids.length) {
        return;
      }
      const wide = (opts.getSources() || []).filter(s => s.Kind !== 'Movie' && s.Kind !== 'Episode');
      if (!wide.length) {
        cb({});
        return;
      }
      const key = JSON.stringify([wide, ids.slice().sort()]);
      if (coverageCache[key]) {
        cb(coverageCache[key]);
        return;
      }
      const seq = ++coverageSeq;
      api('POST', 'Schedule/CoveredBy', { Draft: readDraft(), Sources: wide, ItemIds: ids }).then(list => {
        const map = {};
        (list || []).forEach(c => {
          map[c.ItemId] = (KIND_LABEL[c.Kind] || c.Kind) + ' “' + (c.Name || c.Label) + '”';
        });
        coverageCache[key] = map;
        if (seq === coverageSeq) {
          cb(map);
        }
      }).catch(() => { /* a note only; the picker works without it */ });
    }

    function renderChips() {
      if (mode !== 'sources') {
        return;
      }
      clear(chips);
      const sources = opts.getSources() || [];
      if (!sources.length) {
        editing = -1;
        chips.appendChild(el('span', { className: 'tvs-secondary', text: opts.emptyText || 'No content selected yet. Search below and press Add.' }));
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
        if (tune) {
          children.push(el('button', { type: 'button', title: 'Tune this source', 'aria-label': 'Tune', text: '✎', onClick: () => {
            editing = editing === index ? -1 : index;
            renderChips();
          } }));
        }
        children.push(el('button', { type: 'button', title: 'Remove', 'aria-label': 'Remove', text: '×', onClick: () => {
          opts.onRemove(index);
          editing = -1;
          renderChips();
          renderResults(lastResults);
        } }));
        const chip = el('span', { className: 'tvs-chip' + (editing === index ? ' editing' : '') }, children);
        chip.dataset.itemId = source.ItemId || '';
        chips.appendChild(chip);
        if (editing === index) {
          chips.appendChild(renderSourceEditor(source));
        }
      });
      const picks = sources.filter(s => (s.Kind === 'Movie' || s.Kind === 'Episode') && s.ItemId).map(s => s.ItemId);
      checkCoverage(picks, map => {
        Array.prototype.forEach.call(chips.querySelectorAll('.tvs-chip'), chip => {
          const via = map[chip.dataset.itemId];
          if (!via) {
            return;
          }
          const note = el('span', { className: 'badge covered', text: 'also via ' + via,
            title: 'Already on this channel through ' + via + '. Keeping it here as well pins it: an excluded tag can never drop it.' });
          chip.insertBefore(note, chip.querySelector('button'));
        });
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

      // Audio and subtitle tracks for this source's items: an anime series can play in Japanese with English
      // subtitles burned in while the rest of the station keeps the station's language.
      const audio = languageSelect(source.AudioLanguage, 'Station default', v => { source.AudioLanguage = v; renderChips(); });
      const subs = languageSelect(source.SubtitleLanguage, 'Off', v => { source.SubtitleLanguage = v; onlyIfLabel.hidden = !v; renderChips(); });
      const onlyIf = el('input', { type: 'checkbox', className: 'emby-checkbox' });
      onlyIf.checked = !!source.SubtitlesOnlyIfAudioMissing;
      onlyIf.addEventListener('change', () => { source.SubtitlesOnlyIfAudioMissing = onlyIf.checked; renderChips(); });
      const onlyIfLabel = el('label', { className: 'f row' }, [onlyIf, el('span', { text: 'Only when the audio language is missing' })]);
      onlyIfLabel.hidden = !source.SubtitleLanguage;
      box.appendChild(el('label', { className: 'f', title: 'Which audio track to play when a file has more than one' }, [el('span', { text: 'Audio' }), audio.select]));
      box.appendChild(el('label', { className: 'f', title: 'Subtitles drawn into the picture for every viewer' }, [el('span', { text: 'Subtitles' }), subs.select]));
      box.appendChild(onlyIfLabel);
      if (source.ItemId) {
        api('GET', 'Library/Items/' + source.ItemId + '/Languages').then(found => {
          if (found && Array.isArray(found.Audio) && found.Audio.length) { audio.fill(found.Audio); }
          if (found && Array.isArray(found.Subtitles)) { subs.fill(found.Subtitles); }
        }).catch(() => {});
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
        row.dataset.itemId = item.special ? '' : item.Id;
        results.appendChild(row);
      });
      const movies = list.filter(i => !i.special && (i.Kind === 'Movie' || i.Kind === 'Episode')).map(i => i.Id);
      checkCoverage(movies, map => {
        Array.prototype.forEach.call(results.querySelectorAll('.tvs-result'), row => {
          const via = map[row.dataset.itemId];
          if (!via || row.querySelector('.tvs-covered')) {
            return;
          }
          row.querySelector('.body').appendChild(el('div', { className: 'tvs-secondary tvs-ellipsis tvs-covered',
            text: 'Already included via ' + via,
            title: 'This already plays here through ' + via + '. Adding it by itself as well pins it so an excluded tag can never drop it.' }));
        });
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
        case 'Album': return 'album';
        case 'Artist': return 'person';
        case 'MusicGenre': return 'music_note';
        case 'Audio': return 'audiotrack';
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
      VideoEncoder: 'libx264', OutputRange: 'sdr', Preset: 'veryfast', H264Profile: 'high', H264Level: '4.1', GopSeconds: 2,
      HardwareDecode: 'auto', HardwareDevice: '',
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
        inputHtml(p + 'fps', 'Frame rate (fps)', 'number', { min: 1, max: 120, step: 1 }, 'Constant output frame rate. 24 suits a film station (most movies are 23.976 fps and judder when converted to 30); 30 suits series; 60 only for 60 fps sources.') +
        inputHtml(p + 'gop', 'Keyframe interval (seconds)', 'number', { min: 1, max: 30, step: 1 }, 'GOP length. Shorter makes channel changes faster at a small bitrate cost.') +
        inputHtml(p + 'vbr', 'Video bitrate (kbps)', 'number', { min: 100, max: 100000, step: 50 }, 'Target bitrate for the video stream. 3500 is plenty for 720p and 8000 for 1080p; more only inflates every client\'s download and Jellyfin\'s transcodes.') +
        inputHtml(p + 'abr', 'Audio bitrate (kbps)', 'number', { min: 32, max: 1024, step: 8 }, 'AAC audio bitrate.') +
        selectHtml(p + 'enc', 'Video encoder', ENCODERS, 'Hardware encoders need the matching drivers and may need extra input arguments (e.g. -hwaccel).') +
        inputHtml(p + 'encCustom', 'Custom encoder name', 'text', {}, 'Exact ffmpeg encoder name, e.g. h264_amf or hevc_amf.') +
        selectHtml(p + 'range', 'Output', OUTPUT_RANGES, 'SDR: 8-bit BT.709, plays everywhere. HDR10: the whole channel is 10-bit BT.2020 PQ HEVC Main 10. HDR sources keep their range, SDR programs, slates, cards and weather screens are converted into it at SDR brightness; needs an HEVC encoder, HDR-capable clients direct play it and Jellyfin tone maps it for the rest (turn on tone mapping under Dashboard > Playback > Transcoding).') +
        inputHtml(p + 'preset', 'Preset', 'text', {}, 'Speed/quality preset. libx264, libx265 and Quick Sync take ultrafast … veryslow (Quick Sync has veryfast … veryslow; ultrafast and superfast become veryfast). NVENC takes p1 … p7, x264 words are translated (veryfast is p2). VA-API, AMF and VideoToolbox have no preset. Leave blank for the encoder default.') +
        inputHtml(p + 'profile', 'H.264 profile', 'text', {}, 'baseline, main or high (libx264 only; HEVC encoders take main, or main10 on an HDR10 profile).') +
        inputHtml(p + 'level', 'H.264 level', 'text', {}, 'For example 4.1 (libx264 only).') +
        inputHtml(p + 'lang', 'Preferred audio language', 'text', { maxlength: 3 }, 'ISO 639-2 code (eng, spa, jpn…) used to pick the audio track when compiling.') +
        selectHtml(p + 'hwdec', 'Hardware decoding', HW_DECODERS, 'Decodes the source on the GPU (the heavy part for 4K HEVC); scaling, HDR tone mapping and the logo still run in software. "Follow Jellyfin" uses Dashboard > Playback > Transcoding. ffmpeg falls back to software when the device cannot be used.') +
        inputHtml(p + 'hwdev', 'Hardware device', 'text', {}, 'Render node or adapter for decoding and VA-API encoding, e.g. /dev/dri/renderD128. Empty uses the device Jellyfin is configured with.') +
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
        const range = String(e.OutputRange || 'sdr').toLowerCase();
        q('range').value = OUTPUT_RANGES.some(o => o[0] === range) ? range : 'sdr';
        q('preset').value = e.Preset || '';
        q('profile').value = e.H264Profile || '';
        q('level').value = e.H264Level || '';
        q('lang').value = e.AudioLanguagePreference || '';
        const hw = e.HardwareDecode == null ? 'auto' : String(e.HardwareDecode).trim().toLowerCase();
        q('hwdec').value = HW_DECODERS.some(o => o[0] === hw) ? hw : 'auto';
        q('hwdev').value = e.HardwareDevice || '';
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
          OutputRange: q('range').value,
          Preset: q('preset').value.trim(),
          H264Profile: q('profile').value.trim(),
          H264Level: q('level').value.trim(),
          GopSeconds: parseInt(q('gop').value, 10) || 0,
          HardwareDecode: q('hwdec').value,
          HardwareDevice: q('hwdev').value.trim(),
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
    setVal('#tvsIdleStop', s.IdleStopSeconds !== undefined ? s.IdleStopSeconds : 120);
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
      IdleStopSeconds: parseInt(val('#tvsIdleStop'), 10),
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
    if (isNaN(payload.IdleStopSeconds)) {
      errors.push('Stop an unwatched stream after must be a number.');
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
    loadHardware(false);
    startStatusTimer();
  }

  // ------------------------------------------------------------------------------------------
  // Hardware: capabilities and the decode/encode test
  // ------------------------------------------------------------------------------------------

  const HW_TEST_DECODERS = [['profile', "The station's own setting"]].concat(HW_DECODERS);

  function loadHardware(refresh) {
    const info = $('#tvsHardwareInfo');
    if (!info) {
      return Promise.resolve();
    }
    const stationsReady = state.stations.length ? Promise.resolve(state.stations) : api('GET', 'Stations').then(list => {
      state.stations = Array.isArray(list) ? list : [];
      return state.stations;
    });
    return Promise.all([api('GET', 'Hardware', undefined, refresh ? { refresh: true } : undefined), stationsReady]).then(results => {
      renderHardware(results[0] || {});
      fillHardwareTestSelects();
    }).catch(err => {
      clear(info);
      readErrors(err).then(messages => {
        const notes = clear($('#tvsHardwareNotes'));
        notes.appendChild(el('div', { className: 'tvs-problem bad', text: 'Could not read the hardware picture: ' + messages.join(' ') }));
      });
    });
  }

  function renderHardware(h) {
    const info = clear($('#tvsHardwareInfo'));
    const kv = (label, valueNode) => {
      info.appendChild(el('dt', { text: label }));
      info.appendChild(el('dd', null, valueNode));
    };
    const decoderLabel = kind => {
      const found = HW_DECODERS.find(o => o[0] === kind);
      return found ? found[1] : kind;
    };
    const jfDevice = h.JellyfinQsvDevice || h.JellyfinVaapiDevice;
    kv('Jellyfin transcoding', (h.JellyfinAcceleration || 'none') + (jfDevice ? ' on ' + jfDevice : ''));
    kv('Default profile decodes with', h.DefaultDecoder
      ? el('span', null, [el('span', { className: 'tvs-ok', text: decoderLabel(h.DefaultDecoder) }), el('span', { className: 'tvs-secondary', text: h.DefaultDevice ? ' on ' + h.DefaultDevice : ' (driver default device)' })])
      : el('span', { className: 'tvs-secondary', text: 'software' }));
    kv('Default profile encodes with', h.DefaultEncoder || '');
    kv('Server', (h.Os || '') + ', ' + (h.CpuCores || '?') + ' CPU cores' + (h.CanInspectProcesses ? '' : ' (cannot inspect which device a process holds open on this OS)'));
    if (h.Os === 'linux') {
      kv('GPU nodes', (h.DrmNodes || []).length ? (h.DrmNodes || []).join(', ') : el('span', { className: 'tvs-bad', text: 'none visible (/dev/dri is empty or absent)' }));
    }
    kv('ffmpeg', h.FfmpegError ? el('span', { className: 'tvs-bad', text: h.FfmpegError }) : (h.FfmpegVersion || h.FfmpegPath || ''));
    if (!h.FfmpegError) {
      kv('Hardware decoders in ffmpeg', (h.Hwaccels || []).length ? (h.Hwaccels || []).join(', ') : el('span', { className: 'tvs-bad', text: 'none' }));
      kv('H.264 encoders in ffmpeg', (h.H264Encoders || []).join(', '));
      kv('HEVC encoders in ffmpeg (for HDR10 channels)', (h.HevcEncoders || []).length ? (h.HevcEncoders || []).join(', ') : el('span', { className: 'tvs-bad', text: 'none' }));
      kv('HDR tone mapping filters', h.HasTonemap ? el('span', { className: 'tvs-ok', text: 'zscale + tonemap present' }) : el('span', { className: 'tvs-bad', text: 'missing' }));
    }
    const notes = clear($('#tvsHardwareNotes'));
    (h.Notes || []).forEach(note => notes.appendChild(el('div', { className: 'tvs-problem warn', text: note })));
  }

  function fillHardwareTestSelects() {
    const stationSelect = $('#tvsHwTestStation');
    const decoderSelect = $('#tvsHwTestDecoder');
    if (!stationSelect || !decoderSelect) {
      return;
    }
    const keepStation = stationSelect.value;
    clear(stationSelect);
    stationSelect.appendChild(el('option', { value: '', text: 'Any station with a playable file' }));
    state.stations.slice().sort(channelSort).forEach(s => {
      stationSelect.appendChild(el('option', { value: s.Id, text: (s.ChannelNumber ? s.ChannelNumber + ' ' : '') + s.Name }));
    });
    if (keepStation && state.stations.some(s => s.Id === keepStation)) {
      stationSelect.value = keepStation;
    }
    if (!decoderSelect.options.length) {
      HW_TEST_DECODERS.forEach(o => decoderSelect.appendChild(el('option', { value: o[0], text: o[1] })));
    }
  }

  function runHardwareTest() {
    const button = $('#tvsBtnHwTest');
    const box = clear($('#tvsHwTestResult'));
    const body = { Seconds: 5 };
    const stationId = val('#tvsHwTestStation');
    if (stationId) {
      body.StationId = stationId;
    }
    const decoder = val('#tvsHwTestDecoder');
    if (decoder !== 'profile') {
      body.HardwareDecode = decoder;
    }
    box.appendChild(el('div', { className: 'tvs-info', text: 'Running ffmpeg for a few seconds…' }));
    button.disabled = true;
    api('POST', 'Hardware/Test', body).then(result => {
      renderHardwareTest(result || {});
    }).catch(err => {
      readErrors(err).then(messages => {
        clear(box).appendChild(el('div', { className: 'tvs-problem bad', text: messages.join(' ') }));
      });
    }).then(() => { button.disabled = false; });
  }

  function renderHardwareTest(r) {
    const box = clear($('#tvsHwTestResult'));
    const decoderText = {
      'hardware': 'Hardware decoding confirmed: ffmpeg used ' + (r.DecoderInUse || r.RequestedDecoder) + '.',
      'software': 'Software decoding: ' + r.RequestedDecoder + ' was requested but ffmpeg did not use it.',
      'unverified': r.RequestedDecoder + ' decoding was set up and ffmpeg reported no fallback, but this host cannot confirm the decoder itself used the GPU' + (r.VideoEncoder && r.VideoEncoder.indexOf('lib') !== 0 ? ' (the ' + r.VideoEncoder + ' encoder holds the device too)' : '') + '.',
      'fallback': 'Software decoding: ffmpeg set ' + r.RequestedDecoder + ' up but could not use it for this file.',
      'failed': (r.RequestedDecoder || 'Hardware') + ' decoding could not be set up at all.',
      'not-requested': 'Software decoding (no hardware decoder requested).',
      'skipped': 'Software decoding by choice: ' + (r.DecoderSkipReason || 'this file is not given to ' + r.RequestedDecoder) + '. The plugin does the same when the station plays it.'
    }[r.Decoder] || r.Decoder;
    const ok = r.Problems && r.Problems.length === 0;
    box.appendChild(el('div', { className: 'tvs-problem ' + (ok ? 'ok' : (r.Decoder === 'failed' || r.Encoder === 'failed' || r.Decoder === 'fallback' ? 'bad' : 'warn')), text: decoderText + ' ' +
      (r.Encoder === 'ok' ? 'Encoding with ' + r.VideoEncoder + ' worked.' : r.Decoder === 'failed' ? '' : 'Encoding with ' + r.VideoEncoder + ' failed (exit ' + r.ExitCode + ').') +
      (r.Speed !== null && r.Speed !== undefined ? ' Ran at ' + Number(r.Speed).toFixed(2) + 'x real time.' : '') }));
    (r.Problems || []).forEach(problem => box.appendChild(el('div', { className: 'tvs-problem warn', text: problem })));
    const kv = el('dl', { className: 'tvs-kv' });
    const add = (label, value) => {
      kv.appendChild(el('dt', { text: label }));
      kv.appendChild(el('dd', { text: value }));
    };
    add('File', r.File || '');
    if (r.SourceVideo) {
      add('Source video', r.SourceVideo);
    }
    add('Decoder', (r.RequestedDecoder || 'software') + (r.Device ? ' on ' + r.Device : ''));
    add('Encoder', r.VideoEncoder || '');
    add('Took', Number(r.Seconds || 0).toFixed(1) + ' s');
    box.appendChild(kv);
    const details = el('details');
    details.appendChild(el('summary', { text: 'ffmpeg log (' + (r.Log || []).length + ' lines)' }));
    details.appendChild(el('pre', { className: 'tvs-log', text: (r.Command || '') + '\n\n' + (r.Log || []).join('\n') }));
    box.appendChild(details);
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
    return Promise.all([api('GET', 'Status'), api('GET', 'Health')]).then(results => {
      renderErrorBox($('#tvsStatusError'), null);
      renderStatus(results[0] || {});
      renderHealth(results[1] || {});
      $('#tvsStatusUpdated').textContent = 'Updated ' + new Date().toLocaleTimeString();
    }).catch(err => {
      readErrors(err).then(messages => renderErrorBox($('#tvsStatusError'), messages));
    }).then(() => {
      if (showSpinner) {
        loading(false);
      }
    });
  }

  /** The health section of the status view: one block per station with warnings, or a single all-clear line. */
  function renderHealth(report) {
    const box = clear($('#tvsHealth'));
    const stations = Array.isArray(report.Stations) ? report.Stations : [];
    if (!stations.length) {
      box.appendChild(el('div', { className: 'tvs-empty', text: 'No stations yet.' }));
      return;
    }
    const troubled = stations.filter(h => Array.isArray(h.Warnings) && h.Warnings.length);
    const plural = stations.length === 1 ? '' : 's';
    if (!troubled.length) {
      box.appendChild(el('div', { className: 'tvs-ok', text: 'âœ“ All ' + stations.length + ' station' + plural + ' look healthy: every source has items, each has a day or more of unique content, and nothing was skipped.' }));
      return;
    }
    box.appendChild(el('div', { className: 'tvs-secondary', text: troubled.length + ' of ' + stations.length + ' station' + plural + ' need' + (troubled.length === 1 ? 's' : '') + ' attention.' }));
    troubled.forEach(h => {
      const head = el('div', { className: 'tvs-health-head' }, [
        el('span', { className: 'tvs-secondary', text: 'Ch ' + (h.ChannelNumber || '?') + '  ' }),
        el('span', { className: 'tvs-health-name', text: h.Name || '(unnamed)' }),
        h.Enabled ? null : badge('Disabled', 'off'),
        el('span', { className: 'tvs-secondary', text: '  ' + fmtHours(h.UniqueHours) + ' unique' + (h.CompiledUtc ? ' · scanned ' + fmtDateTime(h.CompiledUtc) : '') })
      ]);
      // A fix for each kind of warning: re-scan when items were skipped or nothing was scanned yet, otherwise open the station.
      const needsRefresh = h.Warnings.some(w => /^Not compiled|skipped:/.test(w));
      const actions = el('div', { className: 'tvs-health-actions' }, [
        needsRefresh ? textButton('Refresh content', () => refreshStationContent(h.StationId, h.Name)) : null,
        textButton(needsRefresh ? 'Edit station' : 'Add content', () => openEditor(h.StationId))
      ]);
      box.appendChild(el('div', { className: 'tvs-health' }, [
        head,
        el('ul', { className: 'tvs-health-list' }, h.Warnings.map(w => el('li', { text: w }))),
        actions
      ]));
    });
  }

  /** Re-scans one station from the Health section and reloads the view. */
  function refreshStationContent(stationId, name) {
    loading(true);
    api('POST', 'Stations/' + stationId + '/Recompile').then(result => {
      toast((name || 'Station') + ': ' + (result ? result.ItemCount : 0) + ' items after the scan.');
      return loadStatus(false);
    }).catch(err => alertErrors(err, 'Refresh content failed')).then(() => loading(false));
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
      el('th', { text: 'Streamed' }), el('th', { text: 'PID' }), el('th', { text: 'Now playing' }), el('th', { text: 'Encoder' }), el('th', { text: '' })
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
        encoderCell(session.Encoder),
        actions
      ]));

      const problems = session.Encoder && Array.isArray(session.Encoder.Problems) ? session.Encoder.Problems : [];
      if (problems.length) {
        tbody.appendChild(el('tr', { className: 'logrow' }, el('td', { colspan: 9 }, problems.map(problem =>
          el('div', { className: 'tvs-problem ' + (session.Encoder.State === 'bad' ? 'bad' : 'warn'), text: problem })))));
      }

      if (logOpen) {
        const logText = logCount ? (session.RecentLog || []).join('\n') : '(no recent log lines)';
        tbody.appendChild(el('tr', { className: 'logrow' }, el('td', { colspan: 9 }, el('pre', { className: 'tvs-log', text: logText }))));
      }
    });
    table.appendChild(tbody);
    wrap.appendChild(table);
  }

  /** Badges for what the encoder is doing: the decoder it really uses, the encoder, tone mapping, CPU and real-time margin. */
  function encoderCell(e) {
    const cell = el('td');
    if (!e) {
      cell.appendChild(el('span', { className: 'tvs-secondary', text: 'starting…' }));
      return cell;
    }
    const badges = el('div', { className: 'tvs-enc' });
    const badge = (text, cls, title) => badges.appendChild(el('span', { className: 'tvs-badge kind ' + (cls || ''), text: text, title: title || '' }));
    if (e.IsSlate) {
      badge('slate', 'off', 'A generated card, not a program file');
    } else if (!e.RequestedDecoder) {
      badge('software decode', 'off', 'No hardware decoder on this station\'s profile');
    } else {
      const decoderBadge = {
        'hardware': [e.RequestedDecoder + ' decode ✓', 'on', 'Confirmed: ffmpeg holds ' + (e.GpuDevice || 'the GPU device') + ' open'],
        'unverified': [e.RequestedDecoder + ' decode', '', 'Requested; this host cannot confirm the device is in use'],
        'fallback': [e.RequestedDecoder + ' fell back', 'bad', e.DecoderDetail || 'ffmpeg is decoding this file in software'],
        'disabled': [e.RequestedDecoder + ' failed', 'bad', 'The device failed when the stream started; software decoding until restart'],
        'skipped': ['software (' + (e.DecoderDetail || 'this file') + ')', e.DecoderDetail && e.DecoderDetail.indexOf('failed on this program') >= 0 ? 'warn' : 'off', 'The plugin decodes this program in software: ' + (e.DecoderDetail || '')],
        'software': ['software decode', 'off', '']
      }[e.Decoder] || [e.Decoder, '', ''];
      badge(decoderBadge[0], decoderBadge[1], decoderBadge[2]);
    }
    badge(e.VideoEncoder || '', e.EncoderIsHardware ? 'on' : '', e.EncoderIsHardware ? 'Hardware encoder' : 'Software encoder');
    if (e.HdrOutput) {
      badge('HDR10 out', 'live', e.SourceIsHdr ? 'HDR source kept in 10-bit PQ on this HDR10 channel' : 'SDR picture converted into this HDR10 channel at SDR brightness');
    } else if (e.ToneMapping) {
      badge('HDR → SDR', 'live', 'Tone mapping this HDR program to BT.709 in software');
    } else if (e.SourceIsHdr) {
      badge('HDR untouched', 'warn', 'HDR source playing without tone mapping');
    }
    if (e.CpuCores !== null && e.CpuCores !== undefined) {
      badge(Number(e.CpuCores).toFixed(1) + ' cores', Number(e.CpuCores) > 3 ? 'warn' : '', 'CPU cores the ffmpeg process is using');
    }
    if (e.ProcessRunning && e.AheadSeconds !== null && e.AheadSeconds !== undefined) {
      const ahead = Number(e.AheadSeconds);
      badge((ahead >= 0 ? '+' : '') + ahead.toFixed(1) + ' s', ahead < 0 ? 'bad' : ahead < 0.4 ? 'warn' : 'on', 'How far the encoder is ahead of real time (about +1.5 s when it keeps up)');
    } else if (!e.ProcessRunning) {
      badge('between programs', 'off', '');
    }
    cell.appendChild(badges);
    return cell;
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

  // ---- Weather channel ---------------------------------------------------------------------

  const MUSIC_KINDS = ['Playlist', 'Album', 'Artist', 'MusicGenre', 'Audio'];
  const WEATHER_SCREEN_LABEL = { TitleCard: 'Title card', Alerts: 'Alerts', Current: 'Current conditions', Hourly: 'Next 12 hours', Daily: '7-day forecast', Radar: 'Local radar', ForecastRadar: 'Forecast radar', Travel: 'Travel cities' };

  function defaultWeather() {
    return {
      Locations: [], Units: 'Imperial', TwentyFourHourClock: false, ScreenSeconds: 10, OnTheEights: false, Theme: 'Classic',
      ShowLogo: true, ShowTravelCities: true, ShowTitleCards: true, TitleCardSeconds: 3, ShowAlerts: true, ShowHourly: true, ShowDaily: true, ShowRadar: true, RadarZoom: 'State', ShowForecastRadar: true, ForecastRadarHours: 3, ForecastRadarStepMinutes: 15, GuideTitle: null, GuideEntryMinutes: 60, StaleHours: 3,
      Provider: 'open-meteo', MusicEnabled: true, MusicSources: [], MusicFolder: null, ShowMusicCredit: true, MusicVolumePercent: 100, NormalizeMusic: true
    };
  }

  function normalizeWeather(w) {
    const d = defaultWeather();
    w = w || {};
    Object.keys(d).forEach(k => {
      if (w[k] === undefined || w[k] === null) {
        w[k] = d[k];
      }
    });
    w.Locations = (Array.isArray(w.Locations) ? w.Locations : []).slice(0, 8).map(l => ({
      Id: l.Id || ZERO_GUID,
      Name: String(l.Name || '').trim(),
      Region: l.Region || null,
      Latitude: Number(l.Latitude) || 0,
      Longitude: Number(l.Longitude) || 0,
      TimeZoneId: l.TimeZoneId || null
    }));
    w.MusicSources = Array.isArray(w.MusicSources) ? w.MusicSources : [];
    w.MusicSources.forEach(normalizeSource);
    w.ScreenSeconds = Math.min(60, Math.max(4, parseInt(w.ScreenSeconds, 10) || 10));
    w.TitleCardSeconds = Math.min(10, Math.max(1, parseInt(w.TitleCardSeconds, 10) || 3));
    w.GuideEntryMinutes = Math.min(240, Math.max(15, parseInt(w.GuideEntryMinutes, 10) || 60));
    w.MusicVolumePercent = Math.min(150, Math.max(20, parseInt(w.MusicVolumePercent, 10) || 100));
    w.MusicEnabled = w.MusicEnabled !== false;
    w.ShowMusicCredit = w.ShowMusicCredit !== false;
    w.NormalizeMusic = w.NormalizeMusic !== false;
    return w;
  }

  function weatherPlaceValid(l) {
    return !!l.Name && !(l.Latitude === 0 && l.Longitude === 0) && Math.abs(l.Latitude) <= 90 && Math.abs(l.Longitude) <= 180;
  }

  /** Fills the weather panel from the draft; called by renderEditor. */
  function renderWeatherPanel() {
    const w = state.draft.Weather;
    setVal('#tvsWeatherUnits', w.Units || 'Imperial');
    setVal('#tvsWeatherTheme', w.Theme || 'Classic');
    setVal('#tvsWeatherScreenSeconds', w.ScreenSeconds);
    setVal('#tvsWeatherEntryMinutes', w.GuideEntryMinutes);
    setChecked('#tvsWeatherEights', !!w.OnTheEights);
    setChecked('#tvsWeatherClock24', !!w.TwentyFourHourClock);
    setChecked('#tvsWeatherTitleCards', w.ShowTitleCards !== false);
    setVal('#tvsWeatherTitleCardSeconds', w.TitleCardSeconds || 3);
    show($('#tvsWeatherTitleCardBox'), w.ShowTitleCards !== false);
    setChecked('#tvsWeatherAlerts', w.ShowAlerts !== false);
    setChecked('#tvsWeatherHourly', w.ShowHourly !== false);
    setChecked('#tvsWeatherDaily', w.ShowDaily !== false);
    setChecked('#tvsWeatherRadar', w.ShowRadar !== false);
    setVal('#tvsWeatherRadarZoom', w.RadarZoom || 'State');
    show($('#tvsWeatherRadarZoomBox'), w.ShowRadar !== false);
    setChecked('#tvsWeatherForecastRadar', w.ShowForecastRadar !== false);
    setVal('#tvsWeatherForecastHours', String(w.ForecastRadarHours || 3));
    setVal('#tvsWeatherForecastStep', String(w.ForecastRadarStepMinutes || 15));
    show($('#tvsWeatherForecastRadarBox'), w.ShowForecastRadar !== false);
    setChecked('#tvsWeatherTravel', w.ShowTravelCities !== false);
    setChecked('#tvsWeatherLogo', w.ShowLogo !== false);
    setVal('#tvsWeatherGuideTitle', w.GuideTitle || '');
    setChecked('#tvsWeatherMusic', w.MusicEnabled !== false);
    show($('#tvsWeatherMusicOptions'), w.MusicEnabled !== false);
    setVal('#tvsWeatherMusicFolder', w.MusicFolder || '');
    setVal('#tvsWeatherVolume', w.MusicVolumePercent);
    setChecked('#tvsWeatherCredit', w.ShowMusicCredit !== false);
    setChecked('#tvsWeatherNormalize', w.NormalizeMusic !== false);
    loadMusicPacks();
    createPicker({
      container: $('#tvsMusicPicker'),
      mode: 'sources',
      kinds: MUSIC_KINDS,
      specials: false,
      tune: false,
      placeholder: 'Search playlists, albums, artists, music genres, tracks…',
      emptyText: 'No music from the library yet. Search below and press Add, or name a folder underneath.',
      getSources: () => state.draft.Weather.MusicSources,
      onAdd: source => state.draft.Weather.MusicSources.push(source),
      onRemove: index => state.draft.Weather.MusicSources.splice(index, 1)
    });
    $('#tvsWeatherSearch').value = '';
    $('#tvsWeatherSearchStatus').textContent = '';
    show($('#tvsWeatherSearchResults'), false);
    show($('#tvsWeatherPreviewImg'), false);
    renderWeatherPlaces();
    renderWeatherStatus(null);
    if (!isNewDraft() && state.draft.Mode === 'Weather') {
      loadWeatherStatus();
    }
  }

  function renderWeatherPlaces() {
    const w = state.draft.Weather;
    const list = clear($('#tvsWeatherPlaces'));
    show($('#tvsWeatherPlacesEmpty'), !w.Locations.length);
    w.Locations.forEach((l, index) => {
      const row = el('div', { className: 'listItem' });
      row.appendChild(el('div', { className: 'tvs-chnum', text: String(index + 1) }));
      const body = el('div', { className: 'listItemBody two-line' });
      body.appendChild(el('div', { className: 'listItemBodyText' }, [
        el('span', { text: l.Name || '(unnamed)' }),
        weatherPlaceValid(l) ? null : badge('Pick from the search', 'warn')
      ]));
      const meta = [l.Region, (l.Latitude || l.Longitude) ? l.Latitude.toFixed(2) + ', ' + l.Longitude.toFixed(2) : null, l.TimeZoneId].filter(Boolean).join(' · ');
      body.appendChild(el('div', { className: 'listItemBodyText secondary tvs-ellipsis', text: meta }));
      row.appendChild(body);
      const actions = el('div', { className: 'tvs-station-actions' });
      actions.appendChild(iconButton('arrow_upward', 'Earlier in the cycle', () => {
        if (index > 0) {
          w.Locations.splice(index - 1, 0, w.Locations.splice(index, 1)[0]);
          renderWeatherPlaces();
        }
      }));
      actions.appendChild(iconButton('arrow_downward', 'Later in the cycle', () => {
        if (index < w.Locations.length - 1) {
          w.Locations.splice(index + 1, 0, w.Locations.splice(index, 1)[0]);
          renderWeatherPlaces();
        }
      }));
      actions.appendChild(iconButton('delete', 'Remove', () => {
        w.Locations.splice(index, 1);
        renderWeatherPlaces();
      }));
      row.appendChild(actions);
      list.appendChild(row);
    });
    const placeSelect = clear($('#tvsWeatherPreviewPlace'));
    w.Locations.forEach((l, index) => placeSelect.appendChild(el('option', { value: String(index), text: l.Name || ('Place ' + (index + 1)) })));
  }

  let weatherSearchSeq = 0;

  function weatherSearch() {
    const q = ($('#tvsWeatherSearch').value || '').trim();
    const status = $('#tvsWeatherSearchStatus');
    const results = $('#tvsWeatherSearchResults');
    const seq = ++weatherSearchSeq;
    if (q.length < 2) {
      status.textContent = '';
      show(results, false);
      return;
    }
    status.textContent = 'Searching…';
    api('GET', 'Weather/Search', undefined, { q: q, count: 8 }).then(list => {
      if (seq !== weatherSearchSeq) {
        return;
      }
      clear(results);
      show(results, true);
      list = Array.isArray(list) ? list : [];
      status.textContent = list.length ? list.length + ' place' + (list.length === 1 ? '' : 's') : '';
      if (!list.length) {
        results.appendChild(el('div', { className: 'tvs-msg', text: 'No places match.' }));
        return;
      }
      list.forEach(p => {
        const row = el('div', { className: 'tvs-result' });
        row.appendChild(el('div', { className: 'tvs-thumb placeholder' }, el('span', { className: 'material-icons place', 'aria-hidden': 'true' })));
        const meta = [p.Region, p.TimeZoneId, p.Population ? 'pop. ' + Number(p.Population).toLocaleString() : null].filter(Boolean).join(' · ');
        row.appendChild(el('div', { className: 'body' }, [
          el('div', { className: 'tvs-ellipsis', text: p.Name }),
          el('div', { className: 'tvs-secondary tvs-ellipsis', text: meta })
        ]));
        const full = state.draft.Weather.Locations.length >= 8;
        const addButton = textButton(full ? 'Full' : 'Add', () => {
          if (state.draft.Weather.Locations.length >= 8) {
            toast('A weather channel shows at most eight places.');
            return;
          }
          state.draft.Weather.Locations.push({ Id: ZERO_GUID, Name: p.Name, Region: p.Region || null, Latitude: p.Latitude, Longitude: p.Longitude, TimeZoneId: p.TimeZoneId || null });
          renderWeatherPlaces();
          addButton.disabled = true;
          addButton.firstChild.textContent = 'Added';
        });
        addButton.disabled = full;
        row.appendChild(addButton);
        results.appendChild(row);
      });
    }).catch(err => {
      if (seq !== weatherSearchSeq) {
        return;
      }
      readErrors(err).then(messages => {
        status.textContent = 'Search failed: ' + messages.join(' ');
        show(results, false);
      });
    });
  }

  // ---- Music packs ----

  let musicPackTimer = null;

  function loadMusicPacks() {
    clearTimeout(musicPackTimer);
    api('GET', 'MusicPacks').then(renderMusicPacks).catch(() => {
      clear($('#tvsMusicPacks')).appendChild(el('div', { className: 'tvs-secondary', text: 'Music packs could not be loaded.' }));
    });
  }

  function folderInUse(folder) {
    const parts = (val('#tvsWeatherMusicFolder') || '').split(/[;\n\r]/).map(s => s.trim()).filter(Boolean);
    return parts.some(p => p.toLowerCase() === folder.toLowerCase());
  }

  function addPackFolder(folder) {
    if (folderInUse(folder)) {
      return;
    }
    const current = (val('#tvsWeatherMusicFolder') || '').trim();
    setVal('#tvsWeatherMusicFolder', current ? current.replace(/;\s*$/, '') + '; ' + folder : folder);
    toast('Folder "' + folder + '" added. Save the station to use it.');
  }

  function renderMusicPacks(packs) {
    const list = clear($('#tvsMusicPacks'));
    packs = Array.isArray(packs) ? packs : [];
    let anyRunning = false;
    packs.forEach(p => {
      const row = el('div', { className: 'listItem', style: 'flex-wrap:wrap' });
      const body = el('div', { className: 'listItemBody three-line' });
      const minutes = Math.round((p.TotalSeconds || 0) / 60);
      const meta = [
        p.Artist,
        p.TrackCount + ' track' + (p.TrackCount === 1 ? '' : 's'),
        minutes >= 60 ? Math.floor(minutes / 60) + ' h ' + (minutes % 60) + ' min' : minutes + ' min',
        p.TotalBytes ? fmtBytes(p.TotalBytes) : null,
        p.Licence
      ].filter(Boolean).join(' · ');
      body.appendChild(el('div', { className: 'listItemBodyText' }, [
        el('span', { text: p.Name }),
        p.Installed ? badge(p.Manual ? p.Present + ' file' + (p.Present === 1 ? '' : 's') + ' in place' : 'Installed', 'on') : null,
        p.Running ? badge('Downloading ' + p.Downloaded + ' / ' + p.TrackCount, 'on') : null,
        !p.Running && p.Error ? badge(p.Error === 'Cancelled.' ? 'Cancelled' : 'Failed', 'warn', p.Error) : null,
        !p.Installed && !p.Running && !p.Error && !p.Manual && p.Present > 0 ? badge(p.Present + ' of ' + p.TrackCount + ' present', 'warn') : null
      ]));
      body.appendChild(el('div', { className: 'listItemBodyText secondary', text: meta }));
      body.appendChild(el('div', { className: 'listItemBodyText secondary', text: p.Description }));
      row.appendChild(body);

      const actions = el('div', { className: 'tvs-station-actions' });
      if (p.Running) {
        anyRunning = true;
        actions.appendChild(textButton('Cancel', () => api('POST', 'MusicPacks/' + p.Id + '/Cancel').then(loadMusicPacks).catch(err => alertErrors(err, 'Could not cancel'))));
      } else if (!p.Manual && !p.Installed) {
        actions.appendChild(textButton(p.Present > 0 ? 'Finish download' : 'Get it', () => {
          api('POST', 'MusicPacks/' + p.Id + '/Install').then(() => {
            toast('Downloading "' + p.Name + '" into the music folder.');
            loadMusicPacks();
          }).catch(err => alertErrors(err, 'Could not start the download'));
        }));
      }
      if (p.Manual) {
        actions.appendChild(textButton('Where to get it', () => {
          const details = row.querySelector('.tvs-pack-details');
          show(details, details.classList.contains('tvs-hidden'));
        }));
      }
      if (!folderInUse(p.Folder)) {
        actions.appendChild(textButton('Use this folder', () => {
          addPackFolder(p.Folder);
          renderMusicPacks(packs);
        }));
      } else {
        actions.appendChild(el('span', { className: 'tvs-secondary', text: 'in use' }));
      }
      row.appendChild(actions);

      const details = el('div', { className: 'tvs-pack-details tvs-secondary' + (p.Manual ? ' tvs-hidden' : ' tvs-hidden'), style: 'flex-basis:100%;padding:.4em 0 .2em 3.8em' });
      if (p.Manual) {
        details.appendChild(el('div', { text: 'Download the albums (a free account is needed there), then put the audio files into this folder on the server:' }));
        details.appendChild(el('div', null, el('code', { text: p.FolderPath })));
        const ul = el('ul', { style: 'margin:.3em 0' });
        (p.Links || []).forEach(l => ul.appendChild(el('li', null, el('a', { href: l.Url, target: '_blank', rel: 'noopener', text: l.Label }))));
        details.appendChild(ul);
        details.appendChild(el('div', { text: 'Then save the station, or press Refresh content, and the files join the rotation. Credit: ' + p.Attribution }));
      }
      row.appendChild(details);
      list.appendChild(row);
    });
    if (anyRunning) {
      musicPackTimer = setTimeout(() => {
        if (!$('#tvsPanelWeather').classList.contains('tvs-hidden') && !$('#tvsViewEditor').classList.contains('tvs-hidden')) {
          loadMusicPacks();
        }
      }, 2000);
    }
  }

  function loadWeatherStatus() {
    if (isNewDraft()) {
      return;
    }
    api('GET', 'Weather/Status/' + state.draft.Id).then(renderWeatherStatus).catch(() => renderWeatherStatus(null));
  }

  function renderWeatherStatus(status) {
    const box = clear($('#tvsWeatherStatus'));
    show($('#tvsWeatherStatusTools'), !isNewDraft());
    if (isNewDraft()) {
      box.textContent = 'Save the station to start fetching forecasts.';
      return;
    }
    if (!status || !Array.isArray(status.Locations) || !status.Locations.length) {
      box.textContent = 'No forecasts fetched yet.';
      return;
    }
    const table = el('table', { className: 'tvs-table' });
    table.appendChild(el('thead', null, el('tr', null, [el('th', { text: 'Place' }), el('th', { text: 'Now' }), el('th', { text: 'Last fetched' }), el('th', { text: 'Alerts' }), el('th', { text: 'Radar' }), el('th', { text: 'Forecast radar' }), el('th', { text: '' })])));
    const tbody = el('tbody');
    const metric = state.draft.Weather.Units === 'Metric';
    status.Locations.forEach(l => {
      const temp = l.TemperatureC === null || l.TemperatureC === undefined ? '' : (metric ? Math.round(l.TemperatureC) + '°C' : Math.round(l.TemperatureC * 9 / 5 + 32) + '°F');
      const now = temp ? temp + (l.Conditions ? ', ' + l.Conditions.toLowerCase() : '') : '—';
      let note = null;
      if (l.LastError) {
        note = badge('Error', 'warn', l.LastError);
      } else if (l.Stale) {
        note = badge('Not current', 'warn', 'Older than the station allows; the screens say so.');
      } else if (l.LastSuccessUtc) {
        note = badge('OK', 'on');
      }
      let radar = '—';
      if (l.RadarFrames > 0) {
        radar = (l.RadarSource ? l.RadarSource.replace(/ via .*$/, '') : 'Radar') + ', ' + l.RadarFrames + ' frames' + (l.RadarLatestUtc ? ', newest ' + fmtDateTime(l.RadarLatestUtc) : '');
      } else if (l.RadarError) {
        radar = l.RadarError;
      } else if (state.draft.Weather.ShowRadar === false) {
        radar = 'off';
      }
      let forecast = '—';
      if (l.ForecastRadarFrames > 0) {
        forecast = (l.ForecastRadarRunUtc ? fmtDateTime(l.ForecastRadarRunUtc) + ' run, ' : '') + l.ForecastRadarFrames + ' frames' + (l.ForecastRadarLatestUtc ? ', out to ' + fmtDateTime(l.ForecastRadarLatestUtc) : '');
      } else if (l.ForecastRadarError) {
        forecast = l.ForecastRadarError;
      } else if (state.draft.Weather.ShowForecastRadar === false) {
        forecast = 'off';
      }
      let alerts = '—';
      let alertTitle = l.AlertSource || '';
      if (l.AlertCount > 0) {
        alerts = l.AlertSummary || (l.AlertCount + ' active');
        alertTitle = (l.AlertSource || '') + (l.AlertsCheckedUtc ? ', checked ' + fmtDateTime(l.AlertsCheckedUtc) : '');
      } else if (state.draft.Weather.ShowAlerts === false) {
        alerts = 'off';
      } else if (l.AlertsNotCovered) {
        alerts = 'not covered';
        alertTitle = l.AlertError || 'No alert source covers this place; the screen is skipped for it.';
      } else if (l.AlertError) {
        alerts = l.AlertError;
      } else if (l.AlertsCheckedUtc) {
        alerts = 'none';
        alertTitle = (l.AlertSource || '') + ', checked ' + fmtDateTime(l.AlertsCheckedUtc);
      }
      tbody.appendChild(el('tr', null, [
        el('td', { text: l.Name }),
        el('td', { text: now }),
        el('td', { className: 'nowrap', text: l.LastSuccessUtc ? fmtDateTime(l.LastSuccessUtc) : 'never' }),
        el('td', { text: alerts, title: alertTitle }),
        el('td', { text: radar, title: l.RadarSource || '' }),
        el('td', { text: forecast, title: l.ForecastRadarSource || '' }),
        el('td', { className: 'nowrap' }, [note, l.LastError ? el('span', { className: 'tvs-secondary', text: ' ' + l.LastError }) : null])
      ]));
    });
    table.appendChild(tbody);
    box.appendChild(table);
    if (status.MusicLevelling && status.MusicTracks > 0) {
      const pending = status.MusicTracks - status.MusicLevelled - (status.MusicUnmeasurable || 0);
      box.appendChild(el('div', { className: 'tvs-secondary', text: 'Music levelling: ' + status.MusicLevelled + ' of ' + status.MusicTracks + ' track' + (status.MusicTracks === 1 ? '' : 's') + ' measured' +
        (status.MusicUnmeasurable ? ', ' + status.MusicUnmeasurable + ' could not be read (played as recorded)' : '') +
        (pending > 0 ? '; the rest are measured in the background, a few seconds each, and play as recorded meanwhile.' : '.') }));
    }
    box.appendChild(el('div', { className: 'tvs-secondary', text: 'Forecasts are refreshed every 15 minutes, alerts and radar every 5 and the forecast radar as each hourly model run arrives, while the station is enabled. ' + (status.Attribution || '') }));
  }

  function refreshWeatherNow() {
    if (isNewDraft()) {
      return;
    }
    loading(true);
    api('POST', 'Weather/Refresh/' + state.draft.Id).then(status => {
      renderWeatherStatus(status);
      toast('Forecasts fetched.');
    }).catch(err => alertErrors(err, 'Could not fetch forecasts')).then(() => loading(false));
  }

  function previewWeatherScreen() {
    if (isNewDraft()) {
      toast('Save the station first.');
      return;
    }
    const img = $('#tvsWeatherPreviewImg');
    const screen = val('#tvsWeatherPreviewScreen') || 'Current';
    const place = parseInt(val('#tvsWeatherPreviewPlace'), 10) || 0;
    img.src = ApiClient.getUrl('JellyStations/Weather/Preview/' + state.draft.Id, { screen: screen, location: place, width: 1280, v: Date.now() });
    img.alt = WEATHER_SCREEN_LABEL[screen] || screen;
    show(img, true);
  }

  function wireHandlers() {
    // List
    on('#tvsBtnNew', 'click', openPresets);
    on('#tvsBtnEmptyNew', 'click', openPresets);
    on('#tvsBtnEmptySuggest', 'click', () => openSuggestions(false));
    on('#tvsBtnPresetsBack', 'click', () => showView('list'));
    on('#tvsBtnSuggestPreset', 'click', openPresets);
    on('#tvsBumpersEnabled', 'change', () => { show($('#tvsBumperOptions'), checked('#tvsBumpersEnabled')); updateWeatherPanel(); });
    on('#tvsBumperWeatherEnabled', 'change', () => { show($('#tvsBumperWeatherOptions'), checked('#tvsBumperWeatherEnabled')); updateWeatherPanel(); });
    on('#tvsPreRollEnabled', 'change', () => show($('#tvsPreRollPicker'), checked('#tvsPreRollEnabled')));
    on('#tvsPostRollEnabled', 'change', () => show($('#tvsPostRollPicker'), checked('#tvsPostRollEnabled')));
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
    on('#tvsBtnRecompileAll', 'click', recompileAll);
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
    on('#tvsBtnSaveTop', 'click', () => saveStation(false));
    on('#tvsSteps', 'click', ev => {
      const button = ev.target.closest ? ev.target.closest('.tvs-step') : null;
      if (button) {
        showStep(button.getAttribute('data-step'));
      }
    });
    on('#tvsBtnGenerateLogo', 'click', generateLogo);
    on('#tvsBtnLogoAnotherLook', 'click', () => {
      state.logoLook = (state.logoLook || 0) + 1;
      generateLogo();
    });
    // A fresh color suggestion follows the name (and number, when seeded) until the person picks one.
    on('#tvsName', 'input', () => { if (state.draft) { suggestLogoColor(); } });
    on('#tvsChannel', 'input', () => { if (state.draft) { suggestLogoColor(); } });
    on('#tvsLogoColor', 'input', () => { $('#tvsLogoColor').dataset.picked = '1'; });
    on('#tvsLogoAutoColor', 'change', () => {
      rememberAutoLogoColor(checked('#tvsLogoAutoColor'));
      suggestLogoColor();
    });
    on('#tvsBtnSaveRefresh', 'click', () => saveStation(true));
    on('#tvsBtnRecompile', 'click', recompileStation);
    on('#tvsMode', 'change', updateModePanels);
    on('#tvsWeatherSearch', 'input', debounce(weatherSearch, 350));
    on('#tvsWeatherMusic', 'change', () => show($('#tvsWeatherMusicOptions'), checked('#tvsWeatherMusic')));
    on('#tvsWeatherRadar', 'change', () => show($('#tvsWeatherRadarZoomBox'), checked('#tvsWeatherRadar')));
    on('#tvsWeatherTitleCards', 'change', () => show($('#tvsWeatherTitleCardBox'), checked('#tvsWeatherTitleCards')));
    on('#tvsWeatherForecastRadar', 'change', () => show($('#tvsWeatherForecastRadarBox'), checked('#tvsWeatherForecastRadar')));
    on('#tvsBtnWeatherRefresh', 'click', refreshWeatherNow);
    on('#tvsBtnWeatherPreview', 'click', previewWeatherScreen);
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
    on('#tvsUpNextEnabled', 'change', () => show($('#tvsUpNextOptions'), checked('#tvsUpNextEnabled')));
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
    on('#tvsBtnCoverage', 'click', () => openCoverage(false));
    on('#tvsBtnCoverageRefresh', 'click', () => openCoverage(true));
    on('#tvsCoverageDisabled', 'change', () => openCoverage(false));
    on('#tvsBtnCoverageBack', 'click', () => showView('list'));
    on('#tvsBtnCoverageNew', 'click', newChannelFromCoverage);
    on('#tvsBtnCoverageAdd', 'click', addCoverageToStation);
    on('#tvsCoverageTarget', 'change', updateCoverageCount);
    on('#tvsCoverageFilter', 'input', renderCoverage);
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
    on('#tvsBtnHwTest', 'click', runHardwareTest);
    on('#tvsBtnHwRefresh', 'click', () => loadHardware(true));
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
