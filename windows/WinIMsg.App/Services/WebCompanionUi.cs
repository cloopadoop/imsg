namespace WinIMsg.App.Services;

/// <summary>The web companion's embedded single-page UI.</summary>
internal static class WebCompanionUi
{
    public const string Manifest = """
{"name":"WinIMsg Web Companion","short_name":"WinIMsg","start_url":"/","scope":"/","display":"standalone","background_color":"#ffffff","theme_color":"#0a84ff"}
""";

    public const string ServiceWorker = """
const CACHE = "win-imsg-shell-v3";
const SHELL = ["/", "/manifest.webmanifest", "/sw.js"];
self.addEventListener("install", event => event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(SHELL)).then(() => self.skipWaiting())));
self.addEventListener("activate", event => event.waitUntil(self.clients.claim()));
self.addEventListener("fetch", event => {
  if (event.request.method !== "GET") return;
  const url = new URL(event.request.url);
  if (url.pathname.startsWith("/api/") || url.pathname === "/bootstrap") {
    event.respondWith(fetch(event.request).catch(() => new Response(JSON.stringify({error:"WinIMsg is not running. Start it and refresh."}), {status:503, headers:{"Content-Type":"application/json"}})));
    return;
  }
  event.respondWith(fetch(event.request).then(response => {
    if (response.ok && url.origin === location.origin) caches.open(CACHE).then(cache => cache.put(event.request, response.clone()));
    return response;
  }).catch(() => caches.match(event.request).then(response => response || caches.match("/"))));
});
""";

    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>iMessage</title>
<link rel="manifest" href="/manifest.webmanifest">
<meta name="theme-color" content="#0a84ff">
<style>
:root {
  --bg: #ffffff; --panel: #f4f4f6; --line: #e3e3e8; --text: #1b1b1f;
  --muted: #6d6d76; --accent: #0a84ff; --bubble-in: #e9e9ec; --bubble-out: #0a84ff;
  --failed: #c0392b;
}
@media (prefers-color-scheme: dark) {
  :root {
    --bg: #1c1c1f; --panel: #242428; --line: #333338; --text: #f2f2f5;
    --muted: #9a9aa3; --bubble-in: #303036;
  }
}
* { box-sizing: border-box; margin: 0; }
html, body { height: 100%; overflow: hidden; }
body {
  font: 14px/1.45 "Segoe UI", system-ui, sans-serif; color: var(--text);
  background: var(--bg); display: grid; grid-template-columns: 300px 1fr;
  grid-template-rows: 100%;
}
button { font: inherit; }
#sidebar { border-right: 1px solid var(--line); display: flex; flex-direction: column; min-width: 0; min-height: 0; }
#sidebarTop { display: flex; gap: 6px; margin: 10px; }
#search { flex: 1; min-width: 0; padding: 7px 10px; border: 1px solid var(--line); border-radius: 8px;
  background: var(--panel); color: var(--text); outline: none; }
.iconBtn { padding: 6px 10px; border: 1px solid var(--line); border-radius: 8px; background: var(--panel);
  color: var(--text); cursor: pointer; }
