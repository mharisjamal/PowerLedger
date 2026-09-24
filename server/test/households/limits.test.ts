import { env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { addressOf } from "../../src/address";
import { handleHouseholdRoutes } from "../../src/households/routes";
import { handleReport } from "../../src/report";
import { createHousehold, newDevice, randomHouseholdId, signedFetch, signedRequest } from "./support";

/** The Worker with an address limit that refuses everyone. */
const refusing = { ...env, ADDRESS_LIMIT: { limit: async () => ({ success: false }) } } as Cloudflare.Env;

function withAddress(address: string): Request {
  return new Request("https://example.com", { headers: { "CF-Connecting-IP": address } });
}

describe("the per-address limit", () => {
  it("covers creating a household, member routes, account routes, signing in and out", async () => {
    const pc = await newDevice();
    const hid = await createHousehold(pc);
    const session = { Authorization: `Session ${"A".repeat(43)}` };
    const requests = [
      await signedRequest(pc, "POST", "/v1/households", JSON.stringify({ id: randomHouseholdId(), sign: pc.sign, dh: pc.dh })),
      await signedRequest(pc, "GET", `/v1/households/${hid}/members`),
      await signedRequest(pc, "POST", `/v1/households/${hid}/batches`, "{}"),
      await signedRequest(pc, "POST", "/v1/account/household", "{}", { headers: session }),
      await signedRequest(pc, "POST", "/v1/auth/signin", "{}"),
      await signedRequest(pc, "POST", "/v1/auth/signout", "", { headers: session }),
    ];

    for (const request of requests) {
      const response = await handleHouseholdRoutes(request, refusing);
      expect(response!.status, `${request.method} ${new URL(request.url).pathname}`).toBe(429);
    }
  });

  it("counts an IPv6 address by its /64, and an IPv4 one as it is", () => {
    const sameNetwork = ["2001:db8:1:2::5", "2001:0db8:0001:0002:ffff:ffff:ffff:ffff", "2001:DB8:1:2:0:0:0:1"];
    expect(new Set(sameNetwork.map((address) => addressOf(withAddress(address))))).toEqual(new Set(["2001:db8:1:2::/64"]));
    expect(addressOf(withAddress("2001:db8:1:3::5"))).toBe("2001:db8:1:3::/64");
    expect(addressOf(withAddress("::1"))).toBe("0:0:0:0::/64");
    expect(addressOf(withAddress("::ffff:192.0.2.7"))).toBe("192.0.2.7");
    expect(addressOf(withAddress("192.0.2.7"))).toBe("192.0.2.7");
    expect(addressOf(new Request("https://example.com"))).toBe("unknown");
  });

  it("keys the report routes' limit by the /64 too", async () => {
    const keys: string[] = [];
    const recording = {
      ...env,
      ADDRESS_LIMIT: {
        limit: async ({ key }: { key: string }) => {
          keys.push(key);
          return { success: false };
        },
      },
    } as unknown as Cloudflare.Env;
    const request = new Request("https://example.com/v1/report", {
      method: "POST",
      headers: { Authorization: `Bearer ${"A".repeat(43)}`, "CF-Connecting-IP": "2001:db8:aa:bb:1:2:3:4" },
    });

    expect((await handleReport(request, recording)).status).toBe(429);
    expect(keys).toEqual(["2001:db8:aa:bb::/64"]);
  });
});

describe("a signed request's headers and signer", () => {
  it("are checked before the body is read: a stranger's 2 MB batch is refused as unsigned, not as too big", async () => {
    const hid = await createHousehold(await newDevice());
    const stranger = await newDevice();

    const response = await signedFetch(stranger, "POST", `/v1/households/${hid}/batches`, "x".repeat(2 * 1024 * 1024));
    expect(response.status).toBe(401);
  });

  it("are checked before the body is read on account routes too: an unknown session's 1 MB body is refused as unsigned", async () => {
    const pc = await newDevice();
    const response = await signedFetch(pc, "PUT", "/v1/account/recovery", "x".repeat(1024 * 1024), {
      headers: { Authorization: `Session ${"A".repeat(43)}` },
    });
    expect(response.status).toBe(401);
  });

  it("are checked before the body is read on creating a household: an unsigned 1 MB body is refused as unsigned", async () => {
    const pc = await newDevice();
    const response = await signedFetch(pc, "POST", "/v1/households", "x".repeat(1024 * 1024), { time: 1 });
    expect(response.status).toBe(401);
  });
});
