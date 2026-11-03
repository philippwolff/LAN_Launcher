"use strict";
const byId = id => document.getElementById(id);
let currentGames = [];
let editingId = null;
const format = bytes => (bytes / 1073741824).toFixed(1) + " GiB";
function table(id, items, fields) {
  const target = byId(id);
  target.replaceChildren();
  if (!items.length) {
    const row = target.insertRow();
    const cell = row.insertCell();
    cell.colSpan = fields.length;
    cell.textContent = "Keine Einträge";
    return;
  }
  for (const item of items) {
    const row = target.insertRow();
    for (const field of fields) row.insertCell().textContent = String(item[field] ?? "–");
  }
}
function renderGames(games) {
  const target = byId("games");
  target.replaceChildren();
  if (!games.length) {
    const row = target.insertRow();
    const cell = row.insertCell();
    cell.colSpan = 3;
    cell.textContent = "Keine Einträge";
    return;
  }
  for (const game of games) {
    const row = target.insertRow();
    row.insertCell().textContent = game.name ?? "–";
    row.insertCell().textContent = game.version ?? "–";
    const actions = row.insertCell();
    actions.className = "manage-actions";
    const edit = document.createElement("button");
    edit.textContent = "Bearbeiten";
    edit.addEventListener("click", () => {
      editingId = game.id;
      byId("editName").value = game.name ?? "";
      byId("editVersion").value = game.version ?? "";
      byId("editDescription").value = game.description ?? "";
      byId("editor").hidden = false;
      byId("editor").scrollIntoView({ behavior: "smooth", block: "nearest" });
    });
    const remove = document.createElement("button");
    remove.textContent = "Entfernen";
    remove.className = "danger";
    remove.addEventListener("click", async () => {
      if (!confirm('Spiel "' + game.name + '" aus der Bibliothek entfernen? Die Paketdatei bleibt auf der NAS erhalten.')) return;
      try {
        await adminRequest("/api/admin/games/" + encodeURIComponent(game.id), "DELETE");
        if (editingId === game.id) closeEditor();
        await refresh();
        byId("message").textContent = "Spiel aus dem Katalog entfernt. NAS-Datei wurde nicht gelöscht.";
      } catch (error) { byId("message").textContent = error.message; }
    });
    actions.append(edit, remove);
  }
}
function closeEditor() {
  editingId = null;
  byId("editor").hidden = true;
}
async function adminRequest(path, method, body) {
  const key = byId("key").value;
  if (!key) throw new Error("Bitte Admin-Schlüssel eingeben.");
  const response = await fetch(path, {
    method,
    headers: { "X-Admin-Key": key, ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    cache: "no-store"
  });
  if (!response.ok) {
    const data = await response.json().catch(() => ({}));
    throw new Error(data.error || "Serverfehler HTTP " + response.status);
  }
  return response.json();
}
byId("editCancel").addEventListener("click", closeEditor);
byId("editForm").addEventListener("submit", async event => {
  event.preventDefault();
  if (!editingId) return;
  try {
    await adminRequest("/api/admin/games/" + encodeURIComponent(editingId) + "/metadata", "PUT", {
      name: byId("editName").value,
      version: byId("editVersion").value,
      description: byId("editDescription").value
    });
    closeEditor();
    await refresh();
    byId("message").textContent = "Spieldaten gespeichert.";
  } catch (error) { byId("message").textContent = error.message; }
});
async function refresh() {
  const key = byId("key").value;
  if (!key) { byId("message").textContent = "Bitte Admin-Schlüssel eingeben."; return; }
  byId("message").textContent = "Daten werden geladen …";
  try {
    const response = await fetch("/api/admin/dashboard", {
      headers: { "X-Admin-Key": key }, cache: "no-store"
    });
    if (!response.ok) throw new Error(response.status === 401 ?
      "Admin-Schlüssel ungültig." : "Serverfehler HTTP " + response.status);
    const data = await response.json();
    byId("status").textContent = data.status;
    byId("status").className = "value green";
    byId("server").textContent = data.serverType + " · Port " + data.port;
    byId("storage").textContent = format(data.storage.free) + " frei";
    byId("storageDetail").textContent = format(data.storage.used) + " belegt von " +
      format(data.storage.total) + " · " + data.storage.path;
    byId("clients").textContent = data.players.length;
    byId("count").textContent = data.games.length;
    table("players", data.players, ["name", "status"]);
    currentGames = data.games;
    renderGames(currentGames);
    byId("message").textContent = "Daten aktualisiert.";
  } catch (error) { byId("message").textContent = error.message; }
}
byId("refresh").addEventListener("click", refresh);
byId("key").addEventListener("keydown", event => { if (event.key === "Enter") refresh(); });
