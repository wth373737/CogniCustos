# smart_scheduler.py
import torch
import librosa
import threading
import time
import sqlite3
import json
import re
import math
import smtplib
import winsound
import calendar 
from datetime import datetime, timedelta
from collections import deque
import os
import requests
import sounddevice as sd
import soundfile as sf
import keyboard
import requests
import plyer
from pynput import mouse
from qwen_asr import Qwen3ASRModel
from datetime import datetime



# ========== 配置 ==========
SAMPLE_RATE = 44100
DURATION = 10
CHANNELS = 1
OUTPUT_WAV = "recording.wav"
MODEL_PATH = "" # 替换为你的模型的路径
OLLAMA_URL = "http://localhost:11434/api/generate"
OLLAMA_MODEL = "" # 选择对应的ollma模型名称
DB_FILE = "events.db"
CHECK_INTERVAL = 30
MOUSE_INACTIVITY_THRESHOLD = 50
ACTIVITY_WINDOW_SECONDS = 5 * 60
# --- Telegram 提醒配置 ---
TELEGRAM_BOT_TOKEN = ""   # 替换为你的 Bot Token
TELEGRAM_CHAT_ID = ""         # 替换为你的 Chat ID

# ========== 数据库 ==========
def init_db():
    conn = sqlite3.connect(DB_FILE)
    c = conn.cursor()
    c.execute('''CREATE TABLE IF NOT EXISTS events (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        description TEXT NOT NULL,
        deadline TEXT NOT NULL,
        reminded INTEGER DEFAULT 0,
        repeat_rule TEXT DEFAULT NULL)''')
    # 兼容旧表：如果不存在 repeat_rule 列则添加
    try:
        c.execute("ALTER TABLE events ADD COLUMN repeat_rule TEXT DEFAULT NULL")
    except sqlite3.OperationalError:
        pass  # 列已存在
    conn.commit()
    conn.close()

def add_event(description, deadline, repeat_rule=None):
    conn = sqlite3.connect(DB_FILE)
    c = conn.cursor()
    c.execute("INSERT INTO events (description, deadline, repeat_rule) VALUES (?, ?, ?)",
              (description, deadline, repeat_rule))
    conn.commit()
    conn.close()
    if repeat_rule:
        print(f"📅 重复事件已添加: {description} 首次截止 {deadline}，规则: {repeat_rule}")
    else:
        print(f"📅 事件已添加: {description} 截止 {deadline}")

def get_unreminded_events():
    now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    conn = sqlite3.connect(DB_FILE)
    c = conn.cursor()
    c.execute("SELECT id, description, deadline, repeat_rule FROM events WHERE reminded=0 AND deadline <= ?", (now,))
    rows = c.fetchall()
    conn.close()
    return rows

def mark_reminded(event_id):
    conn = sqlite3.connect(DB_FILE)
    c = conn.cursor()
    c.execute("UPDATE events SET reminded=1 WHERE id=?", (event_id,))
    conn.commit()
    conn.close()

def update_event_deadline(event_id, new_deadline):
    conn = sqlite3.connect(DB_FILE)
    c = conn.cursor()
    c.execute("UPDATE events SET deadline=?, reminded=0 WHERE id=?", (new_deadline, event_id))
    conn.commit()
    conn.close()

def delete_event(event_id):
    conn = sqlite3.connect(DB_FILE)
    c = conn.cursor()
    c.execute("DELETE FROM events WHERE id=?", (event_id,))
    conn.commit()
    conn.close()

# ========== Ollama 事件提取 ==========
def query_ollama(prompt):
    payload = {"model": OLLAMA_MODEL, "prompt": prompt, "stream": False}
    resp = requests.post(OLLAMA_URL, json=payload, timeout=60)
    resp.raise_for_status()
    return resp.json().get("response", "").strip()

