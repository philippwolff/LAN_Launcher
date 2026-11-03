"use strict";
const byId = id => document.getElementById(id);
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
    table("games", data.games, ["name", "version"]);
    byId("message").textContent = "Daten aktualisiert.";
  } catch (error) { byId("message").textContent = error.message; }
}
byId("refresh").addEventListener("click", refresh);
byId("key").addEventListener("keydown", event => { if (event.key === "Enter") refresh(); });
