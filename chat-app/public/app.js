import { firebaseConfig } from "./firebase-config.js";
import {
  initializeApp, getAuth, connectAuthEmulator, onAuthStateChanged, createUserWithEmailAndPassword,
  signInWithEmailAndPassword, signOut, setPersistence, browserLocalPersistence,
  initializeFirestore, connectFirestoreEmulator, persistentLocalCache, persistentMultipleTabManager,
  doc, collection, getDoc, getDocs, setDoc, updateDoc, writeBatch, query, where, limit,
  limitToLast, orderBy, onSnapshot, serverTimestamp, arrayUnion, arrayRemove,
} from "./vendor/firebase.js";

/* ============================== helpers ============================== */

const appEl = document.getElementById("app");
const toastEl = document.getElementById("toast");

function h(tag, props, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v == null || v === false) continue;
    if (k === "class") el.className = v;
    else if (k.startsWith("on")) el.addEventListener(k.slice(2), v);
    else if (k === "svg") el.innerHTML = v; // only ever our own constant icons
    else if (k === "value") el.value = v;
    else el.setAttribute(k, v === true ? "" : v);
  }
  for (const c of kids.flat()) if (c != null && c !== false) el.append(c instanceof Node ? c : String(c));
  return el;
}

const ICON = {
  back: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 6l6 6-6 6"/></svg>',
  send: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M3 20.5 21 12 3 3.5l.01 6.6L15 12 3.01 13.9z"/></svg>',
  group: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="9" cy="8" r="3.5"/><path d="M2.5 20c.8-3.6 3.4-5.5 6.5-5.5s5.7 1.9 6.5 5.5"/><path d="M16 4.6a3.5 3.5 0 0 1 0 6.8M18 14.8c1.9.7 3.1 2.4 3.5 5.2"/></svg>',
  user: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="8" r="4"/><path d="M4 21c1-4.5 4.2-6.5 8-6.5s7 2 8 6.5"/></svg>',
  check: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12.5l4.5 4.5L19 7.5"/></svg>',
  x: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round"><path d="M6 6l12 12M18 6 6 18"/></svg>',
};

const PALETTE = ["#c0533a", "#3a6fc0", "#8a4fb8", "#b8851f", "#2f8f8a", "#c0407a", "#5d7d2a", "#7a5c3a"];
function colorFor(id) { let n = 0; for (const c of String(id)) n = (n * 31 + c.charCodeAt(0)) >>> 0; return PALETTE[n % PALETTE.length]; }
function initials(name) { return (String(name || "?").trim()[0] || "?"); }
function avatar(id, name, extra = "") { return h("div", { class: "avatar " + extra, style: `background:${colorFor(id)}`, "aria-hidden": "true" }, initials(name)); }

const timeFmt = new Intl.DateTimeFormat("ar-EG", { hour: "numeric", minute: "2-digit" });
const dayFmt = new Intl.DateTimeFormat("ar-EG", { weekday: "long", day: "numeric", month: "long" });
const shortDayFmt = new Intl.DateTimeFormat("ar-EG", { day: "numeric", month: "numeric" });
const toDate = ts => (ts && typeof ts.toDate === "function") ? ts.toDate() : (ts instanceof Date ? ts : null);
function whenLabel(ts) {
  const d = toDate(ts); if (!d) return "";
  const now = new Date();
  if (d.toDateString() === now.toDateString()) return timeFmt.format(d);
  const y = new Date(now); y.setDate(now.getDate() - 1);
  if (d.toDateString() === y.toDateString()) return "امبارح";
  return shortDayFmt.format(d);
}

let toastTimer;
function toast(msg) {
  toastEl.textContent = msg; toastEl.hidden = false;
  clearTimeout(toastTimer); toastTimer = setTimeout(() => (toastEl.hidden = true), 3200);
}

function debounce(fn, ms) { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; }
const store = {
  get(k, d) { try { const v = localStorage.getItem(k); return v == null ? d : JSON.parse(v); } catch { return d; } },
  set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch {} },
};

function cleanUsername(s) { return String(s || "").trim().toLowerCase().replace(/^@/, ""); }
const USERNAME_RE = /^[a-z0-9_]{3,20}$/;

function authError(e) {
  const map = {
    "auth/email-already-in-use": "الإيميل ده متسجل قبل كده. جرّب تدخل بيه.",
    "auth/invalid-email": "الإيميل مكتوب غلط.",
    "auth/weak-password": "الباسورد لازم يكون 6 حروف أو أرقام على الأقل.",
    "auth/invalid-credential": "الإيميل أو الباسورد غلط.",
    "auth/wrong-password": "الإيميل أو الباسورد غلط.",
    "auth/user-not-found": "مفيش حساب بالإيميل ده.",
    "auth/too-many-requests": "محاولات كتير. استنى شوية وجرّب تاني.",
    "auth/network-request-failed": "مفيش نت. اتأكد إنك متصل وجرّب تاني.",
    "auth/operation-not-allowed": "تسجيل الدخول بالإيميل مش متفعّل في Firebase. فعّله من Authentication → Sign-in method.",
  };
  return map[e && e.code] || "حصلت مشكلة. جرّب تاني.";
}