.iconBtn.active { background: var(--accent); color: #fff; border-color: var(--accent); }
#massBar { display: none; gap: 6px; margin: 0 10px 8px; align-items: center; }
#massBar.show { display: flex; }
#massBar .count { flex: 1; color: var(--muted); font-size: 12.5px; }
#massBar button { padding: 5px 10px; border: 0; border-radius: 8px; background: var(--accent); color: #fff; cursor: pointer; }
#massBar button:disabled { opacity: .5; }
#chats { overflow-y: auto; flex: 1; min-height: 0; }
.chat { display: grid; grid-template-columns: 10px 1fr auto; gap: 8px; padding: 9px 12px;
  cursor: pointer; align-items: start; }
.chat:hover { background: var(--panel); }
.chat.active { background: var(--panel); }
.chat .dot { width: 8px; height: 8px; border-radius: 4px; margin-top: 6px; }
.chat.unread .dot { background: var(--accent); }
.chat .name { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.chat.unread .name { font-weight: 700; }
.chat .preview { color: var(--muted); font-size: 12.5px; white-space: nowrap; overflow: hidden;
  text-overflow: ellipsis; grid-column: 2 / 4; }
.chat .when { color: var(--muted); font-size: 11.5px; }
.chat input[type=checkbox] { margin-top: 4px; }
#main { display: flex; flex-direction: column; min-width: 0; min-height: 0; }
#title { display: flex; align-items: center; gap: 8px; padding: 12px 16px; font-weight: 700; font-size: 15px; border-bottom: 1px solid var(--line); }
#activityBtn { width: 16px; height: 16px; padding: 0; border: 1px solid var(--line); border-radius: 50%; background: #8e8e93; cursor: pointer; }
#activityBtn.busy { background: var(--accent); animation: activityPulse 1s infinite; }
#activityBtn.error { background: var(--failed); }
@keyframes activityPulse { 50% { opacity: .35; } }
#activityLog { display: none; position: absolute; z-index: 4; top: 48px; right: 12px; width: 280px; max-height: 220px; overflow-y: auto; padding: 8px; border: 1px solid var(--line); border-radius: 9px; background: var(--bg); box-shadow: 0 6px 24px rgba(0,0,0,.25); font-size: 12px; }
#activityLog.show { display: block; }
#activityLog div { padding: 4px 2px; border-bottom: 1px solid var(--line); }
#compose-to { display: none; gap: 8px; align-items: center; padding: 8px 16px; border-bottom: 1px solid var(--line); }
#compose-to.show { display: flex; }
#compose-to label { color: var(--muted); font-size: 13px; }
#toInput { flex: 1; padding: 6px 10px; border: 1px solid var(--line); border-radius: 8px;
  background: var(--panel); color: var(--text); outline: none; }
#error { display: none; padding: 6px 16px; font-size: 12.5px; color: #fff; background: var(--failed); }
#error.show { display: block; }
#offline { display: none; padding: 6px 16px; font-size: 12.5px; color: var(--text); background: #ffd166; }
#offline.show { display: flex; align-items: center; gap: 8px; }
#offline .offlineActions { display: flex; gap: 6px; margin-left: auto; }
#offline button { border: 0; border-radius: 6px; padding: 3px 9px; cursor: pointer; }
#messages { flex: 1; min-height: 0; overflow-y: auto; padding: 14px 16px; display: flex; flex-direction: column; gap: 6px; }
.msg { max-width: 68%; padding: 7px 11px; border-radius: 14px; position: relative; }
.msg.in { background: var(--bubble-in); align-self: flex-start; border-bottom-left-radius: 4px; }
.msg.out { background: var(--bubble-out); color: #fff; align-self: flex-end; border-bottom-right-radius: 4px; }
.msg.failed { background: var(--failed); }
.msg .meta { display: block; font-size: 11px; opacity: .65; }
.msg .body { white-space: pre-line; overflow-wrap: anywhere; }
.msg .att { display: block; font-size: 12px; font-style: italic; opacity: .8; }
.msg .attimg { display: block; max-width: 300px; max-height: 300px; border-radius: 10px; margin-top: 4px; cursor: pointer; }
.msg .attvideo { display: block; width: 300px; max-width: 100%; height: auto; border-radius: 10px; margin-top: 4px; }
.msg audio { display: block; margin-top: 4px; max-width: 260px; height: 34px; }
.msg .status { display: block; font-size: 11px; opacity: .8; font-style: italic; margin-top: 2px; }
.msg .reactions { position: absolute; top: -12px; font-size: 12px; background: var(--panel);
  border: 1px solid var(--line); border-radius: 10px; padding: 0 5px; white-space: nowrap; }
.msg.in .reactions { right: -6px; }
.msg.out .reactions { left: -6px; }
#composer { display: flex; gap: 6px; padding: 10px 12px; border-top: 1px solid var(--line); align-items: center; position: relative; }
#composer.dragover { outline: 2px dashed var(--accent); outline-offset: -5px; background: color-mix(in srgb, var(--accent) 8%, transparent); }
#draftAttachments { display: none; position: absolute; left: 12px; right: 12px; bottom: calc(100% + 1px); gap: 6px; flex-wrap: wrap; padding: 7px 0; }
#draftAttachments.show { display: flex; }
.draftAttachment { display: inline-flex; align-items: center; gap: 5px; max-width: 260px; padding: 4px 8px; border: 1px solid var(--line); border-radius: 10px; background: var(--panel); font-size: 12px; }
.draftAttachment span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.draftAttachment button { border: 0; background: none; color: var(--muted); cursor: pointer; padding: 0 2px; }
#dropHint { position: absolute; inset: 5px; display: none; place-items: center; pointer-events: none; color: var(--accent); font-weight: 600; background: color-mix(in srgb, var(--bg) 86%, transparent); border-radius: 10px; }
#composer.dragover #dropHint { display: grid; }
#input { flex: 1; min-width: 0; padding: 8px 12px; border: 1px solid var(--line); border-radius: 16px;
  background: var(--panel); color: var(--text); outline: none; }
#sendBtn { padding: 8px 16px; border: 0; border-radius: 16px; background: var(--accent);
  color: #fff; font-weight: 600; cursor: pointer; }
#sendBtn:disabled, .iconBtn:disabled { opacity: .5; }
#voiceBtn.recording { background: var(--failed); color: #fff; border-color: var(--failed); }
#empty { color: var(--muted); margin: auto; }
#menu, #emojiPanel { position: fixed; z-index: 10; background: var(--bg); border: 1px solid var(--line);
  border-radius: 10px; box-shadow: 0 6px 24px rgba(0,0,0,.25); display: none; }
