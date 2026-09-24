/**
 * POST /v1/feedback: what a user writes in the App's feedback box, filed as an issue in the owner's PRIVATE GitHub repo
 * (FEEDBACK_REPO, with the token FEEDBACK_GITHUB_TOKEN). Unsigned: any PC may send it, so an address gets 10 an hour.
 *
 * The body, JSON, is {"text","email","app","os","arch","log","images":[{"name","contentType","data"}]}, 8 MB at most
 * in all. Images (up to 5, PNG or JPEG, 1 MB each) and a log over 60k characters are committed to
 * feedback/<id>/<name> first, then the issue is opened: the metadata in a table, the text in a fenced block, the images
 * and the log after it. User text never goes into markdown as itself: control and bidi characters are stripped, and the
 * text and log are fenced with more backticks than they hold. 202 {"id","issue"}; 400 for a bad field; 429 over the
 * hour's 10; 503 when GitHub fails or feedback isn't set up (the App tries again later).
 */
import { addressOf } from "./address";
import { readBounded } from "./body";
import { GitHub, REPO_NAME } from "./github";
import { errorResponse, overAddressLimit, parseObject } from "./households/http";

export const MAX_FEEDBACK_BYTES = 8 * 1024 * 1024;
export const FEEDBACK_PER_HOUR = 10;
/** A log longer than this is committed as a file and linked rather than put in the issue. */
export const INLINE_LOG_CHARS = 60_000;
export const MAX_TEXT_CHARS = 10_000;
const MAX_EMAIL_CHARS = 254;
const MAX_APP_CHARS = 64;
const MAX_OS_CHARS = 100;
const MAX_LOG_CHARS = 200 * 1024;
const MAX_IMAGES = 5;
const MAX_IMAGE_BYTES = 1024 * 1024;
const MAX_IMAGE_NAME_CHARS = 80;
const TITLE_CHARS = 60;
const ARCHES = new Set(["x64", "arm64", "x86"]);
const IMAGE_TYPES: Record<string, { magic: number[]; extensions: string[] }> = {
  "image/png": { magic: [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], extensions: [".png"] },
  "image/jpeg": { magic: [0xff, 0xd8, 0xff], extensions: [".jpg", ".jpeg"] },
};
const IMAGE_NAME = /^(?!\.+$)[A-Za-z0-9._-]{1,80}$/;
const APP_VERSION = /^[0-9A-Za-z][0-9A-Za-z.+_-]{0,63}$/;
/** An address as far as the issue cares: something at something, no whitespace, nothing markdown could take up. */
const EMAIL = /^[^\s@`|\\]+@[^\s@`|\\]+$/;

/** Control characters other than tab and line ends, the bidi controls, and the invisible zero-width and word joiners. */
const CONTROL_AND_BIDI = /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F\u061C\u200B\u200E\u200F\u202A-\u202E\u2060\u2066-\u2069\uFEFF]/g;
const LINE_ENDS = /[\t\n\r\u2028\u2029]/g;

export interface FeedbackDeps {
  fetch?: typeof fetch;
  now?: () => number;
  /** The id's last part, 4 characters; random by default. */
  suffix?: () => string;
}

interface Image {
  name: string;
  contentType: string;
  bytes: Uint8Array;
  data: string;
}

interface Feedback {
  text: string;
  email: string | null;
  app: string;
  os: string;
  arch: string;
  log: string | null;
  images: Image[];
}

