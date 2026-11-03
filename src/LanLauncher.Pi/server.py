#!/usr/bin/env python3
"""ARMv6-compatible LAN Launcher metadata server. Python standard library only.

Game files are NEVER streamed by this process: Windows clients fetch them via SMB.
"""
import argparse
import html
import shutil
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import secrets
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote, urlsplit

DEFAULT_ROOT = Path("/mnt/lanlauncher-nas")
DEFAULT_SHARE = r"\\192.168.68.250\Daten"
GAME_ID = re.compile(r"^[A-Za-z0-9_-]{1,100}$")
PLAYERS = {}
LOCK = threading.RLock()


def read_json(path, default):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return default


def write_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + "." + secrets.token_hex(6) + ".tmp")
    try:
        temp.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.replace(temp, path)
    finally:
        temp.unlink(missing_ok=True)


def safe_package(root, relative):
    if not isinstance(relative, str) or "\\" in relative:
        raise ValueError("Paketpfad muss relativ sein und '/' verwenden.")
    parts = relative.split("/")
    if len(parts) < 2 or parts[0] not in ("Games", "Mods") or any(
        p in ("", ".", "..") or ":" in p for p in parts
    ):
        raise ValueError("Ungültiger Paketpfad.")
    path = root.joinpath(*parts).resolve()
    if not path.is_relative_to(root.resolve()) or path.suffix.lower() not in (".zip", ".exe"):
        raise ValueError("Ungültiger Paketpfad.")
    if not path.is_file():
        raise FileNotFoundError("Paket auf der NAS nicht gefunden.")
    return path


