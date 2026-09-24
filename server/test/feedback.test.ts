import { env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { FEEDBACK_PER_HOUR, handleFeedback, INLINE_LOG_CHARS, MAX_FEEDBACK_BYTES } from "../src/feedback";
import { runRetention } from "../src/retention";

/** The repo the tests' Worker is set up with (vitest.config.ts). */
const REPO = "owner/feedback-test";
const NOW = Date.UTC(2026, 8, 24, 16, 30, 0);
const ID = "20260924-163000-ab12";

interface Call {
  method: string;
  url: string;
  headers: Record<string, string>;
  body: Record<string, unknown> | null;
}

interface FakeGitHub {
  fetch: typeof fetch;
  calls: Call[];
}

/** GitHub as the Worker sees it: every call recorded, answered by `respond` or, by default, as GitHub would. */
function fakeGitHub(respond: (call: Call) => Response | undefined = () => undefined): FakeGitHub {
  const calls: Call[] = [];
  const fetcher = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const request = new Request(input, init);
    const text = await request.text();
    const call: Call = {
      method: request.method,
      url: request.url,
      headers: Object.fromEntries(request.headers),
      body: text ? (JSON.parse(text) as Record<string, unknown>) : null,
    };
    calls.push(call);
    const answered = respond(call);
    if (answered) return answered;
    const contents = new RegExp(`^https://api\\.github\\.com/repos/${REPO}/contents/(.+)$`).exec(call.url);
    if (call.method === "PUT" && contents) {
      return Response.json({ content: { html_url: `https://github.com/${REPO}/blob/main/${contents[1]}` } }, { status: 201 });
    }
    if (call.method === "POST" && call.url === `https://api.github.com/repos/${REPO}/issues`) {
      return Response.json({ number: 42, html_url: `https://github.com/${REPO}/issues/42` }, { status: 201 });
    }
    return Response.json({ message: "Not Found" }, { status: 404 });
  };
  return { fetch: fetcher as typeof fetch, calls };
}

/** A GitHub that must never be reached: a bad request is refused before any call. */
function noGitHub(): FakeGitHub {
  return fakeGitHub((call) => {
    throw new Error(`GitHub was called: ${call.method} ${call.url}`);
  });
}

function randomAddress(): string {
  const [a, b, c] = crypto.getRandomValues(new Uint8Array(3));
  return `10.${a}.${b}.${c}`;
}

