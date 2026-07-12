// Extract AC6 FXR effect definitions to flat JSON for the Unity-side effect factory.
//
// Usage: node extract-fxr.mjs <outDir> <fxrFile...>
//
// Output schema per effect: { id, emitters: [ { path, attributes, transform, emitter,
// emitterShape, particleAttributes, particleModifier, appearance: { kind, ...toJSON } } ] }
// Only slots the Unity factory understands are interpreted; everything is passed through
// verbatim from @cccode/fxr's toJSON so nothing is lost for later iterations.
import * as fxrlib from '@cccode/fxr';
import fs from 'fs';
import path from 'path';

const [outDir, ...files] = process.argv.slice(2);
if (!outDir || files.length === 0) {
    console.error('usage: node extract-fxr.mjs <outDir> <fxrFile...>');
    process.exit(2);
}
fs.mkdirSync(outDir, { recursive: true });

const slotNames = [
    'nodeAttributes', 'nodeTransform', 'nodeMovement', 'emitter', 'emitterShape',
    'directionSpread', 'particleModifier', 'particleAttributes', 'appearance',
    'particleMovement',
];

// --- Unity-friendly simplification -------------------------------------------------------
// Curves and polymorphic properties get resolved HERE so the Unity factory can stay dumb:
// fixed-size sampled curves, plain gradients, one texture id, one blend mode.

function keyframesOf(prop) {
    if (prop == null) return null;
    if (typeof prop === 'number') return [{ position: 0, value: prop }];
    if (Array.isArray(prop)) return [{ position: 0, value: prop }];
    if (prop.keyframes) return prop.keyframes;
    return null;
}

function lastPosition(kfs) {
    return kfs && kfs.length ? kfs[kfs.length - 1].position : 0;
}

function sampleScalar(kfs, t) {
    if (!kfs || kfs.length === 0) return null;
    if (t <= kfs[0].position) return num(kfs[0].value);
    for (let i = 1; i < kfs.length; i++) {
        if (t <= kfs[i].position) {
            const a = kfs[i - 1], b = kfs[i];
            const f = (t - a.position) / Math.max(b.position - a.position, 1e-6);
            return num(a.value) + (num(b.value) - num(a.value)) * f;
        }
    }
    return num(kfs[kfs.length - 1].value);
}

function num(v) { return Array.isArray(v) ? v[0] : (typeof v === 'number' ? v : 0); }

function gradientOf(colorProp, life) {
    const kfs = keyframesOf(colorProp);
    if (!kfs) return null;
    return kfs.map(k => ({
        t: life > 0 ? Math.min(k.position / life, 1) : 0,
        rgba: Array.isArray(k.value) ? k.value : [k.value, k.value, k.value, 1],
    }));
}

function simplifyForUnity(entry) {
    const app = entry.appearance || {};
    const kind = entry.kind;
    if (kind === 'PointLight') {
        const peakKfs = keyframesOf(app.diffuseMultiplier);
        let peak = 1;
        if (peakKfs) for (const k of peakKfs) peak = Math.max(peak, num(k.value));
        return {
            kind: 'Light',
            color: app.diffuseColor ?? [1, 1, 1, 1],
            radius: num(app.radius ?? 5),
            peakIntensity: peak,
            lifeSeconds: Math.max(lastPosition(peakKfs), 0.2),
        };
    }

    if (kind !== 'BillboardEx' && kind !== 'MultiTextureBillboardEx') return null;

    const widthKfs = keyframesOf(app.width);
    const colorKfs = keyframesOf(app.color1);
    const life = Math.max(lastPosition(widthKfs), lastPosition(colorKfs), 0.3);
    const samples = 6;
    const sizeCurve = [];
    for (let i = 0; i <= samples; i++) {
        const t = life * i / samples;
        sizeCurve.push({ t: i / samples, value: sampleScalar(widthKfs, t) ?? 0.2 });
    }

    return {
        kind: 'Billboard',
        texture: app.texture ?? app.mask ?? 0,
        blendMode: app.blendMode ?? 4,
        lifeSeconds: life,
        sizeCurve,
        heightScale: typeof app.height === 'number' ? app.height : 1,
        gradient: gradientOf(app.color1, life) ?? [{ t: 0, rgba: [1, 1, 1, 1] }, { t: 1, rgba: [1, 1, 1, 0] }],
    };
}

for (const file of files) {
    const buf = fs.readFileSync(file);
    const fxr = fxrlib.FXR.read(
        buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength),
        fxrlib.Game.ArmoredCore6);

    const emitters = [];
    let index = 0;
    for (const node of fxr.root.walk()) {
        if (node.constructor.name !== 'BasicNode' || !node.configs) continue;
        for (const config of node.configs) {
            const entry = { path: `node${index}`, kind: null };
            for (const slot of slotNames) {
                const action = config[slot];
                if (!action) continue;
                const json = typeof action.toJSON === 'function' ? action.toJSON() : null;
                if (json == null) continue;
                entry[slot] = json;
                if (slot === 'appearance') entry.kind = action.constructor.name;
            }
            if (entry.kind) {
                entry.unity = simplifyForUnity(entry);
                emitters.push(entry);
            }
        }
        index++;
    }

    const out = { id: fxr.id, emitterCount: emitters.length, emitters };
    out.unityEmitters = emitters.map(e => e.unity).filter(u => u != null);
    const outPath = path.join(outDir, `ffx_${fxr.id}.json`);
    fs.writeFileSync(outPath, JSON.stringify(out, null, 1));
    console.log(`${path.basename(file)} -> ffx_${fxr.id}.json (${emitters.length} emitters)`);
}
