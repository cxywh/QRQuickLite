"""测试用：直接改 %AppData%\\QRQuickLite\\settings.json 里的某个布尔字段。

用法：python tools/set-setting.py explorerContextMenu true
"""
import io
import json
import os
import sys

key = sys.argv[1]
raw = sys.argv[2]
if raw.lower() in ('true', 'false'):
    value = raw.lower() == 'true'
else:
    value = raw
path = os.path.join(os.environ['APPDATA'], 'QRQuickLite', 'settings.json')

data = json.loads(io.open(path, encoding='utf-8-sig').read())
data[key] = value
io.open(path, 'w', encoding='utf-8').write(json.dumps(data, ensure_ascii=False, indent=2))
print(f'{key} = {value}')
