"""检查 QRQuickLite 的右键菜单注册表项。用于安装/卸载后的验收。"""
import winreg

VERB = 'QRQuickLiteCapture'
ROOTS = [
    r'Software\Classes\*\shell',
    r'Software\Classes\Directory\shell',
    r'Software\Classes\Directory\Background\shell',
    r'Software\Classes\DesktopBackground\Shell',
]

for root in ROOTS:
    path = root + '\\' + VERB
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as key:
            text = winreg.QueryValueEx(key, 'MUIVerb')[0]
            icon = winreg.QueryValueEx(key, 'Icon')[0]
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path + r'\command') as cmd:
            command = winreg.QueryValue(cmd, '')
        print(f'[OK]   {root}')
        print(f'       MUIVerb={text}  Icon={icon}')
        print(f'       command={command}')
    except FileNotFoundError:
        print(f'[MISS] {root}')
