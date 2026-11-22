'use strict';
// Retained historical implementation. The user rejected this synthesized bank.
// Never overwrite the replacement recordings through an old generation command.
console.error('Deprecated: use python -X utf8 Tools/Audio/BuildRecordedFoley.py (or --verify). The recorded bank was not changed.');
process.exit(1);
// Original deterministic Foley / modal / string / nonverbal vocal synthesis. No external samples.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const ROOT = path.resolve(__dirname, '../..');
const OUT = path.join(ROOT, 'Assets/Emberdelve/Resources/Emberdelve/Audio/Sfx');
const REVIEW = path.join(ROOT, 'Logs/acceptance/game-audio-20261122');
const RATE = 48000, TAU = 2 * Math.PI, VERSION = 1;
const verify = process.argv.includes('--verify');
const sha = data => crypto.createHash('sha256').update(data).digest('hex');
const seedOf = s => parseInt(sha(s).slice(0, 8), 16) >>> 0;
function random(seed) {
    let a = seed >>> 0;
    return () => { a ^= a << 13; a ^= a >>> 17; a ^= a << 5; return (a >>> 0) / 4294967296; };
}
const clamp = (x, lo, hi) => Math.min(hi, Math.max(lo, x));
const db = x => x > 0 ? 20 * Math.log10(x) : -160;

// gain is an authored category-relative mix, never a per-file loudness-normalization target.
const definitions = {
    Swing: ['weapon', .57, .80, 'Blade air cut, grip leather and trailing edge hiss'],
    Impact: ['impact', .64, .92, 'Layered bodily thump, cloth tear and short gritty impact'],
    Metal: ['impact', 1.03, .79, 'Hard iron strike with irregular decaying modes'],
    Step: ['movement', .45, .51, 'Boot heel and sole on dusty stone with granular grit'],
    Bow: ['weapon', .78, .74, 'Tensioned string release, wooden limbs and arrow air'],
    Parry: ['guard', 1.26, .83, 'Sharp blade bind, bright irregular ring and scrape'],
    DoorCreak: ['interaction', 1.72, .55, 'Strained wooden door and stuttering iron hinge'],
    DoorThud: ['interaction', .88, .71, 'Heavy wooden close with latch and frame resonance'],
    StepWood: ['movement', .53, .49, 'Boot on timber with cavity knock and grain creak'],
    StepDirt: ['movement', .48, .48, 'Muted heel, granular earth compression and drag'],
    Jump: ['movement', .61, .43, 'Boot push-off, clothing tension and short exertion'],
    Land: ['movement', .72, .66, 'Paired boot landing, equipment settling and dust'],
    LandHeavy: ['movement', 1.07, .88, 'Weighty landing, displaced grit and buckles settling'],
    Cloth: ['equipment', .66, .30, 'Layered wool and leather movement'],
    Equip: ['equipment', .90, .53, 'Leather fastening, buckle click and equipment settle'],
    Draw: ['equipment', .78, .58, 'Steel drawn through leather mouth with restrained ring'],
    Reload: ['weapon', 1.12, .57, 'Ratchet, wooden mechanism and tensioned string latch'],
    DryFire: ['weapon', .35, .39, 'Dry trigger and unloaded mechanism; no projectile report'],
    Hurt: ['actor', .94, .73, 'Original synthesized nonverbal human-like pain grunt and breath'],
    Death: ['actor', 1.94, .78, 'Original synthesized falling nonverbal groan and final exhale'],
    EnemyHurt: ['actor', 1.00, .70, 'Original synthesized rough undead gasp with bone rattle'],
    EnemyDeath: ['actor', 2.14, .78, 'Original synthesized hollow exhale and collapsing dry bones'],
    Pickup: ['interaction', .56, .43, 'Leather handling, object tap and small metal contact'],
    Coins: ['interaction', 1.04, .50, 'Uneven cascade of small coin strikes into a pouch'],
    ChestOpen: ['interaction', 1.45, .58, 'Clasp, lid wood and short rusty hinge drag'],
    ChestClose: ['interaction', .94, .64, 'Wooden coffer lid and double clasp settle'],
    Lever: ['interaction', .89, .60, 'Iron lever scrape, detent and mechanism clunk'],
    SecretStone: ['interaction', 2.38, .70, 'Heavy masonry grind, granular dust and stone stop'],
    Portal: ['magic', 2.12, .65, 'Breathy cavernous fold, low resonances and scattered mineral shimmer'],
    Extract: ['result', 2.62, .62, 'Resolving bowed-metal and breath bloom with a restrained string flourish'],
    Fail: ['result', 1.83, .52, 'Descending hollow metal and fading breath; no electronic alarm'],
    UiHover: ['ui', .14, .11, 'Quiet leather contact'],
    UiClick: ['ui', .22, .22, 'Tactile wood and muted brass confirmation'],
    UiOpen: ['ui', .43, .25, 'Parchment and leather unfurl'],
    UiClose: ['ui', .35, .23, 'Soft book flap and clasp'],
    UiError: ['ui', .43, .29, 'Two muted unpitched latch knocks'],
    ItemMove: ['ui', .39, .32, 'Small padded item shift and buckle tick'],
    Potion: ['consumable', 1.54, .53, 'Cork release, irregular liquid gulps and bottle settling'],
    Bandage: ['consumable', 1.46, .44, 'Cloth pull, tear and two tight wraps'],
    SkillDash: ['skill', .79, .70, 'Forceful air displacement, boot impulse and equipment rush'],
    SkillCry: ['skill', 1.61, .84, 'Original synthesized wordless battle cry with chest and breath'],
    SkillShot: ['skill', .97, .76, 'Taut string snap, sharper arrow rush and mineral tail'],
    SkillBuff: ['skill', 1.83, .59, 'Warm breath swell, rubbed metal and subtle rising resonance'],
    ZoneWarning: ['warning', 1.89, .65, 'Two low ominous struck iron warnings with dusty bloom'],
    ArrowImpact: ['impact', .71, .66, 'Point penetration, wooden shaft vibration and grit'],
    ShieldBreak: ['guard', 1.48, .91, 'Layered plank fracture, metal straps and falling fragments'],
    BluntSwing: ['weapon', .70, .77, 'Heavy broad air displacement and creaking haft'],
    BluntImpact: ['impact', .91, .96, 'Deep weighty hit, wood resonance and rubble scatter'],
    Stab: ['weapon', .48, .70, 'Narrow thrust hiss, leather and sharp short penetration'],
};