/* ============================== setup ============================== */

if (!firebaseConfig || String(firebaseConfig.apiKey || "").startsWith("PASTE")) {
  appEl.replaceChildren(h("div", { class: "setup" },
    h("div", { class: "logo big" }, "ونس"),
    h("h2", null, "باقي خطوة واحدة"),
    h("p", null, "التطبيق محتاج إعدادات مشروع Firebase. افتح الملف ", h("code", null, "public/firebase-config.js"),
      " والصق فيه الإعدادات، والخطوات كلها في ", h("code", null, "README.md"), "."),
  ));
  throw new Error("Firebase config missing");
}

const fbApp = initializeApp(firebaseConfig);
const auth = getAuth(fbApp);
const useEmulators = ["localhost", "127.0.0.1"].includes(location.hostname) && new URLSearchParams(location.search).has("emulator");
const db = initializeFirestore(fbApp, useEmulators ? {} : { localCache: persistentLocalCache({ tabManager: persistentMultipleTabManager() }) });
if (useEmulators) {
  connectAuthEmulator(auth, "http://127.0.0.1:9099", { disableWarnings: true });
  connectFirestoreEmulator(db, "127.0.0.1", 8080);
}
setPersistence(auth, browserLocalPersistence).catch(() => {});

if ("serviceWorker" in navigator && !useEmulators) {
  window.addEventListener("load", () => navigator.serviceWorker.register("sw.js").catch(() => {}));
}

/* ============================== state ============================== */

const state = {
  me: null,          // firebase auth user
  profile: null,     // users/{uid}
  chats: new Map(),  // chatId -> chat data
  chatsLoaded: false,
};
const users = new Map(); // uid -> Promise<profile|null>
function getUser(uid) {
  if (!users.has(uid)) users.set(uid, getDoc(doc(db, "users", uid)).then(s => s.exists() ? { id: uid, ...s.data() } : null).catch(() => { users.delete(uid); return null; }));
  return users.get(uid);
}
async function getUsers(uids) { const out = {}; await Promise.all(uids.map(async u => { out[u] = await getUser(u); })); return out; }

let lastRead = {};
function markRead(chatId) { lastRead[chatId] = Date.now(); store.set("lastRead:" + state.me.uid, lastRead); }
function isUnread(chat) {
  const lm = chat.lastMessage; if (!lm || lm.senderId === state.me.uid) return false;
  const d = toDate(lm.ts); return d ? d.getTime() > (lastRead[chat.id] || 0) : false;
}

const directId = (a, b) => [a, b].sort().join("_");
function otherMember(chat) { return (chat.members || []).find(m => m !== state.me.uid); }
async function chatTitle(chat) {
  if (chat.type === "group") return { title: chat.name, sub: `${(chat.members || []).length} أعضاء`, avatarId: chat.id, group: true };
  const other = await getUser(otherMember(chat));
  return { title: other ? other.name : "مستخدم", sub: other ? "@" + other.username : "", avatarId: otherMember(chat), group: false };
}

/* ============================== routing ============================== */

let cleanups = [];
function onLeave(fn) { cleanups.push(fn); }
function go(hash) { if (location.hash !== hash) location.hash = hash; else route(); }
function back() { if (history.length > 1 && sessionStorage.getItem("navd")) history.back(); else go("#/"); }
window.addEventListener("hashchange", () => { try { sessionStorage.setItem("navd", "1"); } catch {} route(); });

let chatsListeners = new Set();
function route() {
  cleanups.forEach(f => { try { f(); } catch {} }); cleanups = [];
  if (!state.me) return renderAuth();
  if (!state.profile) return renderOnboarding();
  const [, view, id] = (location.hash || "#/").split("/");
  if (view === "chat" && id) return renderChat(decodeURIComponent(id));
  if (view === "info" && id) return renderInfo(decodeURIComponent(id));
  if (view === "new-group") return renderNewGroup();
  if (view === "me") return renderMe();
  return renderHome();
}

/* ============================== auth ============================== */

let unsubChats = null;
onAuthStateChanged(auth, async user => {
  if (unsubChats) { unsubChats(); unsubChats = null; }
  state.me = user; state.profile = null; state.chats.clear(); state.chatsLoaded = false;
  if (!user) return route();
  lastRead = store.get("lastRead:" + user.uid, {});
  try {
    const snap = await getDoc(doc(db, "users", user.uid));
    if (snap.exists()) { state.profile = { id: user.uid, ...snap.data() }; startChats(); }
  } catch (e) { toast("مش قادر يوصل للسيرفر. اتأكد من النت."); }
  route();
});

function startChats() {
  const q = query(collection(db, "chats"), where("members", "array-contains", state.me.uid));
  unsubChats = onSnapshot(q, snap => {
    state.chats.clear();
    snap.forEach(d => state.chats.set(d.id, { id: d.id, ...d.data({ serverTimestamps: "estimate" }) }));
    state.chatsLoaded = true;
    chatsListeners.forEach(f => f());
  }, () => toast("مش قادر يجيب المحادثات دلوقتي."));
}
function watchChats(fn) { chatsListeners.add(fn); onLeave(() => chatsListeners.delete(fn)); }

