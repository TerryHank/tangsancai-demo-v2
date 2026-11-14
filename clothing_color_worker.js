importScripts('assets/vendor/tasks-vision/vision_bundle.js');

const assets = new URL('assets/vendor/tasks-vision/', self.location.href);
const model = Vision.FilesetResolver.forVisionTasks(new URL('wasm', assets).href)
    .then(files => Vision.ImageSegmenter.createFromOptions(files, {
        baseOptions: { modelAssetPath: new URL('selfie_multiclass_256x256.tflite', assets).href },
        runningMode: 'IMAGE',
        outputCategoryMask: true,
        outputConfidenceMasks: false
    }));
const canvas = new OffscreenCanvas(256, 256);
const context = canvas.getContext('2d', { willReadFrequently: true });
const colors = ['白', '黑', '灰', '红', '蓝'];

function colorBin(r, g, b) {
    const max = Math.max(r, g, b);
    const min = Math.min(r, g, b);
    const range = max - min;
    const saturation = max ? range / max : 0;
    if (max < 55) return 1;
    if (saturation < 0.21) return max > 178 ? 0 : max < 85 ? 1 : 2;

    let hue;
    if (max === r) hue = (g - b) / range;
    else if (max === g) hue = (b - r) / range + 2;
    else hue = (r - g) / range + 4;
    hue = (hue * 60 + 360) % 360;
    if (hue < 28 || hue >= 335) return 3;
    if (hue >= 172 && hue <= 265) return 4;
    return -1;
}

function insideTorso(x, y, torso) {
    let sign = 0;
    for (let i = 0; i < torso.length; i++) {
        const a = torso[i];
        const b = torso[(i + 1) % torso.length];
        const cross = (b.x - a.x) * (y - a.y) - (b.y - a.y) * (x - a.x);
        if (!cross) continue;
        if (sign && Math.sign(cross) !== sign) return false;
        sign = Math.sign(cross);
    }
    return sign !== 0;
}

async function sample({ image, joints }) {
    const bitmap = await createImageBitmap(image);
    const names = ['leftShoulder', 'rightShoulder', 'rightHip', 'leftHip'];
    const points = names.map(name => ({
        x: joints[name].x * bitmap.width,
        y: joints[name].y * bitmap.height
    }));
    const xs = points.map(point => point.x);
    const ys = points.map(point => point.y);
    // Keep head and some surrounding context for the selfie model.
    const span = Math.max(...xs) - Math.min(...xs);
    const height = Math.max(...ys) - Math.min(...ys);
    const size = Math.ceil(Math.max(span * 1.62, height * 1.75));
    if (size <= 1) {
        bitmap.close();
        return null;
    }
    const left = (Math.min(...xs) + Math.max(...xs) - size) / 2;
    const top = (Math.min(...ys) + Math.max(...ys) - size) / 2 - height * 0.18;
    context.fillStyle = '#000';
    context.fillRect(0, 0, 256, 256);
    context.drawImage(bitmap, left, top, size, size, 0, 0, 256, 256);
    bitmap.close();
    const rgb = context.getImageData(0, 0, 256, 256).data;
    const center = { x: xs.reduce((sum, x) => sum + x, 0) / 4, y: ys.reduce((sum, y) => sum + y, 0) / 4 };
    const torso = points.map(point => ({
        x: (point.x * 0.86 + center.x * 0.14 - left) * 256 / size,
        y: (point.y * 0.86 + center.y * 0.14 - top) * 256 / size
    }));
    const segmenter = await model;
    return new Promise(resolve => segmenter.segment(canvas, ({ categoryMask }) => {
        if (!categoryMask) return resolve(null);
        const mask = categoryMask.getAsUint8Array();
        const bins = Array(colors.length).fill(0);
        let pixels = 0;
        for (let y = 0; y < categoryMask.height; y++) {
            for (let x = 0; x < categoryMask.width; x++) {
                if (mask[y * categoryMask.width + x] !== 4) continue;
                const px = Math.min(255, Math.floor((x + 0.5) * 256 / categoryMask.width));
                const py = Math.min(255, Math.floor((y + 0.5) * 256 / categoryMask.height));
                if (!insideTorso(px + 0.5, py + 0.5, torso)) continue;
                const i = (py * 256 + px) * 4;
                const bin = colorBin(rgb[i], rgb[i + 1], rgb[i + 2]);
                if (bin >= 0) bins[bin]++;
                pixels++;
            }
        }
        resolve({ histogram: Object.fromEntries(colors.map((color, i) => [color, bins[i]])), pixels });
    }));
}

model.then(() => self.postMessage({ ready: true }), error => self.postMessage({ error: error.message }));
self.onmessage = async ({ data }) => {
    const { frameId, round } = data;
    try {
        self.postMessage({ frameId, round, result: await sample(data) });
    } catch (error) {
        self.postMessage({ frameId, round, error: error.message });
    }
};