class Sound {
    constructor(cue, variant) {
        this.rng = random(seedOf(`DakaDaka/SFX/v${VERSION}/${cue}/${variant}`));
        this.pitch = .94 + this.rng() * .12;
        this.duration = definitions[cue][1] * (.97 + .06 * this.rng());
        this.data = new Float64Array(Math.round(this.duration * RATE));
    }
    r(lo, hi) { return lo + this.rng() * (hi - lo); }
    put(i, value) { if (i >= 0 && i < this.data.length) this.data[i] += value; }
    noise(start, duration, gain, low = 0, high = 7000, attack = .003, decay = 5, swell = false, flutter = 0) {
        const begin = Math.round(start * RATE), n = Math.round(duration * RATE);
        const aHigh = 1 - Math.exp(-TAU * high / RATE), aLow = 1 - Math.exp(-TAU * low / RATE);
        let lp = 0, slow = 0;
        const phase = this.r(0, TAU);
        for (let j = 0; j < n; j++) {
            const t = j / RATE, u = j / n;
            const noise = (this.rng() + this.rng() + this.rng() - 1.5) * 1.25;
            lp += aHigh * (noise - lp); slow += aLow * (lp - slow);
            let env = swell ? Math.sin(Math.PI * u) ** 1.8 : (1 - Math.exp(-t / attack)) * Math.exp(-decay * u);
            env *= Math.min(1, (1 - u) * 18);
            if (flutter) env *= .70 + .30 * Math.sin(TAU * flutter * t + phase);
            this.put(begin + j, (lp - slow) * env * gain);
        }
    }
    modes(start, freqs, gain, decay, pitchDrop = 0) {
        const begin = Math.round(start * RATE);
        const phases = freqs.map(() => this.r(0, .35));
        const n = Math.min(this.data.length - begin, Math.round(decay * 7 * RATE));
        for (let j = 0; j < n; j++) {
            const t = j / RATE;
            let v = 0;
            for (let k = 0; k < freqs.length; k++) {
                const f = freqs[k] * this.pitch;
                const angle = TAU * (f * t + pitchDrop * f * .045 * (1 - Math.exp(-t / .045))) + phases[k];
                v += Math.sin(angle) * Math.exp(-t / (decay / (1 + k * .24))) / (1 + k * .8);
            }
            this.put(begin + j, gain * v * (1 - Math.exp(-t / .0008)));
        }
    }
    cloth(start, length, gain = .22) {
        this.noise(start, length, gain, 650, 6200, .01, 2, true, 31);
        this.noise(start + length * .29, length * .56, gain * .6, 1800, 10000, .004, 3, true, 73);
    }
    grit(start, length, gain, count = 15, hard = false) {
        for (let k = 0; k < count; k++) {
            const at = start + length * this.rng() ** 1.7;
            this.noise(at, this.r(.013, .052), gain * this.r(.4, 1), hard ? 1100 : 200,
                hard ? this.r(4500, 11000) : this.r(1700, 5000), .0007, this.r(3, 7));
        }
    }
    wood(start, gain = .25, heavy = false) {
        this.modes(start, heavy ? [73, 141, 293, 517, 981] : [161, 319, 579, 1127], gain, heavy ? .095 : .052, .11);
        this.noise(start, .15, gain * 1.1, 300, 5200, .0007, 7);
    }
    iron(start, gain = .23, bright = false, tail = .34) {
        const f = bright ? [943, 1471, 2179, 3181, 4667, 6113] : [367, 631, 1091, 1759, 2693];
        this.modes(start, f.map(x => x * this.r(.984, 1.016)), gain, tail);
        this.noise(start, .055, gain * 1.7, 1200, 11500, .0003, 6);
    }
    thud(start, gain = .3, weight = 1) {
        this.modes(start, [63 / weight, 117 / weight, 203 / weight], gain, .055 * weight, .45);
        this.noise(start, .13 * weight, gain * 1.1, 35, 680 / weight, .002, 6);
    }
    swish(start, length, gain = .45, heavy = false) {
        this.noise(start, length, gain, heavy ? 80 : 600, heavy ? 2700 : 8500, .004, 3, true, 12);
        this.noise(start + length * .37, length * .42, gain * .55, 1600, 10500, .002, 4, true);
        this.cloth(start, length * .48, gain * .18);
    }
    string(start, frequency, gain = .3, length = .55) {
        const delay = Math.max(8, Math.round(RATE / (frequency * this.pitch)));
        const ring = new Float64Array(delay);
        let low = 0;
        for (let i = 0; i < delay; i++) { low += .7 * ((this.rng() * 2 - 1) - low); ring[i] = low; }
        const begin = Math.round(start * RATE), n = Math.round(length * RATE);
        let index = 0;
        for (let j = 0; j < n; j++) {
            const current = ring[index], next = ring[(index + 1) % delay];
            ring[index] = (current + next) * .493;
            index = (index + 1) % delay;
            this.put(begin + j, current * gain * Math.exp(-j / (RATE * length * .5)) * Math.min(1, j / 35));
        }
    }
    hinge(start, length, gain = .20, metal = false) {
        const bursts = metal ? 9 : 7;
        for (let i = 0; i < bursts; i++) {
            const t = i / bursts;
            const f = (metal ? 207 : 125) * (1 + .43 * Math.sin(t * 5) + .16 * this.rng());
            this.modes(start + length * t, [f, f * 2.71, f * 4.13], gain * this.r(.35, .8), this.r(.025, .07), -.12);
            this.noise(start + length * t, length / bursts * 1.15, gain * .7, 180, 3300, .006, 2, false, 43);
        }
    }
    voice(start, length, gain, kind) {
        const base = kind === 'undead' ? 68 : kind === 'cry' ? 148 : kind === 'death' ? 105 : 126;
        const begin = Math.round(start * RATE), n = Math.round(length * RATE);
        let phase = 0;
        const jitterPhase = this.r(0, TAU);
        for (let j = 0; j < n; j++) {
            const t = j / RATE, u = j / n;
            const fall = kind === 'cry' ? .12 * Math.sin(u * Math.PI) : -.35 * u;
            const fundamental = base * this.pitch * (1 + fall + .023 * Math.sin(t * 41 + jitterPhase) + .012 * Math.sin(t * 79));
            phase += fundamental / RATE;
            const f1 = kind === 'undead' ? 440 : kind === 'cry' ? 780 : 570;
            const f2 = kind === 'undead' ? 1190 : 1320;
            let v = 0;
            for (let h = 1; h <= 24; h++) {
                const freq = h * fundamental;
                const formants = .22 * Math.exp(-(((freq - f1) / 180) ** 2))
                    + .17 * Math.exp(-(((freq - f2) / 270) ** 2)) + .055 * Math.exp(-(((freq - 2450) / 430) ** 2));
                const amplitude = .22 / (h ** 1.5) + formants;
                v += Math.sin(TAU * h * phase) * amplitude;
            }
            if (kind === 'undead') v += .16 * Math.sin(TAU * phase * .5);
            const env = Math.sin(Math.PI * Math.min(1, u)) ** (kind === 'cry' ? .8 : 1.4);
            this.put(begin + j, Math.tanh(v * 1.5) * gain * env * (.86 + .14 * Math.sin(t * 53)));
        }
        this.noise(start, length, gain * .7, 400, kind === 'undead' ? 3900 : 5900, .015, 2, true, 37);
        this.noise(start + length * .56, length * .65, gain * .45, 650, 4300, .025, 2, true);
    }
    mineral(start, gain = .10, length = .7) {
        for (let i = 0; i < 6; i++)
            this.modes(start + i * length / 9 + this.r(0, .018), [this.r(780, 1190), this.r(1770, 2630), this.r(3050, 4180)], gain / (1 + i * .3), .09);
    }
    step(start, material, gain = 1) {
        const sole = this.r(.045, .083);
        this.thud(start, gain * (material === 'dirt' ? .22 : .28), material === 'wood' ? .92 : 1);
        this.thud(start + sole, gain * .16, .7);
        if (material === 'stone') {
            this.modes(start + .007, [287, 743, 1249], .10 * gain, .023);
            this.grit(start + sole * .7, .19, .16 * gain, 16, true);
        } else if (material === 'wood') {
            this.wood(start + .007, .16 * gain);
            this.hinge(start + .085, .18, .055 * gain);
        } else this.grit(start + .015, .22, .25 * gain, 24);
        this.cloth(start + .035, .19, .10 * gain);
    }
}

