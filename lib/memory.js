// SPDX-License-Identifier: MIT
// ============================================================================
// dsh-voice-memory — 语音输入记忆层。
//
// 目标：让语音输入从“每次重新猜”变成“越用越懂你”。
//
// 1. 纠错记忆：转写文本被放进输入框后，用户几乎总会改几个字再发送。
//    宿主监听 agent/pre-step，拿到真正发送出去的文本，与刚才的转写做
//    对齐 diff，把“识别错 → 用户改成”的词对存进 memory.json。
//    下一次转写时先应用这些词对（在 hot.txt 之后、润色之前），于是同一个
//    发音习惯（尤其是英语发音、人名、术语）第二次就能被正确识别。
//
// 2. 风格记忆：如果开启了润色，用户看到的是润色后的文本；此时用户的
//    进一步修改学习为“表达偏好”，写进润色提示词的附录，而不是当作
//    ASR 纠错词去替换原文。
//
// 3. 完全本地：memory.json 位于 $DSH_HOME/voice/，不上传任何地方。
// ============================================================================

import { mkdir, readFile, rename, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";

const MAX_CORRECTIONS = 800;
const MAX_EXAMPLES = 40;
const MAX_PENDING_PER_SESSION = 6;
const PENDING_TTL_MS = 15 * 60 * 1000;
const MAX_TOKENS = 360;
const MAX_PAIR_CHARS = 32;
const MIN_SIMILARITY = 0.42;
const POLISH_MEMORY_BUDGET = 1600;

/** State directory ($DSH_HOME/voice). */
export function memoryDir() {
	const home = process.env.DSH_HOME && process.env.DSH_HOME.length > 0 ? process.env.DSH_HOME : join(homedir(), ".dsh");
	return join(home, "voice");
}

/** Path of the learned memory file. */
export function memoryPath() {
	return join(memoryDir(), "memory.json");
}

function emptyData() {
	return { version: 1, updatedAt: 0, corrections: {}, examples: [] };
}

let data = null;
let loadPromise = null;
let writeChain = Promise.resolve();
/** sessionId -> [{ text, at }] — recent transcripts awaiting the sent message. */
const pending = new Map();
/** sessionId -> { raw, polished, at } — what the user actually saw before editing. */
const polished = new Map();

function isObject(value) {
	return value !== null && typeof value === "object" && !Array.isArray(value);
}

function normalizeData(raw) {
	const base = emptyData();
	if (!isObject(raw)) return base;
	if (isObject(raw.corrections)) {
		for (const [key, value] of Object.entries(raw.corrections)) {
			if (!isObject(value)) continue;
			const wrong = typeof value.wrong === "string" ? value.wrong.trim() : "";
			const right = typeof value.right === "string" ? value.right.trim() : "";
			if (wrong === "" || right === "" || wrong === right) continue;
			base.corrections[key] = {
				wrong,
				right,
				count: Number.isSafeInteger(value.count) && value.count > 0 ? value.count : 1,
				source: value.source === "style" ? "style" : "asr",
				createdAt: Number.isFinite(value.createdAt) ? value.createdAt : Date.now(),
				updatedAt: Number.isFinite(value.updatedAt) ? value.updatedAt : Date.now()
			};
		}
	}
	if (Array.isArray(raw.examples)) {
		base.examples = raw.examples.filter((item) => isObject(item) && typeof item.raw === "string" && typeof item.final === "string").slice(-MAX_EXAMPLES);
	}
	base.updatedAt = Number.isFinite(raw.updatedAt) ? raw.updatedAt : 0;
	return base;
}

/** Load memory once; a corrupt file is ignored instead of breaking dictation. */
export async function loadMemory() {
	if (data !== null) return data;
	if (loadPromise !== null) return loadPromise;
	loadPromise = (async () => {
		try {
			const text = await readFile(memoryPath(), "utf8");
			data = normalizeData(JSON.parse(text));
		} catch {
			data = emptyData();
		}
		return data;
	})().finally(() => { loadPromise = null; });
	return loadPromise;
}

function persist() {
	const snapshot = JSON.stringify(data, null, 2);
	writeChain = writeChain.then(async () => {
		await mkdir(memoryDir(), { recursive: true });
		const target = memoryPath();
		const tmp = target + ".tmp";
		await writeFile(tmp, snapshot, "utf8");
		await rename(tmp, target);
	}).catch(() => { /* memory is best-effort; never break transcription */ });
	return writeChain;
}

/** Remember one raw transcript so the next sent message can be compared. */
export function rememberTranscript(sessionId, text) {
	const session = typeof sessionId === "string" && sessionId !== "" ? sessionId : "unknown";
	const value = typeof text === "string" ? text.trim() : "";
	if (value === "") return;
	const now = Date.now();
	const list = (pending.get(session) || []).filter((item) => now - item.at <= PENDING_TTL_MS);
	list.push({ text: value, at: now });
	while (list.length > MAX_PENDING_PER_SESSION) list.shift();
	pending.set(session, list);
	// A new transcript invalidates a polish record from an older take.
	polished.delete(session);
}

/** Remember the polished form the user saw, so edits are attributed correctly. */
export function rememberPolish(sessionId, raw, polishedText) {
	const session = typeof sessionId === "string" && sessionId !== "" ? sessionId : "unknown";
	if (typeof raw !== "string" || typeof polishedText !== "string") return;
	if (polishedText.trim() === "" || raw.trim() === polishedText.trim()) return;
	polished.set(session, { raw: raw.trim(), polished: polishedText.trim(), at: Date.now() });
}

function latestPending(sessionId) {
	const list = pending.get(sessionId);
	if (!list || list.length === 0) return null;
	const now = Date.now();
	const fresh = list.filter((item) => now - item.at <= PENDING_TTL_MS);
	if (fresh.length === 0) { pending.delete(sessionId); return null; }
	return fresh[fresh.length - 1];
}

/** Extract the text of the last ordinary user message from an admitted batch. */
export function textOfMessages(messages) {
	if (!Array.isArray(messages)) return "";
	let chosen = "";
	for (const message of messages) {
		if (!message) continue;
		const content = message.content;
		let text = "";
		if (typeof content === "string") text = content;
		else if (Array.isArray(content)) {
			text = content
				.filter((block) => block && block.type === "text" && typeof block.text === "string")
				.map((block) => block.text)
				.join("\n");
		}
		if (text.trim() === "") continue;
		const kind = message.source && typeof message.source === "object" ? message.source.kind : undefined;
		if (kind === undefined || kind === "user") chosen = text;
	}
	return chosen.trim();
}

// ── token-level diff ───────────────────────────────────────────────────────

const TOKEN_PATTERN = /[\u3400-\u9fff]|[A-Za-z]+(?:['’][A-Za-z]+)?|\d+(?:\.\d+)?|\S/gu;

function tokenize(text) {
	const tokens = [];
	TOKEN_PATTERN.lastIndex = 0;
	for (let match = TOKEN_PATTERN.exec(text); match !== null; match = TOKEN_PATTERN.exec(text)) {
		tokens.push({ text: match[0], start: match.index, end: match.index + match[0].length });
	}
	return tokens;
}

/**
 * Token-level LCS edits. Each op keeps the token indexes so original spacing
 * survives in the extracted wrong/right substrings.
 */
function diffTokens(left, right) {
	const n = left.length;
	const m = right.length;
	if (n === 0 || m === 0) return [];
	const table = Array.from({ length: n + 1 }, () => new Uint16Array(m + 1));
	for (let i = n - 1; i >= 0; i--) {
		for (let j = m - 1; j >= 0; j--) {
			table[i][j] = left[i].text === right[j].text
				? table[i + 1][j + 1] + 1
				: Math.max(table[i + 1][j], table[i][j + 1]);
		}
	}
	const ops = [];
	let i = 0;
	let j = 0;
	while (i < n && j < m) {
		if (left[i].text === right[j].text) {
			ops.push({ type: "same", left: i, right: j });
			i++; j++;
		} else if (table[i + 1][j] >= table[i][j + 1]) {
			ops.push({ type: "del", left: i });
			i++;
		} else {
			ops.push({ type: "ins", right: j });
			j++;
		}
	}
	while (i < n) { ops.push({ type: "del", left: i }); i++; }
	while (j < m) { ops.push({ type: "ins", right: j }); j++; }
	return ops;
}

function isNoise(text) {
	return /^[\s\p{P}\p{S}]*$/u.test(text);
}

/** Turn a raw/final pair into high-signal replacement pairs. */
export function extractCorrections(raw, final) {
	if (typeof raw !== "string" || typeof final !== "string") return [];
	const left = tokenize(raw);
	const right = tokenize(final);
	if (left.length === 0 || right.length === 0) return [];
	if (left.length > MAX_TOKENS || right.length > MAX_TOKENS) return [];
	const ops = diffTokens(left, right);
	const pairs = [];
	let run = [];
	const flush = () => {
		const dels = run.filter((op) => op.type === "del");
		const ins = run.filter((op) => op.type === "ins");
		run = [];
		if (dels.length === 0 || ins.length === 0) return;
		const wrong = raw.slice(left[dels[0].left].start, left[dels[dels.length - 1].left].end).trim();
		const rightText = final.slice(right[ins[0].right].start, right[ins[ins.length - 1].right].end).trim();
		if (wrong === "" || rightText === "" || wrong === rightText) return;
		if (wrong.length > MAX_PAIR_CHARS || rightText.length > MAX_PAIR_CHARS) return;
		if (isNoise(wrong) || isNoise(rightText)) return;
		if (/[\r\n]/.test(wrong) || /[\r\n]/.test(rightText)) return;
		pairs.push({ wrong, right: rightText });
	};
	for (const op of ops) {
		if (op.type === "same") flush();
		else run.push(op);
	}
	flush();
	// A wholesale rewrite is not a "pronunciation habit"; keep it only as a style example.
	if (pairs.length > 10) return [];
	return pairs;
}

function similarity(raw, final) {
	const a = tokenize(raw);
	const b = tokenize(final);
	const left = a.length;
	const right = b.length;
	if (left === 0 || right === 0) return 0;
	const grid = Array.from({ length: left + 1 }, () => new Uint16Array(right + 1));
	for (let i = 1; i <= left; i++) {
		for (let j = 1; j <= right; j++) {
			grid[i][j] = a[i - 1].text === b[j - 1].text ? grid[i - 1][j - 1] + 1 : Math.max(grid[i - 1][j], grid[i][j - 1]);
		}
	}
	return (2 * grid[left][right]) / (left + right);
}

function mergeCorrection(store, wrong, right, source) {
	const key = wrong.toLowerCase();
	const existing = store.corrections[key];
	const now = Date.now();
	if (existing && existing.right !== right) {
		// The same misrecognition got two different fixes. The newest wins,
		// but keep the evidence count so it stays applicable.
		existing.right = right;
		existing.source = source;
		existing.count += 1;
		existing.updatedAt = now;
		return;
	}
	if (existing) {
		existing.count += 1;
		existing.updatedAt = now;
		if (source === "asr" && existing.source === "style") existing.source = "asr";
		return;
	}
	store.corrections[key] = { wrong, right, count: 1, source, createdAt: now, updatedAt: now };
}

/**
 * Learn from the message the user actually sent.
 * @returns {Promise<{learned: number, similar: boolean, source: string}|null>}
 */
export async function learnFromMessages(sessionId, messages) {
	const store = await loadMemory();
	const final = textOfMessages(messages);
	if (final === "" || final.startsWith("/")) return null;
	const session = typeof sessionId === "string" && sessionId !== "" ? sessionId : "unknown";
	const recent = latestPending(session);
	if (recent === null) return null;
	const polish = polished.get(session);
	const polishedForm = polish && polish.raw === recent.text && Date.now() - polish.at <= PENDING_TTL_MS ? polish.polished : null;
	const baseline = polishedForm || recent.text;
	if (baseline === final) return null;
	if (similarity(baseline, final) < MIN_SIMILARITY) return null;
	const source = polishedForm ? "style" : "asr";
	const pairs = extractCorrections(baseline, final);
	if (pairs.length === 0) {
		store.examples.push({ raw: recent.text, baseline, final, source, at: Date.now() });
		while (store.examples.length > MAX_EXAMPLES) store.examples.shift();
		store.updatedAt = Date.now();
		await persist();
		return { learned: 0, similar: true, source };
	}
	for (const pair of pairs) {
		mergeCorrection(store, pair.wrong, pair.right, source);
		// If the wrong span was in the raw transcript too, polishing did not
		// catch it — remember it as an ASR correction so the next transcript is
		// fixed before polishing, not only by the style prompt.
		if (source === "style" && recent.text.toLowerCase().includes(pair.wrong.toLowerCase())) {
			mergeCorrection(store, pair.wrong, pair.right, "asr");
		}
	}
	store.examples.push({ raw: recent.text, baseline, final, source, at: Date.now() });
	while (store.examples.length > MAX_EXAMPLES) store.examples.shift();
	// Keep the dictionary bounded: forget the least-used, oldest entries.
	const entries = Object.entries(store.corrections);
	if (entries.length > MAX_CORRECTIONS) {
		entries.sort((a, b) => (a[1].count - b[1].count) || (a[1].updatedAt - b[1].updatedAt));
		for (const [key] of entries.slice(0, entries.length - MAX_CORRECTIONS)) delete store.corrections[key];
	}
	store.updatedAt = Date.now();
	await persist();
	pending.delete(session);
	return { learned: pairs.length, similar: true, source };
}

function escapeRegExp(text) {
	return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function replacementPattern(wrong) {
	const escaped = escapeRegExp(wrong).replace(/\\?\s+/g, "\\s+");
	const startsWord = /^[A-Za-z0-9]/.test(wrong);
	const endsWord = /[A-Za-z0-9]$/.test(wrong);
	return new RegExp((startsWord ? "(?<![A-Za-z0-9])" : "") + escaped + (endsWord ? "(?![A-Za-z0-9])" : ""), "gi");
}

/** Apply learned ASR corrections; style-only entries never rewrite the transcript. */
export async function applyMemory(text) {
	const store = await loadMemory();
	if (typeof text !== "string" || text === "") return text;
	const entries = Object.values(store.corrections)
		.filter((entry) => entry.source === "asr")
		// A single-character pair is ambiguous until it repeats; multi-character
		// phrases (names, English terms) are safe from the first occurrence.
		.filter((entry) => entry.count >= 2 || Array.from(entry.wrong).length > 1)
		.sort((a, b) => b.wrong.length - a.wrong.length);
	let output = text;
	for (const entry of entries) {
		try {
			output = output.replace(replacementPattern(entry.wrong), entry.right);
		} catch { /* a malformed learned pair must not block the transcript */ }
	}
	return output;
}

/** Build the style appendix injected into the polish prompt. */
export async function buildPolishMemory() {
	const store = await loadMemory();
	const lines = [];
	const stylePairs = Object.values(store.corrections).filter((entry) => entry.source === "style" && entry.count >= 1).slice(-25);
	const examples = store.examples.filter((item) => item.source === "style").slice(-8);
	if (stylePairs.length === 0 && examples.length === 0) return "";
	lines.push("【用户表达偏好（从过往修改中学习，仅用于让改写更贴近用户习惯，不要照抄内容）】");
	for (const entry of stylePairs) {
		lines.push("- 用户把「" + entry.wrong + "」改成「" + entry.right + "」");
	}
	for (const item of examples) {
		lines.push("- 用户原稿：" + item.baseline.slice(0, 160) + " → 用户最终：" + item.final.slice(0, 160));
	}
	let text = lines.join("\n");
	if (text.length > POLISH_MEMORY_BUDGET) text = text.slice(0, POLISH_MEMORY_BUDGET);
	return text;
}

/** Non-secret snapshot for the settings panel. */
export async function memorySummary() {
	const store = await loadMemory();
	const corrections = Object.values(store.corrections)
		.sort((a, b) => (b.count - a.count) || (b.updatedAt - a.updatedAt))
		.slice(0, 30)
		.map((entry) => ({ wrong: entry.wrong, right: entry.right, count: entry.count, source: entry.source }));
	return {
		count: Object.keys(store.corrections).length,
		corrections,
		examples: store.examples.length,
		path: memoryPath(),
		updatedAt: store.updatedAt
	};
}

/** Forget one learned pair (by its wrong text, case-insensitive). */
export async function forgetCorrection(wrong) {
	const store = await loadMemory();
	if (typeof wrong !== "string" || wrong === "") return false;
	const key = wrong.toLowerCase();
	if (!Object.hasOwn(store.corrections, key)) return false;
	delete store.corrections[key];
	store.updatedAt = Date.now();
	await persist();
	return true;
}

/** Forget everything; the caller decides whether that needs confirmation. */
export async function clearMemory() {
	const store = await loadMemory();
	store.corrections = {};
	store.examples = [];
	store.updatedAt = Date.now();
	pending.clear();
	polished.clear();
	await persist();
}

/** Test hook: drop the loaded snapshot and transient buffers. */
export function resetMemoryForTests() {
	data = null;
	loadPromise = null;
	writeChain = Promise.resolve();
	pending.clear();
	polished.clear();
}

/**
 * Drop only the cached file snapshot. Called when the plugin (re)mounts so a
 * memory.json edited outside the process, or written before a reload, is read
 * again; in-flight transcript buffers stay.
 */
export function resetMemoryCache() {
	data = null;
	loadPromise = null;
}