def extract_events_from_text(text):
    now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    system_prompt = (
        f"当前时间是 {now}。你是一个日程助手。从用户输入中提取所有待办事件及其截止时间，并判断是否为重复事件。\n"
        "如果用户使用相对时间（如 '一分钟后'、'半小时后'、'明天下午3点'），请基于当前时间计算出具体的绝对时间。\n"
        "截止时间必须转换为 'YYYY-MM-DD HH:MM' 格式（24小时制）。如果无法确定具体时间，默认截止时间为当天 23:59:59。\n\n"
        "如果事件是重复的（如“每小时提醒一次”、“每周五提醒”、“每天早上8点提醒”），请提取重复规则，格式如下：\n"
        "- 间隔重复：repeat: {\"type\": \"interval\", \"value\": 数字, \"unit\": \"minutes\"/\"hours\"/\"days\"/\"weeks\", \"end\": \"可选的结束时间 YYYY-MM-DD HH:MM\"}\n"
        "- 每周重复：repeat: {\"type\": \"weekly\", \"weekdays\": [数字0-6，0=周日，6=周六], \"end\": \"可选\"}\n"
        "- 每日重复：repeat: {\"type\": \"daily\", \"end\": \"可选\"}\n"
        "- 每月重复：repeat: {\"type\": \"monthly\", \"day\": 日期数字, \"end\": \"可选\"}\n\n"
        "如果事件不是重复事件，则不包含 repeat 字段。\n"
        "请只输出一个 JSON 数组，例如：\n"
        "[{\"description\": \"开会\", \"deadline\": \"2025-05-27 15:00\"},\n"
        " {\"description\": \"喝水\", \"deadline\": \"2025-05-28 14:00\", \"repeat\": {\"type\": \"interval\", \"value\": 1, \"unit\": \"hours\", \"end\": \"2025-05-29 08:00\"}},\n"
        " {\"description\": \"周末提醒\", \"deadline\": \"2025-05-30 09:00\", \"repeat\": {\"type\": \"weekly\", \"weekdays\": [4]}}]\n"
        f"用户输入：{text}\n输出 JSON："
    )
    raw = query_ollama(system_prompt)
    print("Ollama 原始返回:", raw)
    match = re.search(r"\[.*\]", raw, re.DOTALL)
    if match:
        try:
            events = json.loads(match.group(0))
            return events
        except json.JSONDecodeError:
            return []
    return []

# ========== 鼠标监控 ==========
class MouseActivityMonitor:
    def __init__(self):
        self.positions = deque()
        self.lock = threading.Lock()
        self.listener = None
    def on_move(self, x, y):
        with self.lock:
            now = time.time()
            self.positions.append((now, (x, y)))
            while self.positions and self.positions[0][0] < now - ACTIVITY_WINDOW_SECONDS:
                self.positions.popleft()
    def start(self):
        self.listener = mouse.Listener(on_move=self.on_move)
        self.listener.daemon = True
        self.listener.start()
    def get_total_distance_last_window(self):
        with self.lock:
            if len(self.positions) < 2:
                return 0.0
            snapshot = list(self.positions)
        total = 0.0
        for i in range(1, len(snapshot)):
            _, (x1, y1) = snapshot[i-1]
            _, (x2, y2) = snapshot[i]
            total += math.hypot(x2 - x1, y2 - y1)
        return total

# ========== Telegram 提醒 ==========
def send_telegram_message(message):
    if not TELEGRAM_BOT_TOKEN or not TELEGRAM_CHAT_ID:
        print("⚠️ Telegram 未配置，无法发送消息")
        return False
    url = f"https://api.telegram.org/bot{TELEGRAM_BOT_TOKEN}/sendMessage"
    payload = {
        "chat_id": TELEGRAM_CHAT_ID,
        "text": message,
        "parse_mode": "HTML",
        "disable_notification": False
    }
    try:
        response = requests.post(url, json=payload, timeout=10)
        response.raise_for_status()
        print("✅ Telegram 消息已送达")
        return True
    except requests.exceptions.RequestException as e:
        print(f"❌ Telegram 消息发送失败: {e}")
        return False

def check_and_send_mail_if_inactive(event_desc, deadline, mouse_monitor):
    distance = mouse_monitor.get_total_distance_last_window()
    print(f"📊 过去{ACTIVITY_WINDOW_SECONDS//60}分钟鼠标移动距离: {distance:.1f} 像素")
    if distance < MOUSE_INACTIVITY_THRESHOLD:
        print("🚨 检测到低活动，发送 Telegram...")
        tg_message = f"⚠️ <b>事件提醒</b>\n事件: {event_desc}\n截止: {deadline}\n\n过去{ACTIVITY_WINDOW_SECONDS//60}分钟鼠标移动距离仅{distance:.0f}像素，可能不在电脑前。"
        send_telegram_message(tg_message)
    else:
        print("✅ 鼠标活动充足")

# ========== 录音与转录 ==========
def record_audio():
    print(f"🔴 开始录音 {DURATION} 秒...")
    recording = sd.rec(int(DURATION * SAMPLE_RATE), samplerate=SAMPLE_RATE, channels=CHANNELS, dtype='float32')
    sd.wait()
    sf.write(OUTPUT_WAV, recording, SAMPLE_RATE)
    print(f"✅ 录音已保存: {OUTPUT_WAV}")
    return OUTPUT_WAV