export async function handleFeedback(request: Request, env: Cloudflare.Env, deps: FeedbackDeps = {}): Promise<Response> {
  const limited = await overAddressLimit(request, env);
  if (limited) return limited;

  const repo = env.FEEDBACK_REPO ?? "";
  const token = env.FEEDBACK_GITHUB_TOKEN ?? "";
  if (!REPO_NAME.test(repo) || token === "") return errorResponse(503, "Feedback isn't set up on the server yet.");

  const now = deps.now?.() ?? Date.now();
  if (await overHourLimit(env, addressOf(request), now)) {
    return errorResponse(429, "Too much feedback from this address; try again in an hour.");
  }

  const body = await readBounded(request, MAX_FEEDBACK_BYTES);
  if (body === null) return errorResponse(413, "The body is larger than 8 MB.");
  const feedback = readFeedback(parseObject(body));
  if (typeof feedback === "string") return errorResponse(400, feedback);

  const id = feedbackId(now, deps.suffix?.() ?? randomSuffix());
  const github = new GitHub({ repo, token }, deps.fetch);
  try {
    const files: Record<string, string> = {};
    for (const image of feedback.images) {
      files[image.name] = await github.putFile(`feedback/${id}/${image.name}`, image.bytes, `Feedback ${id}: ${image.name}`);
    }
    if (feedback.log !== null && feedback.log.length > INLINE_LOG_CHARS) {
      files["log.txt"] = await github.putFile(`feedback/${id}/log.txt`, new TextEncoder().encode(feedback.log), `Feedback ${id}: log.txt`);
    }
    const issue = await github.createIssue(issueTitle(feedback), issueBody(feedback, id, now, files), ["feedback"]);
    return Response.json({ id, issue }, { status: 202 });
  } catch (error) {
    console.error(`Feedback ${id}: GitHub failed.`, error);
    return errorResponse(503, "The feedback couldn't be recorded; try again later.");
  }
}

/** Counts this piece against the address's hour; true once it's past the 10. */
async function overHourLimit(env: Cloudflare.Env, address: string, now: number): Promise<boolean> {
  const row = await env.DB.prepare(
    `INSERT INTO feedback_addresses (address, utc_hour, count) VALUES (?, ?, 1)
     ON CONFLICT (address, utc_hour) DO UPDATE SET count = count + 1 RETURNING count`,
  )
    .bind(address, utcHour(now))
    .first<{ count: number }>();
  return (row?.count ?? 0) > FEEDBACK_PER_HOUR;
}

/** `now` as yyyy-MM-ddTHH, UTC: the hour a feedback count belongs to. */
export function utcHour(now: number): string {
  return new Date(now).toISOString().slice(0, 13);
}

/** yyyyMMdd-HHmmss-xxxx, the time UTC. */
function feedbackId(now: number, suffix: string): string {
  const iso = new Date(now).toISOString();
  return `${iso.slice(0, 10).replace(/-/g, "")}-${iso.slice(11, 19).replace(/:/g, "")}-${suffix}`;
}

function randomSuffix(): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(2)), (byte) => byte.toString(16).padStart(2, "0")).join("");
}

/** The posted fields, checked and cleaned; or the message of the first that's wrong. */
function readFeedback(posted: Record<string, unknown> | null): Feedback | string {
  if (!posted) return "The body must be a JSON object.";

  const text = typeof posted.text === "string" ? clean(posted.text) : "";
  if (text.trim() === "" || text.length > MAX_TEXT_CHARS) return `text is required, ${MAX_TEXT_CHARS} characters at most.`;

  let email: string | null = null;
  if (posted.email !== undefined && posted.email !== null) {
    if (typeof posted.email !== "string") return badEmail();
    email = cleanLine(posted.email);
    if (email === "") email = null;
    else if (email.length > MAX_EMAIL_CHARS || !EMAIL.test(email)) return badEmail();
  }

  const app = typeof posted.app === "string" ? cleanLine(posted.app) : "";
  if (!APP_VERSION.test(app)) return `app must be the version, ${MAX_APP_CHARS} characters at most.`;
  const os = typeof posted.os === "string" ? cleanLine(posted.os).trim() : "";
  if (os === "" || os.length > MAX_OS_CHARS) return `os is required, ${MAX_OS_CHARS} characters at most.`;
  const arch = posted.arch;
  if (typeof arch !== "string" || !ARCHES.has(arch)) return "arch must be x64, arm64 or x86.";

  let log: string | null = null;
  if (posted.log !== undefined && posted.log !== null) {
    if (typeof posted.log !== "string" || posted.log.length > MAX_LOG_CHARS) return "log must be text, 200 KB at most, or left out.";
    log = clean(posted.log);
    if (log.trim() === "") log = null;
  }

  const images = readImages(posted.images);
  if (typeof images === "string") return images;

  return { text, email, app, os, arch, log, images };
}

function badEmail(): string {
  return `email must be an address, ${MAX_EMAIL_CHARS} characters at most, or left out.`;
}

