"""リリースビルドして配布物を dist に作る。
  dist/EffectivePosition.ymme               … YMM4 のインストーラで入れる 1 ファイル
  dist/YMM4-EffectivePosition-v<版>.zip     … 配布用（GitHub Release・Discord 共通）
使い方: python build.py
"""
import os, re, subprocess, zipfile, hashlib

ROOT = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.join(ROOT, "src", "EffectivePosition")
DLL = os.path.join(PROJ, "bin", "Release", "net10.0-windows10.0.19041.0", "EffectivePosition.dll")
DIST = os.path.join(ROOT, "dist")
VERSION = re.search(r"<Version>(.+?)</Version>", open(os.path.join(PROJ, "EffectivePosition.csproj"), encoding="utf-8").read()).group(1)

env = dict(os.environ)
dotnet = os.path.join(os.path.expanduser("~"), ".dotnet", "dotnet.exe")
if os.path.exists(dotnet):
    env["DOTNET_ROOT"] = os.path.dirname(dotnet)
else:
    dotnet = "dotnet"
subprocess.run([dotnet, "build", "-c", "Release", "--nologo", "-v", "q"], cwd=PROJ, env=env, check=True)


def text(path):
    # BOM 付き UTF-8・CRLF（メモ帳でも YMM4 のインストーラでも化けないように）
    s = open(path, encoding="utf-8").read().replace("\r\n", "\n").replace("\n", "\r\n")
    return "﻿".encode("utf-8") + s.encode("utf-8")


def write_zip(path, entries):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for arc, data in entries:
            info = zipfile.ZipInfo(arc, date_time=(2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)
    print(os.path.basename(path), os.path.getsize(path), "bytes")


dll = open(DLL, "rb").read()
readme = text(os.path.join(ROOT, "package", "README.txt"))
license_ = text(os.path.join(ROOT, "LICENSE"))

os.makedirs(DIST, exist_ok=True)
for old in os.listdir(DIST):
    os.remove(os.path.join(DIST, old))

# .ymme は直下に置く。YMM4 のインストーラは .ymme のファイル名をプラグインのフォルダ名にする
ymme = os.path.join(DIST, "EffectivePosition.ymme")
write_zip(ymme, [("EffectivePosition.dll", dll), ("README.txt", readme), ("LICENSE.txt", license_)])

# 配布 zip: .ymme ＋ 説明 ＋ 手動インストール用フォルダ（名前は ASCII にして展開ソフトによる文字化けを避ける）
write_zip(os.path.join(DIST, f"YMM4-EffectivePosition-v{VERSION}.zip"), [
    ("EffectivePosition.ymme", open(ymme, "rb").read()),
    ("README.txt", readme),
    ("LICENSE.txt", license_),
    ("manual-install/EffectivePosition/EffectivePosition.dll", dll),
])
print("dll sha256", hashlib.sha256(dll).hexdigest())
