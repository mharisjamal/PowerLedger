/**
 * The little of GitHub's REST API that feedback needs: committing a file through the Contents API and opening an issue,
 * in one repo with one token. `fetch` is injectable, so tests stand in for GitHub.
 */

/** "owner/name", each part as GitHub allows: letters, digits, dots, dashes and underscores. */
export const REPO_NAME = /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/;
const API = "https://api.github.com";

export interface GitHubConfig {
  repo: string;
  token: string;
}

export class GitHubError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = "GitHubError";
  }
}

export class GitHub {
  constructor(
    private readonly config: GitHubConfig,
    private readonly fetcher: typeof fetch = (input, init) => fetch(input, init),
  ) {}

  /** Commits `bytes` at `path` on the default branch, one commit; gives the file's page on github.com. */
  async putFile(path: string, bytes: Uint8Array, message: string): Promise<string> {
    const reply = await this.call<{ content?: { html_url?: string } }>("PUT", `contents/${path}`, { message, content: base64(bytes) });
    return reply.content?.html_url ?? `https://github.com/${this.config.repo}/blob/main/${path}`;
  }

  /** Opens an issue, giving its number. */
  async createIssue(title: string, body: string, labels: string[]): Promise<number> {
    const reply = await this.call<{ number?: number }>("POST", "issues", { title, body, labels });
    if (typeof reply.number !== "number") throw new GitHubError(502, "GitHub gave no issue number.");
    return reply.number;
  }

  private async call<Reply>(method: string, path: string, body: unknown): Promise<Reply> {
    const response = await this.fetcher(`${API}/repos/${this.config.repo}/${path}`, {
      method,
      headers: {
        Authorization: `Bearer ${this.config.token}`,
        Accept: "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "powerledger-data",
        "Content-Type": "application/json",
      },
      body: JSON.stringify(body),
    });
    if (!response.ok) throw new GitHubError(response.status, `GitHub gave ${response.status} to ${method} ${path}.`);
    return (await response.json()) as Reply;
  }
}

/** Standard base64 of any length of bytes (btoa wants a binary string, built here a chunk at a time). */
export function base64(bytes: Uint8Array): string {
  let binary = "";
  for (let offset = 0; offset < bytes.length; offset += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  }
  return btoa(binary);
}