function compose(cue, variant) {
    const s = new Sound(cue, variant), p = s.pitch;
    switch (cue) {
        case 'Swing': s.swish(.018, .40, .75); s.iron(.16, .024, true, .06); break;
        case 'BluntSwing': s.swish(.013, .53, .82, true); s.hinge(.01, .19, .05); break;
        case 'Stab': s.swish(.009, .20, .64); s.noise(.15, .16, .45, 700, 6100, .001, 6); s.thud(.17, .16, .7); break;
        case 'Impact': s.thud(.006, .46); s.noise(.012, .16, .69, 200, 4100, .001, 7); s.cloth(.025, .27, .34); s.grit(.07, .22, .12, 7); break;
        case 'BluntImpact': s.thud(.008, .56, 1.4); s.wood(.014, .26, true); s.grit(.028, .41, .28, 20); s.cloth(.03, .3, .25); break;
        case 'Metal': s.iron(.006, .38, false, .24); s.iron(.023, .15, true, .13); s.noise(.035, .22, .22, 1300, 7200, .004, 5); break;
        case 'Parry': s.iron(.004, .35, true, .40); s.iron(.018, .19, false, .24); s.noise(.025, .18, .40, 1800, 10200, .003, 5); s.thud(.006, .1, .7); break;
        case 'Step': s.step(.008, 'stone'); break;
        case 'StepWood': s.step(.008, 'wood'); break;
        case 'StepDirt': s.step(.008, 'dirt'); break;
        case 'Jump': s.step(.008, 'stone', .65); s.cloth(.09, .37, .28); s.voice(.05, .26, .10, 'hurt'); break;
        case 'Land': s.step(.01, 'stone', .95); s.step(.065, 'stone', .64); s.cloth(.04, .37, .30); s.iron(.17, .04, false, .06); break;
        case 'LandHeavy': s.thud(.01, .54, 1.5); s.step(.025, 'stone', 1.1); s.step(.08, 'stone', .82); s.grit(.09, .48, .20, 27); s.cloth(.07, .45, .35); s.iron(.21, .08, false, .08); break;
        case 'Cloth': s.cloth(.01, .54, .65); break;
        case 'Equip': s.cloth(.01, .57, .45); s.wood(.19, .09); s.iron(.28, .10, false, .07); s.noise(.34, .28, .3, 700, 4500, .01, 3, true); break;
        case 'Draw': s.noise(.018, .40, .56, 900, 6300, .02, 3, true, 19); s.cloth(.01, .28, .28); s.iron(.36, .12, true, .11); break;
        case 'Bow': s.string(.008, 173, .95, .65); s.wood(.008, .18); s.swish(.025, .24, .55); break;
        case 'SkillShot': s.string(.007, 192, 1.0, .70); s.string(.013, 307, .3, .48); s.wood(.007, .19); s.swish(.022, .30, .65); s.mineral(.16, .045, .4); break;
        case 'Reload': s.wood(.01, .16); s.cloth(.025, .5, .33); s.hinge(.09, .52, .12, true); s.string(.57, 131, .32, .38); s.wood(.67, .20); s.iron(.68, .07, false, .07); break;
        case 'DryFire': s.wood(.008, .23); s.iron(.018, .048, false, .025); break;
        case 'Hurt': s.voice(.012, .57, .64, 'hurt'); s.cloth(.01, .26, .15); break;
        case 'Death': s.voice(.025, 1.19, .57, 'death'); s.noise(.70, 1.02, .27, 500, 4800, .025, 2, true); s.cloth(.34, .60, .13); break;
        case 'EnemyHurt': s.voice(.012, .65, .61, 'undead'); s.grit(.03, .29, .15, 9, true); s.wood(.07, .07); break;
        case 'EnemyDeath': s.voice(.025, 1.30, .51, 'undead'); s.grit(.19, 1.10, .25, 25, true); for (let i = 0; i < 6; i++) s.wood(.27 + i * .11, .10 / (1 + i * .25)); break;
        case 'DoorCreak': s.hinge(.025, 1.29, .24); s.hinge(.10, 1.11, .09, true); s.noise(.12, 1.18, .19, 100, 1900, .04, 2, true); break;
        case 'DoorThud': s.wood(.01, .39, true); s.thud(.016, .28, 1.25); s.iron(.06, .09, false, .11); s.wood(.11, .095); break;
        case 'ChestOpen': s.iron(.012, .13, false, .06); s.wood(.06, .16); s.hinge(.15, .82, .17); s.cloth(.23, .57, .14); s.wood(1.01, .08); break;
        case 'ChestClose': s.wood(.01, .37, true); s.iron(.078, .11, false, .09); s.wood(.14, .10); s.cloth(.02, .23, .18); break;
        case 'Pickup': s.cloth(.01, .28, .34); s.wood(.08, .13); s.iron(.16, .06, false, .07); break;
        case 'Coins': s.cloth(.01, .63, .22); for (let i = 0; i < 9; i++) s.modes(.03 + i * .052 + s.r(0, .03), [s.r(2100, 2900), s.r(4700, 6300), s.r(7300, 8400)], s.r(.07, .15), .07); break;
        case 'Lever': s.hinge(.02, .44, .14, true); s.iron(.34, .13, false, .14); s.wood(.37, .23, true); s.thud(.39, .12); break;
        case 'SecretStone': s.noise(.01, 1.93, .70, 60, 2200, .06, 2, true, 17); s.grit(.05, 1.84, .22, 48); s.thud(.07, .19, 1.6); s.thud(1.83, .40, 1.35); s.modes(1.84, [117, 241, 509], .14, .09); break;
        case 'Portal': s.noise(.01, 1.77, .40, 120, 4700, .05, 2, true, 9); s.modes(.13, [58, 109, 173, 263], .18, .45, -.22); s.mineral(.57, .07, .93); s.swish(.76, .82, .37, true); break;
        case 'Extract': s.noise(.02, 1.83, .32, 240, 3700, .08, 2, true); s.modes(.18, [147, 220.4, 294.3, 441.8], .16, .58); s.string(.52, 294, .38, 1.5); s.string(.70, 440, .25, 1.2); s.mineral(.71, .06, .85); break;
        case 'Fail': s.modes(.018, [103, 169, 277, 433], .23, .32, -.45); s.noise(.02, 1.42, .30, 100, 2100, .05, 2, true); s.wood(.06, .16, true); break;
        case 'UiHover': s.cloth(.004, .095, .35); s.noise(.005, .055, .22, 700, 3200, .001, 6); break;
        case 'UiClick': s.wood(.004, .19); s.iron(.009, .035, false, .018); break;
        case 'UiOpen': s.cloth(.008, .31, .50); s.wood(.026, .08); break;
        case 'UiClose': s.cloth(.006, .23, .39); s.wood(.08, .16); break;
        case 'UiError': s.wood(.006, .16); s.wood(.11, .13); s.noise(.012, .26, .15, 250, 1600, .004, 3); break;
        case 'ItemMove': s.cloth(.005, .22, .40); s.wood(.042, .14); s.iron(.09, .04, false, .027); break;
        case 'Potion':
            s.noise(.012, .065, .42, 250, 3300, .001, 6); s.modes(.012, [391, 1127, 2179], .10, .035);
            for (let i = 0; i < 4; i++) { const t = .20 + i * .20 + s.r(0, .035); s.modes(t, [s.r(175, 245), s.r(490, 680)], .18, .045, -.7); s.noise(t, .13, .25, 130, 2300, .01, 4); }
            s.modes(1.14, [871, 2059, 3463], .08, .06); s.cloth(1.03, .24, .16); break;
        case 'Bandage': s.cloth(.01, .42, .56); s.noise(.22, .17, .52, 1600, 8500, .005, 4); s.cloth(.48, .35, .45); s.cloth(.85, .38, .40); break;
        case 'SkillDash': s.thud(.008, .27); s.swish(.01, .55, .95, true); s.cloth(.02, .44, .34); s.iron(.11, .04, false, .09); break;
        case 'SkillCry': s.voice(.02, 1.12, .72, 'cry'); s.thud(.03, .15, 1.3); s.cloth(.03, .40, .2); break;
        case 'SkillBuff': s.noise(.012, 1.42, .32, 180, 3700, .08, 2, true, 7); s.modes(.12, [131, 196.4, 295, 469], .15, .39, .09); s.mineral(.38, .065, .82); break;
        case 'ZoneWarning': s.modes(.012, [89, 151, 267, 421], .29, .26); s.modes(.72, [89, 151, 267, 421], .24, .27); s.noise(.015, 1.47, .20, 90, 1700, .03, 2, true); break;
        case 'ArrowImpact': s.noise(.006, .105, .52, 700, 7300, .0005, 7); s.wood(.012, .24); s.string(.017, 337, .43, .5); s.grit(.065, .22, .11, 6); break;
        case 'ShieldBreak': s.wood(.01, .35, true); s.wood(.043, .24); s.noise(.012, .17, .65, 800, 9200, .0005, 6); s.iron(.03, .18, true, .20); s.grit(.09, .89, .23, 26, true); for (let i = 0; i < 5; i++) s.wood(.12 + i * .14, .13 / (1 + i * .3)); break;
        default: throw new Error(`No recipe: ${cue}`);
    }
    // Tiny diffuse early reflections, not a baked cavern reverb; positional runtime remains in charge.
    if (!cue.startsWith('Ui') && cue !== 'ItemMove') {
        const dry = s.data.slice();
        for (const [seconds, gain] of [[.019, .055], [.037, -.031], [.067, .020]]) {
            const offset = Math.round(seconds * RATE);
            for (let i = offset; i < s.data.length; i++) s.data[i] += dry[i - offset] * gain;
        }
    }
    const gain = definitions[cue][2];
    // Remove DC/subsonic drift, then apply fixed authored mix and an attenuation-only safety ceiling.
    let previous = 0, hp = 0, peak = 0;
    const coefficient = Math.exp(-TAU * 24 / RATE);
    for (let i = 0; i < s.data.length; i++) {
        const x = s.data[i]; hp = coefficient * (hp + x - previous); previous = x;
        const edge = Math.min(1, i / (RATE * .0015), (s.data.length - 1 - i) / (RATE * .018));
        const y = hp * gain * edge;
        if (!Number.isFinite(y)) throw new Error(`Non-finite sample ${cue}/${variant}`);
        s.data[i] = y; peak = Math.max(peak, Math.abs(y));
    }
    const attenuation = Math.min(1, .84 / Math.max(peak, 1e-12));
    const pcm = new Int16Array(s.data.length);
    for (let i = 0; i < pcm.length; i++) pcm[i] = Math.round(clamp(s.data[i] * attenuation, -.999, .999) * 32767);
    pcm[0] = 0; pcm[pcm.length - 1] = 0;
    return { pcm, attenuation, pitch: p };
}