class Handler(BaseHTTPRequestHandler):
    server_version = "LANLauncherPi/0.1"

    def send_json(self, status, data):
        payload = json.dumps(data, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(payload)

    def send_html(self, page):
        data = page.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Security-Policy",
                         "default-src 'self'; style-src 'self' 'unsafe-inline'; "
                         "script-src 'self'; connect-src 'self'; object-src 'none'; "
                         "base-uri 'none'; frame-ancestors 'none'")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        self.wfile.write(data)

    def body(self):
        # .NET HttpClient may send JSON with Transfer-Encoding: chunked instead
        # of Content-Length. Decode it with strict size and framing limits.
        max_size = 65536
        transfer_encoding = self.headers.get("Transfer-Encoding", "").lower().strip()
        content_length = self.headers.get("Content-Length")
        if transfer_encoding:
            if transfer_encoding != "chunked" or content_length is not None:
                raise ValueError("Nicht unterstützte HTTP-Übertragung.")
            data = bytearray()
            while True:
                line = self.rfile.readline(128)
                if not line.endswith(b"\r\n"):
                    raise ValueError("Ungültige Chunk-Kodierung.")
                try:
                    size = int(line[:-2].split(b";", 1)[0], 16)
                except ValueError as exc:
                    raise ValueError("Ungültige Chunk-Größe.") from exc
                if size < 0 or len(data) + size > max_size:
                    raise ValueError("Ungültige Anfragegröße.")
                if size == 0:
                    # Consume trailer fields, bounded to avoid hanging on malformed input.
                    for _ in range(16):
                        trailer = self.rfile.readline(1024)
                        if trailer == b"\r\n":
                            break
                        if not trailer.endswith(b"\r\n"):
                            raise ValueError("Ungültiger Chunk-Abschluss.")
                    else:
                        raise ValueError("Zu viele HTTP-Trailer.")
                    break
                data.extend(self.rfile.read(size))
                if self.rfile.read(2) != b"\r\n":
                    raise ValueError("Ungültiger Chunk-Abschluss.")
            return json.loads(data)
        try:
            size = int(content_length) if content_length is not None else -1
        except ValueError as exc:
            raise ValueError("Ungültige Anfragegröße.") from exc
        if size < 0 or size > max_size:
            raise ValueError("Ungültige Anfragegröße.")
        return json.loads(self.rfile.read(size))

    def authorized(self):
        supplied = self.headers.get("X-Admin-Key", "")
        expected = self.server.admin_key
        return bool(expected) and hmac.compare_digest(
            hashlib.sha256(supplied.encode()).digest(), self.server.admin_digest
        )

    def games(self):
        return read_json(self.server.catalog_path, [])

    def game(self, game_id):
        return next((g for g in self.games() if g.get("id") == game_id), None)

    def do_GET(self):
        path = urlsplit(self.path).path
        if path == "/admin":
            page = Path(__file__).with_name("admin.html")
            if not page.is_file():
                return self.send_json(404, {"error": "Weboberfläche fehlt"})
            return self.send_html(page.read_text(encoding="utf-8"))
        if path == "/admin.js":
            page = Path(__file__).with_name("admin.js")
            if not page.is_file():
                return self.send_json(404, {"error": "Weboberfläche fehlt"})
            payload = page.read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", "text/javascript; charset=utf-8")
            self.send_header("Content-Length", str(len(payload)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.end_headers()
            self.wfile.write(payload)
            return
        if path == "/api/admin/dashboard":
            if not self.authorized():
                return self.send_json(401, {"error": "Admin-Schlüssel ungültig"})
            usage = shutil.disk_usage(self.server.root)
            with LOCK:
                now = time.monotonic()
                players = [{"clientId": key, "name": v["name"], "status": v["status"]}
                           for key, v in PLAYERS.items() if now - v["seen"] < 45]
            return self.send_json(200, {
                "status": "Online", "port": self.server.server_port,
                "serverType": "Raspberry Pi", "storage": {
                    "total": usage.total, "used": usage.used, "free": usage.free,
                    "path": str(self.server.root)},
                "players": players, "games": self.games(),
                "downloadRates": [],
                "note": "SMB-Downloads laufen direkt zwischen Client und NAS; Raten werden noch nicht gemeldet."
            })
        if path == "/api/health":
            return self.send_json(200, {"status": "ok", "service": "LAN Launcher Pi"})
        if path == "/api/capabilities":
            return self.send_json(200, {
                "protocolVersion": 1, "downloadMode": "smb", "smbShare": self.server.smb_share
            })
        if path == "/api/games":
            return self.send_json(200, [
                {k: g.get(k) for k in ("id", "name", "version", "description")}
                for g in self.games()
            ])
        if path == "/api/players":
            with LOCK:
                now = time.monotonic()
                return self.send_json(200, [
                    {k: v[k] for k in ("name", "role", "status", "gameId")}
                    for v in PLAYERS.values() if now - v["seen"] < 45
                ])
        if path == "/api/admin/check":
            return self.send_json(200 if self.authorized() else 401,
                                  {"status": "authorized"} if self.authorized() else {"error": "Unauthorized"})
        match = re.fullmatch(r"/api/games/([^/]+)/([^/]+)", path)
        if match:
            game_id, action = unquote(match.group(1)), match.group(2)
            game = self.game(game_id)
            if not game:
                return self.send_json(404, {"error": "Spiel nicht gefunden"})
            if action == "package-location":
                try:
                    safe_package(self.server.root, game["relativePath"])
                except (ValueError, FileNotFoundError) as exc:
                    return self.send_json(404, {"error": str(exc)})
                return self.send_json(200, {"relativePath": game["relativePath"]})
            if action == "test-status":
                return self.send_json(200, game.get("testStatus", {"tested": False, "operatingSystem": None}))
            if action == "start-notice":
                return self.send_json(200, {"text": game.get("startNotice", "")})
            if action == "tags":
                return self.send_json(200, game.get("tags", {"genres": [], "tags": []}))
            if action == "mod":
                return self.send_json(200, game.get("mod"))
            if action == "launch-options":
                return self.send_json(200, {"paths": game.get("launchPaths", [])}) if game.get("launchPaths") else self.send_json(404, {})
            if action == "launch-path":
                return self.send_json(200, {"path": game["launchPath"]}) if game.get("launchPath") else self.send_json(404, {})
            if action == "package":
                return self.send_json(409, {"error": "NAS-Server: Pakete ausschließlich per SMB abrufen"})
        return self.send_json(404, {"error": "Unbekannter Endpunkt"})

    def do_POST(self):
        path = urlsplit(self.path).path
        try:
            data = self.body()
            if path == "/api/join":
                name, client_id = str(data.get("name", "")).strip(), str(data.get("clientId", ""))
                if not 2 <= len(name) <= 32 or not client_id or len(client_id) > 128:
                    return self.send_json(400, {"error": "Ungültiger Name oder ClientId"})
                with LOCK:
                    if any(p["name"].casefold() == name.casefold() and key != client_id
                           and time.monotonic() - p["seen"] < 45 for key, p in PLAYERS.items()):
                        return self.send_json(409, {"error": "Name bereits online"})
                    PLAYERS[client_id] = {
                        "name": name, "role": "User", "status": "Online",
                        "gameId": None, "seen": time.monotonic()
                    }
                return self.send_json(200, {"name": name, "role": "User", "clientId": client_id})
            if path == "/api/status":
                client_id, status = data.get("clientId"), data.get("status")
                if status not in ("Online", "Im Spiel"):
                    return self.send_json(400, {"error": "Ungültiger Status"})
                with LOCK:
                    if client_id not in PLAYERS:
                        return self.send_json(404, {"error": "Client unbekannt"})
                    PLAYERS[client_id].update(status=status,
                                              gameId=data.get("gameId") if status == "Im Spiel" else None,
                                              seen=time.monotonic())
                return self.send_json(200, {})
            if path == "/api/admin/games/register":
                if not self.authorized():
                    return self.send_json(401, {"error": "Admin-Schlüssel ungültig"})
                game_id = data.get("id")
                if not isinstance(game_id, str) or not GAME_ID.fullmatch(game_id):
                    return self.send_json(400, {"error": "Ungültige Spiel-ID"})
                name, version, relative = data.get("name"), data.get("version"), data.get("relativePath")
                if not isinstance(name, str) or not 1 <= len(name) <= 150 or not isinstance(version, str) or not 1 <= len(version) <= 60:
                    return self.send_json(400, {"error": "Name oder Version ungültig"})
                safe_package(self.server.root, relative)
                with LOCK:
                    games = self.games()
                    if any(g["id"] == game_id for g in games):
                        return self.send_json(409, {"error": "Spiel-ID existiert bereits"})
                    game = {"id": game_id, "name": name, "version": version,
                            "description": str(data.get("description") or ""), "relativePath": relative}
                    games.append(game)
                    write_json(self.server.catalog_path, games)
                return self.send_json(201, game)
            return self.send_json(404, {"error": "Unbekannter Endpunkt"})
        except (ValueError, KeyError, TypeError, json.JSONDecodeError) as exc:
            return self.send_json(400, {"error": str(exc)})
        except FileNotFoundError as exc:
            return self.send_json(404, {"error": str(exc)})

    def do_PUT(self):
        path = urlsplit(self.path).path
        match = re.fullmatch(r"/api/admin/games/([^/]+)/(test-status|start-notice|tags|mod|launch-options|launch-path)", path)
        if not match:
            return self.send_json(404, {"error": "Unbekannter Endpunkt"})
        if not self.authorized():
            return self.send_json(401, {"error": "Admin-Schlüssel ungültig"})
        try:
            data = self.body()
            game_id, action = unquote(match.group(1)), match.group(2)
            field = {"test-status": "testStatus", "start-notice": "startNotice",
                     "tags": "tags", "mod": "mod", "launch-options": "launchPaths",
                     "launch-path": "launchPath"}[action]
            value = data.get("paths") if action == "launch-options" else data.get("path") if action == "launch-path" else data.get("text", "") if action == "start-notice" else data
            with LOCK:
                games = self.games()
                game = next((g for g in games if g["id"] == game_id), None)
                if not game:
                    return self.send_json(404, {"error": "Spiel nicht gefunden"})
                game[field] = value
                write_json(self.server.catalog_path, games)
            return self.send_json(200, data)
        except (ValueError, TypeError, json.JSONDecodeError) as exc:
            return self.send_json(400, {"error": str(exc)})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--root", type=Path, default=DEFAULT_ROOT)
    parser.add_argument("--smb-share", default=DEFAULT_SHARE)
    parser.add_argument("--admin-key-file", type=Path, required=True)
    args = parser.parse_args()
    if not args.root.is_dir() or not (args.root / "Games").is_dir() or not (args.root / "Mods").is_dir():
        parser.error("NAS nicht eingebunden oder Games/Mods fehlen. Server wird nicht gestartet.")
    if not args.smb_share.startswith("\\\\"):
        parser.error("SMB-Freigabe muss ein UNC-Pfad sein.")
    if not args.admin_key_file.is_file():
        parser.error("Admin-Schlüsseldatei fehlt.")
    key = args.admin_key_file.read_text(encoding="utf-8").strip()
    if len(key) < 8:
        parser.error("Admin-Schlüssel muss mindestens 8 Zeichen haben.")
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    server.daemon_threads = True
    server.root = args.root.resolve()
    server.catalog_path = args.root / "catalog.json"
    server.smb_share = args.smb_share.rstrip("\\")
    server.admin_key = True
    server.admin_digest = hashlib.sha256(key.encode()).digest()
    print(f"LAN Launcher Pi läuft auf {args.host}:{args.port}; NAS: {args.root}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