function renderAuth() {
  let mode = "login";
  const err = h("p", { class: "err", role: "alert" });
  const nameIn = h("input", { class: "input", id: "su-name", autocomplete: "name", maxlength: "40", required: true });
  const userIn = h("input", { class: "input", id: "su-user", dir: "ltr", autocomplete: "username", autocapitalize: "none", spellcheck: "false", maxlength: "21", required: true, placeholder: "ahmed_99" });
  const emailIn = h("input", { class: "input", id: "au-email", type: "email", dir: "ltr", autocomplete: "email", required: true });
  const passIn = h("input", { class: "input", id: "au-pass", type: "password", dir: "ltr", autocomplete: "current-password", minlength: "6", required: true });
  const submit = h("button", { class: "btn", type: "submit" }, "دخول");
  const signupOnly = [
    h("label", { class: "field" }, "اسمك", nameIn),
    h("label", { class: "field" }, h("span", null, "اليوزر نيم ", h("small", null, "(صحابك هيدوروا عليك بيه: حروف إنجليزي صغيرة وأرقام و _ )")), userIn),
  ];
  const form = h("form", { class: "stack", novalidate: true },
    ...signupOnly,
    h("label", { class: "field" }, "الإيميل", emailIn),
    h("label", { class: "field" }, "الباسورد", passIn),
    err, submit);
  const tabLogin = h("button", { type: "button", role: "tab" }, "دخول");
  const tabSignup = h("button", { type: "button", role: "tab" }, "حساب جديد");
  function setMode(m) {
    mode = m; err.textContent = "";
    tabLogin.setAttribute("aria-selected", m === "login"); tabSignup.setAttribute("aria-selected", m === "signup");
    signupOnly.forEach(el => (el.hidden = m === "login"));
    submit.textContent = m === "login" ? "دخول" : "اعمل الحساب";
    passIn.autocomplete = m === "login" ? "current-password" : "new-password";
  }
  tabLogin.onclick = () => setMode("login"); tabSignup.onclick = () => setMode("signup");
  setMode("login");

  form.addEventListener("submit", async e => {
    e.preventDefault(); err.textContent = "";
    const email = emailIn.value.trim(), pass = passIn.value;
    if (mode === "signup") {
      const name = nameIn.value.trim(), uname = cleanUsername(userIn.value);
      if (!name) return (err.textContent = "اكتب اسمك.");
      if (!USERNAME_RE.test(uname)) return (err.textContent = "اليوزر نيم من 3 لـ 20 حرف: إنجليزي صغير وأرقام و _ بس.");
      if (!email || pass.length < 6) return (err.textContent = "اكتب الإيميل وباسورد 6 حروف على الأقل.");
      submit.disabled = true;
      pendingSignup = { name, uname };
      try { await createUserWithEmailAndPassword(auth, email, pass); }
      catch (ex) { pendingSignup = null; err.textContent = authError(ex); submit.disabled = false; }
    } else {
      if (!email || !pass) return (err.textContent = "اكتب الإيميل والباسورد.");
      submit.disabled = true;
      try { await signInWithEmailAndPassword(auth, email, pass); }
      catch (ex) { err.textContent = authError(ex); submit.disabled = false; }
    }
  });

  appEl.replaceChildren(h("div", { class: "auth" },
    h("div", { class: "hello" }, h("div", { class: "logo big", style: "margin:0 auto" }, "ونس"),
      h("h2", null, "أهلًا بيك في ونس"), h("p", null, "كلّم أي حد باليوزر نيم بتاعه، واعمل مجموعات لصحابك.")),
    h("div", { class: "tabs", role: "tablist" }, tabLogin, tabSignup),
    form));
}

let pendingSignup = null;
async function claimUsername(name, uname) {
  const uid = state.me.uid;
  const batch = writeBatch(db);
  batch.set(doc(db, "users", uid), { username: uname, name, createdAt: serverTimestamp() });
  batch.set(doc(db, "usernames", uname), { uid });
  await batch.commit();
  state.profile = { id: uid, username: uname, name };
  users.set(uid, Promise.resolve(state.profile));
  startChats();
}