#menu.show, #emojiPanel.show { display: block; }
#menu .tapbacks { display: flex; gap: 2px; padding: 6px; border-bottom: 1px solid var(--line); }
#menu .tapbacks button { border: 0; background: none; font-size: 18px; padding: 4px 6px; cursor: pointer; border-radius: 8px; }
#menu .tapbacks button:hover { background: var(--panel); }
#menu .item { padding: 8px 14px; cursor: pointer; font-size: 13.5px; }
#menu .item:hover { background: var(--panel); }
#menu .item.danger { color: var(--failed); }
#emojiPanel { width: 340px; height: 360px; display: none; flex-direction: column; }
#emojiPanel.show { display: flex; }
#emojiSearch { margin: 8px; padding: 6px 10px; border: 1px solid var(--line); border-radius: 8px;
  background: var(--panel); color: var(--text); outline: none; }
#emojiGrid { flex: 1; min-height: 0; overflow-y: auto; padding: 0 8px 8px; }
#emojiGrid h4 { font-size: 11.5px; color: var(--muted); font-weight: 600; margin: 8px 2px 2px; }
#emojiGrid button { border: 0; background: none; font-size: 20px; padding: 3px; cursor: pointer; border-radius: 6px;
  font-family: "Segoe UI Emoji", system-ui; }
#emojiGrid button:hover { background: var(--panel); }
#emojiGrid .none { color: var(--muted); font-size: 13px; padding: 12px 4px; }
</style>
</head>
<body>
<div id="sidebar">
  <div id="sidebarTop">
    <input id="search" placeholder="Search chats" autocomplete="off">
    <button id="selectBtn" class="iconBtn" title="Select conversations">☑</button>
    <button id="newBtn" class="iconBtn" title="New message">✎</button>
  </div>
  <div id="massBar">
    <span class="count">0 selected</span>
    <button id="massAllBtn">All</button>
    <button id="massReadBtn" disabled>Mark read</button>
  </div>
  <div id="chats"></div>
</div>
<div id="main">
  <div id="title"><button id="activityBtn" title="Activity"></button><span>iMessage</span></div>
  <div id="activityLog"></div>
  <div id="compose-to"><label>To:</label><input id="toInput" placeholder="phone or email, comma-separated for a group" autocomplete="off"></div>
  <div id="error"></div>
  <div id="offline"><span id="offlineText">WinIMsg is not running.</span><span class="offlineActions"><button id="offlineLaunch">Launch WinIMsg</button><button id="offlineRefresh">Refresh</button></span></div>
  <div id="messages"><div id="empty">Select a conversation</div></div>
  <div id="composer">
    <div id="draftAttachments"></div>
    <div id="dropHint">Drop files here to add them to the draft</div>
    <button id="attachBtn" class="iconBtn" title="Attach file">📎</button>
    <input id="fileInput" type="file" multiple hidden>
    <input id="input" placeholder="Message" autocomplete="off">
    <button id="emojiBtn" class="iconBtn" title="Emoji">🙂</button>
    <button id="voiceBtn" class="iconBtn" title="Record voice message">🎤</button>
    <button id="sendBtn">Send</button>
  </div>
</div>
<div id="menu"></div>
<div id="emojiPanel">
  <input id="emojiSearch" placeholder="Search emoji" autocomplete="off">
  <div id="emojiGrid"></div>
