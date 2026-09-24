/// <reference types="vite/client" />
import { describe, expect, it } from "vitest";

// Owner's round (0.8.0): no em dash or en dash reaches the App's screen. The Worker's own words that the App shows (its
// {"error": ...} messages, the feedback issue it files) are string literals in src/, so none of them may hold either,
// written out or escaped. Comments are left alone.

const EM = "\u2014";
const EN = "\u2013";

/** The lines of `source` where a string or template literal holds an em or en dash, written or as a \u escape. */
function dashLines(source: string): number[] {
  const hits = new Set<number>();
  let i = 0;
  let last = "";   // the last significant character of code, to tell a regex literal from a division

  const at = (offset = 0): string => source[i + offset] ?? "";
  const hit = (index: number): void => {
    hits.add(source.slice(0, index).split("\n").length);
  };
  const escape = (): void => {
    const kind = at(1);
    let digits = "";
    let length = 2;
    if (kind === "u" && at(2) === "{") {
      const end = source.indexOf("}", i + 3);
      digits = end < 0 ? "" : source.slice(i + 3, end);
      length = end < 0 ? 2 : end - i + 1;
    } else if (kind === "u") {
      digits = source.slice(i + 2, i + 6);
      length = 6;
    }
    const code = /^[0-9a-f]+$/i.test(digits) ? parseInt(digits, 16) : NaN;
    if (code === 0x2014 || code === 0x2013) hit(i);
    i += length;
  };
  const text = (char: string): void => {
    if (char === EM || char === EN) hit(i);
    i++;
  };
  const quoted = (quote: string): void => {
    i++;
    while (i < source.length && at() !== quote && at() !== "\n") {
      if (at() === "\\") escape();
      else text(at());
    }
    i++;
  };
  const template = (): void => {
    i++;
    while (i < source.length && at() !== "`") {
      if (at() === "\\") escape();
      else if (at() === "$" && at(1) === "{") {
        i += 2;
        code(true);
        i++;
      } else text(at());
    }
    i++;
  };
  const regex = (): void => {
    i++;
    let inClass = false;
    while (i < source.length && at() !== "\n") {
      const c = at();
      if (c === "\\") i += 2;
      else {
        if (c === "[") inClass = true;
        else if (c === "]") inClass = false;
        else if (c === "/" && !inClass) break;
        i++;
      }
    }
    i++;
    while (/[a-z]/i.test(at())) i++;
  };
  const code = (inHole: boolean): void => {
    let depth = 0;
    while (i < source.length) {
      const c = at();
      if (c === "/" && at(1) === "/") {
        while (i < source.length && at() !== "\n") i++;
      } else if (c === "/" && at(1) === "*") {
        const end = source.indexOf("*/", i + 2);
        i = end < 0 ? source.length : end + 2;
      } else if (c === '"' || c === "'") {
        quoted(c);
        last = "a";
      } else if (c === "`") {
        template();
        last = "a";
      } else if (c === "/" && (last === "" || "(,=:[!&|?{};+-*%<>~^".includes(last) || /\b(return|typeof|case|of|in)$/.test(source.slice(0, i).trimEnd()))) {
        regex();
        last = "a";
      } else {
        if (c === "{") depth++;
        else if (c === "}" && depth-- === 0 && inHole) return;
        if (!/\s/.test(c)) last = c;
        i++;
      }
    }
  };

  code(false);
  return [...hits].sort((a, b) => a - b);
}

const sources = import.meta.glob("../src/**/*.ts", { query: "?raw", import: "default", eager: true }) as Record<string, string>;

describe("no dashes in the Worker's words", () => {
  it("reads every source file", () => {
    expect(Object.keys(sources).length).toBeGreaterThan(10);
    expect(Object.values(sources).every((source) => source.includes("export"))).toBe(true);
  });

  it("holds no em or en dash in any string in src/", () => {
    const found = Object.entries(sources).flatMap(([file, source]) =>
      dashLines(source).map((line) => `${file.replace("../", "")}:${line}`),
    );
    expect(found).toEqual([]);
  });

  it.each([
    [`const a = "x ${EM} y";`, [1]],
    [`const a = 'x ${EN} y';`, [1]],
    ["const a = \"x \\u2014 y\";", [1]],
    ["const a = 'x \\u{2013} y';", [1]],
    [`const a = \`\${b} ${EM} \${c}\`;`, [1]],
    [`const a = \`\${b ? "x" : "${EM}"}\`;`, [1]],
    [`const a = 1;\nconst b = \`\n${EN}\`;`, [3]],
    [`const a = /"/.test(b); const c = "${EM}";`, [1]],
  ])("finds a dash in %s", (source, lines) => {
    expect(dashLines(source)).toEqual(lines);
  });

  it.each([
    `// x ${EM} y`,
    `/** x ${EM} "y" */ const a = "z";`,
    `const a = "x - y"; // ${EM}`,
    `const a = b / c; // ${EN} "`,
    `const a = /[${EM}"]/; // "`,
    `const a = "\\""; // ${EM}`,
  ])("leaves comments and code alone in %s", (source) => {
    expect(dashLines(source)).toEqual([]);
  });
});
