(() => {
  "use strict";

  const BUILTIN_PRESETS = [
    {
      id: "builtin-pairpair-excel",
      name: "PairPair Excel",
      path: "/Users/lingkunwang/Desktop/twoblocks-frontend/BlockColorMatch/configExcel"
    },
    {
      id: "builtin-ffm-excel",
      name: "FFM Excel",
      path: "/Users/lingkunwang/Desktop/maker5-ffm-frontend/maker5-ffm-pmt-config/excel"
    },
    {
      id: "builtin-ffm-lua",
      name: "FFM Lua",
      path: "/Users/lingkunwang/Desktop/maker5-ffm-frontend/match3/LuaProject/gen/conf"
    }
  ];

  const state = {
    payload: null,
    selectedId: localStorage.getItem("selectedWorkbookId") || "",
    tableFilter: "",
    query: "",
    loading: false,
    editing: false,
    saving: false,
    changes: new Map(),
    loadingIds: new Set(),
    matchIndex: -1,
    matchNodes: [],
    presets: loadPresets(),
    defaultPresetId: localStorage.getItem("configToolDefaultPresetId") || "",
    draftPresets: [],
    draftDefaultPresetId: "",
    pendingLocation: null,
    backStack: [],
    forwardStack: [],
    workbookLoadWaiters: new Map(),
    relationLookupGeneration: 0,
    reverseLookupRequestId: 0,
    pendingReverseLookup: null,
    searchScope: "current",
    globalQuery: "",
    globalSearchRequestId: 0,
    pendingGlobalSearchRequestId: null,
    globalMatches: [],
    globalTotalCount: 0,
    globalMatchIndex: -1,
    globalSearchTimer: null,
  };

  const el = {};
  const $ = (id) => document.getElementById(id);
  const send = (action, extra = {}) => {
    window.webkit?.messageHandlers?.configTool?.postMessage({ action, ...extra });
  };

  function bindElements() {
    [
      "tableSearch", "tableNavigation", "folderPath", "statusDot", "statusText",
      "navigationSummary",
      "categoryLabel", "sheetLabel", "workbookTitle", "globalSearch", "matchCount",
      "refreshButton", "chooseButton", "revealButton", "statWorkbook", "statRows",
      "statColumns", "statModified", "emptyState", "errorState", "errorMessage",
      "errorChooseButton", "tableViewport", "configTable", "toast", "editButton",
      "saveButton", "modePill", "previousMatchButton", "nextMatchButton",
      "presetButton", "presetMenu",
      "presetMenuList", "saveCurrentPresetButton", "managePresetsButton",
      "presetModal", "closePresetModalButton", "cancelPresetChangesButton",
      "savePresetChangesButton", "addCurrentPresetButton", "presetEditorList",
      "followLastPresetRadio", "relationBackButton", "relationForwardButton",
      "relationChooser", "relationChooserList", "reverseReferencePanel",
      "reverseReferenceTitle", "reverseReferenceSummary", "reverseReferenceList",
      "closeReverseReferenceButton", "currentSearchScopeButton",
      "globalSearchScopeButton", "globalSearchPanel", "globalSearchSummary",
      "globalSearchResultList", "closeGlobalSearchButton"
    ].forEach((id) => { el[id] = $(id); });
  }

  function bindEvents() {
    el.tableSearch.addEventListener("input", (event) => {
      state.tableFilter = event.target.value.trim().toLowerCase();
      renderNavigation();
    });
    el.globalSearch.addEventListener("input", (event) => {
      if (state.searchScope === "global") {
        state.globalQuery = event.target.value.trim();
        scheduleGlobalSearch();
      } else {
        state.query = event.target.value.trim().toLowerCase();
        state.matchIndex = 0;
        renderTable();
      }
    });
    el.globalSearch.addEventListener("keydown", (event) => {
      if (event.key !== "Enter") return;
      event.preventDefault();
      if (state.searchScope === "global") {
        navigateGlobalMatch(event.shiftKey ? -1 : 1);
      } else {
        navigateMatch(event.shiftKey ? -1 : 1);
      }
    });
    el.currentSearchScopeButton.addEventListener("click", () => setSearchScope("current"));
    el.globalSearchScopeButton.addEventListener("click", () => setSearchScope("global"));
    el.previousMatchButton.addEventListener("click", () => {
      if (state.searchScope === "global") navigateGlobalMatch(-1);
      else navigateMatch(-1);
    });
    el.nextMatchButton.addEventListener("click", () => {
      if (state.searchScope === "global") navigateGlobalMatch(1);
      else navigateMatch(1);
    });
    el.relationBackButton.addEventListener("click", navigateBack);
    el.relationForwardButton.addEventListener("click", navigateForward);
    el.closeReverseReferenceButton.addEventListener("click", closeReverseReferencePanel);
    el.closeGlobalSearchButton.addEventListener("click", closeGlobalSearchPanel);
    el.refreshButton.addEventListener("click", () => {
      if (confirmDiscard()) send("refresh");
    });
    el.chooseButton.addEventListener("click", () => {
      closePresetMenu();
      if (confirmDiscard()) send("chooseDirectory");
    });
    el.errorChooseButton.addEventListener("click", () => send("chooseDirectory"));
    el.revealButton.addEventListener("click", () => send("revealDirectory"));
    el.editButton.addEventListener("click", toggleEditMode);
    el.saveButton.addEventListener("click", saveChanges);
    el.presetButton.addEventListener("click", (event) => {
      event.stopPropagation();
      renderPresetMenu();
      el.presetMenu.classList.toggle("hidden");
    });
    el.saveCurrentPresetButton.addEventListener("click", () => {
      closePresetMenu();
      openPresetModal({ addCurrent: true });
    });
    el.managePresetsButton.addEventListener("click", () => {
      closePresetMenu();
      openPresetModal();
    });
    el.closePresetModalButton.addEventListener("click", closePresetModal);
    el.cancelPresetChangesButton.addEventListener("click", closePresetModal);
    el.addCurrentPresetButton.addEventListener("click", addCurrentDirectoryDraft);
    el.savePresetChangesButton.addEventListener("click", savePresetChanges);
    el.followLastPresetRadio.addEventListener("change", () => {
      state.draftDefaultPresetId = "";
    });
    el.presetModal.addEventListener("click", (event) => {
      if (event.target === el.presetModal) closePresetModal();
    });
    document.addEventListener("click", (event) => {
      if (!event.target.closest(".preset-control")) closePresetMenu();
      if (!event.target.closest(".relation-chooser")) closeRelationChooser();
      if (!event.target.closest(".reverse-reference-panel") && !event.altKey) {
        closeReverseReferencePanel();
      }
      if (!event.target.closest(".global-search-panel") && !event.target.closest(".table-search-controls")) {
        closeGlobalSearchPanel();
      }
    });
    document.addEventListener("keydown", (event) => {
      document.body.classList.toggle("relation-modifier-active", event.metaKey || event.ctrlKey);
      document.body.classList.toggle("reverse-modifier-active", event.altKey);
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "k") {
        event.preventDefault();
        el.tableSearch.focus();
        el.tableSearch.select();
      }
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "f") {
        event.preventDefault();
        el.globalSearch.focus();
        el.globalSearch.select();
      }
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "r") {
        event.preventDefault();
        if (confirmDiscard()) send("refresh");
      }
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "s") {
        event.preventDefault();
        saveChanges();
      }
      if ((event.metaKey || event.ctrlKey) && event.key === "[") {
        event.preventDefault();
        navigateBack();
      }
      if ((event.metaKey || event.ctrlKey) && event.key === "]") {
        event.preventDefault();
        navigateForward();
      }
      if (event.key === "Escape") {
        closeRelationChooser();
        closeReverseReferencePanel();
        closeGlobalSearchPanel();
      }
    });
    document.addEventListener("keyup", (event) => {
      document.body.classList.toggle("relation-modifier-active", event.metaKey || event.ctrlKey);
      document.body.classList.toggle("reverse-modifier-active", event.altKey);
    });
    window.addEventListener("blur", () => {
      document.body.classList.remove("relation-modifier-active");
      document.body.classList.remove("reverse-modifier-active");
    });
  }

  function activeWorkbook() {
    if (!state.payload) return null;
    return state.payload.workbooks.find((book) => book.id === state.selectedId) || null;
  }

  function ensureSelection() {
    if (!state.payload?.workbooks?.length) return;
    if (!activeWorkbook()) {
      const firstHealthy = state.payload.workbooks.find((book) => !book.error);
      state.selectedId = (firstHealthy || state.payload.workbooks[0]).id;
    }
    localStorage.setItem("selectedWorkbookId", state.selectedId);
  }

  function confirmDiscard() {
    if (!state.changes.size) return true;
    const confirmed = window.confirm("当前配置有未保存的修改，是否放弃这些修改？");
    if (!confirmed) return false;
    discardChanges();
    return true;
  }

  function discardChanges() {
    const book = activeWorkbook();
    if (book) {
      state.changes.forEach((change) => {
        if (book.rows[change.row]) book.rows[change.row][change.column] = change.original;
      });
    }
    state.changes.clear();
    state.editing = false;
    updateEditUI();
  }

  function toggleEditMode() {
    if (state.saving) return;
    if (state.editing) {
      if (!confirmDiscard()) return;
      state.editing = false;
    } else {
      state.editing = true;
    }
    updateEditUI();
    renderTable();
    if (state.editing && activeWorkbook()?.sourceKind === "lua") {
      showToast("Lua 是生成配置，重新生成时可能覆盖本次修改");
    }
  }

  function saveChanges() {
    const book = activeWorkbook();
    if (!book || !state.editing || !state.changes.size || state.saving) return;
    send("save", {
      id: book.id,
      sourceSignature: book.sourceSignature,
      changes: [...state.changes.values()].map(({ row, column, value }) => ({ row, column, value }))
    });
  }

  function updateEditUI() {
    el.editButton.classList.toggle("active", state.editing);
    el.editButton.textContent = state.editing ? "退出编辑" : "编辑";
    el.saveButton.disabled = !state.editing || !state.changes.size || state.saving;
    el.saveButton.textContent = state.saving ? "保存中…" : "保存";
    el.modePill.classList.toggle("edit-mode", state.editing);
    el.modePill.innerHTML = state.editing ? "<span>●</span> 编辑模式" : "<span>●</span> 只读模式";
  }

  function loadPresets() {
    try {
      const saved = JSON.parse(localStorage.getItem("configToolPresetsV1") || "null");
      if (Array.isArray(saved)) return saved.filter((item) => item?.id && item?.name && item?.path);
    } catch (_) {
      // Fall back to built-ins.
    }
    localStorage.setItem("configToolPresetsV1", JSON.stringify(BUILTIN_PRESETS));
    return BUILTIN_PRESETS.map((item) => ({ ...item }));
  }

  function persistPresets() {
    localStorage.setItem("configToolPresetsV1", JSON.stringify(state.presets));
    localStorage.setItem("configToolDefaultPresetId", state.defaultPresetId);
  }

  function currentPreset() {
    const path = state.payload?.directory;
    return state.presets.find((preset) => preset.path === path) || null;
  }

  function updatePresetButton() {
    const preset = currentPreset();
    const label = preset?.name || (state.payload ? "未保存目录" : "目录预设");
    el.presetButton.innerHTML = `${escapeHTML(label)} <span>⌄</span>`;
    el.presetButton.title = state.payload?.directory || "切换配置目录";
  }

  function renderPresetMenu() {
    const currentPath = state.payload?.directory || "";
    el.presetMenuList.replaceChildren();
    state.presets.forEach((preset) => {
      const button = document.createElement("button");
      button.className = "preset-menu-item";
      button.innerHTML = `
        <span class="preset-check">${preset.path === currentPath ? "✓" : ""}</span>
        <span class="preset-menu-copy">
          <strong>${escapeHTML(preset.name)}</strong>
          <small>${escapeHTML(preset.path)}</small>
        </span>
        <span class="preset-default-mark">${preset.id === state.defaultPresetId ? "★" : ""}</span>`;
      button.addEventListener("click", () => switchToPreset(preset));
      el.presetMenuList.appendChild(button);
    });
  }

  function closePresetMenu() {
    el.presetMenu.classList.add("hidden");
  }

  function switchToPreset(preset) {
    closePresetMenu();
    if (preset.path === state.payload?.directory) return;
    if (!confirmDiscard()) return;
    send("switchDirectory", { path: preset.path });
  }

  function openPresetModal({ addCurrent = false } = {}) {
    state.draftPresets = state.presets.map((preset) => ({ ...preset }));
    state.draftDefaultPresetId = state.defaultPresetId;
    if (addCurrent) addCurrentDirectoryDraft();
    renderPresetEditor();
    el.presetModal.classList.remove("hidden");
    if (addCurrent) {
      requestAnimationFrame(() => {
        const inputs = el.presetEditorList.querySelectorAll(".preset-name-input");
        inputs[inputs.length - 1]?.focus();
        inputs[inputs.length - 1]?.select();
      });
    }
  }

  function closePresetModal() {
    el.presetModal.classList.add("hidden");
  }

  function addCurrentDirectoryDraft() {
    const path = state.payload?.directory;
    if (!path) return;
    const existing = state.draftPresets.find((preset) => preset.path === path);
    if (existing) {
      showToast("当前目录已经存在于预设中");
      return;
    }
    const folderName = path.split("/").filter(Boolean).pop() || "配置目录";
    state.draftPresets.push({
      id: `preset-${Date.now()}-${Math.random().toString(16).slice(2)}`,
      name: folderName,
      path
    });
    renderPresetEditor();
  }

  function renderPresetEditor() {
    el.followLastPresetRadio.checked = !state.draftDefaultPresetId;
    el.presetEditorList.replaceChildren();
    state.draftPresets.forEach((preset, index) => {
      const row = document.createElement("div");
      row.className = "preset-editor-row";
      row.innerHTML = `
        <input type="radio" name="defaultPreset" value="${escapeHTML(preset.id)}" ${preset.id === state.draftDefaultPresetId ? "checked" : ""} title="设为启动默认">
        <input class="preset-name-input" type="text" value="${escapeHTML(preset.name)}" placeholder="预设名称">
        <input class="preset-path-input" type="text" value="${escapeHTML(preset.path)}" placeholder="目录绝对路径">
        <span class="preset-row-actions">
          <button class="move-preset-up" title="上移">↑</button>
          <button class="move-preset-down" title="下移">↓</button>
          <button class="delete-preset-button" title="删除">×</button>
        </span>`;
      const [radio, nameInput, pathInput] = row.querySelectorAll("input");
      radio.addEventListener("change", () => {
        state.draftDefaultPresetId = preset.id;
        el.followLastPresetRadio.checked = false;
      });
      nameInput.addEventListener("input", () => { preset.name = nameInput.value; });
      pathInput.addEventListener("input", () => { preset.path = pathInput.value; });
      row.querySelector(".move-preset-up").addEventListener("click", () => movePreset(index, -1));
      row.querySelector(".move-preset-down").addEventListener("click", () => movePreset(index, 1));
      row.querySelector(".delete-preset-button").addEventListener("click", () => {
        state.draftPresets.splice(index, 1);
        if (state.draftDefaultPresetId === preset.id) state.draftDefaultPresetId = "";
        renderPresetEditor();
      });
      el.presetEditorList.appendChild(row);
    });
  }

  function movePreset(index, delta) {
    const target = index + delta;
    if (target < 0 || target >= state.draftPresets.length) return;
    const [preset] = state.draftPresets.splice(index, 1);
    state.draftPresets.splice(target, 0, preset);
    renderPresetEditor();
  }

  function savePresetChanges() {
    const normalized = state.draftPresets.map((preset) => ({
      ...preset,
      name: preset.name.trim(),
      path: preset.path.trim().replace(/\/+$/, "")
    }));
    if (normalized.some((preset) => !preset.name || !preset.path.startsWith("/"))) {
      showToast("预设名称不能为空，路径必须是绝对路径");
      return;
    }
    const duplicatePath = normalized.some((preset, index) =>
      normalized.findIndex((other) => other.path === preset.path) !== index
    );
    if (duplicatePath) {
      showToast("存在重复目录，请合并或删除后再保存");
      return;
    }
    state.presets = normalized;
    state.defaultPresetId = state.draftDefaultPresetId;
    persistPresets();
    closePresetModal();
    updatePresetButton();
    showToast("目录预设已保存");
  }

  function render() {
    if (!state.payload) return;
    ensureSelection();
    el.folderPath.textContent = state.payload.directory;
    el.statusDot.className = "status-dot ready";
    const failures = state.payload.workbooks.filter((book) => book.error).length;
    el.navigationSummary.textContent = `${state.payload.fileCount} 个文件 · ${state.payload.workbooks.length} 个 Sheet`;
    el.statusText.textContent = failures
      ? `${state.payload.fileCount} 个文件 / ${state.payload.workbooks.length} 个 Sheet，${failures} 个异常`
      : `${state.payload.fileCount} 个文件 / ${state.payload.workbooks.length} 个 Sheet 已同步`;
    el.statWorkbook.textContent = `${state.payload.fileCount.toLocaleString("zh-CN")} / ${state.payload.workbooks.length.toLocaleString("zh-CN")}`;
    updatePresetButton();
    renderPresetMenu();
    updateRelationHistoryUI();
    renderNavigation();
    renderTable();
  }

  function renderNavigation() {
    if (!state.payload) return;
    const categoryOrder = ["活动", "关卡", "奖励与商业化", "物品与角色", "基础配置"];
    const filtered = state.payload.workbooks.filter((book) =>
      !state.tableFilter ||
      book.name.toLowerCase().includes(state.tableFilter) ||
      book.sheetName.toLowerCase().includes(state.tableFilter) ||
      book.category.toLowerCase().includes(state.tableFilter)
    );
    el.tableNavigation.replaceChildren();

    categoryOrder.forEach((category) => {
      const books = filtered.filter((book) => book.category === category);
      if (!books.length) return;
      const group = document.createElement("section");
      group.className = "group";
      const title = document.createElement("div");
      title.className = "group-title";
      const fileCount = new Set(books.map((book) => book.fileName)).size;
      const countLabel = fileCount === books.length
        ? `${fileCount} 文件`
        : `${fileCount} 文件 · ${books.length} Sheet`;
      title.innerHTML = `<span>${escapeHTML(category)}</span><span>${countLabel}</span>`;
      group.appendChild(title);

      books.forEach((book) => {
        const button = document.createElement("button");
        button.className = `table-link${book.id === state.selectedId ? " active" : ""}${book.error ? " error" : ""}`;
        const navigationName = book.sheetCount > 1 ? `${book.name} · ${book.sheetName}` : book.name;
        button.title = book.error || `${book.fileName} / ${book.sheetName}`;
        button.innerHTML = `<span class="name">${escapeHTML(navigationName)}</span><span class="rows">${book.error ? "!" : Math.max(0, book.rowCount - 3)}</span>`;
        button.addEventListener("click", () => {
          if (!confirmDiscard()) return;
          cancelPendingReverseLookup();
          state.selectedId = book.id;
          state.pendingLocation = null;
          state.query = "";
          state.matchIndex = -1;
          state.matchNodes = [];
          state.globalQuery = "";
          resetGlobalSearch();
          el.globalSearch.value = "";
          localStorage.setItem("selectedWorkbookId", state.selectedId);
          el.tableViewport.scrollTop = 0;
          el.tableViewport.scrollLeft = 0;
          renderNavigation();
          renderTable();
        });
        group.appendChild(button);
      });
      el.tableNavigation.appendChild(group);
    });
  }

  function normalizeRelationToken(value) {
    return String(value || "")
      .replace(/\.(xlsx|lua)$/i, "")
      .split("@")[0]
      .replace(/(?:config|cfg|table|design|column|server)$/i, "")
      .replace(/[^a-zA-Z0-9]/g, "")
      .toLowerCase();
  }

  function workbookRelationTokens(book) {
    const fileBase = String(book.fileName || "").replace(/\.(xlsx|lua)$/i, "");
    return new Set([
      book.name,
      fileBase,
      book.sheetName,
      String(book.sheetName || "").split("@")[0]
    ].map(normalizeRelationToken).filter(Boolean));
  }

  function workbookPrimaryRelationToken(book) {
    const sheetToken = normalizeRelationToken(book.sheetName);
    const nameToken = normalizeRelationToken(book.name);
    return book.sheetCount > 1 && sheetToken && !/^sheet\d*$/.test(sheetToken)
      ? sheetToken
      : nameToken;
  }

  function findRelationBooks(targets) {
    if (!state.payload) return [];
    const wanted = new Set(targets.map(normalizeRelationToken).filter(Boolean));
    return state.payload.workbooks.filter((book) =>
      [...workbookRelationTokens(book)].some((token) => wanted.has(token))
    );
  }

  function relationRuleForField(book, fieldValue) {
    const field = String(fieldValue || "").trim();
    if (!field || ["id", "subid"].includes(field.toLowerCase())) return null;
    const explicit = (window.CONFIG_RELATION_RULES || []).find((rule) =>
      rule.fields.some((candidate) => candidate.toLowerCase() === field.toLowerCase())
    );
    if (explicit && findRelationBooks(explicit.targets).length) return explicit;

    const inferredTarget = field
      .replace(/(?:Config|Cfg)?(?:ID|Id)$/i, "")
      .replace(/(?:_id)$/i, "");
    if (!inferredTarget || inferredTarget === field) return null;
    const targets = findRelationBooks([inferredTarget]);
    if (!targets.length) return null;
    return {
      fields: [field],
      targets: [inferredTarget],
      targetKey: "ID",
      mode: "scalar",
      label: inferredTarget
    };
  }

  function relationRuleForColumn(book, columnIndex) {
    return relationRuleForField(book, book.rows[1]?.[columnIndex]);
  }

  function activitySubIdRule(book, rowIndex, columnIndex) {
    const bookTokens = workbookRelationTokens(book);
    const field = String(book.rows[1]?.[columnIndex] || "").trim().toLowerCase();
    if (!bookTokens.has("activity") || field !== "subid") return null;
    const headers = book.rows[1] || [];
    const nameIndex = headers.findIndex((header) => String(header).toLowerCase() === "name");
    if (nameIndex < 0) return null;
    const targetName = String(book.rows[rowIndex]?.[nameIndex] || "").trim();
    if (!targetName || !findRelationBooks([targetName]).length) return null;
    return {
      fields: ["SubID"],
      targets: [targetName],
      targetKey: "SubID",
      mode: "scalar",
      label: `${targetName} 活动配置`
    };
  }

  function hierarchyRuleForField(book, fieldValue) {
    const field = String(fieldValue || "").trim();
    if (!["id", "subid"].includes(field.toLowerCase()) || !state.payload) return null;
    const sourceToken = workbookPrimaryRelationToken(book);
    if (!sourceToken || sourceToken === "activity") return null;

    const parents = state.payload.workbooks
      .filter((candidate) => candidate.id !== book.id)
      .map((candidate) => ({
        book: candidate,
        token: workbookPrimaryRelationToken(candidate)
      }))
      .filter(({ token }) =>
        token.length >= 4 &&
        sourceToken.startsWith(token) &&
        sourceToken.length > token.length
      )
      .sort((a, b) => b.token.length - a.token.length);

    const longestLength = parents[0]?.token.length || 0;
    let targets = parents.filter(({ token }) => token.length === longestLength).map(({ book: candidate }) => candidate);
    if (!targets.length && ["battlepass", "weeklyrank"].includes(sourceToken)) {
      targets = findRelationBooks(["Activity"]);
    }
    if (!targets.length) return null;
    return {
      bookIds: targets.map((target) => target.id),
      targetKey: field.toLowerCase() === "subid" ? "SubID|ID" : "ID|SubID",
      mode: "scalar",
      label: "上级配置"
    };
  }

  function hierarchyRuleForColumn(book, columnIndex) {
    return hierarchyRuleForField(book, book.rows[1]?.[columnIndex]);
  }

  function extractRelationValues(value, mode) {
    const text = String(value || "").trim();
    if (!text) return [];
    if (mode !== "jsonKeys") return [text];
    if (!text.includes("{")) return [];
    const values = [];
    const pattern = /["']?([a-zA-Z0-9_.:-]+)["']?\s*:/g;
    let match;
    while ((match = pattern.exec(text))) values.push(match[1]);
    return [...new Set(values)];
  }

  function relationDestinations(book, rowIndex, columnIndex, value, columnRule) {
    const rule = columnRule || activitySubIdRule(book, rowIndex, columnIndex);
    if (!rule) return [];
    const values = extractRelationValues(value, rule.mode);
    const targets = rule.bookIds
      ? state.payload.workbooks.filter((target) => rule.bookIds.includes(target.id))
      : findRelationBooks(rule.targets || []);
    const destinations = [];
    targets.forEach((target) => {
      values.forEach((referenceValue) => {
        destinations.push({
          bookId: target.id,
          bookLabel: target.sheetCount > 1 ? `${target.name} · ${target.sheetName}` : target.name,
          key: rule.targetKey || "ID",
          value: referenceValue,
          label: rule.label || rule.targets[0]
        });
      });
    });
    return destinations.filter((destination, index, all) =>
      all.findIndex((candidate) =>
        candidate.bookId === destination.bookId &&
        candidate.key === destination.key &&
        candidate.value === destination.value
      ) === index
    );
  }

  function matchingRelationRows(book, destination) {
    if (!book?.isLoaded || book.error) return null;
    const headers = book.rows[1] || [];
    const expected = comparableRelationValue(destination.value);
    const keyCandidates = String(destination.key || "ID").split("|");
    for (const keyCandidate of keyCandidates) {
      const column = headers.findIndex((header) =>
        String(header).trim().toLowerCase() === keyCandidate.toLowerCase()
      );
      if (column < 0) continue;
      const rows = [];
      for (let row = 3; row < book.rows.length; row += 1) {
        if (comparableRelationValue(book.rows[row]?.[column]) === expected) rows.push(row);
      }
      if (rows.length) return { key: keyCandidate, column, rows };
    }
    return null;
  }

  function ensureWorkbookLoaded(bookId) {
    const book = state.payload?.workbooks.find((candidate) => candidate.id === bookId);
    if (!book) return Promise.reject(new Error("关联配置表不存在或已更名"));
    if (book.isLoaded) return Promise.resolve(book);
    if (book.error) return Promise.reject(new Error(book.error));

    return new Promise((resolve, reject) => {
      const waiters = state.workbookLoadWaiters.get(bookId) || [];
      waiters.push({ resolve, reject });
      state.workbookLoadWaiters.set(bookId, waiters);
      if (!state.loadingIds.has(bookId)) {
        state.loadingIds.add(bookId);
        send("loadWorkbook", { id: bookId });
      }
    });
  }

  function settleWorkbookLoad(bookId, result, error = null) {
    const waiters = state.workbookLoadWaiters.get(bookId) || [];
    state.workbookLoadWaiters.delete(bookId);
    waiters.forEach(({ resolve, reject }) => {
      if (error) reject(error);
      else resolve(result);
    });
  }

  function cancelPendingReverseLookup() {
    state.reverseLookupRequestId += 1;
    state.pendingReverseLookup = null;
    document.body.classList.remove("reverse-lookup-active");
    closeReverseReferencePanel();
  }

  function cancelRelationLookups(message = "配置目录已变化") {
    state.relationLookupGeneration += 1;
    cancelPendingReverseLookup();
    document.body.classList.remove("relation-lookup-active");
    state.workbookLoadWaiters.forEach((waiters) => {
      waiters.forEach(({ reject }) => reject(new Error(message)));
    });
    state.workbookLoadWaiters.clear();
  }

  function closeGlobalSearchPanel() {
    el.globalSearchPanel?.classList.add("hidden");
  }

  function resetGlobalSearch() {
    clearTimeout(state.globalSearchTimer);
    state.globalSearchTimer = null;
    state.globalSearchRequestId += 1;
    state.pendingGlobalSearchRequestId = null;
    state.globalMatches = [];
    state.globalTotalCount = 0;
    state.globalMatchIndex = -1;
    closeGlobalSearchPanel();
    updateGlobalSearchNavigation();
  }

  function setSearchScope(scope) {
    const nextScope = scope === "global" ? "global" : "current";
    if (state.searchScope === nextScope) {
      if (nextScope === "global" && state.globalMatches.length) showGlobalSearchPanel();
      return;
    }
    state.searchScope = nextScope;
    el.currentSearchScopeButton.classList.toggle("active", nextScope === "current");
    el.globalSearchScopeButton.classList.toggle("active", nextScope === "global");
    el.currentSearchScopeButton.setAttribute("aria-pressed", String(nextScope === "current"));
    el.globalSearchScopeButton.setAttribute("aria-pressed", String(nextScope === "global"));
    el.globalSearch.placeholder = nextScope === "global"
      ? "搜索所有配置表的 ID 或内容"
      : "在当前表中查找 ID 或内容";

    if (nextScope === "global") {
      state.query = "";
      state.matchIndex = -1;
      state.matchNodes = [];
      state.globalQuery = el.globalSearch.value.trim();
      renderTable();
      scheduleGlobalSearch();
    } else {
      resetGlobalSearch();
      state.query = el.globalSearch.value.trim().toLowerCase();
      state.matchIndex = 0;
      renderTable();
    }
    el.globalSearch.focus();
  }

  function scheduleGlobalSearch() {
    clearTimeout(state.globalSearchTimer);
    state.globalSearchTimer = null;
    const query = state.globalQuery.trim();
    if (state.searchScope !== "global" || !query) {
      resetGlobalSearch();
      return;
    }

    const requestId = ++state.globalSearchRequestId;
    state.pendingGlobalSearchRequestId = requestId;
    state.globalMatches = [];
    state.globalTotalCount = 0;
    state.globalMatchIndex = -1;
    el.matchCount.textContent = "查找中…";
    el.previousMatchButton.disabled = true;
    el.nextMatchButton.disabled = true;
    closeGlobalSearchPanel();
    state.globalSearchTimer = setTimeout(() => {
      state.globalSearchTimer = null;
      send("findGlobalMatches", { requestId, query });
    }, 240);
  }

  function updateGlobalSearchNavigation() {
    if (state.searchScope !== "global") return;
    const hasMatches = state.globalMatches.length > 0;
    el.previousMatchButton.disabled = !hasMatches;
    el.nextMatchButton.disabled = !hasMatches;
    if (state.pendingGlobalSearchRequestId !== null) {
      el.matchCount.textContent = "查找中…";
    } else if (!state.globalQuery) {
      el.matchCount.textContent = "";
    } else if (!hasMatches) {
      el.matchCount.textContent = "无匹配";
    } else if (state.globalMatchIndex >= 0) {
      el.matchCount.textContent = `${state.globalMatchIndex + 1} / ${state.globalTotalCount}`;
    } else {
      el.matchCount.textContent = `${state.globalTotalCount} 条`;
    }
  }

  function showGlobalSearchPanel() {
    if (state.searchScope !== "global" || !state.globalMatches.length) return;
    el.globalSearchResultList.replaceChildren();
    const groups = new Map();
    state.globalMatches.forEach((match, index) => {
      const items = groups.get(match.bookLabel) || [];
      items.push({ match, index });
      groups.set(match.bookLabel, items);
    });
    groups.forEach((items, bookLabel) => {
      const group = document.createElement("section");
      group.className = "global-search-result-group";
      const title = document.createElement("div");
      title.className = "global-search-result-group-title";
      title.innerHTML = `<strong>${escapeHTML(bookLabel)}</strong><span>${items.length}</span>`;
      group.appendChild(title);
      items.forEach(({ match, index }) => {
        const button = document.createElement("button");
        button.className = `global-search-result-item${index === state.globalMatchIndex ? " active" : ""}`;
        button.dataset.index = String(index);
        button.innerHTML = `
          <span class="global-search-result-meta">
            <strong>${escapeHTML(match.field || `第 ${match.column + 1} 列`)}</strong>
            <small>第 ${match.row + 1} 行</small>
          </span>
          <code>${escapeHTML(match.value)}</code>
          <small class="global-search-result-preview">${escapeHTML(match.rowPreview || "")}</small>`;
        button.addEventListener("click", (event) => {
          event.stopPropagation();
          state.globalMatchIndex = index;
          jumpToGlobalResult(match);
        });
        group.appendChild(button);
      });
      el.globalSearchResultList.appendChild(group);
    });
    const shown = state.globalMatches.length;
    el.globalSearchSummary.textContent = state.globalTotalCount > shown
      ? `共 ${state.globalTotalCount} 条，显示前 ${shown} 条`
      : `共 ${state.globalTotalCount} 条`;
    const controls = document.querySelector(".table-search-controls");
    const rect = controls.getBoundingClientRect();
    const width = Math.min(560, window.innerWidth - 28);
    el.globalSearchPanel.style.width = `${width}px`;
    el.globalSearchPanel.style.left = `${Math.max(14, Math.min(rect.right - width, window.innerWidth - width - 14))}px`;
    el.globalSearchPanel.style.top = `${Math.min(rect.bottom + 8, window.innerHeight - 120)}px`;
    el.globalSearchPanel.classList.remove("hidden");
  }

  function jumpToGlobalResult(match) {
    if (!match || !confirmDiscard()) return;
    state.backStack.push(captureLocation());
    state.forwardStack = [];
    updateRelationHistoryUI();
    goToLocation({
      bookId: match.bookId,
      row: match.row,
      column: match.column
    }, { preserveSearch: true });
    closeGlobalSearchPanel();
    updateGlobalSearchNavigation();
  }

  function navigateGlobalMatch(delta) {
    if (!state.globalMatches.length) return;
    if (state.globalMatchIndex < 0) {
      state.globalMatchIndex = delta < 0 ? state.globalMatches.length - 1 : 0;
    } else {
      state.globalMatchIndex =
        (state.globalMatchIndex + delta + state.globalMatches.length) %
        state.globalMatches.length;
    }
    jumpToGlobalResult(state.globalMatches[state.globalMatchIndex]);
  }

  async function validateRelationDestinations(destinations, generation, directory) {
    const bookIds = [...new Set(destinations.map((destination) => destination.bookId))];
    await Promise.allSettled(bookIds.map((bookId) => ensureWorkbookLoaded(bookId)));
    if (
      generation !== state.relationLookupGeneration ||
      directory !== state.payload?.directory
    ) return [];

    return destinations.flatMap((destination) => {
      const book = state.payload.workbooks.find((candidate) => candidate.id === destination.bookId);
      const match = matchingRelationRows(book, destination);
      return match ? [{ ...destination, key: match.key, matchCount: match.rows.length }] : [];
    }).filter((destination, index, all) =>
      all.findIndex((candidate) =>
        candidate.bookId === destination.bookId &&
        candidate.key === destination.key &&
        candidate.value === destination.value
      ) === index
    );
  }

  function comparableRelationValue(value) {
    const text = String(value ?? "").trim();
    const number = Number(text);
    return text !== "" && Number.isFinite(number) ? String(number) : text;
  }

  function captureLocation(row = null, column = null) {
    return {
      bookId: state.selectedId,
      row,
      column,
      scrollTop: el.tableViewport.scrollTop,
      scrollLeft: el.tableViewport.scrollLeft
    };
  }

  function updateRelationHistoryUI() {
    el.relationBackButton.disabled = !state.backStack.length;
    el.relationForwardButton.disabled = !state.forwardStack.length;
  }

  function goToLocation(location, { preserveSearch = false } = {}) {
    const book = state.payload?.workbooks.find((candidate) => candidate.id === location.bookId);
    if (!book) {
      showToast("关联配置表不存在或已更名");
      return;
    }
    state.selectedId = book.id;
    closeReverseReferencePanel();
    closeGlobalSearchPanel();
    if (!preserveSearch) {
      state.query = "";
      state.matchIndex = -1;
      state.matchNodes = [];
      state.globalQuery = "";
      resetGlobalSearch();
      el.globalSearch.value = "";
    }
    state.pendingLocation = location;
    localStorage.setItem("selectedWorkbookId", state.selectedId);
    renderNavigation();
    renderTable();
  }

  function jumpToRelation(destination, sourceRow, sourceColumn) {
    if (!confirmDiscard()) return;
    state.backStack.push(captureLocation(sourceRow, sourceColumn));
    state.forwardStack = [];
    updateRelationHistoryUI();
    goToLocation({
      bookId: destination.bookId,
      key: destination.key,
      value: destination.value,
      relationLabel: destination.label
    });
  }

  function navigateBack() {
    if (!state.backStack.length || !confirmDiscard()) return;
    const target = state.backStack.pop();
    state.forwardStack.push(captureLocation());
    updateRelationHistoryUI();
    goToLocation(target, { preserveSearch: state.searchScope === "global" });
  }

  function navigateForward() {
    if (!state.forwardStack.length || !confirmDiscard()) return;
    const target = state.forwardStack.pop();
    state.backStack.push(captureLocation());
    updateRelationHistoryUI();
    goToLocation(target, { preserveSearch: state.searchScope === "global" });
  }

  function closeRelationChooser() {
    el.relationChooser.classList.add("hidden");
  }

  function showRelationChooser(destinations, sourceRow, sourceColumn, event) {
    el.relationChooserList.replaceChildren();
    destinations.forEach((destination) => {
      const button = document.createElement("button");
      button.className = "relation-choice";
      button.innerHTML = `
        <strong>${escapeHTML(destination.bookLabel)}</strong>
        <small>${escapeHTML(destination.key)} = ${escapeHTML(destination.value)}</small>`;
      button.addEventListener("click", (choiceEvent) => {
        choiceEvent.stopPropagation();
        closeRelationChooser();
        jumpToRelation(destination, sourceRow, sourceColumn);
      });
      el.relationChooserList.appendChild(button);
    });
    el.relationChooser.style.left = `${Math.min(event.clientX + 8, window.innerWidth - 374)}px`;
    el.relationChooser.style.top = `${Math.min(event.clientY + 8, window.innerHeight - 334)}px`;
    el.relationChooser.classList.remove("hidden");
  }

  function closeReverseReferencePanel() {
    el.reverseReferencePanel?.classList.add("hidden");
  }

  function reverseQueryPlan(targetBook) {
    const targetTokens = [...workbookRelationTokens(targetBook)];
    const matchingRules = (window.CONFIG_RELATION_RULES || []).filter((rule) =>
      findRelationBooks(rule.targets || []).some((book) => book.id === targetBook.id)
    );
    return {
      targetTokens,
      scalarFields: [...new Set(matchingRules
        .filter((rule) => rule.mode !== "jsonKeys")
        .flatMap((rule) => rule.fields))],
      jsonFields: [...new Set(matchingRules
        .filter((rule) => rule.mode === "jsonKeys")
        .flatMap((rule) => rule.fields))]
    };
  }

  function reverseReferenceTargetsBook(reference, targetBook) {
    const sourceBook = state.payload?.workbooks.find((book) => book.id === reference.bookId);
    if (!sourceBook) return false;
    const field = String(reference.field || "").trim();
    const lowerField = field.toLowerCase();
    if (
      workbookRelationTokens(sourceBook).has("activity") &&
      lowerField === "subid" &&
      [...workbookRelationTokens(targetBook)].includes(normalizeRelationToken(reference.rowName))
    ) return true;

    const hierarchyRule = hierarchyRuleForField(sourceBook, field);
    if (hierarchyRule?.bookIds?.includes(targetBook.id)) return true;

    const relationRule = relationRuleForField(sourceBook, field);
    if (!relationRule) return false;
    const targets = relationRule.bookIds
      ? state.payload.workbooks.filter((book) => relationRule.bookIds.includes(book.id))
      : findRelationBooks(relationRule.targets || []);
    return targets.some((book) => book.id === targetBook.id);
  }

  function showReverseReferencePanel(references, context) {
    const groups = new Map();
    references.forEach((reference) => {
      const key = reference.bookLabel;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(reference);
    });
    el.reverseReferenceTitle.textContent = `ID ${context.value} 的反向引用`;
    el.reverseReferenceSummary.textContent =
      `${references.length} 处引用 · ${groups.size} 张配置表`;
    el.reverseReferenceList.replaceChildren();
    const fragment = document.createDocumentFragment();
    groups.forEach((items, label) => {
      const section = document.createElement("section");
      section.className = "reverse-reference-group";
      const heading = document.createElement("div");
      heading.className = "reverse-reference-group-title";
      heading.innerHTML = `<strong>${escapeHTML(label)}</strong><span>${items.length}</span>`;
      section.appendChild(heading);
      items.forEach((reference) => {
        const button = document.createElement("button");
        button.className = "reverse-reference-item";
        button.innerHTML = `
          <span>
            <strong>${escapeHTML(reference.field)}</strong>
            <small>第 ${reference.row + 1} 行</small>
          </span>
          <code>${escapeHTML(reference.cellValue)}</code>`;
        button.addEventListener("click", (event) => {
          event.stopPropagation();
          closeReverseReferencePanel();
          state.backStack.push(captureLocation(context.sourceRow, context.sourceColumn));
          state.forwardStack = [];
          updateRelationHistoryUI();
          goToLocation({
            bookId: reference.bookId,
            row: reference.row,
            column: reference.column
          });
        });
        section.appendChild(button);
      });
      fragment.appendChild(section);
    });
    el.reverseReferenceList.appendChild(fragment);
    el.reverseReferencePanel.style.left =
      `${Math.min(context.anchor.clientX + 10, window.innerWidth - 494)}px`;
    el.reverseReferencePanel.style.top =
      `${Math.min(context.anchor.clientY + 10, window.innerHeight - 504)}px`;
    el.reverseReferencePanel.classList.remove("hidden");
  }

  function handleReverseReferenceClick(event, book, rowIndex, columnIndex, value) {
    if (!event.altKey) return;
    event.preventDefault();
    event.stopPropagation();
    if (state.changes.size) {
      showToast("请先保存或放弃当前修改，再查询反向引用");
      return;
    }
    const requestId = ++state.reverseLookupRequestId;
    const plan = reverseQueryPlan(book);
    state.pendingReverseLookup = {
      requestId,
      targetBookId: book.id,
      sourceRow: rowIndex,
      sourceColumn: columnIndex,
      value: String(value),
      anchor: { clientX: event.clientX, clientY: event.clientY }
    };
    closeRelationChooser();
    closeReverseReferencePanel();
    document.body.classList.add("reverse-lookup-active");
    showToast(`正在查找 ID ${value} 的引用…`);
    send("findReverseReferences", {
      requestId,
      value: String(value),
      targetTokens: plan.targetTokens,
      scalarFields: plan.scalarFields,
      jsonFields: plan.jsonFields
    });
  }

  async function handleRelationClick(event, book, rowIndex, columnIndex, value, columnRule) {
    if (event.altKey || !(event.metaKey || event.ctrlKey)) return;
    event.preventDefault();
    event.stopPropagation();
    cancelPendingReverseLookup();
    const destinations = relationDestinations(book, rowIndex, columnIndex, value, columnRule);
    if (!destinations.length) {
      showToast("没有找到这个值对应的配置");
      return;
    }
    const generation = ++state.relationLookupGeneration;
    const directory = state.payload?.directory;
    const anchor = { clientX: event.clientX, clientY: event.clientY };
    closeRelationChooser();
    document.body.classList.add("relation-lookup-active");
    showToast("正在查找真实关联位置…");
    try {
      const matches = await validateRelationDestinations(destinations, generation, directory);
      if (generation !== state.relationLookupGeneration || directory !== state.payload?.directory) return;
      if (!matches.length) {
        const values = [...new Set(destinations.map((destination) => destination.value))];
        const label = destinations[0]?.label || "关联配置";
        showToast(values.length === 1
          ? `${label} ID ${values[0]} 不存在`
          : `没有找到关联配置：${values.join("、")}`);
        return;
      }
      if (matches.length === 1) {
        jumpToRelation(matches[0], rowIndex, columnIndex);
      } else {
        showRelationChooser(matches, rowIndex, columnIndex, anchor);
      }
    } finally {
      if (generation === state.relationLookupGeneration) {
        document.body.classList.remove("relation-lookup-active");
      }
    }
  }

  function applyPendingLocation(book) {
    const location = state.pendingLocation;
    if (!location || location.bookId !== book.id) return;
    state.pendingLocation = null;

    if (Number.isInteger(location.row) && Number.isInteger(location.column)) {
      const cell = el.configTable.querySelector(
        `td[data-row="${location.row}"][data-column="${location.column}"]`
      );
      if (cell) {
        cell.classList.add("relation-jump-cell");
        cell.scrollIntoView({ block: "center", inline: "center", behavior: "smooth" });
      } else {
        el.tableViewport.scrollTop = location.scrollTop || 0;
        el.tableViewport.scrollLeft = location.scrollLeft || 0;
      }
      return;
    }

    if (!location.key) {
      el.tableViewport.scrollTop = location.scrollTop || 0;
      el.tableViewport.scrollLeft = location.scrollLeft || 0;
      return;
    }
    const headers = book.rows[1] || [];
    const expected = comparableRelationValue(location.value);
    const keyCandidates = String(location.key).split("|");
    let keyColumn = -1;
    let resolvedKey = "";
    let matchingRows = [];
    for (const keyCandidate of keyCandidates) {
      const candidateColumn = headers.findIndex((header) =>
        String(header).trim().toLowerCase() === keyCandidate.toLowerCase()
      );
      if (candidateColumn < 0) continue;
      const candidateRows = [];
      for (let rowIndex = 3; rowIndex < book.rows.length; rowIndex += 1) {
        if (comparableRelationValue(book.rows[rowIndex]?.[candidateColumn]) === expected) {
          candidateRows.push(rowIndex);
        }
      }
      if (candidateRows.length) {
        keyColumn = candidateColumn;
        resolvedKey = keyCandidate;
        matchingRows = candidateRows;
        break;
      }
    }
    if (keyColumn < 0) {
      showToast(`没有找到 ${keyCandidates.join(" / ")} = ${location.value}`);
      return;
    }
    if (!matchingRows.length) {
      showToast(`没有找到 ${location.key} = ${location.value}`);
      return;
    }
    matchingRows.forEach((rowIndex) => {
      el.configTable.querySelector(`td[data-row="${rowIndex}"]`)?.parentElement.classList.add("relation-jump-row");
    });
    const targetCell = el.configTable.querySelector(
      `td[data-row="${matchingRows[0]}"][data-column="${keyColumn}"]`
    );
    targetCell?.classList.add("relation-jump-cell");
    targetCell?.scrollIntoView({ block: "center", inline: "center", behavior: "smooth" });
    showToast(`已定位 ${matchingRows.length} 条 ${location.relationLabel || "关联配置"}（${resolvedKey}）`);
  }

  function renderTable() {
    const book = activeWorkbook();
    if (!book) return;
    el.categoryLabel.textContent = book.category;
    el.sheetLabel.textContent = book.sheetName || "读取异常";
    el.workbookTitle.textContent = book.name;
    el.statRows.textContent = Math.max(0, book.rowCount - 3).toLocaleString("zh-CN");
    el.statColumns.textContent = book.columnCount.toLocaleString("zh-CN");
    el.statModified.textContent = formatDate(book.modifiedAt);
    state.matchNodes = [];
    if (state.searchScope === "global") {
      updateGlobalSearchNavigation();
    } else {
      el.matchCount.textContent = "";
      el.previousMatchButton.disabled = true;
      el.nextMatchButton.disabled = true;
    }
    updateEditUI();

    if (!book.isLoaded && !book.error) {
      el.tableViewport.classList.add("hidden");
      el.errorState.classList.add("hidden");
      el.emptyState.classList.remove("hidden");
      if (!state.loadingIds.has(book.id)) {
        state.loadingIds.add(book.id);
        send("loadWorkbook", { id: book.id });
      }
      return;
    }

    if (book.error) {
      el.emptyState.classList.add("hidden");
      el.tableViewport.classList.add("hidden");
      el.errorState.classList.remove("hidden");
      el.errorMessage.textContent = `${book.fileName}：${book.error}`;
      return;
    }

    el.emptyState.classList.add("hidden");
    el.errorState.classList.add("hidden");
    el.tableViewport.classList.remove("hidden");
    const query = state.query;
    let matches = 0;
    const fragment = document.createDocumentFragment();
    const body = document.createElement("tbody");
    const lockedCells = new Set(book.lockedCells || []);
    const columnRelations = Array.from(
      { length: book.columnCount },
      (_, columnIndex) => hierarchyRuleForColumn(book, columnIndex) || relationRuleForColumn(book, columnIndex)
    );

    book.rows.forEach((row, rowIndex) => {
      const tr = document.createElement("tr");
      let rowHasMatch = false;
      const indexCell = document.createElement("td");
      indexCell.className = "row-index";
      indexCell.textContent = String(rowIndex + 1);
      tr.appendChild(indexCell);

      for (let columnIndex = 0; columnIndex < book.columnCount; columnIndex += 1) {
        const value = String(row[columnIndex] ?? "");
        const td = document.createElement("td");
        td.textContent = value;
        td.title = value;
        td.dataset.row = String(rowIndex);
        td.dataset.column = String(columnIndex);
        const cellKey = `${rowIndex}:${columnIndex}`;
        const editable = state.editing &&
          rowIndex >= (book.editableFromRow || 0) &&
          !lockedCells.has(cellKey);
        if (editable) {
          td.contentEditable = "true";
          td.spellcheck = false;
          td.classList.add("editable-cell");
          if (state.changes.has(cellKey)) td.classList.add("dirty-cell");
          td.addEventListener("input", () => {
            const nextValue = td.innerText.replace(/\r/g, "");
            const existing = state.changes.get(cellKey);
            const original = existing?.original ?? value;
            book.rows[rowIndex][columnIndex] = nextValue;
            if (nextValue === original) {
              state.changes.delete(cellKey);
              td.classList.remove("dirty-cell");
            } else {
              state.changes.set(cellKey, { row: rowIndex, column: columnIndex, value: nextValue, original });
              td.classList.add("dirty-cell");
            }
            td.title = nextValue;
            updateEditUI();
          });
        } else if (state.editing) {
          td.classList.add("locked-cell");
        }
        if (rowIndex >= 3 && value) {
          const relationRule =
            activitySubIdRule(book, rowIndex, columnIndex) ||
            columnRelations[columnIndex];
          const destinations = relationDestinations(book, rowIndex, columnIndex, value, relationRule);
          if (destinations.length) {
            td.classList.add("relation-cell");
            td.addEventListener("mousedown", (event) => {
              if (event.metaKey || event.ctrlKey) event.preventDefault();
            });
            td.addEventListener("click", (event) => {
              handleRelationClick(event, book, rowIndex, columnIndex, td.innerText, relationRule);
            });
          }
          const field = String(book.rows[1]?.[columnIndex] || "").trim().toLowerCase();
          if (["id", "subid"].includes(field)) {
            td.classList.add("reverse-reference-cell");
            td.addEventListener("mousedown", (event) => {
              if (event.altKey) event.preventDefault();
            });
            td.addEventListener("click", (event) => {
              handleReverseReferenceClick(
                event,
                book,
                rowIndex,
                columnIndex,
                td.innerText
              );
            });
          }
        }
        if (!value) td.classList.add("empty");
        if (query && value.toLowerCase().includes(query)) {
          td.classList.add("match");
          matches += 1;
          rowHasMatch = true;
        }
        tr.appendChild(td);
      }
      if (rowHasMatch) tr.classList.add("target-row");
      body.appendChild(tr);
    });
    fragment.appendChild(body);
    el.configTable.replaceChildren(fragment);
    el.configTable.classList.toggle("editing", state.editing);
    state.matchNodes = query ? [...el.configTable.querySelectorAll(".match")] : [];
    if (!state.matchNodes.length) {
      state.matchIndex = -1;
    } else {
      state.matchIndex = Math.min(Math.max(0, state.matchIndex), state.matchNodes.length - 1);
    }
    requestAnimationFrame(() => {
      updateStickyHeaderOffsets();
      updateMatchNavigation({ scroll: Boolean(query) });
      applyPendingLocation(book);
    });
  }

  function navigateMatch(delta) {
    if (!state.matchNodes.length) return;
    state.matchIndex = (state.matchIndex + delta + state.matchNodes.length) % state.matchNodes.length;
    updateMatchNavigation({ scroll: true });
  }

  function updateMatchNavigation({ scroll = false } = {}) {
    if (state.searchScope === "global") {
      updateGlobalSearchNavigation();
      return;
    }
    state.matchNodes.forEach((node) => node.classList.remove("active-match"));
    const hasMatches = state.matchNodes.length > 0;
    el.previousMatchButton.disabled = !hasMatches;
    el.nextMatchButton.disabled = !hasMatches;

    if (!state.query) {
      el.matchCount.textContent = "";
      return;
    }
    if (!hasMatches) {
      el.matchCount.textContent = "无匹配";
      return;
    }

    const active = state.matchNodes[state.matchIndex];
    active?.classList.add("active-match");
    el.matchCount.textContent = `${state.matchIndex + 1} / ${state.matchNodes.length}`;
    if (scroll) active?.scrollIntoView({ block: "center", inline: "center", behavior: "smooth" });
  }

  function updateStickyHeaderOffsets() {
    const rows = el.configTable.querySelectorAll("tr");
    const firstHeight = rows[0]?.getBoundingClientRect().height || 32;
    const secondHeight = rows[1]?.getBoundingClientRect().height || 32;
    el.configTable.style.setProperty("--header-row-1-height", `${firstHeight}px`);
    el.configTable.style.setProperty("--header-row-2-height", `${secondHeight}px`);
  }

  function formatDate(value) {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return "—";
    return new Intl.DateTimeFormat("zh-CN", {
      month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit"
    }).format(date);
  }

  function escapeHTML(value) {
    return String(value).replace(/[&<>"']/g, (character) => ({
      "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#039;"
    })[character]);
  }

  function showToast(message) {
    el.toast.textContent = message;
    el.toast.classList.add("show");
    clearTimeout(showToast.timer);
    showToast.timer = setTimeout(() => el.toast.classList.remove("show"), 1800);
  }

  window.ConfigTool = {
    receiveData(payload) {
      if (state.changes.size && !state.saving) {
        showToast("源文件已变化，请先保存或退出编辑后刷新");
        return;
      }
      cancelRelationLookups("配置已刷新");
      resetGlobalSearch();
      const wasLoaded = Boolean(state.payload);
      const previousDirectory = state.payload?.directory;
      if (previousDirectory && previousDirectory !== payload.directory) {
        state.pendingLocation = null;
        state.backStack = [];
        state.forwardStack = [];
      }
      state.payload = payload;
      state.loadingIds.clear();
      state.loading = false;
      state.saving = false;
      el.refreshButton.classList.remove("loading");
      render();
      if (state.searchScope === "global" && state.globalQuery) scheduleGlobalSearch();
      if (!sessionStorage.getItem("configToolStartupPresetApplied")) {
        sessionStorage.setItem("configToolStartupPresetApplied", "1");
        const startupPreset = state.presets.find((preset) => preset.id === state.defaultPresetId);
        if (startupPreset && startupPreset.path !== payload.directory) {
          send("switchDirectory", { path: startupPreset.path });
        }
      }
      if (wasLoaded) showToast("已读取最新配置");
    },
    receiveWorkbook(workbook) {
      const index = state.payload?.workbooks?.findIndex((item) => item.id === workbook.id) ?? -1;
      state.loadingIds.delete(workbook.id);
      if (index >= 0) state.payload.workbooks[index] = workbook;
      settleWorkbookLoad(workbook.id, workbook);
      if (workbook.id === state.selectedId) renderTable();
    },
    receiveWorkbookError(error) {
      state.loadingIds.delete(error.id);
      settleWorkbookLoad(error.id, null, new Error(error.message || "配置加载失败"));
      if (error.id === state.selectedId) {
        el.emptyState.classList.add("hidden");
        el.tableViewport.classList.add("hidden");
        el.errorState.classList.remove("hidden");
        el.errorMessage.textContent = error.message || "配置加载失败";
      }
    },
    receiveReverseReferences(response) {
      const context = state.pendingReverseLookup;
      if (!context || response.requestId !== context.requestId) return;
      state.pendingReverseLookup = null;
      document.body.classList.remove("reverse-lookup-active");
      const targetBook = state.payload?.workbooks.find((book) => book.id === context.targetBookId);
      if (!targetBook) {
        showToast("目标配置表已变化，请重新查询");
        return;
      }
      const references = (response.references || [])
        .filter((reference) => reverseReferenceTargetsBook(reference, targetBook))
        .filter((reference, index, all) =>
          all.findIndex((candidate) =>
            candidate.bookId === reference.bookId &&
            candidate.row === reference.row &&
            candidate.column === reference.column
          ) === index
        );
      if (!references.length) {
        showToast(`没有配置引用 ID ${context.value}`);
        return;
      }
      showReverseReferencePanel(references, context);
    },
    receiveReverseReferenceError(error) {
      const context = state.pendingReverseLookup;
      if (!context || error.requestId !== context.requestId) return;
      state.pendingReverseLookup = null;
      document.body.classList.remove("reverse-lookup-active");
      showToast(error.message || "反向引用查询失败");
    },
    receiveGlobalSearchResults(response) {
      if (
        state.searchScope !== "global" ||
        response.requestId !== state.pendingGlobalSearchRequestId ||
        response.query !== state.globalQuery
      ) return;
      state.pendingGlobalSearchRequestId = null;
      state.globalMatches = Array.isArray(response.matches) ? response.matches : [];
      state.globalTotalCount = Number(response.totalCount) || state.globalMatches.length;
      state.globalMatchIndex = -1;
      updateGlobalSearchNavigation();
      if (state.globalMatches.length) showGlobalSearchPanel();
      else closeGlobalSearchPanel();
    },
    receiveGlobalSearchError(error) {
      if (error.requestId !== state.pendingGlobalSearchRequestId) return;
      state.pendingGlobalSearchRequestId = null;
      state.globalMatches = [];
      state.globalTotalCount = 0;
      state.globalMatchIndex = -1;
      updateGlobalSearchNavigation();
      closeGlobalSearchPanel();
      showToast(error.message || "全局搜索失败");
    },
    receiveError(error) {
      state.loading = false;
      el.refreshButton.classList.remove("loading");
      el.emptyState.classList.add("hidden");
      el.tableViewport.classList.add("hidden");
      el.errorState.classList.remove("hidden");
      el.errorMessage.textContent = error.message || "未知错误";
      el.statusDot.className = "status-dot error";
      el.statusText.textContent = "读取失败";
    },
    setLoading({ loading }) {
      state.loading = loading;
      el.refreshButton.classList.toggle("loading", loading);
      if (loading) {
        el.statusDot.className = "status-dot";
        el.statusText.textContent = "正在读取最新配置…";
      }
    },
    setSaving({ saving }) {
      state.saving = saving;
      updateEditUI();
    },
    saveSucceeded() {
      state.changes.clear();
      showToast("保存成功，正在重新读取");
      updateEditUI();
    },
    saveFailed(error) {
      state.saving = false;
      updateEditUI();
      showToast(error.message || "保存失败");
    },
    directorySwitchFailed(error) {
      showToast(error.message || "目录切换失败");
    }
  };

  bindElements();
  bindEvents();
  setSearchScope("current");
  updateEditUI();
  window.addEventListener("resize", () => requestAnimationFrame(() => {
    updateStickyHeaderOffsets();
    if (!el.globalSearchPanel.classList.contains("hidden")) showGlobalSearchPanel();
  }));
  send("ready");
})();
