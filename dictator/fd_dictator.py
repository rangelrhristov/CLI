import ctypes
import os
import queue
import threading
import time
from pathlib import Path
from tkinter import BOTH, LEFT, RIGHT, Button, Frame, Label, Tk

import numpy as np
import sounddevice as sd
from pynput import keyboard
import sherpa_onnx


APP_ROOT = Path(__file__).resolve().parent
MODEL_DIR = APP_ROOT / "models" / "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8"
SAMPLE_RATE = 16000
CHANNELS = 1
HOTKEY = "<ctrl>+<cmd>"


user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

SW_RESTORE = 9
INPUT_KEYBOARD = 1
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_UNICODE = 0x0004
VK_RETURN = 0x0D
VK_CONTROL = 0x11
VK_LWIN = 0x5B
VK_RWIN = 0x5C


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [
        ("wVk", ctypes.c_ushort),
        ("wScan", ctypes.c_ushort),
        ("dwFlags", ctypes.c_ulong),
        ("time", ctypes.c_ulong),
        ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong)),
    ]


class INPUT_UNION(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", ctypes.c_ulong), ("union", INPUT_UNION)]


def send_key(vk):
    extra = ctypes.c_ulong(0)
    down = INPUT(
        type=INPUT_KEYBOARD,
        union=INPUT_UNION(ki=KEYBDINPUT(vk, 0, 0, 0, ctypes.pointer(extra))),
    )
    up = INPUT(
        type=INPUT_KEYBOARD,
        union=INPUT_UNION(ki=KEYBDINPUT(vk, 0, KEYEVENTF_KEYUP, 0, ctypes.pointer(extra))),
    )
    user32.SendInput(1, ctypes.pointer(down), ctypes.sizeof(INPUT))
    user32.SendInput(1, ctypes.pointer(up), ctypes.sizeof(INPUT))


def send_unicode_unit(unit):
    extra = ctypes.c_ulong(0)
    down = INPUT(
        type=INPUT_KEYBOARD,
        union=INPUT_UNION(
            ki=KEYBDINPUT(0, unit, KEYEVENTF_UNICODE, 0, ctypes.pointer(extra))
        ),
    )
    up = INPUT(
        type=INPUT_KEYBOARD,
        union=INPUT_UNION(
            ki=KEYBDINPUT(0, unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, 0, ctypes.pointer(extra))
        ),
    )
    user32.SendInput(1, ctypes.pointer(down), ctypes.sizeof(INPUT))
    user32.SendInput(1, ctypes.pointer(up), ctypes.sizeof(INPUT))


def type_text(text):
    encoded = text.encode("utf-16-le")
    for index in range(0, len(encoded), 2):
        unit = encoded[index] | (encoded[index + 1] << 8)
        if unit == 10:
            continue
        if unit == 13:
            send_key(VK_RETURN)
        else:
            send_unicode_unit(unit)
        time.sleep(0.001)


def get_foreground_window():
    return user32.GetForegroundWindow()


def get_window_pid(hwnd):
    pid = ctypes.c_ulong(0)
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return pid.value


def restore_window(hwnd):
    if hwnd:
        if user32.IsIconic(hwnd):
            user32.ShowWindow(hwnd, SW_RESTORE)
        user32.SetForegroundWindow(hwnd)
        time.sleep(0.08)


def is_key_down(vk):
    return bool(user32.GetAsyncKeyState(vk) & 0x8000)


def wait_for_dictation_hotkey_release(timeout=2.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if not (
            is_key_down(VK_CONTROL)
            or is_key_down(VK_LWIN)
            or is_key_down(VK_RWIN)
        ):
            return
        time.sleep(0.02)


def clean_transcript(text):
    text = " ".join((text or "").strip().split())
    replacements = {
        "AGENTS dot md": "AGENTS.md",
        "README dot md": "README.md",
        "Power Shell": "PowerShell",
        "open AI": "OpenAI",
        "codex": "Codex",
    }
    for old, new in replacements.items():
        text = text.replace(old, new)
    return text


class DictatorApp:
    def __init__(self):
        self.root = Tk()
        self.root.title("FD Dictator")
        self.root.configure(bg="#050607")
        self.root.attributes("-topmost", True)
        self.root.resizable(False, False)
        self.root.protocol("WM_DELETE_WINDOW", self.close)

        self.ui_events = queue.Queue()
        self.frames = []
        self.recording = False
        self.loading = False
        self.recognizer = None
        self.last_target_hwnd = 0
        self.recording_target_hwnd = 0
        self.own_pid = os.getpid()
        self.hotkey_listener = None
        self.closing = False

        self.status = Label(
            self.root,
            text="loading model",
            bg="#050607",
            fg="#12d6e7",
            font=("Segoe UI", 9, "bold"),
            padx=10,
            pady=5,
        )
        self.status.pack(side=LEFT, fill=BOTH)

        self.button = Button(
            self.root,
            text="dictate",
            command=self.toggle_recording,
            bg="#111519",
            fg="#ffffff",
            activebackground="#17252a",
            activeforeground="#ffffff",
            relief="flat",
            font=("Segoe UI", 9, "bold"),
            padx=14,
            pady=5,
        )
        self.button.pack(side=RIGHT)

        self.root.bind("<ButtonPress-1>", self.capture_current_target)
        self.root.after(150, self.track_foreground_window)
        self.root.after(50, self.drain_ui_events)

        threading.Thread(target=self.load_model, daemon=True).start()
        self.start_hotkey()

    def run(self):
        self.root.mainloop()

    def close(self):
        self.closing = True
        try:
            if self.hotkey_listener is not None:
                self.hotkey_listener.stop()
        except Exception:
            pass
        self.root.destroy()

    def set_status(self, text, color="#12d6e7"):
        self.status.configure(text=text, fg=color)

    def post(self, fn):
        self.ui_events.put(fn)

    def drain_ui_events(self):
        while True:
            try:
                fn = self.ui_events.get_nowait()
            except queue.Empty:
                break
            fn()
        if not self.closing:
            self.root.after(50, self.drain_ui_events)

    def start_hotkey(self):
        try:
            self.hotkey_listener = keyboard.GlobalHotKeys({HOTKEY: self.on_hotkey})
            self.hotkey_listener.start()
        except Exception:
            self.set_status("hotkey unavailable", "#f2c95f")

    def on_hotkey(self):
        self.post(self.toggle_recording)

    def capture_current_target(self, _event=None):
        hwnd = get_foreground_window()
        if hwnd and get_window_pid(hwnd) != self.own_pid:
            self.last_target_hwnd = hwnd

    def track_foreground_window(self):
        hwnd = get_foreground_window()
        if hwnd and get_window_pid(hwnd) != self.own_pid:
            self.last_target_hwnd = hwnd
        if not self.closing:
            self.root.after(150, self.track_foreground_window)

    def load_model(self):
        self.loading = True
        try:
            required = [
                MODEL_DIR / "encoder.int8.onnx",
                MODEL_DIR / "decoder.int8.onnx",
                MODEL_DIR / "joiner.int8.onnx",
                MODEL_DIR / "tokens.txt",
            ]
            missing = [str(path) for path in required if not path.exists()]
            if missing:
                self.post(lambda: self.set_status("run setup first", "#f2c95f"))
                return

            recognizer = sherpa_onnx.OfflineRecognizer.from_transducer(
                encoder=str(MODEL_DIR / "encoder.int8.onnx"),
                decoder=str(MODEL_DIR / "decoder.int8.onnx"),
                joiner=str(MODEL_DIR / "joiner.int8.onnx"),
                tokens=str(MODEL_DIR / "tokens.txt"),
                num_threads=max(2, min(6, os.cpu_count() or 2)),
                model_type="nemo_transducer",
                decoding_method="greedy_search",
                provider="cpu",
            )
            self.recognizer = recognizer
            self.post(lambda: self.set_status("ready", "#12d6e7"))
        except Exception as exc:
            self.post(lambda exc=exc: self.set_status("model error: " + str(exc)[:60], "#ff7575"))
        finally:
            self.loading = False

    def toggle_recording(self):
        if self.recording:
            self.stop_recording()
            return
        self.start_recording()

    def start_recording(self):
        if self.loading:
            self.set_status("still loading", "#f2c95f")
            return
        if self.recognizer is None:
            self.set_status("model missing", "#ff7575")
            return

        self.capture_current_target()
        self.recording_target_hwnd = self.last_target_hwnd
        self.frames = []
        self.recording = True
        self.button.configure(text="stop")
        self.set_status("listening", "#f2c95f")

        try:
            self.stream = sd.InputStream(
                samplerate=SAMPLE_RATE,
                channels=CHANNELS,
                dtype="float32",
                callback=self.audio_callback,
            )
            self.stream.start()
        except Exception as exc:
            self.recording = False
            self.button.configure(text="dictate")
            self.set_status("mic error: " + str(exc)[:60], "#ff7575")

    def audio_callback(self, indata, _frames, _time_info, status):
        if status:
            return
        if self.recording:
            self.frames.append(indata.copy())

    def stop_recording(self):
        self.recording = False
        self.button.configure(text="dictate")
        try:
            self.stream.stop()
            self.stream.close()
        except Exception:
            pass

        if not self.frames:
            self.set_status("no audio", "#ff7575")
            return

        audio = np.concatenate(self.frames, axis=0).reshape(-1).astype(np.float32)
        self.frames = []
        self.set_status("transcribing", "#f2c95f")
        threading.Thread(target=self.transcribe_and_type, args=(audio, self.recording_target_hwnd), daemon=True).start()

    def transcribe_and_type(self, audio, target_hwnd):
        try:
            if not target_hwnd:
                self.post(lambda: self.set_status("click target app first", "#ff7575"))
                return

            stream = self.recognizer.create_stream()
            stream.accept_waveform(SAMPLE_RATE, audio)
            self.recognizer.decode_stream(stream)
            text = clean_transcript(stream.result.text)
            if not text:
                self.post(lambda: self.set_status("no speech", "#ff7575"))
                return

            wait_for_dictation_hotkey_release()
            restore_window(target_hwnd)
            type_text(text)
            self.post(lambda: self.set_status("typed", "#12d6e7"))
            self.post(lambda: self.root.after(1200, lambda: self.set_status("ready", "#12d6e7")))
        except Exception as exc:
            self.post(lambda exc=exc: self.set_status("error: " + str(exc)[:60], "#ff7575"))


if __name__ == "__main__":
    DictatorApp().run()