function renderOnboarding() {
  const err = h("p", { class: "err", role: "alert" });
  const nameIn = h("input", { class: "input", id: "ob-name", maxlength: "40", value: pendingSignup?.name || "" });
  const userIn = h("input", { class: "input", id: "ob-user", dir: "ltr", autocapitalize: "none", spellcheck: "false", maxlength: "21", value: pendingSignup?.uname || "", placeholder: "ahmed_99" });
  const submit = h("button", { class: "btn", type: "submit" }, "يلا بينا");
  const form = h("form", { class: "stack", novalidate: true },
    h("label", { class: "field" }, "اسمك", nameIn),
    h("label", { class: "field" }, h("span", null, "اليوزر نيم ", h("small", null, "(مش هتقدر تغيّره بعد كده)")), userIn),
    err, submit,
    h("button", { class: "btn ghost", type: "button", onclick: () => signOut(auth) }, "خروج"));
  async function attempt() {
    err.textContent = "";
    const name = nameIn.value.trim(), uname = cleanUsername(userIn.value);
    if (!name) return (err.textContent = "اكتب اسمك.");
    if (!USERNAME_RE.test(uname)) return (err.textContent = "اليوزر نيم من 3 لـ 20 حرف: إنجليزي صغير وأرقام و _ بس.");
    submit.disabled = true;
    try {
      const taken = await getDoc(doc(db, "usernames", uname));
      if (taken.exists()) { err.textContent = `@${uname} محجوز. اختار يوزر نيم تاني.`; submit.disabled = false; return; }
      await claimUsername(name, uname);
      pendingSignup = null;
      go("#/");
    } catch (ex) {
      err.textContent = "مقدرتش أحجز اليوزر نيم ده. غالبًا حد خده، جرّب واحد تاني.";
      submit.disabled = false;
    }
  }
  form.addEventListener("submit", e => { e.preventDefault(); attempt(); });
  appEl.replaceChildren(h("div", { class: "auth" },
    h("div", { class: "hello" }, h("h2", null, "اختار اليوزر نيم بتاعك"), h("p", null, "ده اللي صحابك هيدوروا عليك بيه.")),
    form));
  if (pendingSignup) attempt();
}

/* ============================== people search ============================== */

async function searchUsers(text) {
  const q = cleanUsername(text);
  if (!q) return [];
  const snap = await getDocs(query(collection(db, "users"), where("username", ">=", q), where("username", "<=", q + ""), limit(20)));
  const out = [];
  snap.forEach(d => { if (d.id !== state.me.uid) { const u = { id: d.id, ...d.data() }; users.set(d.id, Promise.resolve(u)); out.push(u); } });
  return out;
}

function userRow(u, { onclick, pressed, tag } = {}) {
  return h("button", { class: "row", type: "button", onclick, "aria-pressed": pressed == null ? null : String(pressed) },
    avatar(u.id, u.name),
    h("div", { class: "main" }, h("div", { class: "top" }, h("span", { class: "name" }, u.name)), h("span", { class: "preview", dir: "ltr", style: "text-align:right" }, "@" + u.username)),
    tag ? h("span", { class: "tag" }, tag) : null,
    pressed == null ? null : h("span", { class: "check", svg: pressed ? ICON.check : "" }));
}

/* ============================== home ============================== */

function renderHome() {
  const listEl = h("div", { class: "scroll", id: "home-list" });
  const searchIn = h("input", { class: "input", id: "home-search", type: "search", placeholder: "دوّر على حد باليوزر نيم…", autocapitalize: "none", spellcheck: "false", "aria-label": "بحث" });
  let searchText = "", results = null, seq = 0;

  async function draw() {
    const rows = [];
    const chats = [...state.chats.values()].sort((a, b) => (toDate(b.updatedAt)?.getTime() || 0) - (toDate(a.updatedAt)?.getTime() || 0));
    const titles = await Promise.all(chats.map(chatTitle));
    const s = searchText.trim().toLowerCase().replace(/^@/, "");
    const shown = chats.map((c, i) => [c, titles[i]]).filter(([, t]) => !s || t.title.toLowerCase().includes(s) || t.sub.toLowerCase().includes(s));

    if (s && results) {
      rows.push(h("div", { class: "section-label" }, "ناس"));
      if (!results.length) rows.push(h("div", { class: "empty-state" }, `مفيش حد باليوزر نيم ده "${s}".`));
      results.forEach(u => rows.push(userRow(u, { onclick: () => go("#/chat/" + directId(state.me.uid, u.id)) })));
      if (shown.length) rows.push(h("div", { class: "section-label" }, "محادثاتك"));
    }
    for (const [c, t] of shown) {
      const lm = c.lastMessage, unread = isUnread(c);
      let preview = "ابدأ الكلام";
      if (lm) {
        let who = "";
        if (lm.senderId === state.me.uid) who = "إنت: ";
        else if (c.type === "group") { const u = await getUser(lm.senderId); who = (u ? u.name : "حد") + ": "; }
        preview = who + lm.text;
      } else if (c.type === "group") preview = "المجموعة اتعملت";
      rows.push(h("button", { class: "row" + (unread ? " unread" : ""), type: "button", onclick: () => go("#/chat/" + c.id) },
        avatar(t.avatarId, t.title, t.group ? "group" : ""),
        h("div", { class: "main" },
          h("div", { class: "top" }, h("span", { class: "name" }, t.title), h("span", { class: "when" }, whenLabel(lm?.ts || c.updatedAt))),
          h("div", { class: "top" }, h("span", { class: "preview" }, preview), unread ? h("span", { class: "dot" }) : null))));
    }
    if (!s && !chats.length && state.chatsLoaded) {
      rows.push(h("div", { class: "empty-state" }, h("b", null, "لسه مفيش محادثات"),
        "اكتب اليوزر نيم بتاع صاحبك في البحث فوق علشان تكلمه، أو اعمل مجموعة من الزرار تحت.",
        h("br"), h("br"), "اليوزر نيم بتاعك: ", h("b", { dir: "ltr", style: "display:inline" }, "@" + state.profile.username)));
    }
    listEl.replaceChildren(...rows, h("div", { style: "height:96px" }));
  }

  const doSearch = debounce(async () => {
    const my = ++seq;
    const s = searchText.trim();
    if (!s) { results = null; return draw(); }
    try { const r = await searchUsers(s); if (my === seq) { results = r; draw(); } }
    catch { if (my === seq) { results = []; draw(); } }
  }, 250);
  searchIn.addEventListener("input", () => { searchText = searchIn.value; if (!searchText.trim()) results = null; draw(); doSearch(); });

  watchChats(draw);
  appEl.replaceChildren(
    h("header", { class: "bar" },
      h("div", { class: "logo" }, "ونس"),
      h("h1", null, "المحادثات"),
      h("button", { class: "icon-btn", type: "button", "aria-label": "حسابي", svg: ICON.user, onclick: () => go("#/me") })),
    h("div", { class: "search" }, searchIn),
    listEl,
    h("button", { class: "fab", type: "button", onclick: () => go("#/new-group") }, h("span", { svg: ICON.group, style: "display:grid" }), "مجموعة جديدة"));
  draw();
}

