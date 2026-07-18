# -*- coding: utf-8 -*-
"""AC6 BHV 行为图状态表解析器。

把三份数据接成一张"官方语义表"：
  1. .bhv 二进制（chr/cXXXX.behbnd.dcx 内，BND4+Oodle 解出）——状态表/动画表/转换表；
  2. soulsmods/ac6-bhv-debug 的调试 JSON（Tools/BhvDebug/）——按索引 1:1 的官方状态名；
  3. TAE 转储（AC6AnimExportProbe --dump-tae）——TAE anim ID → HKX 文件名。

逆向出的格式（版本 0x0005/0x0003，小端；已在 basenormal.bhv v1.03 对齐数据上验证：
222/222 状态转换数吻合、5746/5746 可解析转换目标吻合）：

  头部  0x00 u16 版本 0x0005, u16 0x0003；0x04 u32 文件大小
        0x20 u32 图区起始 G；0x24 u32 状态数 S；0x28 u32 动画表偏移 A；0x2C u32 动画数 N
        0x30 u32 附加块偏移；0x34 u16 ?, u16 骨骼数；0x38 u32 骨骼名表偏移
  状态表  G+0x20 起，每状态 0x80 字节：
        +0x00 u16 状态索引, u16 旗标
        +0x08 u32 转换块偏移 T（文件绝对）
        +0x0C u32 转换数
        +0x10 u16 动画条目索引（0xFFFF=无）
        +0x16 u16 类别字段（观测值 3=移动系, 14=动作系……语义未定）
  转换块  T+0x20 起，每条 0x20 字节：
        +0x00 u16 目标状态索引
        +0x04 u32 条件列表偏移；+0x08 u32 条件数
  动画表  A 起，每条 0x40 字节；TAE anim ID 在 +0x20 或 +0x3C（0xFFFFFFFF=空，取非空者）。
        +0x00 起为 float 混合参数（0.2f 常见值=blend 时长量级，语义待核）

用法：
  python Tools/ParseBhvStateTable.py <bhv> <dbg.json> [tae_dump.json] [--tae-file-prefix 5000000] [-o out.json]
例：
  python Tools/ParseBhvStateTable.py <解包目录>/basenormal.bhv Tools/BhvDebug/c0000/basenormal_dbg.json \
      Tools/LocalOut/c0000_tae_dump.json --tae-file-prefix 5000000 -o Tools/BhvDebug/c0000/basenormal_states.json

版本警告：调试 JSON 依赖与编译 bhv 的 1:1 索引（1.03）。本脚本用"逐状态转换数 +
转换目标名"双重校验对齐，任何一项失配即报错退出，不产出错表。
"""

import argparse
import json
import os
import re
import struct
import sys


def u16(data, o):
    return struct.unpack_from("<H", data, o)[0]


def u32(data, o):
    return struct.unpack_from("<I", data, o)[0]


STRIP_RE = re.compile(r"《[^》]*》")


def strip_name(name):
    return STRIP_RE.sub("", name.replace("\\", "")).strip()


def parse_bhv(data, dbg_states):
    graph_start = u32(data, 0x20)
    state_count = u32(data, 0x24)
    anim_base = u32(data, 0x28)
    anim_count = u32(data, 0x2C)

    if state_count != len(dbg_states):
        raise SystemExit(
            f"状态数不匹配: bhv={state_count} dbg={len(dbg_states)} —— 调试数据版本漂移，索引对名不可用")

    names = [s["StateName"] for s in dbg_states]
    name_to_idx = {}
    for i, n in enumerate(names):
        name_to_idx.setdefault(strip_name(n), i)

    def anim_tae_id(entry):
        if entry == 0xFFFF:
            return None
        a = u32(data, anim_base + entry * 0x40 + 0x20)
        b = u32(data, anim_base + entry * 0x40 + 0x3C)
        if a != 0xFFFFFFFF:
            return a
        if b != 0xFFFFFFFF:
            return b
        return None

    base = graph_start + 0x20
    states = []
    checked = matched = 0
    for i in range(state_count):
        o = base + i * 0x80
        trans_count = u32(data, o + 0x0C)
        dbg_next = dbg_states[i]["NextStates"]
        if trans_count != len(dbg_next):
            raise SystemExit(
                f"状态 {i} 转换数不匹配: bhv={trans_count} dbg={len(dbg_next)} —— 版本漂移")

        toff = u32(data, o + 0x08) + 0x20
        transitions = []
        for k in range(trans_count):
            target = u16(data, toff + k * 0x20)
            cond_count = u32(data, toff + k * 0x20 + 0x08)
            expect = name_to_idx.get(strip_name(dbg_next[k]["NextStateName"]))
            if expect is not None:
                checked += 1
                if expect == target:
                    matched += 1
            transitions.append({
                "target": target,
                "targetName": names[target] if target < state_count else f"#{target}",
                "conditionCount": cond_count,
                "conditions": dbg_next[k].get("Conditions", []),
            })

        anim_entry = u16(data, o + 0x10)
        states.append({
            "index": i,
            "name": names[i],
            "animEntry": None if anim_entry == 0xFFFF else anim_entry,
            "taeAnimId": anim_tae_id(anim_entry),
            "category": u16(data, o + 0x16),
            "transitions": transitions,
        })

    if checked and matched != checked:
        raise SystemExit(f"转换目标校验失败: {matched}/{checked} —— 结构或版本漂移")

    return {
        "stateCount": state_count,
        "animCount": anim_count,
        "targetValidation": f"{matched}/{checked}",
        "states": states,
    }


def attach_hkx_names(result, tae_dump_path, file_prefix):
    doc = json.load(open(tae_dump_path, encoding="utf-8"))
    tae_map = {}
    for f in doc["taeFiles"]:
        if os.path.basename(f["file"]).startswith(file_prefix):
            for a in f["animations"]:
                tae_map[a["id"]] = a["name"]
    for s in result["states"]:
        tid = s["taeAnimId"]
        s["hkxName"] = tae_map.get(tid) if tid is not None else None
    return sum(1 for s in result["states"] if s.get("hkxName"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("bhv")
    ap.add_argument("dbg_json")
    ap.add_argument("tae_dump", nargs="?")
    ap.add_argument("--tae-file-prefix", default="5000000")
    ap.add_argument("-o", "--out")
    args = ap.parse_args()

    data = open(args.bhv, "rb").read()
    dbg = json.load(open(args.dbg_json, encoding="utf-8-sig"))
    result = parse_bhv(data, dbg["AllStates"])
    print(f"states={result['stateCount']} anims={result['animCount']} "
          f"target-validation={result['targetValidation']}")

    if args.tae_dump:
        resolved = attach_hkx_names(result, args.tae_dump, args.tae_file_prefix)
        print(f"hkx resolved: {resolved}/{result['stateCount']}")

    if args.out:
        json.dump(result, open(args.out, "w", encoding="utf-8"),
                  ensure_ascii=False, indent=1)
        print("written:", args.out)


if __name__ == "__main__":
    sys.exit(main())
