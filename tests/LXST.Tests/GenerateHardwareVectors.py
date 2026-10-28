"""Execute primary hardware drivers against disclosed fake GPIO/SMBus boundaries."""
import argparse
import hashlib
import importlib.machinery
import json
import os
from pathlib import Path
import sys
from types import ModuleType, SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Interop"))
from reference_environment import verify_reference


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    verify_reference(require_lxst=True)
    operations, writes, delays = [], [], []
    active, driven = set(), set()
    hook = [1]
    gpio = ModuleType("RPi.GPIO")
    gpio.IN, gpio.OUT, gpio.LOW, gpio.HIGH = "in", "out", 0, 1
    gpio.PUD_UP, gpio.PUD_DOWN, gpio.PUD_OFF = "up", "down", "off"
    gpio.setup = lambda pin, mode, pull_up_down=None: operations.append(["setup", pin, mode, pull_up_down])
    def output(pin, value):
        operations.append(["write", pin, value])
        if value:
            driven.add(pin)
        else:
            driven.discard(pin)
    gpio.output = output
    gpio.input = lambda pin: hook[0] if pin == 5 else int(any((row, pin) in active for row in driven))
    rpi = ModuleType("RPi")
    rpi.__spec__ = importlib.machinery.ModuleSpec("RPi", loader=None, is_package=True)
    rpi.__path__ = []
    rpi.GPIO = gpio
    sys.modules["RPi"], sys.modules["RPi.GPIO"] = rpi, gpio
    smbus = ModuleType("smbus")
    smbus.__spec__ = importlib.machinery.ModuleSpec("smbus", loader=None)
    class Bus:
        def __init__(self, channel):
            self.channel = channel
        def write_byte(self, address, value):
            writes.append([address, value])
        def close(self):
            writes.append(["close"])
    smbus.SMBus = Bus
    sys.modules["smbus"] = smbus
    from LXST.Primitives.hardware import keypad_gpio_4x4 as keypad_module
    from LXST.Primitives.hardware import display_i2c_lcd1602 as lcd_module
    now = [0.0]
    keypad_module.time = SimpleNamespace(time=lambda: now[0])
    lcd_module.time = SimpleNamespace(sleep=lambda duration: delays.append(duration))
    events = []
    keypad = keypad_module.Keypad(callback=lambda sender, event: events.append(list(event)))
    keypad.enable_hook()
    result = {
        "revision": "3b596f48ddd252ddc9c315ef8f3020cfd3688223",
        "source_sha256": {name: hashlib.sha256((Path(os.environ["DOTRETICULUM_LXST_UPSTREAM"]) / "LXST" / "Primitives" / "hardware" / name).read_bytes()).hexdigest()
                          for name in ["keypad_gpio_4x4.py", "display_i2c_lcd1602.py"]},
        "substitutes": "Only GPIO/SMBus calls and wall clock/sleep are fake; actual driver constructors/scan/print/sleep/wake/close execute unchanged.",
        "keypad": [], "lcd": [],
    }
    for elapsed, pairs, hook_value in [
        (.02, [], 1), (.04, [(21, 26)], 1), (.06, [(21, 26)], 0),
        (.1, [(20, 19)], 1), (.22, [], 1),
        (.24, [(21, 26), (21, 19), (21, 13), (21, 6), (20, 26)], 1),
        (.26, [], 1),
    ]:
        now[0], hook[0] = elapsed, hook_value
        active.clear()
        active.update(pairs)
        operations.clear()
        events.clear()
        keypad._Keypad__scan()
        result["keypad"].append({
            "elapsed": elapsed, "active": pairs, "hook": hook_value,
            "events": list(events), "down": [key for key, value in keypad.key_states.items() if value],
            "operations": list(operations),
        })
    lcd = None
    for action, text, x, y in [("init", "", 0, 0), ("print", "Hello", -3, 4), ("sleep", "", 0, 0),
                               ("print", "0123456789abcdefEXCESS", 20, -1), ("clear", "", 0, 0), ("close", "", 0, 0)]:
        writes.clear()
        delays.clear()
        if action == "init":
            lcd = lcd_module.LCD()
        elif action == "print":
            lcd.print(text, x=x, y=y)
        else:
            getattr(lcd, action)()
        result["lcd"].append({
            "action": action, "text": text, "x": x, "y": y,
            "writes": list(writes), "delays": list(delays), "sleeping": lcd.is_sleeping,
        })
    Path(args.output).write_text(json.dumps(result, indent=2) + "\n")


if __name__ == "__main__":
    main()
