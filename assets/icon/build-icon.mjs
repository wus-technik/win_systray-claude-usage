import sharp from 'sharp';
import pngToIco from 'png-to-ico';
import { readFileSync, writeFileSync } from 'node:fs';

const svg = readFileSync(process.argv[2]);
const out = process.argv[3];
const sizes = [16, 24, 32, 48, 64, 128, 256];

// Render big, trim the SVG's empty top/bottom margin, then letterbox into a square
// with a small breathing margin so every size fills its canvas the same way.
const base = await sharp(svg, { density: 600 })
  .resize(1024, 1024, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
  .png().toBuffer();
const trimmed = await sharp(base).trim({ threshold: 1 }).png().toBuffer();
const meta = await sharp(trimmed).metadata();
console.log('trimmed', meta.width, meta.height);

const buffers = [];
for (const s of sizes) {
  const inner = s <= 24 ? s : Math.round(s * 0.94);
  const buf = await sharp(trimmed)
    .resize(inner, inner, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
    .extend({
      top: Math.floor((s - inner) / 2), bottom: Math.ceil((s - inner) / 2),
      left: Math.floor((s - inner) / 2), right: Math.ceil((s - inner) / 2),
      background: { r: 0, g: 0, b: 0, alpha: 0 },
    })
    .png().toBuffer();
  writeFileSync(`${out}.${s}.png`, buf);
  buffers.push(buf);
}
writeFileSync(out, await pngToIco(buffers));
console.log('wrote', out);
