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

function scalarExtent(prop, fallback = 1) {
    const kfs = keyframesOf(prop);
    if (!kfs || kfs.length === 0) return num(prop) || fallback;
    let result = fallback;
    for (const key of kfs) result = Math.max(result, Math.abs(num(key.value)));
    return result;
}

function firstTexture(app) {
    return app.texture ?? app.layer1 ?? app.mask ?? 0;
}

function firstColor(app) {
    const kfs = keyframesOf(app.color1);
    if (kfs && kfs.length > 0 && Array.isArray(kfs[0].value)) return kfs[0].value;
    return Array.isArray(app.color1) ? app.color1 : [1, 1, 1, 1];
}

function appearanceLife(entry, fallback = 0.25) {
    const app = entry.appearance || {};
    const nodeDuration = num(entry.nodeAttributes?.duration);
    const particleDuration = num(entry.particleAttributes?.duration);
    const candidates = [nodeDuration, particleDuration];
    for (const value of Object.values(app)) candidates.push(lastPosition(keyframesOf(value)));
    return Math.max(fallback, ...candidates.filter(v => Number.isFinite(v) && v > 0));
}

function frameBounds(prop) {
    const values = (keyframesOf(prop) || []).map(keyframe => num(keyframe.value));
    let min = values.length > 0 ? Math.min(...values) : num(prop);
    let max = values.length > 0 ? Math.max(...values) : min;

    if (prop && Array.isArray(prop.modifiers)) {
        const randomRange = prop.modifiers.find(modifier => modifier?.type === 'RandomRange');
        if (randomRange) {
            min += num(randomRange.min);
            max += num(randomRange.max);
        }
    }

    return { min, max };
}

function atlasInfo(app) {
    const columns = Math.max(1, Math.trunc(num(app.columns) || 1));
    const totalFrames = Math.max(1, Math.trunc(num(app.totalFrames) || 1));
    const frameIndex = frameBounds(app.frameIndex);
    const frameOffset = frameBounds(app.frameIndexOffset);
    const frameMin = Math.max(0, Math.trunc(frameIndex.min + frameOffset.min));
    const frameMax = Math.max(frameMin, Math.trunc(frameIndex.max + frameOffset.max));

    return {
        columns,
        totalFrames,
        frameMin: Math.min(frameMin, totalFrames - 1),
        frameMax: Math.min(frameMax, totalFrames - 1),
    };
}

function emissionCount(entry) {
    return Math.max(1, Math.trunc(num(entry.emitter?.perEmission) || 1));
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

    if (kind === 'Tracer') {
        const life = appearanceLife(entry, app.fadeOutTime ?? 0.2);
        return {
            kind: 'Tracer',
            texture: firstTexture(app),
            blendMode: app.blendMode ?? 4,
            color: firstColor(app),
            width: scalarExtent(app.width, 0.08) * Math.max(num(app.widthMultiplier), 1),
            lifeSeconds: life,
        };
    }

    if (kind === 'Model' || kind === 'RichModel') {
        return {
            kind: 'Model',
            model: app.model ?? 0,
            color: firstColor(app),
            size: [
                scalarExtent(app.sizeX, 0.2),
                scalarExtent(app.sizeY, 0.2),
                scalarExtent(app.sizeZ, 0.8),
            ],
            lifeSeconds: appearanceLife(entry, 0.25),
            burstCount: emissionCount(entry),
            ...atlasInfo(app),
        };
    }

    if (kind === 'QuadLine') {
        return {
            kind: 'Beam',
            texture: firstTexture(app),
            blendMode: app.blendMode ?? 4,
            color: firstColor(app),
            width: scalarExtent(app.width, 0.08),
            length: scalarExtent(app.length, 1),
            lifeSeconds: appearanceLife(entry, 0.2),
        };
    }

    if (kind !== 'BillboardEx' && kind !== 'MultiTextureBillboardEx' && kind !== 'LensFlare') return null;

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
        texture: firstTexture(app),
        blendMode: app.blendMode ?? 4,
        lifeSeconds: life,
        burstCount: emissionCount(entry),
        ...atlasInfo(app),
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
    const proxyReferences = [];
    let index = 0;
    for (const node of fxr.root.walk()) {
        if (node.constructor.name === 'ProxyNode') {
            const proxy = typeof node.toJSON === 'function' ? node.toJSON() : null;
            if (proxy?.sfx > 0) proxyReferences.push(proxy.sfx);
            continue;
        }
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

    const out = { id: fxr.id, emitterCount: emitters.length, proxyReferences, emitters };
    out.unityEmitters = emitters.map(e => e.unity).filter(u => u != null);
    const outPath = path.join(outDir, `ffx_${fxr.id}.json`);
    fs.writeFileSync(outPath, JSON.stringify(out, null, 1));
    console.log(`${path.basename(file)} -> ffx_${fxr.id}.json (${emitters.length} emitters)`);
}