function feedbackRequest(body: unknown, headers: Record<string, string> = {}): Request {
  return new Request("https://example.com/v1/feedback", {
    method: "POST",
    headers: { "Content-Type": "application/json", "CF-Connecting-IP": randomAddress(), ...headers },
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
}

function base64(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}

/** Bytes that begin as a PNG, or a JPEG, of `length` in all. */
function png(length = 40): Uint8Array {
  const bytes = new Uint8Array(length).fill(0x2a);
  bytes.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  return bytes;
}

function jpeg(length = 40): Uint8Array {
  const bytes = new Uint8Array(length).fill(0x2a);
  bytes.set([0xff, 0xd8, 0xff, 0xe0]);
  return bytes;
}

const GOOD = {
  text: "The night mode doesn't stick after a reboot.\nSecond line.",
  email: "someone@example.com",
  app: "0.7.0+abc1234",
  os: "Windows 11 Home 26200",
  arch: "x64",
  log: "line 1\nline 2",
  images: [
    { name: "shot1.png", contentType: "image/png", data: base64(png()) },
    { name: "shot2.jpg", contentType: "image/jpeg", data: base64(jpeg()) },
  ],
};

const deps = (github: FakeGitHub) => ({ fetch: github.fetch, now: () => NOW, suffix: () => "ab12" });

function send(body: unknown, github: FakeGitHub = noGitHub(), headers?: Record<string, string>, target = env): Promise<Response> {
  return handleFeedback(feedbackRequest(body, headers), target, deps(github));
}

async function refused(body: unknown, status = 400): Promise<string> {
  const response = await send(body);
  expect(response.status, JSON.stringify(body).slice(0, 200)).toBe(status);
  return ((await response.json()) as { error: string }).error;
}

describe("POST /v1/feedback", () => {
  it("commits the images, then opens an issue with the metadata, the text, the images and the log; 202 with the id and issue", async () => {
    const github = fakeGitHub();

    const response = await send(GOOD, github);

    expect(response.status).toBe(202);
    expect(await response.json()).toEqual({ id: ID, issue: 42 });

    expect(github.calls.map((call) => `${call.method} ${call.url}`)).toEqual([
      `PUT https://api.github.com/repos/${REPO}/contents/feedback/${ID}/shot1.png`,
      `PUT https://api.github.com/repos/${REPO}/contents/feedback/${ID}/shot2.jpg`,
      `POST https://api.github.com/repos/${REPO}/issues`,
    ]);
    for (const call of github.calls) {
      expect(call.headers.authorization).toBe("Bearer test-github-token");
      expect(call.headers.accept).toBe("application/vnd.github+json");
      expect(call.headers["x-github-api-version"]).toBe("2022-11-28");
      expect(call.headers["user-agent"]).toBe("powerledger-data");
      expect(call.headers["content-type"]).toContain("application/json");
    }
    expect(github.calls[0].body).toEqual({ message: `Feedback ${ID}: shot1.png`, content: GOOD.images[0].data });
    expect(github.calls[1].body).toEqual({ message: `Feedback ${ID}: shot2.jpg`, content: GOOD.images[1].data });
    expect(github.calls[2].body).toEqual({
      title: "The night mode doesn't stick after a reboot. Second line. (0.7.0+abc1234, Windows 11 Home 26200, x64)",
      labels: ["feedback"],
      body: [
        "| Field | Value |",
        "|---|---|",
        `| id | \`${ID}\` |`,
        "| app | `0.7.0+abc1234` |",
        "| os | `Windows 11 Home 26200` |",
        "| arch | `x64` |",
        "| email | `someone@example.com` |",
        "| time | 2026-09-24 16:30:00 UTC |",
        "",
        "```",
        "The night mode doesn't stick after a reboot.",
        "Second line.",
        "```",
        "",
        `![shot1.png](https://github.com/${REPO}/blob/main/feedback/${ID}/shot1.png?raw=true)`,
        "",
        `![shot2.jpg](https://github.com/${REPO}/blob/main/feedback/${ID}/shot2.jpg?raw=true)`,
        "",
        "<details>",
        "<summary>Log</summary>",
        "",
        "```",
        "line 1",
        "line 2",
        "```",
        "</details>",
        "",
      ].join("\n"),
    });
  });

  it("does with the text alone: no email, log or images, and a title cut at 60 characters", async () => {
    const github = fakeGitHub();
    const text = "Word ".repeat(30).trim();

    const response = await send({ text, app: "0.7.0+abc1234", os: "Windows 11 Pro 26200", arch: "arm64" }, github);

    expect(response.status).toBe(202);
    expect(github.calls).toHaveLength(1);
    expect(github.calls[0].body).toEqual({
      title: `${"Word ".repeat(12).trim()}… (0.7.0+abc1234, Windows 11 Pro 26200, arm64)`,
      labels: ["feedback"],
      body: [
        "| Field | Value |",
        "|---|---|",
        `| id | \`${ID}\` |`,
        "| app | `0.7.0+abc1234` |",
        "| os | `Windows 11 Pro 26200` |",
        "| arch | `arm64` |",
        "| email | not given |",
        "| time | 2026-09-24 16:30:00 UTC |",
        "",
        "```",
        text,
        "```",
        "",
      ].join("\n"),
    });
  });

  it("commits a log over 60k characters as a file and links it, keeping a shorter one in the issue", async () => {
    const github = fakeGitHub();
    const log = "x".repeat(INLINE_LOG_CHARS + 1);

    expect((await send({ ...GOOD, images: [], log }, github)).status).toBe(202);

    expect(github.calls.map((call) => `${call.method} ${call.url}`)).toEqual([
      `PUT https://api.github.com/repos/${REPO}/contents/feedback/${ID}/log.txt`,
      `POST https://api.github.com/repos/${REPO}/issues`,
    ]);
    expect(github.calls[0].body).toEqual({ message: `Feedback ${ID}: log.txt`, content: btoa(log) });
    const body = github.calls[1].body!.body as string;
    expect(body).toContain(`\n[log.txt](https://github.com/${REPO}/blob/main/feedback/${ID}/log.txt)\n`);
    expect(body).not.toContain("<details>");

    const inline = fakeGitHub();
    expect((await send({ ...GOOD, images: [], log: "y".repeat(INLINE_LOG_CHARS) }, inline)).status).toBe(202);
    expect(inline.calls).toHaveLength(1);
    expect(inline.calls[0].body!.body).toContain("<details>");
  });

  it("fences the text and the log with more backticks than they hold, so neither can end its block", async () => {
    const github = fakeGitHub();

    expect((await send({ ...GOOD, images: [], text: "a\n```\nb", log: "````\nc" }, github)).status).toBe(202);

    const body = github.calls[0].body!.body as string;
    expect(body).toContain("\n````\na\n```\nb\n````\n");
    expect(body).toContain("\n`````\n````\nc\n`````\n");
  });

  it("strips control and bidi characters from the text, the email, the app, the os and the log", async () => {
    const github = fakeGitHub();
    const text = "Looks ‮odd‬ here\u0000\u0007​‏.\r\nNext\tline";
    const email = "me‮@example.com\u0000";
    const log = "one\u001B[31m two⁦three⁩\nfour";

    expect((await send({ ...GOOD, images: [], text, email, app: "0.7.0‎+sha", os: "Windows\u0000 11 Home", log }, github)).status).toBe(202);

    const issue = github.calls[0].body!;
    expect(issue.title).toBe("Looks odd here. Next line (0.7.0+sha, Windows 11 Home, x64)");
    expect(issue.body).toContain("\n```\nLooks odd here.\r\nNext\tline\n```\n");
    expect(issue.body).toContain("| email | `me@example.com` |");
    expect(issue.body).toContain("| app | `0.7.0+sha` |");
    expect(issue.body).toContain("| os | `Windows 11 Home` |");
    expect(issue.body).toContain("\n```\none[31m twothree\nfour\n```\n");
  });

  it("keeps the metadata cells to inline code, pipes and backticks made harmless, so nothing there is markdown", async () => {
    const github = fakeGitHub();

    expect((await send({ ...GOOD, images: [], os: "Win | `x` <img src=x>", email: "a@b.c" }, github)).status).toBe(202);

    expect(github.calls[0].body!.body).toContain("| os | `Win \\| 'x' <img src=x>` |");
  });

  it("refuses a body that isn't a JSON object, or text missing, empty, blank, not a string or over 10000 characters", async () => {
    expect(await refused("not json")).toBe("The body must be a JSON object.");
    expect(await refused([1])).toBe("The body must be a JSON object.");
    expect(await refused({ ...GOOD, text: undefined })).toBe("text is required, 10000 characters at most.");
    expect(await refused({ ...GOOD, text: "" })).toBe("text is required, 10000 characters at most.");
    expect(await refused({ ...GOOD, text: " \n​" })).toBe("text is required, 10000 characters at most.");
    expect(await refused({ ...GOOD, text: 12 })).toBe("text is required, 10000 characters at most.");
    expect(await refused({ ...GOOD, text: "x".repeat(10001) })).toBe("text is required, 10000 characters at most.");

    const github = fakeGitHub();
    expect((await send({ ...GOOD, images: [], text: "x".repeat(10000) }, github)).status).toBe(202);
  });

  it("refuses an email that isn't a string or null, is over 254 characters, or isn't an address", async () => {
    const message = "email must be an address, 254 characters at most, or left out.";
    expect(await refused({ ...GOOD, email: 5 })).toBe(message);
    expect(await refused({ ...GOOD, email: `${"a".repeat(245)}@example.com` })).toBe(message);
    expect(await refused({ ...GOOD, email: "no-at-sign" })).toBe(message);
    expect(await refused({ ...GOOD, email: "two words@example.com" })).toBe(message);
    expect(await refused({ ...GOOD, email: "a`b@example.com" })).toBe(message);

    const github = fakeGitHub();
    expect((await send({ ...GOOD, images: [], email: null }, github)).status).toBe(202);
    expect((await send({ ...GOOD, images: [], email: "" }, github)).status).toBe(202);
    expect(github.calls[1].body!.body).toContain("| email | not given |");
  });

  it("refuses app, os or arch missing or malformed", async () => {
    const app = "app must be the version, 64 characters at most.";
    expect(await refused({ ...GOOD, app: undefined })).toBe(app);
    expect(await refused({ ...GOOD, app: "" })).toBe(app);
    expect(await refused({ ...GOOD, app: "0.7.0 (sha)" })).toBe(app);
    expect(await refused({ ...GOOD, app: "1".repeat(65) })).toBe(app);
    const os = "os is required, 100 characters at most.";
    expect(await refused({ ...GOOD, os: undefined })).toBe(os);
    expect(await refused({ ...GOOD, os: "  " })).toBe(os);
    expect(await refused({ ...GOOD, os: "W".repeat(101) })).toBe(os);
    const arch = "arch must be x64, arm64 or x86.";
    expect(await refused({ ...GOOD, arch: undefined })).toBe(arch);
    expect(await refused({ ...GOOD, arch: "X64" })).toBe(arch);
    expect(await refused({ ...GOOD, arch: "amd64" })).toBe(arch);
  });

  it("refuses a log that isn't a string or null, or over 200 KB", async () => {
    const message = "log must be text, 200 KB at most, or left out.";
    expect(await refused({ ...GOOD, log: 5 })).toBe(message);
    expect(await refused({ ...GOOD, log: "x".repeat(200 * 1024 + 1) })).toBe(message);

    const github = fakeGitHub();
    expect((await send({ ...GOOD, images: [], log: null }, github)).status).toBe(202);
    expect((await send({ ...GOOD, images: [], log: "" }, github)).status).toBe(202);
    expect(github.calls[1].body!.body).not.toContain("<details>");
  });

  it("refuses images that aren't up to 5 of a PNG or JPEG with a matching name, each 1 MB at most", async () => {
    const image = (over: Partial<(typeof GOOD.images)[0]>) => ({ ...GOOD, images: [{ ...GOOD.images[0], ...over }] });
    const shape = "images must be up to 5 of {\"name\",\"contentType\",\"data\"}.";
    expect(await refused({ ...GOOD, images: {} })).toBe(shape);
    expect(await refused({ ...GOOD, images: Array.from({ length: 6 }, () => GOOD.images[0]) })).toBe(shape);
    expect(await refused({ ...GOOD, images: ["shot.png"] })).toBe(shape);

    const name = "images[0].name must be a file name of up to 80 letters, digits, dots, dashes and underscores, ending as its type: .png, .jpg or .jpeg.";
    expect(await refused(image({ name: "" }))).toBe(name);
    expect(await refused(image({ name: "a b.png" }))).toBe(name);
    expect(await refused(image({ name: "../x.png" }))).toBe(name);
    expect(await refused(image({ name: `${"a".repeat(77)}.png` }))).toBe(name);
    expect(await refused(image({ name: "shot.jpg" }))).toBe(name);
    expect(await refused(image({ name: "shot.png", contentType: "image/jpeg", data: base64(jpeg()) }))).toBe(name);
    expect(await refused({ ...GOOD, images: [GOOD.images[0], { ...GOOD.images[0], name: "SHOT1.png" }] })).toBe(
      "images[1].name is the same as another's.",
    );

    expect(await refused(image({ contentType: "image/gif" }))).toBe("images[0].contentType must be image/png or image/jpeg.");

    const data = "images[0].data must be the image as base64, 1 MB at most.";
    expect(await refused(image({ data: "not base64!" }))).toBe(data);
    expect(await refused(image({ data: base64(png()).slice(1) }))).toBe(data);
    expect(await refused(image({ data: base64(png(1024 * 1024 + 1)) }))).toBe(data);
    expect(await refused(image({ data: base64(crypto.getRandomValues(new Uint8Array(40))) }))).toBe(
      "images[0].data isn't a PNG or JPEG.",
    );
    expect(await refused(image({ contentType: "image/jpeg", name: "shot1.jpeg" }))).toBe("images[0].data isn't a PNG or JPEG.");

    const github = fakeGitHub();
    const full = image({ data: base64(png(1024 * 1024)) });
    expect((await send({ ...full, images: [...full.images, { name: "b.jpeg", contentType: "image/jpeg", data: base64(jpeg()) }] }, github)).status).toBe(202);
    expect(github.calls).toHaveLength(3);
  });

  it("refuses a request over 8 MB before reading it", async () => {
    const response = await send(GOOD, noGitHub(), { "Content-Length": String(MAX_FEEDBACK_BYTES + 1) });
    expect(response.status).toBe(413);
  });

  it("takes 10 an hour from an address, an IPv6 one by its /64, then 429 until the hour passes", async () => {
    const address = randomAddress();
    const other = randomAddress();
    const github = fakeGitHub();
    for (let i = 0; i < FEEDBACK_PER_HOUR; i++) {
      expect((await send(GOOD, github, { "CF-Connecting-IP": address })).status, `request ${i + 1}`).toBe(202);
    }

    const limited = await send(GOOD, noGitHub(), { "CF-Connecting-IP": address });
    expect(limited.status).toBe(429);
    expect(await limited.json()).toEqual({ error: "Too much feedback from this address; try again in an hour." });
    expect((await send(GOOD, github, { "CF-Connecting-IP": other })).status).toBe(202);

    const v6 = "2001:db8:7:8::";
    for (let i = 0; i < FEEDBACK_PER_HOUR; i++) {
      expect((await send(GOOD, github, { "CF-Connecting-IP": `${v6}${i + 1}` })).status).toBe(202);
    }
    expect((await send(GOOD, noGitHub(), { "CF-Connecting-IP": "2001:db8:7:8:ffff::1" })).status).toBe(429);
    expect((await send(GOOD, github, { "CF-Connecting-IP": "2001:db8:7:9::1" })).status).toBe(202);

    // The next hour starts afresh.
    const later = { fetch: github.fetch, now: () => NOW + 60 * 60 * 1000, suffix: () => "ab12" };
    expect((await handleFeedback(feedbackRequest(GOOD, { "CF-Connecting-IP": address }), env, later)).status).toBe(202);
  });

  it("is behind the per-address limit every route shares", async () => {
    const refusing = { ...env, ADDRESS_LIMIT: { limit: async () => ({ success: false }) } } as Cloudflare.Env;
    expect((await send(GOOD, noGitHub(), undefined, refusing)).status).toBe(429);
  });

  it("gives 503 when GitHub fails, at a commit or at the issue, or can't be reached", async () => {
    const message = "The feedback couldn't be recorded; try again later.";
    for (const status of [500, 502, 401, 404]) {
      const failing = fakeGitHub(() => Response.json({ message: "nope" }, { status }));
      const response = await send(GOOD, failing);
      expect(response.status, `GitHub ${status}`).toBe(503);
      expect(await response.json()).toEqual({ error: message });
      expect(failing.calls).toHaveLength(1);
    }

    const atIssue = fakeGitHub((call) => (call.method === "POST" ? Response.json({}, { status: 503 }) : undefined));
    expect((await send(GOOD, atIssue)).status).toBe(503);
    expect(atIssue.calls).toHaveLength(3);

    const unreachable: FakeGitHub = { fetch: (async () => { throw new TypeError("fetch failed"); }) as typeof fetch, calls: [] };
    expect((await send(GOOD, unreachable)).status).toBe(503);
  });

  it("gives 503 while the repo or the token isn't set, before reading the body", async () => {
    const message = "Feedback isn't set up on the server yet.";
    for (const target of [
      { ...env, FEEDBACK_REPO: "" },
      { ...env, FEEDBACK_REPO: undefined },
      { ...env, FEEDBACK_GITHUB_TOKEN: "" },
      { ...env, FEEDBACK_REPO: "not-a-repo" },
    ] as Cloudflare.Env[]) {
      const response = await send(GOOD, noGitHub(), { "Content-Length": String(MAX_FEEDBACK_BYTES + 1) }, target);
      expect(response.status).toBe(503);
      expect(await response.json()).toEqual({ error: message });
    }
  });

  it("is routed at POST /v1/feedback, and nowhere else", async () => {
    const posted = await SELF.fetch("https://example.com/v1/feedback", {
      method: "POST",
      headers: { "CF-Connecting-IP": randomAddress() },
      body: "not json",
    });
    expect(posted.status).toBe(400);
    expect((await SELF.fetch("https://example.com/v1/feedback", { headers: { "CF-Connecting-IP": randomAddress() } })).status).toBe(404);
  });

  it("has its address counts dropped by the daily cron once their hour is past", async () => {
    const address = randomAddress();
    await env.DB.prepare("INSERT INTO feedback_addresses (address, utc_hour, count) VALUES (?, '2026-09-24T15', 3), (?, '2026-09-24T16', 1)")
      .bind(address, address)
      .run();

    await runRetention(env, new Date(NOW));

    const kept = await env.DB.prepare("SELECT utc_hour FROM feedback_addresses WHERE address = ?").bind(address).all();
    expect(kept.results).toEqual([{ utc_hour: "2026-09-24T16" }]);
  });
});
