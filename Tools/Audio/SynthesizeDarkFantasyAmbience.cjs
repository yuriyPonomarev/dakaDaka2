// Original deterministic dark-fantasy chamber arrangement; no external samples.
const fs = require('node:fs');
const rate = 48000, duration = 48, frames = rate * duration, tau = Math.PI * 2;
if (!process.argv[2]) throw new Error('Usage: node Tools/Audio/SynthesizeDarkFantasyAmbience.cjs output.wav');
const left = new Float32Array(frames), right = new Float32Array(frames);
const hz = midi => 440 * 2 ** ((midi - 69) / 12);
const wrap = i => (i % frames + frames) % frames;
function noise(i, seed) {
    let x = Math.imul(i ^ seed, 0x45d9f3b);
    x = Math.imul(x ^ (x >>> 16), 0x45d9f3b);
    return ((x ^ (x >>> 16)) >>> 0) / 2147483648 - 1;
}
const tables = {};
function table(name, harmonics) {
    const data = new Float32Array(4097);
    for (let i = 0; i <= 4096; i++)
        for (let h = 0; h < harmonics.length; h++)
            data[i] += harmonics[h] * Math.sin(tau * (h + 1) * i / 4096);
    tables[name] = data;
}
table('cello', [1, .34, .22, .13, .08, .048, .026, .015]);
table('viola', [1, .38, .21, .12, .07, .035, .02]);
table('horn', [1, .28, .16, .065, .025]);
table('flute', [1, .13, .055, .018]);
table('harp', [1, .40, .20, .085, .045, .018]);
function wave(name, phase) {
    const p = (phase - Math.floor(phase)) * 4096, i = Math.floor(p);
    return tables[name][i] + (tables[name][i + 1] - tables[name][i]) * (p - i);
}
function add(i, value, pan) {
    left[wrap(i)] += value * Math.sqrt((1 - pan) * .5);
    right[wrap(i)] += value * Math.sqrt((1 + pan) * .5);
}
function note(instrument, midi, start, sustain, gain, pan, seed) {
    const pluck = instrument === 'harp';
    const release = pluck ? 2.8 : instrument === 'flute' ? .65 : 1.25;
    const attack = pluck ? .008 : instrument === 'horn' ? .48 : instrument === 'flute' ? .13 : .65;
    const length = sustain + release, pitch = hz(midi), offset = Math.round(start * rate);
    let phase = 0, phase2 = .173, breath = 0;
    for (let i = 0; i < length * rate; i++) {
        const t = i / rate;
        const onset = Math.sin(Math.min(1, t / attack) * Math.PI * .5) ** 2;
        const tail = t < sustain ? 1 : Math.cos(Math.min(1, (t - sustain) / release) * Math.PI * .5) ** 2;
        const envelope = onset * tail * (pluck ? Math.exp(-t * 1.6) : .84 + .16 * Math.sin(Math.PI * Math.min(t / length, 1)));
        const vibrato = pluck ? 0 : .0018 * Math.sin(tau * 4.7 * t + seed) * Math.min(1, t * 1.3);
        phase += pitch * (1 + vibrato) / rate;
        phase2 += pitch * (1.0018 + vibrato * .7) / rate;
        let tone = wave(instrument, phase);
        if (instrument === 'cello' || instrument === 'viola') tone = .72 * tone + .28 * wave(instrument, phase2);
        breath += .16 * (noise(i, seed) - breath);
        if (instrument === 'flute') tone += .08 * breath;
        else if (pluck) tone += .08 * breath * Math.exp(-t * 55);
        add(offset + i, gain * envelope * tone, pan);
    }
}
function drum(start, gain, seed) {
    const offset = Math.round(start * rate);
    let phase = 0, skin = 0;
    for (let i = 0; i < rate * 1.6; i++) {
        const t = i / rate;
        phase += (58 + 39 * Math.exp(-t * 22)) / rate;
        skin += .09 * (noise(i, seed) - skin);
        const attack = Math.min(1, t * 500);
        const tone = Math.sin(tau * phase) * Math.exp(-t * 5.6) + .22 * skin * Math.exp(-t * 18);
        add(offset + i, tone * attack * gain, .12);
    }
}
// Twelve bars at 60 BPM: D minor, B-flat, G minor, A dominant.
// Bowed bass/viola sustain harmony; woodwind develops three melodic phrases.
const chords = [[38, 50, 53, 57], [34, 50, 53, 58], [31, 50, 55, 58], [33, 49, 52, 57]];
const melody = [
    [0.5, 69, 1.2], [2.25, 65, 1.0], [4.5, 62, 2.1], [7, 65, .8],
    [8.5, 67, 1.4], [10.5, 65, .9], [12.5, 64, 1.0], [14, 61, 1.6],
    [16.5, 62, 1.2], [18.25, 65, 1.1], [20.5, 69, 1.5], [23, 70, .75],
    [24.5, 69, 1.0], [26, 67, 1.5], [28.5, 64, 1.3], [30.25, 61, 1.0],
    [32.5, 65, 1.2], [34.25, 64, 1.0], [36.5, 62, 2.1], [39, 65, .65],
    [40.5, 67, 1.2], [42.25, 62, 1.0], [44.5, 61, 1.4], [46.5, 64, .8],
];
for (let bar = 0; bar < 12; bar++) {
    const chord = chords[bar % 4], at = bar * 4;
    note('cello', chord[0], at - .2, 3.5, .13, -.24, 100 + bar);
    note('viola', chord[1], at + .12, 3.35, .046, -.45, 200 + bar);
    note('viola', chord[2], at + .18, 3.25, .037, .44, 300 + bar);
    note('horn', chord[0] + 12, at + .3, 2.8, .038, .18, 400 + bar);
    // Space between plucks leaves room for footsteps and interaction cues.
    [0, 1.5, 2.5].forEach((beat, n) =>
        note('harp', chord[1 + (n + bar) % 3] + 12, at + beat + .025, .5, .075, n % 2 ? .5 : -.5, 500 + bar * 3 + n));
    if (bar % 2 === 0) drum(at, .078, 700 + bar);
    if (bar % 4 === 3) drum(at + 2.5, .035, 800 + bar);
}
melody.forEach(([at, midi, hold], i) => note('flute', midi, at, hold, .105, .08, 900 + i));