def transcribe_audio(model, file_path, max_duration=30.0):
    speech, sr = librosa.load(file_path, sr=16000)
    if len(speech) == 0:
        raise ValueError("音频为空")
    chunk_samples = int(max_duration * sr)
    chunks = [speech[i:i+chunk_samples] for i in range(0, len(speech), chunk_samples)]
    full_texts = []
    for idx, chunk in enumerate(chunks):
        temp_file = f"temp_chunk_{idx}.wav"
        sf.write(temp_file, chunk, sr)
        result = model.transcribe(temp_file, language=None)
        os.remove(temp_file)
        text = result[0].text.strip()
        full_texts.append(text)
        print(f"  分段 {idx+1}/{len(chunks)} 识别完成: {text[:50]}...")
    full_text = " ".join(full_texts)
    print(f"📝 转录文本: {full_text}")
    base, _ = os.path.splitext(file_path)
    txt_path = base + ".txt"
    with open(txt_path, "w", encoding="utf-8") as f:
        f.write(full_text)
    return full_text

def on_hotkey(model):
    def wrapper():
        audio_file = record_audio()
        text = transcribe_audio(model, audio_file)
        events = extract_events_from_text(text)
        for ev in events:
            desc = ev.get("description", "未命名")
            deadline = ev.get("deadline", "")
            if not deadline:
                deadline = datetime.now().replace(hour=23, minute=59, second=59).strftime("%Y-%m-%d %H:%M:%S")
            repeat_rule = ev.get("repeat")
            # 将 repeat 对象转换为 JSON 字符串存储
            repeat_json = json.dumps(repeat_rule, ensure_ascii=False) if repeat_rule else None
            add_event(desc, deadline, repeat_json)
        print("✅ 语音已转录并添加至日程")
    return wrapper

# ========== 重复事件下一次提醒计算 ==========
def compute_next_reminder(current_deadline_str, repeat_rule_json):
    """
    根据当前提醒时间和重复规则，计算下一次提醒时间（字符串）。
    """
    try:
        rule = json.loads(repeat_rule_json)
    except json.JSONDecodeError:
        return None

    try:
        current_dt = datetime.strptime(current_deadline_str, "%Y-%m-%d %H:%M:%S")
    except ValueError:
        return None

    rtype = rule.get("type")
    end_str = rule.get("end")

    # --- 处理结束时间 ---
    end_dt = None
    if end_str:
        try:
            end_dt = datetime.strptime(end_str, "%Y-%m-%d %H:%M:%S")
        except ValueError:
            pass  # 如果结束时间格式错误，忽略结束条件

    # --- 计算下一次时间 ---
    next_dt = None

    if rtype == "interval":
        # 间隔重复
        value = int(rule.get("value", 1))
        unit = rule.get("unit", "hours")
        
        delta_kwargs = {
            "minutes": timedelta(minutes=value),
            "hours": timedelta(hours=value),
            "days": timedelta(days=value),
            "weeks": timedelta(weeks=value)
        }
        
        if unit not in delta_kwargs:
            return None
            
        # 基于当前时间计算，防止累积漂移（或者基于 current_dt，取决于需求）
        # 这里保持原逻辑：基于 current_dt
        next_dt = current_dt + delta_kwargs[unit]

    elif rtype == "weekly":
        # 每周重复 - 修复映射错位
        weekdays = rule.get("weekdays", [])
        if not weekdays:
            return None

        # 修正映射逻辑：
        # LLM 输出: 0=周日, 1=周一, ..., 6=周六
        # Python weekday(): 0=周一, ..., 6=周日
        # 转换公式：py_wd = (llm_wd + 6) % 7
        # 例如：LLM周日(0) -> (0+6)%7=6 (Python周日)；LLM周一(1) -> (1+6)%7=0 (Python周一)
        target_py_weekdays = [(d + 6) % 7 for d in weekdays]

        # 寻找下一个最近的指定星期
        for i in range(1, 8):  # 检查未来7天
            candidate = current_dt + timedelta(days=i)
            if candidate.weekday() in target_py_weekdays:
                next_dt = candidate
                break

    elif rtype == "daily":
        # 每日重复
        next_dt = current_dt + timedelta(days=1)

    elif rtype == "monthly":
        # 每月重复 - 修复日期越界 (如 1月31日 -> 2月28/29日)
        day = int(rule.get("day", 1))
        
        # 获取下一个月的年份和月份
        year = current_dt.year
        month = current_dt.month + 1
        if month > 12:
            month = 1
            year += 1

        # 安全地设置日期：如果指定日期超过该月最大天数，则设为该月最后一天
        # 例如：如果 day=31，但下个月只有30天，则设为30
        _, max_day_in_month = calendar.monthrange(year, month)
        safe_day = min(day, max_day_in_month)
        
        try:
            next_dt = current_dt.replace(year=year, month=month, day=safe_day)
        except ValueError:
            # 理论上不会发生，但作为双重保险
            # 如果还是出错（比如2月30日），直接设为该月最后一天
            next_dt = current_dt.replace(year=year, month=month, day=max_day_in_month)

    else:
        return None  # 不支持的类型

    # --- 如果以上逻辑未计算出 next_dt ---
    if next_dt is None:
        return None

    # --- 检查结束条件 ---
    if end_dt and next_dt > end_dt:
        return None  # 已超过结束时间

    # --- 防止重复触发 ---
    # 如果计算出的时间小于等于当前时间，理论上应该跳过（但在提醒线程中应该由调用方处理）
    # 这里简单返回，由提醒线程的逻辑决定是否立即再次触发
    # 注意：原代码中有一个递归修正逻辑，这里移除以防止无限递归，建议在 reminder_worker 中处理

    return next_dt.strftime("%Y-%m-%d %H:%M:%S")

