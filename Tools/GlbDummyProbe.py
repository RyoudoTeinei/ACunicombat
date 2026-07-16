#!/usr/bin/env python3
"""Probe dummy/node world axes inside an exported GLB, at rest and at animation frame 0.

Usage:
    python Tools/GlbDummyProbe.py <file.glb> [name-filter ...] [--anim <substring>]

Prints each matching node's world position, forward (+Z) and up (+Y) in glTF space
(right-handed, Y-up — identical to the AC6PartToGltf output space, before glTFast's
x-mirror import). Pass one or more name filters (default: "dmy"). With --anim, also
prints the same axes with the first frame of the first animation whose name contains
the substring applied (rotation channels only), so display-pose sign questions can be
settled numerically instead of visually.
"""
import json
import math
import struct
import sys


def read_glb(path):
    with open(path, 'rb') as f:
        data = f.read()
    magic, _, length = struct.unpack_from('<III', data, 0)
    if magic != 0x46546C67:
        raise SystemExit(f'not a GLB: {path}')
    off = 12
    js = None
    bin_ = None
    while off < length:
        clen, ctype = struct.unpack_from('<II', data, off)
        off += 8
        chunk = data[off:off + clen]
        if ctype == 0x4E4F534A:
            js = json.loads(chunk.decode('utf-8'))
        elif ctype == 0x004E4942:
            bin_ = chunk
        off += clen
    return js, bin_


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def qrot(q, v):
    x, y, z, w = q

    def cross(a, b):
        return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])

    t = cross((x, y, z), v)
    t = (2 * t[0], 2 * t[1], 2 * t[2])
    c2 = cross((x, y, z), t)
    return (v[0] + w * t[0] + c2[0], v[1] + w * t[1] + c2[1], v[2] + w * t[2] + c2[2])


def accessor_values(gltf, bin_, index):
    acc = gltf['accessors'][index]
    view = gltf['bufferViews'][acc['bufferView']]
    base = view.get('byteOffset', 0) + acc.get('byteOffset', 0)
    ncomp = {'SCALAR': 1, 'VEC3': 3, 'VEC4': 4}[acc['type']]
    stride = view.get('byteStride', 4 * ncomp)
    fmt = '<' + 'f' * ncomp
    return [struct.unpack_from(fmt, bin_, base + k * stride) for k in range(acc['count'])]


def main():
    args = [a for a in sys.argv[1:]]
    anim_filter = None
    if '--anim' in args:
        i = args.index('--anim')
        anim_filter = args[i + 1]
        del args[i:i + 2]
    if not args:
        raise SystemExit(__doc__)
    path = args[0]
    filters = args[1:] or ['dmy']

    gltf, bin_ = read_glb(path)
    nodes = gltf['nodes']
    parent = {}
    for i, nd in enumerate(nodes):
        for c in nd.get('children', []):
            parent[c] = i

    def world(i, override=None):
        chain = []
        k = i
        while k is not None:
            chain.append(k)
            k = parent.get(k)
        chain.reverse()
        pos, rot = (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0)
        for k in chain:
            nd = nodes[k]
            t = nd.get('translation', [0, 0, 0])
            r = nd.get('rotation', [0, 0, 0, 1])
            if override and k in override:
                r = override[k]
            tp = qrot(rot, t)
            pos = (pos[0] + tp[0], pos[1] + tp[1], pos[2] + tp[2])
            rot = qmul(rot, r)
        return pos, rot

    override = {}
    anim_name = None
    if anim_filter is not None:
        for an in gltf.get('animations', []):
            if anim_filter in (an.get('name') or ''):
                anim_name = an.get('name')
                for ch in an['channels']:
                    if ch['target']['path'] != 'rotation':
                        continue
                    sampler = an['samplers'][ch['sampler']]
                    override[ch['target']['node']] = accessor_values(gltf, bin_, sampler['output'])[0]
                break
        if anim_name is None:
            raise SystemExit(f'no animation matching "{anim_filter}" in {path}')

    print(f'# {path}  (glTF space: right-handed, Y-up; fwd = node +Z, up = node +Y)')
    for i, nd in enumerate(nodes):
        name = nd.get('name', f'#{i}')
        if not any(f.lower() in name.lower() for f in filters):
            continue
        pos, rot = world(i)
        fwd = qrot(rot, (0, 0, 1))
        up = qrot(rot, (0, 1, 0))
        line = (f'{name:28s} pos=({pos[0]:+.3f},{pos[1]:+.3f},{pos[2]:+.3f}) '
                f'fwd=({fwd[0]:+.3f},{fwd[1]:+.3f},{fwd[2]:+.3f}) up=({up[0]:+.3f},{up[1]:+.3f},{up[2]:+.3f})')
        if override:
            fpos, frot = world(i, override)
            ffwd = qrot(frot, (0, 0, 1))
            pitch = math.degrees(math.asin(max(-1.0, min(1.0, ffwd[1]))))
            line += (f'\n{"":28s} [{anim_name} f0] fwd=({ffwd[0]:+.3f},{ffwd[1]:+.3f},{ffwd[2]:+.3f})'
                     f' pitch={pitch:+.2f}deg')
        print(line)


if __name__ == '__main__':
    main()