/* ============================== chat ============================== */

function renderChat(chatId) {
  const isDirect = chatId.includes("_");
  if (isDirect && !chatId.split("_").includes(state.me.uid)) return go("#/");
  let chat = state.chats.get(chatId) || null;
  let messages = [];
  let exists = !!chat;
  let names = {};

  const titleEl = h("h1", null, "…"), subEl = h("span", { class: "sub" });
  const avatarSlot = h("div");
  const thread = h("main", { class: "thread", "aria-live": "polite" });
  const input = h("textarea", { id: "composer-" + chatId, rows: "1", placeholder: "اكتب رسالة…", "aria-label": "الرسالة", maxlength: "4000" });
  const send = h("button", { class: "send", type: "submit", "aria-label": "ابعت", disabled: true, svg: ICON.send });
  const form = h("form", { class: "composer" }, input, send);

  const draftKey = "draft:" + chatId;
  input.value = store.get(draftKey, "");

  async function drawHeader() {
    let t;
    if (chat) t = await chatTitle(chat);
    else if (isDirect) { const other = await getUser(chatId.split("_").find(x => x !== state.me.uid)); t = { title: other ? other.name : "مستخدم", sub: other ? "@" + other.username : "", avatarId: other?.id || chatId, group: false }; }
    else t = { title: "محادثة", sub: "", avatarId: chatId, group: true };
    titleEl.textContent = t.title; subEl.textContent = t.sub;
    avatarSlot.replaceChildren(avatar(t.avatarId, t.title, "sm" + (t.group ? " group" : "")));
  }

  let firstDraw = true;
  async function drawMessages() {
    const near = thread.scrollHeight - thread.scrollTop - thread.clientHeight < 120;
    const isGroup = chat?.type === "group";
    if (isGroup) {
      const ids = [...new Set(messages.map(m => m.senderId).filter(id => id !== state.me.uid && !names[id]))];
      if (ids.length) Object.assign(names, await getUsers(ids));
    }
    const out = [];
    if (!messages.length) out.push(h("div", { class: "system-note" }, exists ? "لسه مفيش رسايل. قول أهلًا 👋" : "دي أول مرة تكلموا بعض. ابعت أول رسالة."));
    let lastDay = "", lastSender = null, lastTime = 0;
    for (const m of messages) {
      const d = toDate(m.ts) || new Date();
      if (d.toDateString() !== lastDay) { out.push(h("div", { class: "day" }, dayFmt.format(d))); lastDay = d.toDateString(); lastSender = null; }
      const mine = m.senderId === state.me.uid;
      const cont = m.senderId === lastSender && d - lastTime < 5 * 60e3;
      out.push(h("div", { class: "msg" + (mine ? " mine" : "") + (cont ? " cont" : "") + (m.pending ? " pending" : "") },
        isGroup && !mine ? h("div", { class: "who", style: `color:${colorFor(m.senderId)}` }, names[m.senderId]?.name || "عضو سابق") : null,
        h("div", { class: "bubble" }, m.text, h("span", { class: "t" }, m.pending ? "بيتبعت…" : timeFmt.format(d)))));
      lastSender = m.senderId; lastTime = d;
    }
    if (chat && !(chat.members || []).includes(state.me.uid)) out.push(h("div", { class: "system-note" }, "إنت مبقتش عضو في المجموعة دي."));
    thread.replaceChildren(...out);
    if (firstDraw || near) { thread.scrollTop = thread.scrollHeight; firstDraw = false; }
    markRead(chatId);
  }

  // Live chat document (name / members changes)
  onLeave(onSnapshot(doc(db, "chats", chatId), snap => {
    exists = snap.exists();
    chat = exists ? { id: snap.id, ...snap.data({ serverTimestamps: "estimate" }) } : null;
    if (!exists && !isDirect) { toast("المحادثة دي مش موجودة."); return go("#/"); }
    drawHeader(); if (!exists) drawMessages();
    if (exists && !msgUnsub) subscribeMessages();
  }, () => { if (!isDirect) { toast("مش مسموحلك تفتح المحادثة دي."); go("#/"); } }));

  let msgUnsub = null;
  function subscribeMessages() {
    msgUnsub = onSnapshot(query(collection(db, "chats", chatId, "messages"), orderBy("ts"), limitToLast(300)), { includeMetadataChanges: true }, snap => {
      messages = snap.docs.map(d => ({ id: d.id, ...d.data({ serverTimestamps: "estimate" }), pending: d.metadata.hasPendingWrites }));
      drawMessages();
    }, () => {});
    onLeave(() => msgUnsub && msgUnsub());
  }

  function grow() {
    input.style.height = "auto"; input.style.height = Math.min(input.scrollHeight, 140) + "px";
    send.disabled = !input.value.trim();
  }
  input.addEventListener("input", () => { grow(); store.set(draftKey, input.value); });
  input.addEventListener("keydown", e => {
    if (e.key === "Enter" && !e.shiftKey && !e.isComposing && !matchMedia("(pointer: coarse)").matches) { e.preventDefault(); form.requestSubmit(); }
  });
  form.addEventListener("submit", async e => {
    e.preventDefault();
    const text = input.value.trim(); if (!text) return;
    input.value = ""; store.set(draftKey, ""); grow(); input.focus();
    try {
      if (!exists && isDirect) {
        const members = chatId.split("_").sort();
        await setDoc(doc(db, "chats", chatId), { type: "direct", members, createdBy: state.me.uid, updatedAt: serverTimestamp(), lastMessage: null });
        exists = true;
        if (!msgUnsub) subscribeMessages();
      }
      const batch = writeBatch(db);
      batch.set(doc(collection(db, "chats", chatId, "messages")), { senderId: state.me.uid, text, ts: serverTimestamp() });
      batch.update(doc(db, "chats", chatId), { lastMessage: { text: text.slice(0, 200), senderId: state.me.uid, ts: serverTimestamp() }, updatedAt: serverTimestamp() });
      await batch.commit();
    } catch (ex) {
      input.value = text; store.set(draftKey, text); grow();
      toast(chat && !(chat.members || []).includes(state.me.uid) ? "مش هتقدر تبعت، إنت مش عضو في المجموعة." : "الرسالة موصلتش. اتأكد من النت وجرّب تاني.");
    }
  });

  appEl.replaceChildren(
    h("header", { class: "bar" },
      h("button", { class: "icon-btn", type: "button", "aria-label": "رجوع", svg: ICON.back, onclick: back }),
      avatarSlot,
      h("button", { class: "titlebox", type: "button", onclick: () => go("#/info/" + chatId), "aria-label": "تفاصيل المحادثة" }, titleEl, subEl)),
    thread, form);
  drawHeader(); drawMessages(); grow();
  if (!matchMedia("(pointer: coarse)").matches) input.focus();
}

