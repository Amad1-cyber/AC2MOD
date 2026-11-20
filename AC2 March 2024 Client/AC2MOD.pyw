"""AC2MOD - a basic control window for running Asheron's Call 2 against a local server.

  * Start / Stop the local AC2 server (ac2server/server.py) in-process
  * Play: launches AC2client.exe straight at the local server, the same way RunAC2 does:
        AC2client.exe --host H --port P --rodat on --account NAME --glsticketdirect TICKET
  * Create account: adds NAME/password to the local account store (ac2server/accounts.json)
"""
import asyncio
import ctypes
import json
import logging
import os
import queue
import socket
import subprocess
import sys
import threading
import time
import tkinter as tk
from ctypes import wintypes
from tkinter import messagebox, ttk

HERE = os.path.dirname(os.path.abspath(__file__))
SERVER_DIR = os.path.join(HERE, "ac2server")
CFG = os.path.join(HERE, "ac2mod.cfg")
sys.path.insert(0, SERVER_DIR)
os.chdir(SERVER_DIR)            # raw_packets.log is written relative to the working directory
import accounts                 # noqa: E402
import server                   # noqa: E402

BG, PANEL, FG, MUTED = "#14161a", "#1d2026", "#e9eaee", "#8b91a0"
ACCENT, GOOD, BAD = "#d4a72c", "#3fb950", "#f85149"


# The recorded world-entry data crashes the client (it belongs to another account), so by default the replay
# stops after the login/loading stage. Raise this to experiment.
MAX_REPLAY_RUN = 3

# --- MOD2: the real open-source AC2RE server (patched for autosave + clean stop) and a localhost-only MySQL ----
REAL_DIR = r"c:\AC2IF\AC2RE-server"
MYSQL_DIR = r"c:\AC2IF\tools\mysql"
DOTNET_DIR = r"c:\AC2IF\tools\dotnet"
REAL_EXE = os.path.join(REAL_DIR, "bin", "AC2RE.Server.exe")
REAL_LOGS = os.path.join(REAL_DIR, "logs")
USE_REAL = os.path.isfile(REAL_EXE) and os.path.isfile(os.path.join(MYSQL_DIR, "bin", "mysqld.exe"))
NO_WINDOW = 0x08000000          # CREATE_NO_WINDOW
SERVER_IMAGE = "AC2RE.Server.exe"


def tcp_open(host, port, timeout=0.5):
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


def process_running(image):
    out = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {image}", "/NH"], capture_output=True, text=True,
                         creationflags=NO_WINDOW).stdout
    return image.lower() in out.lower()


def press_config_ok(timeout=45):
    """The client opens its configuration tool first (Preferences.ini RunConfigTool=1); press its OK button."""
    user32 = ctypes.windll.user32
    enum_proc = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)

    def text(h):
        buf = ctypes.create_unicode_buffer(256)
        user32.GetWindowTextW(h, buf, 256)
        return buf.value

    def cls(h):
        buf = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(h, buf, 256)
        return buf.value

    end = time.time() + timeout
    while time.time() < end:
        hit = []

        def child(ch, _l):
            if cls(ch) == "Button" and text(ch) == "OK":
                hit.append(ch)
            return True

        def top(h, _l):
            if user32.IsWindowVisible(h) and text(h) == "Asheron's Call 2 - Configuration":
                user32.EnumChildWindows(h, enum_proc(child), 0)
            return True

        user32.EnumWindows(enum_proc(top), 0)
        if hit:
            user32.SendMessageW(hit[0], 0x00F5, 0, 0)       # BM_CLICK
            return True
        time.sleep(0.4)
    return False


