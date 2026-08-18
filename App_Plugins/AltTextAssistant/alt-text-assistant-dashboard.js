import { UmbElementMixin } from "@umbraco-cms/backoffice/element-api";
import { UMB_NOTIFICATION_CONTEXT } from "@umbraco-cms/backoffice/notification";
import { UMB_AUTH_CONTEXT } from "@umbraco-cms/backoffice/auth";

export default class AltTextAssistantDashboard extends UmbElementMixin(HTMLElement) {
    #notificationContext;
    #authToken;
    #currentPage = 1;
    #pageSize = 25;
    #totalPages = 0;
    #totalItems = 0;
    #editingMediaId = null;
    #aiEnabled = false;
    #bulkActive = false;
    #bulkAbort = null;

    static API_BASE = "/umbraco/management/api/v1/alt-text-assistant";

    constructor() {
        super();
        this.attachShadow({ mode: "open" });
        this.#loadStyles();
        this.#loadTemplate();
        this.#initContexts();
    }

    #initContexts() {
        this.consumeContext(UMB_NOTIFICATION_CONTEXT, (instance) => {
            this.#notificationContext = instance;
        });
        this.consumeContext(UMB_AUTH_CONTEXT, (instance) => {
            this.#authToken = instance;
        });
    }

    #loadStyles() {
        const link = document.createElement("link");
        link.setAttribute("rel", "stylesheet");
        link.setAttribute("href", "/App_Plugins/AltTextAssistant/alt-text-assistant-style.css?v=PACKAGE_VERSION");
        this.shadowRoot.appendChild(link);
    }

    async #loadTemplate() {
        try {
            const response = await fetch("/App_Plugins/AltTextAssistant/alt-text-assistant-template.html?v=PACKAGE_VERSION");
            const html = await response.text();
            const container = document.createElement("div");
            container.innerHTML = html;
            while (container.firstChild) {
                this.shadowRoot.appendChild(container.firstChild);
            }
            requestAnimationFrame(() => {
                this.#bindEvents();
                this.#checkAiConfig();
                this.#loadImages();
            });
        } catch (err) {
            console.error("Failed to load Missing Alt Text template", err);
            this.shadowRoot.innerHTML = `<p style="padding:20px;color:red;">Failed to load dashboard template.</p>`;
        }
    }

    #bindEvents() {
        this.shadowRoot.getElementById("alt-refresh-btn")?.addEventListener("click", () => {
            this.#currentPage = 1;
            this.#loadImages();
        });
        this.shadowRoot.getElementById("alt-error-retry")?.addEventListener("click", () => this.#loadImages());
        this.shadowRoot.getElementById("alt-prev-btn")?.addEventListener("click", () => {
            if (this.#currentPage > 1) {
                this.#currentPage--;
                this.#loadImages();
            }
        });
        this.shadowRoot.getElementById("alt-next-btn")?.addEventListener("click", () => {
            if (this.#currentPage < this.#totalPages) {
                this.#currentPage++;
                this.#loadImages();
            }
        });
        this.shadowRoot.getElementById("alt-modal-cancel")?.addEventListener("click", () => this.#closeModal());
        this.shadowRoot.getElementById("alt-modal-save")?.addEventListener("click", () => this.#saveAltText());
        this.shadowRoot.getElementById("alt-modal-suggest")?.addEventListener("click", () => this.#suggestAltText());

        this.shadowRoot.getElementById("alt-bulk-suggest-btn")?.addEventListener("click", () => this.#startBulkSuggest());
        this.shadowRoot.getElementById("alt-bulk-save")?.addEventListener("click", () => this.#saveBulkSuggestions());
        this.shadowRoot.getElementById("alt-bulk-cancel")?.addEventListener("click", () => this.#cancelBulkSuggest());

        // Close modal on overlay click
        this.shadowRoot.getElementById("alt-modal")?.addEventListener("click", (e) => {
            if (e.target.id === "alt-modal") this.#closeModal();
        });
    }

    async #getAuthHeaders() {
        const headers = { "Content-Type": "application/json" };
        try {
            if (this.#authToken) {
                const config = this.#authToken.getOpenApiConfiguration();
                if (config?.token) {
                    const token = await config.token();
                    if (token) headers["Authorization"] = `Bearer ${token}`;
                }
            }
        } catch { /* proceed without auth header */ }
        return headers;
    }

    async #checkAiConfig() {
        let altFieldExists = true;
        let altFieldAlias = "umbracoAltText";
        try {
            const headers = await this.#getAuthHeaders();
            const response = await fetch(`${AltTextAssistantDashboard.API_BASE}/config`, { headers });
            if (response.ok) {
                const data = await response.json();
                this.#aiEnabled = data.aiEnabled === true;
                if (data.altFieldExists === false) altFieldExists = false;
                if (data.altFieldAlias) altFieldAlias = data.altFieldAlias;
            }
        } catch { /* default to disabled */ }

        // Surface a clear warning if the Image media type has no alt-text property —
        // otherwise the dashboard would just list every image and saves would fail.
        const warning = this.shadowRoot.getElementById("alt-field-warning");
        const warningMsg = this.shadowRoot.getElementById("alt-field-warning-msg");
        if (warning) {
            if (altFieldExists) {
                warning.style.display = "none";
            } else {
                warning.style.display = "";
                if (warningMsg) {
                    warningMsg.textContent =
                        ` The Image media type has no "${altFieldAlias}" property, so alt text cannot be saved. ` +
                        `Add an alt text property to the Image media type, or set ` +
                        `AltTextAssistant:AltTextPropertyAlias in appsettings.json to the correct alias.`;
                }
            }
        }

        const suggestBtn = this.shadowRoot.getElementById("alt-modal-suggest");
        const aiNotice = this.shadowRoot.getElementById("alt-ai-notice");
        const bulkBtn = this.shadowRoot.getElementById("alt-bulk-suggest-btn");

        if (this.#aiEnabled) {
            if (suggestBtn) suggestBtn.style.display = "";
            if (aiNotice) aiNotice.style.display = "none";
            if (bulkBtn) bulkBtn.style.display = "";
        } else {
            if (suggestBtn) suggestBtn.style.display = "none";
            if (aiNotice) aiNotice.style.display = "";
            if (bulkBtn) bulkBtn.style.display = "none";
        }
    }

    async #loadImages() {
        const loading = this.shadowRoot.getElementById("alt-loading");
        const tableWrap = this.shadowRoot.getElementById("alt-table-wrap");
        const emptyState = this.shadowRoot.getElementById("alt-empty");
        const errorState = this.shadowRoot.getElementById("alt-error");

        loading.classList.add("active");
        tableWrap.style.display = "none";
        emptyState.classList.remove("active");
        errorState.classList.remove("active");

        try {
            const headers = await this.#getAuthHeaders();
            const url = `${AltTextAssistantDashboard.API_BASE}/images?page=${this.#currentPage}&pageSize=${this.#pageSize}`;
            const response = await fetch(url, { headers });

            if (!response.ok) throw new Error(`HTTP ${response.status}`);

            const data = await response.json();
            this.#totalItems = data.totalItems;
            this.#totalPages = data.totalPages;

            loading.classList.remove("active");

            if (data.totalItems === 0) {
                emptyState.classList.add("active");
                this.#updateBadge(0);
                return;
            }

            this.#renderTable(data.items);
            this.#updatePagination();
            this.#updateBadge(data.totalItems);
            tableWrap.style.display = "block";
        } catch (err) {
            console.error("Failed to load images", err);
            loading.classList.remove("active");
            errorState.classList.add("active");
            this.shadowRoot.getElementById("alt-error-msg").textContent = `Error: ${err.message}`;
        }
    }

    #renderTable(items) {
        const table = this.shadowRoot.getElementById("alt-table");
        // Remove existing rows but keep the head
        table.querySelectorAll("uui-table-row").forEach((r) => r.remove());

        const openModal = (el) => {
            this.#openModal(
                parseInt(el.dataset.id, 10),
                el.dataset.name,
                el.dataset.src
            );
        };

        for (const item of items) {
            const row = document.createElement("uui-table-row");
            row.dataset.mediaId = item.id;

            const thumbSrc = item.src || "";

            row.innerHTML = `
                <uui-table-cell><img class="thumb js-thumb-click" src="${this.#escapeHtml(thumbSrc)}" alt="" loading="lazy" data-id="${item.id}" data-name="${this.#escapeAttr(item.name)}" data-src="${this.#escapeAttr(item.src || "")}" /></uui-table-cell>
                <uui-table-cell><span class="media-name">${this.#escapeHtml(item.name)}</span></uui-table-cell>
                <uui-table-cell>${this.#escapeHtml(item.createDate)}</uui-table-cell>
                <uui-table-cell class="suggestion-cell"></uui-table-cell>
                <uui-table-cell>
                    <div class="actions-cell">
                        <uui-button class="js-add-alt" look="primary" color="positive" label="Add Alt Text" data-id="${item.id}" data-name="${this.#escapeAttr(item.name)}" data-src="${this.#escapeAttr(item.src || "")}">Add Alt Text</uui-button>
                        <uui-button look="secondary" label="Edit in Media" href="/umbraco/section/media/workspace/media/edit/${item.key}" target="_blank">Edit in Media</uui-button>
                    </div>
                </uui-table-cell>
            `;
            table.appendChild(row);

            row.querySelectorAll(".js-add-alt").forEach((btn) => {
                btn.addEventListener("click", (e) => openModal(e.currentTarget));
            });
            row.querySelectorAll(".js-thumb-click").forEach((img) => {
                img.addEventListener("click", (e) => openModal(e.currentTarget));
            });
        }
    }

    #updatePagination() {
        const info = this.shadowRoot.getElementById("alt-page-info");
        const prevBtn = this.shadowRoot.getElementById("alt-prev-btn");
        const nextBtn = this.shadowRoot.getElementById("alt-next-btn");

        info.textContent = `Page ${this.#currentPage} of ${this.#totalPages} (${this.#totalItems} images)`;
        prevBtn.disabled = this.#currentPage <= 1;
        nextBtn.disabled = this.#currentPage >= this.#totalPages;
    }

    #updateBadge(count) {
        const badge = this.shadowRoot.getElementById("alt-count-badge");
        if (count > 0) {
            badge.textContent = `${count} missing`;
            badge.style.display = "inline-block";
        } else {
            badge.style.display = "none";
        }
    }

    #openModal(mediaId, name, src) {
        this.#editingMediaId = mediaId;
        const modal = this.shadowRoot.getElementById("alt-modal");
        const preview = this.shadowRoot.getElementById("alt-modal-preview");
        const input = this.shadowRoot.getElementById("alt-modal-input");
        const title = this.shadowRoot.getElementById("alt-modal-title");

        title.textContent = `Add Alt Text — ${name}`;
        preview.src = src || "";
        preview.style.display = src ? "block" : "none";
        input.value = "";
        modal.classList.add("active");

        requestAnimationFrame(() => input.focus());
    }

    #closeModal() {
        this.#editingMediaId = null;
        const modal = this.shadowRoot.getElementById("alt-modal");
        modal.classList.remove("active");
    }

    async #suggestAltText() {
        const suggestBtn = this.shadowRoot.getElementById("alt-modal-suggest");
        const input = this.shadowRoot.getElementById("alt-modal-input");

        suggestBtn.disabled = true;
        suggestBtn.textContent = "Thinking...";

        try {
            const headers = await this.#getAuthHeaders();
            const response = await fetch(`${AltTextAssistantDashboard.API_BASE}/suggest`, {
                method: "POST",
                headers,
                body: JSON.stringify({ mediaId: this.#editingMediaId }),
            });

            if (!response.ok) {
                const err = await response.json().catch(() => ({}));
                throw new Error(err.error || `HTTP ${response.status}`);
            }

            const data = await response.json();
            input.value = data.altText || "";
            input.focus();
        } catch (err) {
            console.error("Failed to suggest alt text", err);
            if (this.#notificationContext) {
                this.#notificationContext.peek("danger", {
                    data: { headline: "AI Suggestion Failed", message: err.message },
                });
            }
        } finally {
            suggestBtn.disabled = false;
            suggestBtn.textContent = "Suggest with AI";
        }
    }

    async #saveAltText() {
        const input = this.shadowRoot.getElementById("alt-modal-input");
        const altText = input.value.trim();

        if (!altText) {
            input.focus();
            return;
        }

        const saveBtn = this.shadowRoot.getElementById("alt-modal-save");
        saveBtn.disabled = true;
        saveBtn.textContent = "Saving...";

        try {
            const headers = await this.#getAuthHeaders();
            const response = await fetch(`${AltTextAssistantDashboard.API_BASE}/save`, {
                method: "POST",
                headers,
                body: JSON.stringify({ mediaId: this.#editingMediaId, altText }),
            });

            if (!response.ok) throw new Error(`HTTP ${response.status}`);

            // Remove row from table
            const row = this.shadowRoot.querySelector(`uui-table-row[data-media-id="${this.#editingMediaId}"]`);
            if (row) row.remove();

            this.#totalItems--;
            this.#updateBadge(this.#totalItems);

            this.#closeModal();

            if (this.#notificationContext) {
                this.#notificationContext.peek("positive", {
                    data: { headline: "Alt text saved", message: `Alt text has been set successfully.` },
                });
            }

            // If the table is now empty, reload to check for more or show empty state
            const remainingRows = this.shadowRoot.querySelectorAll("#alt-table uui-table-row");
            if (remainingRows.length === 0) {
                this.#loadImages();
            } else {
                this.#updatePagination();
            }
        } catch (err) {
            console.error("Failed to save alt text", err);
            if (this.#notificationContext) {
                this.#notificationContext.peek("danger", {
                    data: { headline: "Error", message: `Failed to save: ${err.message}` },
                });
            }
        } finally {
            saveBtn.disabled = false;
            saveBtn.textContent = "Save";
        }
    }

    async #startBulkSuggest() {
        const tableWrap = this.shadowRoot.getElementById("alt-table-wrap");
        const bulkBtn = this.shadowRoot.getElementById("alt-bulk-suggest-btn");
        const bulkActions = this.shadowRoot.getElementById("alt-bulk-actions");
        const bulkSaveBtn = this.shadowRoot.getElementById("alt-bulk-save");
        const progressEl = this.shadowRoot.getElementById("alt-bulk-progress");

        this.#bulkActive = true;
        this.#bulkAbort = new AbortController();
        tableWrap.classList.add("bulk-active");
        bulkBtn.disabled = true;
        bulkActions.style.display = "";
        bulkSaveBtn.disabled = true;

        const rows = Array.from(this.shadowRoot.querySelectorAll("#alt-table uui-table-row"));
        const total = rows.length;
        let completed = 0;

        progressEl.textContent = `Suggesting 0/${total}...`;

        // Throttled concurrency: max 2 at a time
        const queue = [...rows];
        const workers = [];
        const concurrency = 2;

        const processNext = async () => {
            while (queue.length > 0) {
                if (this.#bulkAbort?.signal.aborted) return;
                const row = queue.shift();
                const mediaId = parseInt(row.dataset.mediaId, 10);
                const cell = row.querySelector(".suggestion-cell");

                cell.innerHTML = `<span class="suggesting-text">Thinking...</span>`;

                try {
                    const headers = await this.#getAuthHeaders();
                    const response = await fetch(`${AltTextAssistantDashboard.API_BASE}/suggest`, {
                        method: "POST",
                        headers,
                        body: JSON.stringify({ mediaId }),
                        signal: this.#bulkAbort.signal,
                    });

                    if (!response.ok) throw new Error(`HTTP ${response.status}`);
                    const data = await response.json();
                    const text = data.altText || "";
                    cell.innerHTML = `<textarea class="bulk-suggestion-input" rows="2">${this.#escapeHtml(text)}</textarea>`;
                } catch (err) {
                    if (err.name === "AbortError") return;
                    console.error(`Bulk suggest failed for media ${mediaId}`, err);
                    cell.innerHTML = `<span class="suggestion-error">Failed</span><textarea class="bulk-suggestion-input" rows="2" placeholder="Type manually..."></textarea>`;
                }

                completed++;
                progressEl.textContent = completed < total
                    ? `Suggesting ${completed}/${total}...`
                    : `Done — review and save`;
            }
        };

        for (let i = 0; i < concurrency; i++) {
            workers.push(processNext());
        }

        await Promise.all(workers);

        if (!this.#bulkAbort?.signal.aborted) {
            bulkSaveBtn.disabled = false;
        }
    }

    async #saveBulkSuggestions() {
        const bulkSaveBtn = this.shadowRoot.getElementById("alt-bulk-save");
        const progressEl = this.shadowRoot.getElementById("alt-bulk-progress");

        const rows = Array.from(this.shadowRoot.querySelectorAll("#alt-table uui-table-row"));
        const toSave = [];
        for (const row of rows) {
            const textarea = row.querySelector(".bulk-suggestion-input");
            const text = textarea?.value?.trim();
            if (text) {
                toSave.push({ row, mediaId: parseInt(row.dataset.mediaId, 10), altText: text });
            }
        }

        if (toSave.length === 0) return;

        bulkSaveBtn.disabled = true;
        let saved = 0;
        progressEl.textContent = `Saving 0/${toSave.length}...`;

        for (const item of toSave) {
            try {
                const headers = await this.#getAuthHeaders();
                const response = await fetch(`${AltTextAssistantDashboard.API_BASE}/save`, {
                    method: "POST",
                    headers,
                    body: JSON.stringify({ mediaId: item.mediaId, altText: item.altText }),
                });

                if (!response.ok) throw new Error(`HTTP ${response.status}`);

                item.row.remove();
                this.#totalItems--;
                saved++;
                progressEl.textContent = `Saving ${saved}/${toSave.length}...`;
            } catch (err) {
                console.error(`Failed to save alt text for media ${item.mediaId}`, err);
                const cell = item.row.querySelector(".suggestion-cell");
                const errorEl = cell.querySelector(".suggestion-error");
                if (!errorEl) {
                    const span = document.createElement("span");
                    span.className = "suggestion-error";
                    span.textContent = "Save failed";
                    cell.prepend(span);
                }
            }
        }

        this.#updateBadge(this.#totalItems);

        if (this.#notificationContext) {
            this.#notificationContext.peek("positive", {
                data: { headline: "Bulk save complete", message: `${saved} alt text value${saved !== 1 ? "s" : ""} saved.` },
            });
        }

        // If table is now empty, reload
        const remaining = this.shadowRoot.querySelectorAll("#alt-table uui-table-row");
        if (remaining.length === 0) {
            this.#cancelBulkSuggest();
            this.#loadImages();
        } else {
            this.#cancelBulkSuggest();
            this.#updatePagination();
        }
    }

    #cancelBulkSuggest() {
        if (this.#bulkAbort) {
            this.#bulkAbort.abort();
            this.#bulkAbort = null;
        }
        this.#bulkActive = false;

        const tableWrap = this.shadowRoot.getElementById("alt-table-wrap");
        const bulkBtn = this.shadowRoot.getElementById("alt-bulk-suggest-btn");
        const bulkActions = this.shadowRoot.getElementById("alt-bulk-actions");

        tableWrap.classList.remove("bulk-active");
        bulkBtn.disabled = false;
        bulkActions.style.display = "none";

        // Clear suggestion cells
        this.shadowRoot.querySelectorAll(".suggestion-cell").forEach((cell) => {
            if (cell.closest("uui-table-head")) return;
            cell.innerHTML = "";
        });
    }

    #escapeHtml(str) {
        if (!str) return "";
        const div = document.createElement("div");
        div.textContent = str;
        return div.innerHTML;
    }

    #escapeAttr(str) {
        if (!str) return "";
        return str.replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/'/g, "&#39;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
    }
}

customElements.define("alt-text-assistant-dashboard", AltTextAssistantDashboard);