/* ============================== new group ============================== */

function renderNewGroup() {
  const selected = new Map(); // uid -> user
  let results = [], seq = 0;
  const nameIn = h("input", { class: "input", id: "ng-name", maxlength: "60", placeholder: "اسم المجموعة (مثلًا: شلة الجامعة)" });
  const searchIn = h("input", { class: "input", id: "ng-search", type: "search", placeholder: "ضيف ناس باليوزر نيم…", autocapitalize: "none", spellcheck: "false" });
  const chips = h("div", { class: "chips" });
  const list = h("div");
  const createBtn = h("button", { class: "btn", type: "button", disabled: true }, "اعمل المجموعة");

  function draw() {
    chips.replaceChildren(...[...selected.values()].map(u => h("button", { class: "chip", type: "button", onclick: () => { selected.delete(u.id); draw(); }, "aria-label": "شيل " + u.name }, u.name, h("span", { svg: ICON.x, style: "display:grid" }))));
    const rows = results.map(u => userRow(u, { pressed: selected.has(u.id), onclick: () => { selected.has(u.id) ? selected.delete(u.id) : selected.set(u.id, u); draw(); } }));
    if (searchIn.value.trim() && !results.length) rows.push(h("div", { class: "empty-state" }, "مفيش حد باليوزر نيم ده."));
    if (!searchIn.value.trim()) rows.push(h("div", { class: "empty-state" }, "اكتب اليوزر نيم بتاع كل واحد عاوز تضيفه، ودوس عليه."));
    list.replaceChildren(...rows);
    createBtn.disabled = !nameIn.value.trim() || selected.size < 1;
    createBtn.textContent = selected.size ? `اعمل المجموعة (${selected.size + 1} أعضاء)` : "اعمل المجموعة";
  }
  const doSearch = debounce(async () => {
    const my = ++seq;
    try { const r = await searchUsers(searchIn.value); if (my === seq) { results = r; draw(); } } catch { if (my === seq) { results = []; draw(); } }
  }, 250);
  searchIn.addEventListener("input", () => { if (!searchIn.value.trim()) { results = []; draw(); } doSearch(); });
  nameIn.addEventListener("input", draw);
  createBtn.addEventListener("click", async () => {
    createBtn.disabled = true;
    try {
      const ref = doc(collection(db, "chats"));
      await setDoc(ref, {
        type: "group", name: nameIn.value.trim(), members: [state.me.uid, ...selected.keys()], admins: [state.me.uid],
        createdBy: state.me.uid, updatedAt: serverTimestamp(), lastMessage: null,
      });
      history.replaceState(null, "", "#/chat/" + ref.id); route();
    } catch { toast("مقدرتش أعمل المجموعة. جرّب تاني."); draw(); }
  });

  appEl.replaceChildren(
    h("header", { class: "bar" },
      h("button", { class: "icon-btn", type: "button", "aria-label": "رجوع", svg: ICON.back, onclick: back }),
      h("h1", null, "مجموعة جديدة")),
    h("div", { class: "scroll" },
      h("div", { class: "inline-form" }, nameIn),
      h("div", { class: "inline-form" }, searchIn),
      chips, list,
      h("div", { class: "actions" }, createBtn)));
  draw();
  nameIn.focus();
}