# ========== 提醒服务 ==========
def reminder_worker(stop_event, mouse_monitor):
    while not stop_event.is_set():
        events = get_unreminded_events()
        for eid, desc, deadline, repeat_rule in events:
            # 触发提醒
            try:
                plyer.notification.notify(title="⏰ 事件提醒", message=f"{desc}\n截止: {deadline}", timeout=10)
            except Exception as e: # <-- 修改这里
                print(f"⚠️ 弹窗失败: {e}")
            winsound.Beep(1000, 500)
            print(f"🔔 提醒: {desc} (截止 {deadline})")
            check_and_send_mail_if_inactive(desc, deadline, mouse_monitor)

            # 处理重复规则
            if repeat_rule:
                next_deadline = compute_next_reminder(deadline, repeat_rule)
                if next_deadline:
                # 确保 next_deadline 确实大于当前时间，防止“马不停蹄”地提醒
                    next_dt_obj = datetime.strptime(next_deadline, "%Y-%m-%d %H:%M:%S")
                    if next_dt_obj <= datetime.now():
            # 如果计算出的时间已经过了，可能需要计算再下一次，或者直接标记为结束
            # 这取决于你的需求：是“漏掉就不管了”还是“必须补上”
            # 目前逻辑：直接更新，可能会导致立即再次触发提醒
                        pass
                    update_event_deadline(eid, next_deadline)
                    print(f"🔄 重复事件更新下一次提醒时间为: {next_deadline}")
                else:
                    # 重复结束，标记为已完成
                    mark_reminded(eid)
                    print(f"🏁 重复事件 '{desc}' 已结束")
            else:
                # 一次性事件，标记为已提醒
                mark_reminded(eid)

        time.sleep(CHECK_INTERVAL)

# ========== 主程序 ==========
if __name__ == "__main__":
    print("🚀 启动智能日程助手...")
    init_db()
    # 加载 ASR 模型
    print("📥 加载语音识别模型...")
    model = Qwen3ASRModel.from_pretrained(
        MODEL_PATH,
        dtype=torch.bfloat16,
        device_map="cuda" if torch.cuda.is_available() else "cpu",
        max_new_tokens=256,
    )
    print("✅ 模型加载完成")
    # 鼠标监控
    mouse_monitor = MouseActivityMonitor()
    mouse_monitor.start()
    # 注册录音热键
    keyboard.add_hotkey('F9', on_hotkey(model))
    print("🎙️ 按 F9 开始录音（10秒自动停止）")
    # 启动提醒线程
    stop_event = threading.Event()
    reminder_thread = threading.Thread(target=reminder_worker, args=(stop_event, mouse_monitor), daemon=True)
    reminder_thread.start()
    print("🔔 提醒服务已启动")
    # 主线程保持运行，等待 Ctrl+C
    try:
        keyboard.wait()  # 持续监听热键
    except KeyboardInterrupt:
        print("\n🛑 正在关闭...")
        stop_event.set()
        if mouse_monitor.listener:
            mouse_monitor.listener.stop()
        print("服务已停止")