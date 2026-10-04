"use strict";
const $ = (id) => document.getElementById(id);
const S = {
  token: "",
  pages: [],
  current: null,
  status: null,
  view: "notes",
  dirty: false,
  editSerial: 0,
  saving: null,
  timer: null,
  preview: false,
  history: [],
  chat: null,
  selection: "",
  settingsLoaded: false,
  stopping: false,
};
function toast(text, error = false) {
  $("toast").textContent = text;
  $("toast").hidden = false;
  $("toast").classList.toggle("error", error);
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => ($("toast").hidden = true), 6000);
}
async function api(path, method = "GET", body, options = {}) {
  const response = await fetch("/api/" + path, {
    method,
    headers: {
      "X-Office-Token": S.token,
      ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
    },
    ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
    ...options,
  });
  if (!response.ok) {
    let error;
    try {
      error = (await response.json()).error;
    } catch {}
    throw new Error(error || `処理に失敗しました (${response.status})`);
  }
  if (response.status === 204 || response.status === 202) return null;
  return response.json();
}
function guard(fn) {
  return async (...args) => {
    try {
      await fn(...args);
    } catch (error) {
      toast(error.message, true);
    }
  };
}
function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}
function markdown(node, text) {
  node.innerHTML = DOMPurify.sanitize(
    marked.parse(text || "", { breaks: true }),
    { FORBID_TAGS: ["style", "iframe", "form", "svg"], FORBID_ATTR: ["style"] },
  );
  if (node.id === "note-preview") {
    node.querySelectorAll("input[type=checkbox]").forEach((box, index) => {
      box.disabled = false;
      box.onchange = () => {
        let current = -1;
        $("note-body").value = $("note-body").value.replace(
          /^(\s*[-*+] )\[([ xX])\](.*)$/gm,
          (line, prefix, check, suffix) => {
            current++;
            return current === index
              ? prefix + "[" + (box.checked ? "x" : " ") + "]" + suffix
              : line;
          },
        );
        dirty();
      };
    });
  }
  node.querySelectorAll("a").forEach((a) => {
    let url;
    try {
      url = new URL(a.getAttribute("href"), location.href);
    } catch {
      a.removeAttribute("href");
      return;
    }
    if (!["https:", "http:"].includes(url.protocol)) a.removeAttribute("href");
    else {
      a.target = "_blank";
      a.rel = "noopener noreferrer";
    }
  });
  // External images cannot send note content through URL query strings; preview stays offline.
  node.querySelectorAll("img").forEach((img) => {
    if (!img.getAttribute("src")?.startsWith("data:"))
      img.replaceWith(
        el("span", "muted", "[画像: " + (img.alt || "原典で確認") + "]"),
      );
  });
}
const formatSize = (n) =>
  n >= 1e9 ? (n / 1e9).toFixed(2) + " GB" : Math.round(n / 1e6) + " MB";