function wav(pcm) {
    const out = Buffer.alloc(44 + pcm.length * 2);
    out.write('RIFF', 0); out.writeUInt32LE(out.length - 8, 4); out.write('WAVEfmt ', 8);
    out.writeUInt32LE(16, 16); out.writeUInt16LE(1, 20); out.writeUInt16LE(1, 22);
    out.writeUInt32LE(RATE, 24); out.writeUInt32LE(RATE * 2, 28); out.writeUInt16LE(2, 32); out.writeUInt16LE(16, 34);
    out.write('data', 36); out.writeUInt32LE(pcm.length * 2, 40);
    for (let i = 0; i < pcm.length; i++) out.writeInt16LE(pcm[i], 44 + i * 2);
    return out;
}
function metrics(pcm) {
    let peak = 0, squares = 0, sum = 0, maxDelta = 0, nonzero = 0;
    for (let i = 0; i < pcm.length; i++) {
        const x = pcm[i] / 32768;
        peak = Math.max(peak, Math.abs(x)); squares += x * x; sum += x;
        if (pcm[i]) nonzero++;
        if (i) maxDelta = Math.max(maxDelta, Math.abs(pcm[i] - pcm[i - 1]) / 32768);
    }
    const result = { frames: pcm.length, seconds: +(pcm.length / RATE).toFixed(6), peak_dbfs: +db(peak).toFixed(3),
        rms_dbfs: +db(Math.sqrt(squares / pcm.length)).toFixed(3), mean: +(sum / pcm.length).toFixed(9),
        max_sample_delta: +maxDelta.toFixed(6), first_sample: pcm[0], last_sample: pcm[pcm.length - 1], nonzero_frames: nonzero };
    if (peak >= .85 || nonzero < RATE * .025 || result.first_sample || result.last_sample || Math.abs(result.mean) > .001)
        throw new Error(`Metrics outside contract: ${JSON.stringify(result)}`);
    return result;
}
function meta(relative) {
    const guid = sha(`DakaDaka original SFX / ${relative}`).slice(0, 32);
    return `fileFormatVersion: 2\nguid: ${guid}\nAudioImporter:\n  externalObjects: {}\n  serializedVersion: 8\n  defaultSettings:\n    serializedVersion: 2\n    loadType: 0\n    sampleRateSetting: 0\n    sampleRateOverride: 48000\n    compressionFormat: 0\n    quality: 1\n    conversionMode: 0\n    preloadAudioData: 1\n  platformSettingOverrides: {}\n  forceToMono: 1\n  normalize: 0\n  loadInBackground: 0\n  ambisonic: 0\n  3D: 1\n  userData: Original deterministic procedural synthesis; no external samples.\n  assetBundleName:\n  assetBundleVariant:\n`;
}