/* ============================== chat info ============================== */

function renderInfo(chatId) {
  const body = h("div", { class: "scroll" });
  const titleEl = h("h1", null, "التفاصيل");
  let chat = null, addResults = [], seq = 0;
  const addIn = h("input", { class: "input", id: "info-add", type: "search", placeholder: "ضيف عضو باليوزر نيم…", autocapitalize: "none", spellcheck: "false" });
  const addList = h("div");
  const nameIn = h("input", { class: "input", id: "info-name", maxlength: "60" });

  async function draw() {
    if (!chat) {
      if (chatId.includes("_")) {
        const other = await getUser(chatId.split("_").find(x => x !== state.me.uid));
        if (other) body.replaceChildren(h("div", { class: "info-head" }, avatar(other.id, other.name), h("h2", null, other.name), h("p", { dir: "ltr" }, "@" + other.username)));
      }
      return;
    }
    if (chat.type !== "group") {
      const other = await getUser(otherMember(chat));
      body.replaceChildren(h("div", { class: "info-head" }, avatar(other?.id || chatId, other?.name), h("h2", null, other?.name || "مستخدم"), h("p", { dir: "ltr" }, other ? "@" + other.username : "")));
      return;
    }
    const isAdmin = (chat.admins || []).includes(state.me.uid);
    const isMember = (chat.members || []).includes(state.me.uid);
    const members = await getUsers(chat.members || []);
    const sorted = (chat.members || []).map(id => members[id] || { id, name: "مستخدم", username: "?" })
      .sort((a, b) => (a.id === state.me.uid ? -1 : b.id === state.me.uid ? 1 : a.name.localeCompare(b.name, "ar")));

    const parts = [h("div", { class: "info-head" }, avatar(chat.id, chat.name, "group"), h("h2", null, chat.name), h("p", null, `مجموعة · ${sorted.length} أعضاء`))];
    if (isAdmin) {
      if (document.activeElement !== nameIn) nameIn.value = chat.name;
      parts.push(h("div", { class: "section-label" }, "اسم المجموعة"),
        h("form", { class: "inline-form", onsubmit: async e => {
          e.preventDefault(); const n = nameIn.value.trim(); if (!n || n === chat.name) return;
          try { await updateDoc(doc(db, "chats", chatId), { name: n }); toast("الاسم اتغيّر."); } catch { toast("مقدرتش أغيّر الاسم."); }
        } }, nameIn, h("button", { class: "btn", type: "submit" }, "حفظ")));
      parts.push(h("div", { class: "section-label" }, "إضافة أعضاء"), h("div", { class: "inline-form" }, addIn), addList);
    }
    parts.push(h("div", { class: "section-label" }, `الأعضاء (${sorted.length})`));
    for (const u of sorted) {
      const admin = (chat.admins || []).includes(u.id);
      const row = userRow(u, { tag: u.id === state.me.uid ? (admin ? "إنت · أدمن" : "إنت") : (admin ? "أدمن" : null), onclick: u.id === state.me.uid ? null : () => go("#/chat/" + directId(state.me.uid, u.id)) });
      if (isAdmin && u.id !== state.me.uid) {
        const wrap = h("div", { style: "display:flex;align-items:center" }, row,
          h("button", { class: "icon-btn", type: "button", "aria-label": "شيل " + u.name + " من المجموعة", svg: ICON.x, style: "margin-inline-end:8px;color:var(--danger)", onclick: () => confirmRemove(u) }));
        row.style.flex = "1"; row.style.minWidth = "0";
        parts.push(wrap);
      } else parts.push(row);
    }
    if (isMember) {
      const leaveBtn = h("button", { class: "btn danger", type: "button" }, "اخرج من المجموعة");
      let armed = false;
      leaveBtn.onclick = async () => {
        if (!armed) { armed = true; leaveBtn.textContent = "متأكد؟ دوس تاني للخروج"; setTimeout(() => { armed = false; leaveBtn.textContent = "اخرج من المجموعة"; }, 4000); return; }
        leaveBtn.disabled = true;
        const newMembers = chat.members.filter(m => m !== state.me.uid);
        let newAdmins = (chat.admins || []).filter(a => a !== state.me.uid);
        if (!newAdmins.length && newMembers.length) newAdmins = [newMembers[0]];
        try { await updateDoc(doc(db, "chats", chatId), { members: newMembers, admins: newAdmins }); toast("خرجت من المجموعة."); go("#/"); }
        catch { toast("مقدرتش أخرجك. جرّب تاني."); leaveBtn.disabled = false; }
      };
      parts.push(h("div", { class: "actions" }, leaveBtn));
    }
    body.replaceChildren(...parts);
    drawAdd();
  }

  let removeArmed = null;
  async function confirmRemove(u) {
    if (removeArmed !== u.id) { removeArmed = u.id; toast(`دوس على ✕ تاني علشان تشيل ${u.name}.`); setTimeout(() => { if (removeArmed === u.id) removeArmed = null; }, 4000); return; }
    removeArmed = null;
    try { await updateDoc(doc(db, "chats", chatId), { members: arrayRemove(u.id) }); toast(`${u.name} اتشال من المجموعة.`); }
    catch { toast("مقدرتش أشيله. جرّب تاني."); }
  }

  function drawAdd() {
    if (!chat) return;
    const rows = addResults.map(u => {
      const already = chat.members.includes(u.id);
      return userRow(u, { tag: already ? "عضو" : "إضافة", onclick: already ? null : async () => {
        try { await updateDoc(doc(db, "chats", chatId), { members: arrayUnion(u.id) }); toast(`${u.name} اتضاف.`); addIn.value = ""; addResults = []; drawAdd(); }
        catch { toast("مقدرتش أضيفه. جرّب تاني."); }
      } });
    });
    if (addIn.value.trim() && !addResults.length) rows.push(h("div", { class: "empty-state" }, "مفيش حد باليوزر نيم ده."));
    addList.replaceChildren(...rows);
  }
  const doSearch = debounce(async () => {
    const my = ++seq;
    try { const r = await searchUsers(addIn.value); if (my === seq) { addResults = r; drawAdd(); } } catch {}
  }, 250);
  addIn.addEventListener("input", () => { if (!addIn.value.trim()) { addResults = []; drawAdd(); } doSearch(); });

  onLeave(onSnapshot(doc(db, "chats", chatId), snap => {
    chat = snap.exists() ? { id: snap.id, ...snap.data() } : null;
    titleEl.textContent = chat?.type === "group" ? "تفاصيل المجموعة" : "التفاصيل";
    draw();
  }, () => draw()));

  appEl.replaceChildren(
    h("header", { class: "bar" }, h("button", { class: "icon-btn", type: "button", "aria-label": "رجوع", svg: ICON.back, onclick: back }), titleEl),
    body);
}