function readImages(posted: unknown): Image[] | string {
  if (posted === undefined || posted === null) return [];
  if (!Array.isArray(posted) || posted.length > MAX_IMAGES || !posted.every((item) => item !== null && typeof item === "object")) {
    return `images must be up to ${MAX_IMAGES} of {"name","contentType","data"}.`;
  }
  const images: Image[] = [];
  for (const [index, item] of (posted as Record<string, unknown>[]).entries()) {
    const contentType = item.contentType;
    if (typeof contentType !== "string" || !(contentType in IMAGE_TYPES)) {
      return `images[${index}].contentType must be image/png or image/jpeg.`;
    }
    const type = IMAGE_TYPES[contentType];
    const name = typeof item.name === "string" ? cleanLine(item.name) : "";
    const lower = name.toLowerCase();
    if (!IMAGE_NAME.test(name) || !type.extensions.some((extension) => lower.endsWith(extension))) {
      return `images[${index}].name must be a file name of up to ${MAX_IMAGE_NAME_CHARS} letters, digits, dots, dashes and underscores, ending as its type: .png, .jpg or .jpeg.`;
    }
    if (images.some((other) => other.name.toLowerCase() === lower)) return `images[${index}].name is the same as another's.`;
    const bytes = typeof item.data === "string" ? decodeBase64(item.data, MAX_IMAGE_BYTES) : null;
    if (!bytes) return `images[${index}].data must be the image as base64, 1 MB at most.`;
    if (!type.magic.every((byte, at) => bytes[at] === byte)) return `images[${index}].data isn't a PNG or JPEG.`;
    images.push({ name, contentType, bytes, data: item.data as string });
  }
  return images;
}

/** Standard base64 decoded, or null when it isn't that or holds more than `max` bytes. */
function decodeBase64(data: string, max: number): Uint8Array | null {
  if (data.length > Math.ceil(max / 3) * 4 || data.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(data)) return null;
  try {
    const binary = atob(data);
    if (binary.length > max) return null;
    return Uint8Array.from(binary, (char) => char.charCodeAt(0));
  } catch {
    return null;
  }
}

/** Multi-line text without its control and bidi characters. */
function clean(value: string): string {
  return value.replace(CONTROL_AND_BIDI, "");
}

/** A one-line field: cleaned, and without line ends either. */
function cleanLine(value: string): string {
  return clean(value).replace(LINE_ENDS, "");
}

/** The text's first 60 characters, on one line, then the app, os and arch. */
function issueTitle(feedback: Feedback): string {
  const line = Array.from(feedback.text.replace(/\s+/g, " ").trim());
  const head = line.slice(0, TITLE_CHARS).join("").trimEnd() + (line.length > TITLE_CHARS ? "…" : "");
  return `${head} (${feedback.app}, ${feedback.os}, ${feedback.arch})`;
}

function issueBody(feedback: Feedback, id: string, now: number, files: Record<string, string>): string {
  const time = `${new Date(now).toISOString().slice(0, 19).replace("T", " ")} UTC`;
  const parts = [
    [
      "| Field | Value |",
      "|---|---|",
      `| id | ${cell(id)} |`,
      `| app | ${cell(feedback.app)} |`,
      `| os | ${cell(feedback.os)} |`,
      `| arch | ${cell(feedback.arch)} |`,
      `| email | ${feedback.email === null ? "not given" : cell(feedback.email)} |`,
      `| time | ${time} |`,
    ].join("\n"),
    fenced(feedback.text),
    ...feedback.images.map((image) => `![${image.name}](${files[image.name]}?raw=true)`),
  ];
  if (feedback.log !== null) {
    parts.push(
      "log.txt" in files
        ? `[log.txt](${files["log.txt"]})`
        : `<details>\n<summary>Log</summary>\n\n${fenced(feedback.log)}\n</details>`,
    );
  }
  return `${parts.join("\n\n")}\n`;
}

/** A table cell as inline code, so nothing in it is markdown or HTML: a backtick would end the code, a pipe the cell. */
function cell(value: string): string {
  return `\`${value.replace(/`/g, "'").replace(/\|/g, "\\|")}\``;
}

/** `text` in a fenced block whose fence is longer than any run of backticks in it, so the text can't close it. */
function fenced(text: string): string {
  const longest = Math.max(2, ...Array.from(text.matchAll(/`+/g), (run) => run[0].length));
  const fence = "`".repeat(longest + 1);
  return `${fence}\n${text}\n${fence}`;
}