class QueueHandler(logging.Handler):
    def __init__(self, q):
        super().__init__()
        self.q = q

    def emit(self, record):
        self.q.put(self.format(record))


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("AC2MOD")
        self.configure(bg=BG)
        self.geometry("620x580")
        self.minsize(540, 500)

        self.q = queue.Queue()
        self.loop = self.stop_evt = self.thread = self.game = None
        self.external = False          # a server we didn't start is already listening on the port
        self.busy_text = ""            # shown in the status line while starting/stopping the real server
        self._real_lock = threading.Lock()   # only one start/stop of MySQL + server at a time
        self._proc_cache = (0.0, False)

        cfg = self._load_cfg()
        self.v_server = tk.StringVar(value=cfg.get("server", "127.0.0.1:7777"))
        self.v_account = tk.StringVar(value=cfg.get("account", "Player"))
        self.v_pass = tk.StringVar(value="1234")
        self.v_status = tk.StringVar()

        self._build()
        self._set_status()

        lg = logging.getLogger("ac2server")
        lg.setLevel(logging.INFO)
        lg.propagate = False
        h = QueueHandler(self.q)
        h.setFormatter(logging.Formatter("%(asctime)s  %(message)s", "%H:%M:%S"))
        lg.addHandler(h)

        self.after(150, self._pump)
        self.protocol("WM_DELETE_WINDOW", self._close)

    # ---------------------------------------------------------------- layout
    def _build(self):
        tk.Label(self, text="AC2MOD", font=("Segoe UI", 36, "bold"), fg=ACCENT, bg=BG).pack(pady=(20, 0))
        tk.Label(self, text="Asheron's Call 2  ·  local server" + ("  ·  MOD2" if USE_REAL else ""), font=("Segoe UI", 10),
                 fg=MUTED, bg=BG).pack()

        row = tk.Frame(self, bg=BG)
        row.pack(pady=(10, 4))
        self.dot = tk.Label(row, text="●", font=("Segoe UI", 12), bg=BG, fg=BAD)
        self.dot.pack(side="left")
        tk.Label(row, textvariable=self.v_status, font=("Segoe UI", 10), fg=FG, bg=BG).pack(side="left", padx=6)

        form = tk.Frame(self, bg=PANEL, padx=18, pady=14)
        form.pack(fill="x", padx=24, pady=8)
        form.columnconfigure(1, weight=1)
        for r, (label, var, secret) in enumerate((("Server", self.v_server, False),
                                                  ("Account", self.v_account, False),
                                                  ("Password", self.v_pass, True))):
            tk.Label(form, text=label, fg=MUTED, bg=PANEL, font=("Segoe UI", 10), anchor="w",
                     width=9).grid(row=r, column=0, sticky="w", pady=4)
            tk.Entry(form, textvariable=var, show="•" if secret else "", bg=BG, fg=FG,
                     insertbackground=FG, relief="flat", font=("Segoe UI", 11),
                     highlightthickness=1, highlightbackground="#2c313a",
                     highlightcolor=ACCENT).grid(row=r, column=1, sticky="ew", pady=4, ipady=4)

        btns = tk.Frame(self, bg=BG)
        btns.pack(pady=6)
        self.b_start = self._button(btns, "Start server", self.start_server)
        self.b_stop = self._button(btns, "Stop server", self.stop_server)
        self._button(btns, "Create account", self.create_account)
        self.b_play = self._button(btns, "▶  Play", self.play, accent=True)
        for i, b in enumerate(btns.winfo_children()):
            b.grid(row=0, column=i, padx=5)

        top = tk.Frame(self, bg=BG)
        top.pack(fill="x", padx=24, pady=(10, 0))
        tk.Label(top, text="Server log", fg=MUTED, bg=BG, font=("Segoe UI", 9)).pack(side="left")
        clear = tk.Label(top, text="clear", fg=MUTED, bg=BG, font=("Segoe UI", 9, "underline"), cursor="hand2")
        clear.pack(side="right")
        clear.bind("<Button-1>", lambda e: self._log_clear())

        box = tk.Frame(self, bg=PANEL)
        box.pack(fill="both", expand=True, padx=24, pady=(4, 20))
        self.log = tk.Text(box, height=10, bg=PANEL, fg="#c8ccd4", relief="flat", wrap="none",
                           font=("Consolas", 9), state="disabled", padx=8, pady=6, borderwidth=0)
        sb = ttk.Scrollbar(box, command=self.log.yview)
        self.log.configure(yscrollcommand=sb.set)
        sb.pack(side="right", fill="y")
        self.log.pack(side="left", fill="both", expand=True)

    def _button(self, parent, text, cmd, accent=False):
        return tk.Button(parent, text=text, command=cmd, relief="flat", cursor="hand2",
                         font=("Segoe UI", 10, "bold" if accent else "normal"),
                         bg=ACCENT if accent else PANEL, fg=BG if accent else FG,
                         activebackground="#f0c545" if accent else "#2a2f38",
                         activeforeground=BG if accent else FG, padx=14, pady=7, borderwidth=0)

    # ----------------------------------------------------------------- state
    def _load_cfg(self):
        try:
            with open(CFG) as fh:
                return json.load(fh)
        except (OSError, ValueError):
            return {}

    def _save_cfg(self):
        try:
            with open(CFG, "w") as fh:
                json.dump({"server": self.v_server.get(), "account": self.v_account.get()}, fh)
        except OSError:
            pass

    def _real_running(self):
        now = time.time()
        if now - self._proc_cache[0] > 2.0:                 # don't run tasklist on every UI tick
            self._proc_cache = (now, process_running(SERVER_IMAGE))
        return self._proc_cache[1]

    def _running(self):
        if USE_REAL:
            return self._real_running()
        return self.external or (self.thread is not None and self.thread.is_alive())

    def _set_status(self):
        game = self.game is not None and self.game.poll() is None
        if self.busy_text:
            text, color = self.busy_text, ACCENT
        elif game:
            text, color = "Game running", GOOD
        elif self._running():
            text, color = "Server running" + (" (MOD2)" if USE_REAL else (" (started elsewhere)" if self.external else "")), GOOD
        else:
            text, color = "Server stopped", BAD
        self.v_status.set(text)
        self.dot.configure(fg=color)
        busy = bool(self.busy_text)
        self.b_start.configure(state="disabled" if (self._running() or busy) else "normal")
        self.b_stop.configure(state="normal" if (self._running() and not busy) else "disabled")

    def _parse_server(self):
        host, _, port = self.v_server.get().strip().rpartition(":")
        if not host or not port.isdigit() or not 1 <= int(port) < 65535:
            raise ValueError("Server must look like 127.0.0.1:7777")
        return host, int(port)

    # ------------------------------------------------------------------- log
    def _log(self, line):
        self.log.configure(state="normal")
        self.log.insert("end", (line if len(line) < 220 else line[:217] + "...") + "\n")
        self.log.see("end")
        self.log.configure(state="disabled")

    def _log_clear(self):
        self.log.configure(state="normal")
        self.log.delete("1.0", "end")
        self.log.configure(state="disabled")

    def _pump(self):
        try:
            while True:
                self._log(self.q.get_nowait())
        except queue.Empty:
            pass
        self._set_status()
        self.after(250, self._pump)

    # ------------------------------------------- MOD2: real server (runs on worker threads)
    def _say(self, line):
        self.q.put(line)                      # thread-safe: the UI thread drains this queue

    def _start_real(self):
        """Start MySQL (if needed) and the AC2RE server. True once the server reports it is ready."""
        if not self._real_lock.acquire(blocking=False):
            return False
        self.busy_text = "Starting server..."
        try:
            os.makedirs(REAL_LOGS, exist_ok=True)
            if not tcp_open("127.0.0.1", 3306):
                self._say("MOD2: starting MySQL (127.0.0.1 only)")
                subprocess.Popen([os.path.join(MYSQL_DIR, "bin", "mysqld.exe"),
                                  f"--datadir={os.path.join(REAL_DIR, 'mysql-data')}", "--console",
                                  "--bind-address=127.0.0.1", "--mysqlx=OFF", "--port=3306", "--secure-file-priv="],
                                 stdout=open(os.path.join(REAL_LOGS, "mysqld.out.log"), "w"),
                                 stderr=open(os.path.join(REAL_LOGS, "mysqld.err.log"), "w"),
                                 creationflags=NO_WINDOW)
                for _ in range(80):
                    if tcp_open("127.0.0.1", 3306):
                        break
                    time.sleep(0.5)
                else:
                    self._say("MOD2: MySQL did not start - see AC2RE-server\\logs\\mysqld.err.log")
                    return False
            if process_running(SERVER_IMAGE):
                self._say("MOD2: the AC2RE server is already running")
                return True
            flag = os.path.join(REAL_DIR, "stop.flag")
            if os.path.exists(flag):
                os.remove(flag)
            log_path = os.path.join(REAL_LOGS, "server.out.log")
            env = dict(os.environ, DOTNET_ROOT=DOTNET_DIR, DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")
            subprocess.Popen([REAL_EXE], cwd=REAL_DIR, env=env, stdout=open(log_path, "w"),
                             stderr=subprocess.STDOUT, creationflags=NO_WINDOW)
            self._say("MOD2: starting the AC2RE server (loading game data, up to a minute)...")
            offset, ready = 0, False
            for _ in range(300):
                time.sleep(0.5)
                try:
                    with open(log_path, "r", errors="replace") as fh:
                        fh.seek(offset)
                        chunk = fh.read()
                        offset = fh.tell()
                except OSError:
                    chunk = ""
                for line in chunk.splitlines():
                    if line.strip() and not line.startswith("   at "):
                        self._say("  " + line)
                if "Server initialization complete" in chunk:
                    ready = True
                    break
                if not process_running(SERVER_IMAGE):
                    break
            if not ready:
                self._say("MOD2: the server did not start - see AC2RE-server\\logs\\server.out.log")
                return False
            self._say("MOD2: server ready. The world autosaves every 2 minutes and again on Stop / close.")
            threading.Thread(target=self._tail, args=(log_path, offset), daemon=True).start()
            return True
        finally:
            self.busy_text = ""
            self._proc_cache = (0.0, False)
            self._real_lock.release()

    def _tail(self, path, offset):
        """Show new server log lines in the window while the server runs."""
        idle = 0
        while idle < 10:
            time.sleep(1)
            try:
                with open(path, "r", errors="replace") as fh:
                    fh.seek(offset)
                    chunk = fh.read()
                    offset = fh.tell()
            except OSError:
                return
            for line in chunk.splitlines():
                if line.strip() and not line.startswith("   at "):
                    self._say("  " + line)
            idle = 0 if process_running(SERVER_IMAGE) else idle + 1

    def _stop_real(self):
        """Ask the server to save and exit (stop file), then shut MySQL down cleanly."""
        if not self._real_lock.acquire(blocking=False):
            return
        self.busy_text = "Saving and stopping..."
        try:
            if process_running(SERVER_IMAGE):
                self._say("MOD2: saving the world and stopping the server...")
                open(os.path.join(REAL_DIR, "stop.flag"), "w").close()
                for _ in range(80):
                    if not process_running(SERVER_IMAGE):
                        break
                    time.sleep(0.5)
                else:
                    self._say("MOD2: the server did not exit in time - forcing it (last save may be up to 2 min old)")
                    subprocess.run(["taskkill", "/IM", SERVER_IMAGE, "/F"], capture_output=True, creationflags=NO_WINDOW)
            if tcp_open("127.0.0.1", 3306):
                subprocess.run([os.path.join(MYSQL_DIR, "bin", "mysqladmin.exe"), "-u", "root", "--protocol=tcp",
                                "-h", "127.0.0.1", "shutdown"], capture_output=True, creationflags=NO_WINDOW)
                for _ in range(40):
                    if not tcp_open("127.0.0.1", 3306):
                        break
                    time.sleep(0.5)
            self._say("MOD2: stopped. Characters and world are saved in the database.")
        finally:
            self.busy_text = ""
            self._proc_cache = (0.0, False)
            self._real_lock.release()

    def _play_real(self, exe, name, ticket):
        while self.busy_text:                                 # a start/stop is already in progress
            time.sleep(0.5)
        if not process_running(SERVER_IMAGE) and not self._start_real():
            return
        host, port = self._parse_server()
        self._save_cfg()
        self.game = subprocess.Popen([exe, "--host", host, "--port", str(port), "--rodat", "on",
                                      "--account", name, "--glsticketdirect", ticket], cwd=HERE)
        self._say(f"launched AC2client.exe -> {host}:{port} as '{name}' (accounts are created on first login)")
        threading.Thread(target=press_config_ok, daemon=True).start()   # skip the client's config dialog

    # --------------------------------------------------------------- actions
    def start_server(self):
        if USE_REAL:
            threading.Thread(target=self._start_real, daemon=True).start()
            return True
        if self._running():
            return True
        try:
            _, port = self._parse_server()
        except ValueError as e:
            messagebox.showerror("AC2MOD", str(e))
            return False
        self._save_cfg()
        ready, errors = threading.Event(), []

        def run():
            async def main():
                self.loop = asyncio.get_running_loop()
                self.stop_evt = asyncio.Event()
                bound = asyncio.Event()
                task = asyncio.create_task(server.serve("0.0.0.0", [port, port + 1], None, self.stop_evt, bound,
                                                        max_run=MAX_REPLAY_RUN))
                waiter = asyncio.create_task(bound.wait())
                done, _ = await asyncio.wait({task, waiter}, return_when=asyncio.FIRST_COMPLETED)
                if task in done:
                    await task                      # bind failed: raises
                ready.set()
                await task
            try:
                asyncio.run(main())
            except Exception as exc:                # noqa: BLE001
                errors.append(exc)
            finally:
                self.loop = None
                ready.set()
                self.q.put("server stopped")

        self.thread = threading.Thread(target=run, daemon=True)
        self.thread.start()
        ready.wait(4)
        if errors:
            self.thread = None
            if getattr(errors[0], "winerror", None) == 10048:      # address already in use
                self.external = True
                self._log(f"UDP {port}/{port + 1} already in use - assuming a server is already running there")
                self._set_status()
                return True
            messagebox.showerror("AC2MOD", f"Could not start the server on UDP {port}/{port + 1}:\n{errors[0]}")
            self._set_status()
            return False
        self._set_status()
        return True

    def stop_server(self):
        if USE_REAL:
            threading.Thread(target=self._stop_real, daemon=True).start()
            return
        self.external = False
        if self.loop and self.stop_evt:
            self.loop.call_soon_threadsafe(self.stop_evt.set)

    def create_account(self):
        if USE_REAL:
            self._log("accounts are created automatically the first time you log in - just press Play")
            return
        name, pw = self.v_account.get().strip(), self.v_pass.get()
        if not name or not pw:
            messagebox.showerror("AC2MOD", "Enter an account name and password first.")
            return
        accounts.create(name, pw)
        self._log(f"account '{name}' saved to the local account store")

    def play(self):
        exe = os.path.join(HERE, "AC2client.exe")
        if not os.path.isfile(exe):
            messagebox.showerror("AC2MOD", f"AC2client.exe not found in\n{HERE}")
            return
        name, ticket = self.v_account.get().strip(), self.v_pass.get()
        if not name or not ticket:
            messagebox.showerror("AC2MOD", "Enter an account name and password first.")
            return
        if self.game is not None and self.game.poll() is None:
            messagebox.showinfo("AC2MOD", "The game is already running.")
            return
        if USE_REAL:
            try:
                self._parse_server()
            except ValueError as e:
                messagebox.showerror("AC2MOD", str(e))
                return
            threading.Thread(target=self._play_real, args=(exe, name, ticket), daemon=True).start()
            return
        if not self.start_server():
            return
        host, port = self._parse_server()
        self._save_cfg()
        self.game = subprocess.Popen([exe, "--host", host, "--port", str(port), "--rodat", "on",
                                      "--account", name, "--glsticketdirect", ticket], cwd=HERE)
        self._log(f"launched AC2client.exe -> {host}:{port} as '{name}'")
        threading.Thread(target=press_config_ok, daemon=True).start()   # skip the client's config dialog
        self._set_status()

    def _close(self):
        if USE_REAL:
            if self.game is not None and self.game.poll() is None:
                if not messagebox.askyesno("AC2MOD", "The game is running. Closing AC2MOD saves and stops the "
                                                     "server and disconnects the game.\n\nClose anyway?"):
                    return
            if self._running() or tcp_open("127.0.0.1", 3306):
                self.v_status.set("Saving and stopping...")
                self.update()
                self._stop_real()                     # blocks until the world is saved (up to ~40 s)
            self.destroy()
            return
        self.stop_server()
        self.destroy()


if __name__ == "__main__":
    App().mainloop()