function main() {
    const manifest = { schema: 'dakadaka.original-sfx/v1', generator: 'Tools/Audio/SynthesizeGameSfx.cjs',
        generator_sha256: sha(fs.readFileSync(__filename)), synthesis_version: VERSION, sample_rate: RATE, channels: 1, bits_per_sample: 16,
        provenance: 'Original deterministic mathematical synthesis authored for this project. No downloaded samples, human recordings, cloned voices or paid generation.',
        rights: 'Project-original generated audio; no external audio licenses apply.',
        calibration: 'Fixed recipe/category gains; attenuation-only peak ceiling 0.84. No per-file peak/RMS normalization. PCM16 zero endpoints and edge fades.',
        runtime_note: 'Recipes include material/body layers and restrained early reflections. Runtime owns spatialization, distance attenuation, cooldowns and mix volume.',
        cues: {}, file_count: 0, total_bytes: 0 };
    const previewClips = [], allHashes = new Set();
    if (!verify) fs.mkdirSync(OUT, { recursive: true });
    for (const [cue, [category, , gain, description]] of Object.entries(definitions)) {
        const count = ['Step', 'StepWood', 'StepDirt'].includes(cue) ? 6 : cue.startsWith('Ui') ? 2 : 3;
        const entry = { category, description, authored_gain: gain, variants: [] };
        for (let variant = 1; variant <= count; variant++) {
            const name = `${cue}_${String(variant).padStart(2, '0')}.wav`;
            const { pcm, attenuation, pitch } = compose(cue, variant);
            const bytes = wav(pcm), hash = sha(bytes);
            if (allHashes.has(hash)) throw new Error(`Duplicate audio: ${name}`);
            allHashes.add(hash);
            const info = { file: name, sha256: hash, bytes: bytes.length, metrics: metrics(pcm),
                safety_attenuation: +attenuation.toFixed(6), pitch_factor: +pitch.toFixed(6) };
            if (verify) {
                if (!fs.existsSync(path.join(OUT, name)) || sha(fs.readFileSync(path.join(OUT, name))) !== hash)
                    throw new Error(`Reproducibility mismatch: ${name}`);
            } else {
                fs.writeFileSync(path.join(OUT, name), bytes);
                const metaPath = path.join(OUT, name + '.meta');
                if (!fs.existsSync(metaPath)) fs.writeFileSync(metaPath, meta(name));
            }
            entry.variants.push(info); manifest.file_count++; manifest.total_bytes += bytes.length;
            if (variant === 1) previewClips.push({ cue, category, pcm });
        }
        manifest.cues[cue] = entry;
    }
    if (verify) {
        const saved = JSON.parse(fs.readFileSync(path.join(OUT, 'manifest.json'), 'utf8'));
        if (JSON.stringify(saved) !== JSON.stringify(manifest)) throw new Error('Manifest differs from deterministic rebuild');
        console.log(JSON.stringify({ verified: true, cues: Object.keys(manifest.cues).length, files: manifest.file_count,
            total_bytes: manifest.total_bytes, unique_hashes: allHashes.size }));
        return;
    }
    fs.writeFileSync(path.join(OUT, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
    if (!fs.existsSync(OUT + '.meta'))
        fs.writeFileSync(OUT + '.meta', `fileFormatVersion: 2\nguid: ${sha('DakaDaka original SFX folder').slice(0, 32)}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n`);
    if (!fs.existsSync(path.join(OUT, 'manifest.json.meta')))
        fs.writeFileSync(path.join(OUT, 'manifest.json.meta'), `fileFormatVersion: 2\nguid: ${sha('DakaDaka original SFX manifest').slice(0, 32)}\nTextScriptImporter:\n  externalObjects: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n`);
    const gaps = Math.round(.40 * RATE), opening = Math.round(.5 * RATE);
    const length = opening + previewClips.reduce((n, x) => n + x.pcm.length + gaps, 0);
    const reel = new Int16Array(length), timings = []; let cursor = opening;
    for (const { cue, category, pcm } of previewClips) {
        reel.set(pcm, cursor);
        timings.push({ cue, category, file: `${cue}_01.wav`, start_seconds: +(cursor / RATE).toFixed(6),
            end_seconds: +((cursor + pcm.length) / RATE).toFixed(6) });
        cursor += pcm.length + gaps;
    }
    fs.mkdirSync(REVIEW, { recursive: true });
    const reelBytes = wav(reel);
    fs.writeFileSync(path.join(REVIEW, 'GameSfx-preview.wav'), reelBytes);
    fs.writeFileSync(path.join(REVIEW, 'GameSfx-preview-timings.json'), JSON.stringify({ sample_rate: RATE, channels: 1,
        note: 'First variant of each cue at its authored level; no spoken labels or extra normalization.',
        seconds: length / RATE, sha256: sha(reelBytes), timings }, null, 2) + '\n');
    console.log(JSON.stringify({ generated: true, cues: Object.keys(manifest.cues).length, files: manifest.file_count,
        total_bytes: manifest.total_bytes, preview_seconds: length / RATE, output: path.relative(ROOT, OUT) }));
}
if (require.main === module) main();