/* ============================== my profile ============================== */

function renderMe() {
  const p = state.profile;
  const nameIn = h("input", { class: "input", id: "me-name", maxlength: "40", value: p.name });
  const copyBtn = h("button", { class: "btn ghost", type: "button" }, "انسخ اليوزر نيم بتاعي");
  copyBtn.onclick = async () => {
    try { await navigator.clipboard.writeText("@" + p.username); toast("اتنسخ. ابعته لصحابك."); }
    catch { toast("@" + p.username); }
  };
  appEl.replaceChildren(
    h("header", { class: "bar" }, h("button", { class: "icon-btn", type: "button", "aria-label": "رجوع", svg: ICON.back, onclick: back }), h("h1", null, "حسابي")),
    h("div", { class: "scroll" },
      h("div", { class: "info-head" }, avatar(p.id, p.name), h("h2", null, p.name), h("p", { dir: "ltr" }, "@" + p.username)),
      h("div", { class: "section-label" }, "اسمك اللي بيظهر لصحابك"),
      h("form", { class: "inline-form", onsubmit: async e => {
        e.preventDefault(); const n = nameIn.value.trim(); if (!n || n === p.name) return;
        try { await updateDoc(doc(db, "users", p.id), { name: n }); p.name = n; users.set(p.id, Promise.resolve(p)); toast("اتحفظ."); route(); }
        catch { toast("مقدرتش أحفظ. جرّب تاني."); }
      } }, nameIn, h("button", { class: "btn", type: "submit" }, "حفظ")),
      h("div", { class: "actions" },
        copyBtn,
        h("p", { style: "margin:0;color:var(--muted);font-size:14px;line-height:1.6" }, "علشان تنزّل ونس على شاشة الموبايل: من قايمة المتصفح اختار \"إضافة إلى الشاشة الرئيسية\" أو \"Install app\"."),
        h("button", { class: "btn danger", type: "button", onclick: () => signOut(auth).then(() => go("#/")) }, "تسجيل خروج"))));
}