// Quiet, exactly periodic air bed under the instruments.
for (let i = 0; i < frames; i++) {
    const angle = tau * i / frames;
    const air = .003 * Math.sin(131 * angle + .3 * Math.sin(3 * angle))
        + .002 * Math.sin(227 * angle + .4 * Math.sin(5 * angle));
    left[i] += air; right[i] += air;
}
// Circular room reflections preserve note/reverb tails across the loop boundary.
const dryLeft = left.slice(), dryRight = right.slice();
for (const [seconds, gain, cross] of [[.193, .16, true], [.367, .13, false], [.613, .12, true], [1.079, .10, false], [1.681, .075, true], [2.393, .052, false]]) {
    const delay = Math.round(seconds * rate);
    let l = 0, r = 0;
    for (let i = -rate; i < frames; i++) {
        const from = wrap(i - delay);
        l += .13 * ((cross ? dryRight[from] : dryLeft[from]) - l);
        r += .13 * ((cross ? dryLeft[from] : dryRight[from]) - r);
        if (i >= 0) { left[i] += l * gain; right[i] += r * gain; }
    }
}
let peak = 0;
for (let i = 0; i < frames; i++) peak = Math.max(peak, Math.abs(left[i]), Math.abs(right[i]));
const level = 10 ** (-3.5 / 20) / peak;
const clip = Buffer.alloc(44 + frames * 4);
clip.write('RIFF', 0); clip.writeUInt32LE(36 + frames * 4, 4); clip.write('WAVEfmt ', 8);
clip.writeUInt32LE(16, 16); clip.writeUInt16LE(1, 20); clip.writeUInt16LE(2, 22);
clip.writeUInt32LE(rate, 24); clip.writeUInt32LE(rate * 4, 28);
clip.writeUInt16LE(4, 32); clip.writeUInt16LE(16, 34); clip.write('data', 36); clip.writeUInt32LE(frames * 4, 40);
let power = 0;
for (let i = 0; i < frames; i++) {
    const l = left[i] * level, r = right[i] * level;
    clip.writeInt16LE(Math.round(l * 32767), 44 + i * 4);
    clip.writeInt16LE(Math.round(r * 32767), 46 + i * 4);
    power += l * l + r * r;
}
fs.writeFileSync(process.argv[2], clip);
console.log(JSON.stringify({ seconds: duration, channels: 2, rate, peakDbFS: -3.5,
    rmsDbFS: 20 * Math.log10(Math.sqrt(power / (2 * frames))), instruments: ['cello', 'viola', 'low horn', 'woodwind', 'harp', 'frame drum'] }));
