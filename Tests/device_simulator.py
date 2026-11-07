"""
设备上报模拟器（用于验证 MultiSerialTool 的空闲间隔消息组包与 CSV 导出）。

用法：
    pip install pyserial
    python device_simulator.py COM8            # 默认 115200
    python device_simulator.py COM8 9600       # 指定波特率

行为：
    每 1 秒发送“一整组”数据，整组拆成 3 个分片、分片间隔 10ms（< 组包阈值 30ms，
    应被合并为同一条消息）；组与组之间间隔 1s（> 30ms，应分为不同消息）。
    每个分片自带 CRLF，模拟真实设备 Battery/LED/Solar 多行上报。
"""
import sys
import time

try:
    import serial
except ImportError:
    print("缺少 pyserial，请先执行：pip install pyserial")
    sys.exit(1)

port = sys.argv[1] if len(sys.argv) > 1 else "COM8"
baud = int(sys.argv[2]) if len(sys.argv) > 2 else 115200

# 一整组消息的三个分片（对应图表规则中的 电池组 / LED信息 / 光伏板信息）
FRAME_PARTS = [
    b"Battery voltage: 13.85, current: 1.82, capacity: 99, mode: 1\r\n",
    b"LED voltage: 14.26, current: 0.02, power: 0.27, energy: 0.00\r\n",
    b"Solar voltage: 38.72, current: 0.63, power: 28.94\r\n",
]

ser = serial.Serial(port, baud, bytesize=8, parity="N", stopbits=1, timeout=1)
print(f"已打开 {port} @ {baud}，开始模拟上报，Ctrl+C 停止")

try:
    while True:
        for part in FRAME_PARTS:
            ser.write(part)
            ser.flush()
            time.sleep(0.01)  # 分片间隔 10ms，应被合并
        time.sleep(1.0)        # 帧间隔 1s，应分帧
except KeyboardInterrupt:
    pass
finally:
    ser.close()
    print("已关闭串口")