</div>
<script>
"use strict";
history.replaceState(null, "", location.pathname);
let chats = [];
let selected = null;
let composing = false;
let selectMode = false;
const checked = new Set();
let lastMessages = [];
let pendingSeq = 0;
let pendings = [];
let draftAttachments = [];
let activityEntries = [];
let caps = {connected: false, canTapback: false, canEdit: false, canUnsend: false, canDelete: false};
const TAPBACKS = [["love","❤️"],["like","👍"],["dislike","👎"],["laugh","😂"],["emphasize","‼️"],["question","❓"]];
let emojiGroups = null; // [{g: name, e: [[char, name], ...]}] loaded on first open

const el = id => document.getElementById(id);
const esc = s => (s ?? "").replace(/[&<>"]/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c]));
const when = iso => {
  if (!iso) return "";
  const d = new Date(iso), now = new Date();
  return d.toDateString() === now.toDateString()
    ? d.toLocaleTimeString([], {hour: "numeric", minute: "2-digit"})
    : d.toLocaleDateString([], {month: "short", day: "numeric"});
};
async function api(path, options) {
  let response;
  try { response = await fetch(path, options); }
  catch (error) { setOffline(true); throw error; }
  if (!response.ok) {
    const detail = await response.json().catch(() => ({}));
    if (response.status === 503) setOffline(true);
    throw new Error(detail.error ?? ("HTTP " + response.status));
  }
  setOffline(false);
  return response.json().catch(() => ({}));
}

function setOffline(value) { el("offline").classList.toggle("show", value); }
if ("serviceWorker" in navigator) navigator.serviceWorker.register("/sw.js").catch(() => {});
el("offlineRefresh").addEventListener("click", () => location.replace("/"));
let launchRetryTimer = null;
el("offlineLaunch").addEventListener("click", () => {
  el("offlineText").textContent = "Launching WinIMsg…";
  const nonce = crypto.randomUUID().replaceAll("-", "");
  window.location.href = `winimsg:WebCompanion?nonce=${nonce}`;
  clearInterval(launchRetryTimer);
  let attempts = 0;
  launchRetryTimer = setInterval(async () => {
    attempts++;
    try {
      const response = await fetch(`/bootstrap?nonce=${nonce}`, {cache: "no-store"});
      if (response.ok) {
        clearInterval(launchRetryTimer);
        location.replace("/");
      }
    } catch {}
    if (attempts >= 30) {
      clearInterval(launchRetryTimer);
      el("offlineText").textContent = "WinIMsg did not start. Launch it manually, then refresh.";
    }
  }, 1000);
});
el("activityBtn").addEventListener("click", e => { e.stopPropagation(); el("activityLog").classList.toggle("show"); });
function addActivity(data) {
  const stamp = data.at ? when(data.at) : "now";
  activityEntries.unshift(`${stamp} · ${data.label ?? "Activity"}`);
  activityEntries = activityEntries.slice(0, 20);
  el("activityLog").innerHTML = activityEntries.map(entry => `<div>${esc(entry)}</div>`).join("");
  el("activityBtn").className = data.state ?? "ready";
  el("activityBtn").title = data.label ?? "Activity";
}

async function loadCaps() {
  try { caps = await api("/api/capabilities"); } catch {}
}

async function loadChats() {
  try { chats = await api("/api/chats"); } catch { return; }
  renderChats();
  updateBadge();
}

function renderChats() {
  const q = el("search").value.trim().toLowerCase();
  el("chats").innerHTML = chats
    .filter(c => !q || (c.name ?? "").toLowerCase().includes(q) || (c.preview ?? "").toLowerCase().includes(q))
    .map(c => {
      const box = selectMode ? `<input type="checkbox" data-check="${esc(c.stableId)}"${checked.has(c.stableId) ? " checked" : ""}>` : `<div class="dot"></div>`;
      return `<div class="chat${c.unread > 0 ? " unread" : ""}${selected === c.stableId ? " active" : ""}" data-id="${esc(c.stableId)}">${box}<div class="name">${esc(c.name)}</div><div class="when">${when(c.lastMessageAt)}</div><div class="preview">${esc(c.preview)}</div></div>`;
    })
    .join("");
  updateMassBar();
}

async function openChat(stableId) {
  draftAttachments = [];
  renderDraftAttachments();
  composing = false;
  el("compose-to").classList.remove("show");
  selected = stableId;
  const chat = chats.find(c => c.stableId === stableId);
  el("title").textContent = chat ? chat.name : "iMessage";
  renderChats();
  await loadMessages();
  markRead(stableId);
}

async function markRead(stableId) {
  try {
    const result = await api(`/api/chats/${encodeURIComponent(stableId)}/read`, {method: "POST"});
    if (result?.marked) { await loadChats(); updateBadge(); }
  } catch {}
}

async function loadMessages() {
  if (!selected) return;
  try { lastMessages = await api(`/api/chats/${encodeURIComponent(selected)}/messages?limit=120`); } catch { return; }
  renderMessages();
}

function survivingPendings() {
  const cutoff = Date.now() - 2 * 60 * 1000;
  pendings = pendings.filter(p =>
    p.chatId === selected &&
    (p.failed || p.sentAt > cutoff) &&
    !lastMessages.some(m =>
      (p.guid && m.guid === p.guid) ||
      (!p.guid && m.fromMe && (m.text ?? "").trim() === p.text.trim())));
  return pendings.filter(p => p.chatId === selected);
}

function attachmentHtml(a) {
  // Older payload shape was a plain string; render both.
  if (typeof a === "string") return `<span class="att">📎 ${esc(a)}</span>`;
  if (a.url && a.kind === "image") return `<img class="attimg" src="${esc(a.url)}" alt="${esc(a.name)}" onclick="window.open(this.src)">`;
  if (a.url && a.kind === "audio") return `<audio controls preload="none" src="${esc(a.url)}"></audio>`;
  if (a.url && a.kind === "video") return `<video class="attvideo" controls playsinline preload="metadata"><source src="${esc(a.url)}" type="${esc(a.contentType ?? "video/mp4")}"></video>`;
  return `<span class="att">📎 ${esc(a.name)}</span>`;
}

function messageHtml(m, index) {
  const atts = (m.attachments ?? []).map(attachmentHtml).join("");
  const reactions = (m.reactions ?? []).length
    ? `<span class="reactions">${(m.reactions ?? []).map(r => esc(r.emoji ?? "")).join("")}</span>`
    : "";
  return `<div class="msg ${m.fromMe ? "out" : "in"}" data-index="${index}">${reactions}<span class="meta">${esc(m.fromMe ? "Me" : m.sender)} · ${when(m.date)}</span><span class="body">${esc(m.text)}</span>${atts}</div>`;
}

function pendingHtml(p) {
  const status = p.failed ? `Not sent: ${esc(p.error ?? "send failed")}` : (p.status ?? "Sending…");
  return `<div class="msg out${p.failed ? " failed" : ""}" id="${p.id}"><span class="meta">Me · now</span><span class="body">${esc(p.text)}</span><span class="status">${status}</span></div>`;
}

function renderMessages() {
  const rows = lastMessages.map(messageHtml).join("") + survivingPendings().map(pendingHtml).join("");
  el("messages").innerHTML = rows || '<div id="empty">No messages yet</div>';
  el("messages").scrollTop = el("messages").scrollHeight;
}

let errorTimer = null;
function showError(message) {
  const bar = el("error");
  bar.textContent = message;
  bar.classList.add("show");
  clearTimeout(errorTimer);
  errorTimer = setTimeout(() => bar.classList.remove("show"), 8000);
}

function addPending(text, status) {
  const pending = {id: "pending-" + (++pendingSeq), chatId: selected, text, guid: null, sentAt: Date.now(), failed: false, error: null, status};
  pendings.push(pending);
  renderMessages();
  return pending;
}

async function send() {
  const text = el("input").value.trim();
  if (composing) { await sendDraft(text); return; }
  if (!selected) { showError("Select a conversation first."); return; }
  if (!text && draftAttachments.length === 0) return;
  const attachmentIds = draftAttachments.map(a => a.id);
  el("input").value = "";
  const pending = addPending(text || (attachmentIds.length === 1 ? "📎 " + draftAttachments[0].name : `📎 ${attachmentIds.length} attachments`));
  try {
    const result = await api("/api/send", {
      method: "POST",
      headers: {"Content-Type": "application/json"},
      body: JSON.stringify({stableId: selected, text, attachmentIds})
    });
    pending.guid = result.guid ?? null;
    draftAttachments = [];
    renderDraftAttachments();
  } catch (sendError) {
    el("input").value = text;
    pending.failed = true;
    pending.error = sendError.message;
    showError("Send failed: " + sendError.message);
  }
  renderMessages();
  loadChats();
}

async function sendDraft(text) {
  const recipients = el("toInput").value.split(/[,;]/).map(r => r.trim()).filter(Boolean);
  if (recipients.length === 0) { showError("Enter at least one recipient."); return; }
  if (!text) return;
  el("input").value = "";
  try {
    await api("/api/send", {
      method: "POST",
      headers: {"Content-Type": "application/json"},
      body: JSON.stringify({recipients, text})
    });
    composing = false;
    el("compose-to").classList.remove("show");
    el("title").textContent = "iMessage";
    setTimeout(loadChats, 1500);
  } catch (sendError) {
    el("input").value = text;
    showError("Send failed: " + sendError.message);
  }
}

// --- message context menu -------------------------------------------------
function hidePopovers() { el("menu").classList.remove("show"); el("emojiPanel").classList.remove("show"); }

el("messages").addEventListener("contextmenu", e => {
  const bubble = e.target.closest(".msg[data-index]");
  if (!bubble) return;
  e.preventDefault();
  const m = lastMessages[Number(bubble.dataset.index)];
  if (!m?.guid) return;
  const menu = el("menu");
  const myReactions = new Set((m.reactions ?? []).filter(r => r.fromMe).map(r => r.type));
  let html = "";
  if (caps.canTapback) {
    html += `<div class="tapbacks">${TAPBACKS.map(([type, emoji]) =>
      `<button data-tapback="${type}" data-remove="${myReactions.has(type)}" title="${type}">${emoji}</button>`).join("")}</div>`;
  }
  html += `<div class="item" data-action="copy">Copy text</div>`;
  if (m.fromMe && caps.canEdit) html += `<div class="item" data-action="edit">Edit…</div>`;
  if (m.fromMe && caps.canUnsend) html += `<div class="item danger" data-action="unsend">Unsend</div>`;
  if (caps.canDelete) html += `<div class="item danger" data-action="delete">Delete for me</div>`;
  menu.innerHTML = html;
  menu.dataset.guid = m.guid;
  menu.dataset.text = m.text ?? "";
  menu.classList.add("show");
  const x = Math.min(e.clientX, window.innerWidth - menu.offsetWidth - 8);
  const y = Math.min(e.clientY, window.innerHeight - menu.offsetHeight - 8);
  menu.style.left = x + "px";
  menu.style.top = y + "px";
});

el("menu").addEventListener("click", async e => {
  const menu = el("menu");
  const guid = menu.dataset.guid;
  const tapback = e.target.closest("[data-tapback]");
  hidePopovers();
  try {
    if (tapback) {
      await api("/api/messages/tapback", {method: "POST", headers: {"Content-Type": "application/json"},
        body: JSON.stringify({stableId: selected, messageGuid: guid, reaction: tapback.dataset.tapback, remove: tapback.dataset.remove === "true"})});
      setTimeout(() => { loadMessages(); loadChats(); }, 1200);
      return;
    }
    const action = e.target.closest("[data-action]")?.dataset.action;
    if (action === "copy") { await navigator.clipboard.writeText(menu.dataset.text); return; }
    if (action === "edit") {
      const text = prompt("Edit message:", menu.dataset.text);
      if (text == null || !text.trim() || text === menu.dataset.text) return;
      await api("/api/messages/edit", {method: "POST", headers: {"Content-Type": "application/json"},
        body: JSON.stringify({stableId: selected, messageGuid: guid, text: text.trim()})});
      setTimeout(() => { loadMessages(); loadChats(); }, 1200);
      return;
    }
    if (action === "unsend" || action === "delete") {
      if (!confirm(action === "unsend" ? "Unsend this message for everyone?" : "Delete this message from this device?")) return;
      await api(`/api/messages/${action}`, {method: "POST", headers: {"Content-Type": "application/json"},
        body: JSON.stringify({stableId: selected, messageGuid: guid})});
      setTimeout(() => { loadMessages(); loadChats(); }, 1200);
    }
  } catch (actionError) {
    showError("Action failed: " + actionError.message);
  }
});

// --- emoji picker: full Unicode set with search -----------------------------
function renderEmojiGrid() {
  if (!emojiGroups) return;
  const q = el("emojiSearch").value.trim().toLowerCase();
  const grid = el("emojiGrid");
  let html = "";
  for (const group of emojiGroups) {
    const matches = q ? group.e.filter(([, name]) => name.includes(q)) : group.e;
    if (matches.length === 0) continue;
    html += `<h4>${esc(group.g)}</h4>` +
      matches.map(([char, name]) => `<button title="${esc(name)}">${char}</button>`).join("");
  }
  grid.innerHTML = html || '<div class="none">No emoji match that search.</div>';
}

el("emojiBtn").addEventListener("click", async e => {
  e.stopPropagation();
  const panel = el("emojiPanel");
  if (panel.classList.contains("show")) { panel.classList.remove("show"); return; }
  if (!emojiGroups) {
    try { emojiGroups = await api("/api/emoji"); } catch { showError("Could not load the emoji list."); return; }
  }
  el("emojiSearch").value = "";
  renderEmojiGrid();
  panel.classList.add("show");
  const rect = el("emojiBtn").getBoundingClientRect();
  panel.style.left = Math.max(8, rect.right - panel.offsetWidth) + "px";
  panel.style.top = Math.max(8, rect.top - panel.offsetHeight - 6) + "px";
  el("emojiSearch").focus();
});
el("emojiSearch").addEventListener("input", renderEmojiGrid);
el("emojiPanel").addEventListener("click", e => {
  if (e.target.tagName !== "BUTTON") return;
  const input = el("input");
  const at = input.selectionStart ?? input.value.length;
  input.value = input.value.slice(0, at) + e.target.textContent + input.value.slice(input.selectionEnd ?? at);
  input.focus();
});
document.addEventListener("click", e => {
  if (!e.target.closest("#menu") && !e.target.closest("#emojiPanel") && e.target.id !== "emojiBtn") hidePopovers();
});

// --- attachments ------------------------------------------------------------
function renderDraftAttachments() {
  const container = el("draftAttachments");
  container.classList.toggle("show", draftAttachments.length > 0);
  container.innerHTML = draftAttachments.map(a => `<div class="draftAttachment"><span title="${esc(a.name)}">📎 ${esc(a.name)}</span><button type="button" data-draft-id="${esc(a.id)}" title="Remove attachment">×</button></div>`).join("");
}

async function queueFiles(files) {
  if (!selected || composing) { showError("Open a conversation before attaching files."); return; }
  for (const file of files) {
    try {
      const result = await api(`/api/attachments?stableId=${encodeURIComponent(selected)}&name=${encodeURIComponent(file.name)}`,
        {method: "POST", body: file});
      draftAttachments.push({id: result.draftAttachmentId, name: result.name ?? file.name});
      renderDraftAttachments();
    } catch (uploadError) {
      showError(`Attachment could not be added (${file.name}): ` + uploadError.message);
    }
  }
}

el("attachBtn").addEventListener("click", () => {
  if (!selected || composing) { showError("Open a conversation before attaching files."); return; }
  el("fileInput").click();
});
el("fileInput").addEventListener("change", async () => {
  const files = [...el("fileInput").files];
  el("fileInput").value = "";
  await queueFiles(files);
});
el("draftAttachments").addEventListener("click", async e => {
  const button = e.target.closest("[data-draft-id]");
  if (!button) return;
  const id = button.dataset.draftId;
  draftAttachments = draftAttachments.filter(a => a.id !== id);
  renderDraftAttachments();
  try { await api("/api/attachments/discard", {method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify({draftAttachmentId: id})}); } catch {}
});
const composer = el("composer");
composer.addEventListener("dragenter", e => { e.preventDefault(); if (selected && !composing) composer.classList.add("dragover"); });
composer.addEventListener("dragover", e => e.preventDefault());
composer.addEventListener("dragleave", e => { if (!composer.contains(e.relatedTarget)) composer.classList.remove("dragover"); });
composer.addEventListener("drop", async e => {
  e.preventDefault(); composer.classList.remove("dragover");
  await queueFiles([...e.dataTransfer.files]);
});

// --- voice memo (WAV via AudioContext, deterministic on every device) -------
let recorder = null;
el("voiceBtn").addEventListener("click", async () => {
  if (recorder) { await stopVoice(false); return; }
  if (!selected || composing) { showError("Open a conversation before recording."); return; }
  try {
    const stream = await navigator.mediaDevices.getUserMedia({audio: true});
    const context = new AudioContext();
    const source = context.createMediaStreamSource(stream);
    const processor = context.createScriptProcessor(4096, 1, 1);
    const buffers = [];
    processor.onaudioprocess = e => buffers.push(new Float32Array(e.inputBuffer.getChannelData(0)));
    source.connect(processor);
    processor.connect(context.destination);
    recorder = {stream, context, processor, buffers, sampleRate: context.sampleRate, startedAt: Date.now()};
    el("voiceBtn").classList.add("recording");
    el("voiceBtn").title = "Stop and send voice message";
  } catch (micError) {
    showError("Microphone unavailable: " + micError.message);
  }
});

async function stopVoice(discard) {
  const active = recorder;
  recorder = null;
  el("voiceBtn").classList.remove("recording");
  el("voiceBtn").title = "Record voice message";
  if (!active) return;
  active.processor.disconnect();
  active.stream.getTracks().forEach(track => track.stop());
  await active.context.close();
  if (discard || Date.now() - active.startedAt < 600) return;
  const wav = encodeWav(active.buffers, active.sampleRate);
  const name = "Voice Message " + new Date().toISOString().slice(0, 19).replaceAll(":", "-") + ".wav";
  await queueFiles([new File([wav], name, {type: "audio/wav"})]);
}

function encodeWav(buffers, sampleRate) {
  const length = buffers.reduce((sum, b) => sum + b.length, 0);
  const data = new DataView(new ArrayBuffer(44 + length * 2));
  const writeText = (at, s) => [...s].forEach((c, i) => data.setUint8(at + i, c.charCodeAt(0)));
  writeText(0, "RIFF"); data.setUint32(4, 36 + length * 2, true); writeText(8, "WAVE");
  writeText(12, "fmt "); data.setUint32(16, 16, true); data.setUint16(20, 1, true); data.setUint16(22, 1, true);
  data.setUint32(24, sampleRate, true); data.setUint32(28, sampleRate * 2, true);
  data.setUint16(32, 2, true); data.setUint16(34, 16, true);
  writeText(36, "data"); data.setUint32(40, length * 2, true);
  let at = 44;
  for (const buffer of buffers) {
    for (const sample of buffer) {
      data.setInt16(at, Math.max(-1, Math.min(1, sample)) * 0x7fff, true);
      at += 2;
    }
  }
  return new Blob([data], {type: "audio/wav"});
}

// --- new message + multi-select ----------------------------------------------
el("newBtn").addEventListener("click", () => {
  draftAttachments = [];
  renderDraftAttachments();
  composing = true;
  selected = null;
  el("title").textContent = "New Message";
  el("compose-to").classList.add("show");
  el("messages").innerHTML = '<div id="empty">Enter recipients, then type your message below</div>';
  renderChats();
  el("toInput").focus();
});

el("selectBtn").addEventListener("click", () => {
  selectMode = !selectMode;
  checked.clear();
  el("selectBtn").classList.toggle("active", selectMode);
  el("massBar").classList.toggle("show", selectMode);
  renderChats();
});
el("massAllBtn").addEventListener("click", () => {
  chats.forEach(c => checked.add(c.stableId));
  renderChats();
});
el("massReadBtn").addEventListener("click", async () => {
  const targets = [...checked];
  selectMode = false;
  checked.clear();
  el("selectBtn").classList.remove("active");
  el("massBar").classList.remove("show");
  renderChats();
  for (const id of targets) {
    try { await api(`/api/chats/${encodeURIComponent(id)}/read`, {method: "POST"}); } catch {}
  }
  await loadChats();
  updateBadge();
});
function updateMassBar() {
  el("massBar").querySelector(".count").textContent = `${checked.size} selected`;
  el("massReadBtn").disabled = checked.size === 0;
}

el("chats").addEventListener("click", e => {
  const box = e.target.closest("[data-check]");
  if (box) {
    box.checked ? checked.add(box.dataset.check) : checked.delete(box.dataset.check);
    updateMassBar();
    return;
  }
  const row = e.target.closest(".chat");
  if (!row) return;
  if (selectMode) {
    checked.has(row.dataset.id) ? checked.delete(row.dataset.id) : checked.add(row.dataset.id);
    renderChats();
    return;
  }
  openChat(row.dataset.id);
});

async function updateBadge() {
  try {
    const badge = await api("/api/badge");
    document.title = badge.unread > 0 ? `(${badge.unread}) iMessage` : "iMessage";
  } catch {}
}

el("search").addEventListener("input", renderChats);
el("sendBtn").addEventListener("click", send);
el("input").addEventListener("keydown", e => {
  if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); }
});

const events = new EventSource("/api/events");
events.addEventListener("message", async e => {
  const data = JSON.parse(e.data);
  await loadChats();
  if (selected && data.chatStableId === selected) {
    await loadMessages();
    if (!data.fromMe && document.hasFocus()) markRead(selected);
  }
});
events.addEventListener("unread", () => { loadChats(); updateBadge(); });
events.addEventListener("activity", e => addActivity(JSON.parse(e.data)));

loadCaps();
loadChats();
setInterval(() => { loadChats(); loadCaps(); }, 60000);
</script>
</body>
</html>
""";
}
