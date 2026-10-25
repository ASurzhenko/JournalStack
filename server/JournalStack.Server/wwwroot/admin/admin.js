"use strict";

const messages = {
    invalid_text: "The text must be 1 to 2000 characters and have no control characters.",
    invalid_status: "The status must be draft or published.",
    login_locked: "Too many login attempts. Try again in a minute.",
    invalid_credentials: "The password is not right.",
    csrf_header: "The server refused the request header.",
    admin_not_configured: "Admin login is not set up on this server.",
    tip_not_found: "That tip no longer exists. Press New and save again.",
    database_unavailable: "The database is unavailable. Try again later.",
    server_error: "Something went wrong on the server.",
    payload_too_large: "The request is too large.",
    invalid_json: "The server could not read the request.",
};

const byId = (id) => document.getElementById(id);
let editingId = null;

// Returns { status, data }. A network error is status 0.
async function api(method, path, body) {
    try {
        const response = await fetch("/admin/api" + path, {
            method,
            credentials: "same-origin",
            headers: { "Content-Type": "application/json", "X-Requested-With": "journalstack" },
            body: body === undefined ? undefined : JSON.stringify(body),
        });
        let data = null;
        try {
            data = await response.json();
        } catch {
            data = null;
        }
        return { status: response.status, data };
    } catch {
        return { status: 0, data: null };
    }
}

function showView(name) {
    byId("login-view").hidden = name !== "login";
    byId("app-view").hidden = name !== "app";
}

function setMessage(text) {
    byId("message").textContent = text;
    byId("message").hidden = text === "";
}

// Only a 401 with the code "unauthorized" shows the login form, so an outage never looks like a sign-out.
// The editor text is left alone, so nothing typed is lost.
function fail(result) {
    const code = result.data && result.data.code;
    if (result.status === 401 && code === "unauthorized") {
        showView("login");
        setMessage("Sign in to continue.");
    } else if (messages[code]) {
        setMessage(messages[code]);
    } else if (result.status === 0 || result.status >= 500) {
        setMessage("Server unavailable, try again.");
    } else {
        setMessage("The request was refused (" + (code || result.status) + ").");
    }
}

// Entry times can be extreme, so the text is rearranged and never parsed into a Date.
function formatTime(text) {
    const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2})(\.\d+)?(Z?)$/.exec(String(text));
    if (!match) {
        return String(text);
    }
    return match[1] + " " + match[2] + (match[4] ? " UTC" : "");
}

function cell(row, text, className) {
    const td = document.createElement("td");
    td.textContent = text;
    if (className) {
        td.className = className;
    }
    row.appendChild(td);
    return td;
}

// A button is disabled while its request runs, so a double click cannot send twice.
async function guarded(button, work) {
    button.disabled = true;
    try {
        await work();
    } finally {
        button.disabled = false;
    }
}

function resetEditor() {
    editingId = null;
    byId("editor-title").textContent = "New tip";
    byId("tip-text").value = "";
    byId("tip-status").value = "draft";
}

function editTip(tip) {
    editingId = tip.id;
    byId("editor-title").textContent = "Edit tip";
    byId("tip-text").value = tip.text;
    byId("tip-status").value = tip.status;
}

function drawTips(tips) {
    const rows = tips.map((tip) => {
        const row = document.createElement("tr");
        cell(row, tip.status);
        cell(row, tip.text, "text");
        cell(row, formatTime(tip.updatedAt));
        const edit = document.createElement("button");
        edit.type = "button";
        edit.textContent = "Edit";
        edit.addEventListener("click", () => editTip(tip));
        row.appendChild(document.createElement("td")).appendChild(edit);
        return row;
    });
    // The app shows the first published row, because the list is newest first.
    const shown = tips.findIndex((tip) => tip.status === "published");
    if (shown >= 0) {
        const marker = document.createElement("div");
        marker.className = "marker";
        marker.textContent = "Shown in the app";
        rows[shown].children[1].appendChild(marker);
    }
    byId("tips-body").replaceChildren(...rows);
}

function drawEntries(entries) {
    const rows = entries.map((entry) => {
        const row = document.createElement("tr");
        cell(row, formatTime(entry.receivedAt));
        cell(row, entry.deviceId.slice(0, 8));
        cell(row, formatTime(entry.clientCreatedAt));
        cell(row, entry.text, "text");
        return row;
    });
    byId("entries-body").replaceChildren(...rows);
}

async function loadTips() {
    const result = await api("GET", "/tips");
    if (result.status !== 200) {
        fail(result);
        return false;
    }
    showView("app");
    drawTips(result.data.tips);
    return true;
}

async function loadEntries() {
    const result = await api("GET", "/entries");
    if (result.status !== 200) {
        fail(result);
        return;
    }
    drawEntries(result.data.entries);
}

async function loadAll() {
    setMessage("");
    if (await loadTips()) {
        await loadEntries();
    }
}

async function login(event) {
    event.preventDefault();
    await guarded(byId("login-button"), async () => {
        const result = await api("POST", "/login", { password: byId("password").value });
        if (result.status !== 204) {
            fail(result);
            return;
        }
        byId("password").value = "";
        await loadAll();
    });
}

async function logout() {
    const result = await api("POST", "/logout");
    if (result.status !== 204) {
        fail(result);
        return;
    }
    byId("tips-body").replaceChildren();
    byId("entries-body").replaceChildren();
    resetEditor();
    showView("login");
    setMessage("");
}

async function save() {
    await guarded(byId("save-button"), async () => {
        const body = { text: byId("tip-text").value, status: byId("tip-status").value };
        const result = editingId === null
            ? await api("POST", "/tips", body)
            : await api("PUT", "/tips/" + editingId, body);
        if (result.status !== 200 && result.status !== 201) {
            fail(result);
            return;
        }
        setMessage("");
        resetEditor();
        await loadTips();
    });
}

byId("login-form").addEventListener("submit", login);
byId("logout-button").addEventListener("click", logout);
byId("save-button").addEventListener("click", save);
byId("new-button").addEventListener("click", resetEditor);
byId("refresh-button").addEventListener("click", () => guarded(byId("refresh-button"), loadEntries));
loadAll();