const formatDate = (s) => new Date(s).toLocaleString("ja-JP");
function draftKey() {
  return S.current ? "timsah-draft-" + S.current.id : "";
}
function dirty() {
  if (!S.current) return;
  S.dirty = true;
  S.editSerial++;
  $("save-state").textContent = "保存待ち…";
  localStorage.setItem(
    draftKey(),
    JSON.stringify({
      title: $("note-title").value,
      markdown: $("note-body").value,
      revision: S.current.revision,
    }),
  );
  updateNoteCount();
  clearTimeout(S.timer);
  S.timer = setTimeout(() => guard(save)(), 650);
}
async function save() {
  clearTimeout(S.timer);
  if (S.saving) {
    await S.saving;
    if (S.dirty) return save();
    return;
  }
  if (!S.current || !S.dirty) return;
  const page = S.current,
    serial = S.editSerial;
  const draft = {
    title: $("note-title").value,
    markdown: $("note-body").value,
    parentId: page.parentId,
    favorite: page.favorite,
    revision: page.revision,
  };
  $("save-state").textContent = "保存中…";
  S.saving = (async () => {
    try {
      const updated = await api("notes/" + page.id, "PUT", draft);
      if (S.current?.id === page.id) {
        S.current = updated;
        S.dirty = S.editSerial !== serial;
        if (!S.dirty) localStorage.removeItem(draftKey());
      }
      S.pages = S.pages.map((p) => (p.id === page.id ? updated : p));
      renderTree();
      $("save-state").textContent = S.dirty ? "保存待ち…" : "保存済み";
      if (S.view === "notes") $("breadcrumb").textContent = updated.title;
    } catch (error) {
      $("save-state").textContent = "未保存 · 再試行";
      throw error;
    } finally {
      S.saving = null;
    }
  })();
  await S.saving;
  if (S.dirty) return save();
}
async function loadPages() {
  S.pages = await api("notes");
  renderTree();
}
function renderTree() {
  const tree = $("page-tree");
  tree.replaceChildren();
  const q = $("page-filter").value.trim().toLowerCase();
  const included = q
    ? new Set(
        S.pages
          .filter((p) => (p.title + p.markdown).toLowerCase().includes(q))
          .map((p) => p.id),
      )
    : null;
  function row(page, depth) {
    if (!included || included.has(page.id)) {
      const button = el(
        "button",
        "page-item" + (S.current?.id === page.id ? " active" : ""),
      );
      button.style.paddingLeft = 6 + Math.min(depth, 6) * 12 + "px";
      button.append(
        el("span", "page-icon", page.favorite ? "★" : "▤"),
        el("span", "", page.title),
      );
      button.title = page.title;
      button.onclick = guard(() => openPage(page.id));
      tree.append(button);
    }
    S.pages
      .filter((p) => p.parentId === page.id)
      .sort((a, b) => a.title.localeCompare(b.title, "ja"))
      .forEach((p) => row(p, depth + 1));
  }
  S.pages
    .filter((p) => !p.parentId)
    .sort(
      (a, b) =>
        Number(b.favorite) - Number(a.favorite) ||
        a.title.localeCompare(b.title, "ja"),
    )
    .forEach((p) => row(p, 0));
  if (!tree.children.length)
    tree.append(
      el(
        "p",
        "hint",
        q ? "一致するページがありません。" : "最初のページを作ってみましょう。",
      ),
    );
}
async function setView(view) {
  await save();
  S.view = view;
  document
    .querySelectorAll(".view")
    .forEach((node) =>
      node.classList.toggle("active", node.id === "view-" + view),
    );
  document
    .querySelectorAll("[data-view]")
    .forEach((node) =>
      node.classList.toggle("active", node.dataset.view === view),
    );
  $("breadcrumb").textContent =
    view === "notes"
      ? S.current?.title || "ノート"
      : {
          assistant: "AIアシスタント",
          rules: "CoREルール",
          settings: "モデルと設定",
        }[view];
  $("app").classList.remove("show-sidebar");
  if (view === "assistant") showAI();
  if (view === "settings") {
    S.settingsLoaded = false;
    await refreshStatus();
  }
  if (view === "rules" && !$("rule-directory").children.length)
    await loadDirectory();
}
async function openPage(id) {
  await save();
  const page = await api("notes/" + id);
  S.current = page;
  S.dirty = false;
  S.selection = "";
  $("note-title").value = page.title;
  $("note-body").value = page.markdown;
  $("note-date").textContent = "更新 " + formatDate(page.updatedAt);
  $("favorite").textContent = page.favorite ? "★" : "☆";
  $("note-empty").hidden = true;
  $("note-document").hidden = false;
  $("include-note").checked = true;
  $("selection-badge").textContent = "";
  $("save-state").textContent = "保存済み";
  updateNoteCount();
  const pending = localStorage.getItem(draftKey());
  if (pending) {
    try {
      const draft = JSON.parse(pending);
      if (confirm("このページに未保存の下書きがあります。復元しますか？")) {
        $("note-title").value = draft.title;
        $("note-body").value = draft.markdown;
        dirty();
      } else localStorage.removeItem(draftKey());
    } catch {}
  }
  if (S.preview) markdown($("note-preview"), $("note-body").value);
  await setView("notes");
  renderTree();
}
async function createPage(parentId = null) {
  await save();
  const page = await api("notes", "POST", { parentId });
  await loadPages();
  await openPage(page.id);
  $("note-title").focus();
  $("note-title").select();
}
function updateNoteCount() {
  $("note-count").textContent =
    $("note-body").value.length.toLocaleString() + " 文字";
}
function showAI() {
  $("app").classList.remove("hidden-panel");
}
function togglePreview() {
  S.preview = !S.preview;
  $("note-body").hidden = S.preview;
  $("note-preview").hidden = !S.preview;
  $("preview-toggle").textContent = S.preview ? "編集へ戻る" : "プレビュー";
  if (S.preview) markdown($("note-preview"), $("note-body").value);
}
function insertBlock(type) {
  const text = {
    heading: "\n## 見出し\n",
    todo: "\n- [ ] タスク\n",
    list: "\n- 項目\n",
    table: "\n| 項目 | 内容 |\n| --- | --- |\n|  |  |\n",
    quote: "\n> 引用\n",
  }[type];
  if (S.preview) togglePreview();
  const input = $("note-body");
  input.setRangeText(text, input.selectionStart, input.selectionEnd, "end");
  input.focus();
  dirty();
}
function captureSelection() {
  const input = $("note-body");
  S.selection = input.value.slice(input.selectionStart, input.selectionEnd);
  $("selection-badge").textContent = S.selection
    ? "選択 " + S.selection.length + "字"
    : "";
}
function download(text, filename, type = "text/markdown;charset=utf-8") {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const a = el("a");
  a.href = url;
  a.download = filename;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
function addMessage(role, text) {
  if ($("chat-log").querySelector(".chat-welcome"))
    $("chat-log").replaceChildren();
  const root = el("div", "chat-message " + role);
  root.append(
    el("div", "message-role", role === "user" ? "YOU" : "✧ Timsah-Assitant"),
  );
  const body = el("div", "message-body markdown");
  if (role === "user") body.textContent = text;
  else markdown(body, text);
  root.append(body);
  $("chat-log").append(root);
  scrollChat();
  return { root, body, text, evidence: [] };
}
function scrollChat() {
  $("chat-log").scrollTop = $("chat-log").scrollHeight;
}
function renderEvidence(message, evidence) {
  message.evidence = evidence;
  const group = el("div", "evidence-group");
  for (const source of evidence) {
    const details = el("details", "evidence");
    details.append(
      el(
        "summary",
        "",
        `[${source.label}] ${source.title} · ${source.version}`,
      ),
    );
    details.append(el("div", "source-text", source.excerpt));
    if (source.kind === "note" && source.url.startsWith("note:")) {
      const button = el("button", "", "メモを開く");
      button.onclick = guard(() => openPage(source.url.slice(5)));
      details.append(button);
    } else if (source.url.startsWith("https://")) {
      const link = el("a", "", "公式の原典を開く ↗");
      link.href = source.url;
      link.target = "_blank";
      link.rel = "noopener noreferrer";
      details.append(link);
    }
    group.append(details);
  }
  message.root.append(group);
}
function messageTools(message, sourceNoteId) {
  const tools = el("div", "message-tools");
  const copy = el("button", "", "コピー");
  copy.onclick = guard(async () => {
    await navigator.clipboard.writeText(message.text);
    toast("コピーしました");
  });
  const insert = el("button", "", "メモへ挿入");
  insert.onclick = guard(async () => {
    if (!S.current) {
      toast("挿入先のページを開いてください");
      return;
    }
    if (
      sourceNoteId &&
      S.current.id !== sourceNoteId &&
      !confirm(
        "アシストを依頼したページと異なります。このページへ挿入しますか？",
      )
    )
      return;
    $("note-body").value += "\n\n" + message.text;
    dirty();
    if (S.preview) markdown($("note-preview"), $("note-body").value);
    await save();
    toast("提案をメモの末尾へ挿入しました");
  });
  const newPage = el("button", "", "新規ページへ");
  newPage.onclick = guard(async () => {
    await createPage();
    $("note-title").value = "AIメモ " + new Date().toLocaleDateString("ja-JP");
    $("note-body").value = message.text;
    dirty();
    await save();
    toast("新しいページに保存しました");
  });
  tools.append(copy, insert, newPage);
  message.root.append(tools);
}
async function chat(message, mode = $("chat-mode").value) {
  if (S.chat)
    throw new Error("AIが回答中です。中断または完了後に再試行してください。");
  if (!["ready", "unloaded"].includes(S.status?.engine.state)) {
    await setView("settings");
    throw new Error("モデルを取得・起動してからAIを利用してください。");
  }
  await save();
  showAI();
  const action = ["summarize", "rewrite", "todos", "review"].includes(mode);
  const noteId = action || $("include-note").checked ? S.current?.id : null;
  const selection = noteId && S.selection ? S.selection : null;
  if (action && !noteId) throw new Error("アシストするメモを開いてください。");
  const request = {
    message,
    mode,
    history: action ? [] : S.history.slice(-12),
    noteId: noteId || null,
    selection,
  };
  addMessage("user", message);
  const answer = addMessage("assistant", "");
  answer.body.textContent = "資料を確認しています…";
  const controller = new AbortController();
  S.chat = controller;
  $("send-chat").disabled = true;
  $("abort-chat").hidden = false;
  $("chat-input").value = "";
  document
    .querySelectorAll("[data-assist]")
    .forEach((b) => (b.disabled = true));
  let complete = false;
  try {
    const response = await fetch("/api/chat", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Office-Token": S.token,
      },
      body: JSON.stringify(request),
      signal: controller.signal,
    });
    if (!response.ok)
      throw new Error(
        (await response.json()).error || "回答を開始できませんでした。",
      );
    const reader = response.body.getReader(),
      decoder = new TextDecoder();
    let pending = "";
    function handle(event, data) {
      if (event === "loading") answer.body.textContent = data;
      if (event === "sources") {
        renderEvidence(answer, data);
        answer.body.textContent = "回答を生成しています…";
      }
      if (event === "delta") {
        answer.text += data;
        markdown(answer.body, answer.text);
        scrollChat();
      }
      if (event === "error") throw new Error(data);
      if (event === "done") {
        complete = true;
        if (data.trimmed)
          answer.root.append(
            el(
              "div",
              "message-warning",
              "長さを調整した資料・メモを参照しています。省略箇所は出典カードで確認できます。",
            ),
          );
        if (data.finishReason === "length")
          answer.root.append(
            el(
              "div",
              "message-warning",
              "出力長の上限で回答が途切れました。設定の最大出力を増やすと長く回答できます。",
            ),
          );
        if (data.missingCitation)
          answer.root.append(
            el(
              "div",
              "message-warning",
              "回答内に公式出典番号がありません。下の参照条文と照合してください。",
            ),
          );
        if (data.invalidCitations?.length)
          answer.root.append(
            el(
              "div",
              "message-warning",
              "存在しない出典番号: " +
                data.invalidCitations.join(", ") +
                "。参照条文を確認してください。",
            ),
          );
        answer.root.append(
          el(
            "div",
            "message-meta",
            (data.method === "explicit-checklist"
              ? "原文の未完了チェックリストを抽出 · "
              : "") +
              `入力 ${data.promptTokens} tokens` +
              (data.usage?.completion_tokens
                ? ` · 出力 ${data.usage.completion_tokens} tokens`
                : ""),
          ),
        );
      }
    }
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      pending += decoder.decode(value, { stream: true });
      const chunks = pending.split("\n\n");
      pending = chunks.pop();
      for (const chunk of chunks) {
        let event, data;
        for (const line of chunk.split("\n")) {
          if (line.startsWith("event: ")) event = line.slice(7);
          if (line.startsWith("data: ")) data = JSON.parse(line.slice(6));
        }
        if (event && data !== undefined) handle(event, data);
      }
    }
    if (!complete) throw new Error("回答ストリームが途中で終了しました。");
    if (!action) {
      S.history.push(
        { role: "user", content: message },
        { role: "assistant", content: answer.text },
      );
      S.history = S.history.slice(-20);
    }
    messageTools(answer, noteId);
  } catch (error) {
    if (!answer.text) answer.body.textContent = "";
    answer.root.append(
      el(
        "div",
        "message-error",
        error.name === "AbortError" ? "回答を中断しました。" : error.message,
      ),
    );
    if (answer.text) messageTools(answer, noteId);
  } finally {
    S.chat = null;
    $("send-chat").disabled = false;
    $("abort-chat").hidden = true;
    document
      .querySelectorAll("[data-assist]")
      .forEach((b) => (b.disabled = false));
    scrollChat();
  }
}
async function refreshStatus() {
  if (S.stopping) return;
  const status = await api("status");
  S.status = status;
  const names = {
    ready: "モデル稼働中",
    starting: "モデルをロード中…",
    stopped: "モデル停止中",
    unloaded: "待機でアンロード済み",
    error: "モデル起動エラー",
  };
  const badge = $("engine-badge");
  badge.replaceChildren(
    el("span", "dot " + status.engine.state),
    el("span", "", names[status.engine.state]),
  );
  $("ai-status").textContent =
    status.engine.state === "ready"
      ? status.engine.model + " · ローカル"
      : names[status.engine.state];
  $("engine-info").textContent =
    status.engine.error ||
    (status.engine.state === "ready"
      ? `使用RAM ${formatSize(status.engine.workingSetBytes)} · CPU推論 · llama.cpp ${status.engineVersion}`
      : `llama.cpp ${status.engineVersion} · ` +
        (status.engineInstalled
          ? "同梱エンジン準備済み"
          : "エンジンはモデル取得時に準備されます"));
  const running = status.job.state === "running";
  $("cancel-job").hidden = !running;
  const p = status.job.progress;
  $("job-info").textContent =
    status.job.error ||
    (running
      ? p?.stage +
        (p?.total
          ? ` · ${formatSize(p.received)} / ${formatSize(p.total)}`
          : "")
      : status.job.state === "done"
        ? "処理が完了しました"
        : "");
  $("job-progress").hidden = !running || !p?.total;
  if (p?.total) {
    $("job-progress").max = p.total;
    $("job-progress").value = p.received;
  }
  const stopped = !["ready", "starting"].includes(status.engine.state);
  $("start-model").disabled = running || !stopped;
  $("stop-model").disabled = running || stopped;
  $("save-settings").disabled = running || !stopped;
  $("install-model").disabled = running || !stopped;
  $("update-rules").disabled = running;
  if (!S.settingsLoaded) {
    $("model-select").replaceChildren(
      ...status.models.map((m) => {
        const option = el(
          "option",
          "",
          m.name +
            " · " +
            formatSize(m.size) +
            (m.installed ? " · 取得済み" : ""),
        );
        option.value = m.id;
        return option;
      }),
    );
    $("model-select").value = status.settings.modelId;
    $("custom-model").value = status.settings.customModelPath || "";
    $("threads").value = status.settings.threads;
    $("context-size").value = status.settings.contextSize;
    $("max-tokens").value = status.settings.maxTokens;
    $("direct-ram").checked = status.settings.directRam;
    $("idle-unload").value = status.settings.idleUnloadMinutes;
    S.settingsLoaded = true;
  }
  $("data-path").textContent = status.dataDirectory;
  const sig = JSON.stringify(status.sources);
  if (refreshStatus.sources !== sig) {
    refreshStatus.sources = sig;
    const list = $("source-list");
    list.replaceChildren();
    for (const source of status.sources) {
      const card = el("div", "source-card");
      card.append(
        el("strong", "", source.title + " · " + source.version),
        el("small", "", "取得 " + formatDate(source.retrievedAt)),
        el("small", "", "revision: " + source.revision),
      );
      const link = el("a", "", "原典を開く ↗");
      link.href = source.url;
      link.target = "_blank";
      link.rel = "noopener noreferrer";
      card.append(link);
      list.append(card);
    }
    list.append(
      el(
        "p",
        "hint",
        status.sectionCount +
          "条文を保存済み。取得内容のSHA-256と日時をスナップショットに記録しています。",
      ),
    );
    $("rule-directory").replaceChildren();
  }
}
async function searchRules() {
  const q = $("rule-query").value.trim();
  if (!q) return;
  const results = await api(
    "rules/search?q=" +
      encodeURIComponent(q) +
      "&source=" +
      encodeURIComponent($("rule-source").value),
  );
  $("rule-results").replaceChildren(
    ...results.map((hit) => ruleCard(hit.section)),
  );
  if (!results.length)
    $("rule-results").append(
      el(
        "p",
        "muted",
        "一致する条文がありません。用語を変えて検索してください。",
      ),
    );
}
function ruleCard(section) {
  const card = el("div", "rule-result");
  const source = S.status.sources.find((s) => s.id === section.sourceId);
  card.append(
    el("h3", "", "§" + section.number + " " + section.title),
    el("p", "", source.title + " · " + source.version),
  );
  const details = el("details");
  details.append(el("summary", "", "条文を読む"), el("pre", "", section.text));
  card.append(details);
  const link = el("a", "", "原典を開く ↗");
  link.href = section.url;
  link.target = "_blank";
  link.rel = "noopener noreferrer";
  card.append(link);
  return card;
}
async function loadDirectory() {
  const sections = await api("rules/sections");
  $("rule-directory").replaceChildren();
  for (const section of sections) {
    const button = el(
      "button",
      "",
      (section.sourceId === "core2"
        ? "CoRE-2"
        : section.sourceId === "common"
          ? "共通"
          : "競技システム") +
        " · §" +
        section.number +
        " " +
        section.title,
    );
    button.onclick = guard(async () => {
      const full = await api(
        "rules/section?id=" + encodeURIComponent(section.id),
      );
      $("rule-results").replaceChildren(ruleCard(full));
      $("rule-results").querySelector("details").open = true;
      $("rule-results").scrollIntoView({ behavior: "smooth" });
    });
    $("rule-directory").append(button);
  }
}
function showPageDialog() {
  if (!S.current) return;
  const descendants = new Set([S.current.id]);
  let changed;
  do {
    changed = false;
    for (const p of S.pages)
      if (descendants.has(p.parentId) && !descendants.has(p.id)) {
        descendants.add(p.id);
        changed = true;
      }
  } while (changed);
  const root = el("option", "", "ワークスペース直下");
  root.value = "";
  $("parent-select").replaceChildren(root);
  for (const page of S.pages.filter((p) => !descendants.has(p.id))) {
    const option = el("option", "", page.title);
    option.value = page.id;
    $("parent-select").append(option);
  }
  $("parent-select").value = S.current.parentId || "";
  $("page-dialog").showModal();
}
async function init() {
  const session = await fetch("/api/session").then((r) => r.json());
  S.token = session.token;
  if (window.innerWidth < 950) $("app").classList.add("hidden-panel");
  await Promise.all([loadPages(), refreshStatus()]);
  if (S.pages.length) await openPage(S.pages[0].id);
  document
    .querySelectorAll("[data-view]")
    .forEach(
      (button) => (button.onclick = guard(() => setView(button.dataset.view))),
    );
  $("new-page").onclick = guard(() => createPage());
  $("first-page").onclick = guard(() => createPage());
  $("child-page").onclick = guard(() => createPage(S.current?.id));
  $("page-filter").oninput = renderTree;
  $("note-title").oninput = dirty;
  $("note-body").oninput = dirty;
  $("note-body").onselect = captureSelection;
  $("note-body").onkeyup = captureSelection;
  $("note-body").onmouseup = captureSelection;
  $("save-state").onclick = guard(save);
  $("preview-toggle").onclick = togglePreview;
  document
    .querySelectorAll("[data-insert]")
    .forEach(
      (button) => (button.onclick = () => insertBlock(button.dataset.insert)),
    );
  $("favorite").onclick = guard(async () => {
    if (!S.current) return;
    await save();
    S.current.favorite = !S.current.favorite;
    S.dirty = true;
    S.editSerial++;
    await save();
    $("favorite").textContent = S.current.favorite ? "★" : "☆";
  });
  $("export-note").onclick = guard(async () => {
    await save();
    if (S.current)
      download(
        "# " + S.current.title + "\n\n" + S.current.markdown,
        S.current.title.replace(/[\\/:*?"<>|]/g, "_") + ".md",
      );
  });
  $("export-workspace").onclick = guard(async () => {
    await save();
    const pages = await api("notes");
    download(
      JSON.stringify({ schemaVersion: 1, pages }, null, 2),
      "Timsah-Office-notes.json",
      "application/json",
    );
  });
  $("note-options").onclick = showPageDialog;
  $("move-page").onclick = guard(async () => {
    await save();
    S.current.parentId = $("parent-select").value || null;
    S.dirty = true;
    S.editSerial++;
    await save();
    $("page-dialog").close();
    toast("ページを移動しました");
  });
  $("delete-page").onclick = guard(async () => {
    await save();
    if (
      !confirm(
        "このページを削除しますか？直前の状態はnotes.json.bakに残ります。",
      )
    )
      return;
    await api(
      "notes/" + S.current.id + "?revision=" + S.current.revision,
      "DELETE",
    );
    S.current = null;
    S.dirty = false;
    $("page-dialog").close();
    $("note-document").hidden = true;
    $("note-empty").hidden = false;
    await loadPages();
    if (S.pages.length) await openPage(S.pages[0].id);
  });
  $("import-button").onclick = () => $("import-file").click();
  $("import-file").onchange = guard(async () => {
    const file = $("import-file").files[0];
    if (!file) return;
    if (file.size > 600000)
      throw new Error("読み込めるファイルは600KBまでです。");
    const content = await file.text();
    if (content.length > 200000)
      throw new Error("読み込める本文は20万文字までです。");
    await createPage();
    $("note-title").value = file.name
      .replace(/\.(md|markdown|txt)$/i, "")
      .slice(0, 200);
    $("note-body").value = content;
    dirty();
    await save();
    $("import-file").value = "";
    toast("ページとして読み込みました");
  });
  $("sidebar-toggle").onclick = () => $("app").classList.toggle("show-sidebar");
  $("ai-toggle").onclick = () => $("app").classList.toggle("hidden-panel");
  $("close-ai").onclick = () => $("app").classList.add("hidden-panel");
  $("chat-form").onsubmit = guard(async (event) => {
    event.preventDefault();
    const message = $("chat-input").value.trim();
    if (message) await chat(message);
  });
  $("chat-input").onkeydown = (event) => {
    if ((event.ctrlKey || event.metaKey) && event.key === "Enter") {
      event.preventDefault();
      $("chat-form").requestSubmit();
    }
  };
  $("abort-chat").onclick = () => S.chat?.abort();
  $("clear-chat").onclick = () => {
    if (S.chat) return toast("回答を中断してからクリアしてください");
    S.history = [];
    $("chat-log").replaceChildren(
      el("p", "hint", "新しい会話を開始しました。"),
    );
  };
  const actions = {
    summarize: "このメモを要約してください。",
    rewrite: "このメモの文章を整理してください。",
    todos: "このメモからTODOを抽出してください。",
    review: "このメモをCoRE-2ルールと照合してください。",
  };
  document
    .querySelectorAll("[data-assist]")
    .forEach(
      (button) =>
        (button.onclick = guard(() =>
          chat(actions[button.dataset.assist], button.dataset.assist),
        )),
    );
  document
    .querySelectorAll("[data-question]")
    .forEach(
      (button) =>
        (button.onclick = guard(() => chat(button.dataset.question, "rules"))),
    );
  $("rule-search-form").onsubmit = guard(async (event) => {
    event.preventDefault();
    await searchRules();
  });
  $("settings-form").onsubmit = guard(async (event) => {
    event.preventDefault();
    await api("settings", "PUT", {
      modelId: $("model-select").value,
      customModelPath: $("custom-model").value.trim() || null,
      threads: +$("threads").value,
      contextSize: +$("context-size").value,
      maxTokens: +$("max-tokens").value,
      directRam: $("direct-ram").checked,
      idleUnloadMinutes: +$("idle-unload").value,
    });
    toast("設定を保存しました");
    S.settingsLoaded = false;
    await refreshStatus();
  });
  for (const [id, path] of [
    ["install-model", "install"],
    ["start-model", "engine/start"],
    ["stop-model", "engine/stop"],
    ["cancel-job", "jobs/cancel"],
    ["update-rules", "rules/update"],
  ])
    $(id).onclick = guard(async () => {
      await api(path, "POST");
      await refreshStatus();
    });
  $("shutdown").onclick = guard(async () => {
    await save();
    if (S.chat) S.chat.abort();
    await api("shutdown", "POST");
    S.stopping = true;
    toast("アプリを終了しました。このタブを閉じてください。");
  });
  window.addEventListener("beforeunload", (event) => {
    if (S.dirty) {
      event.preventDefault();
      event.returnValue = "";
    }
  });
  setInterval(() => refreshStatus().catch(() => {}), 1800);
}
init().catch((error) => toast("起動エラー: " + error.message, true));
