/**
 * The key the per-address rate limit (ADDRESS_LIMIT) counts a request under: an IPv4 address as it is, and an IPv6 one
 * by its /64, which a subscriber is usually given whole, so stepping through the addresses in it gains nothing. An
 * IPv4-mapped IPv6 address counts as its IPv4 one; a request with no address, as "unknown".
 */
export function addressOf(request: Request): string {
  const address = request.headers.get("CF-Connecting-IP");
  if (!address) return "unknown";
  if (!address.includes(":")) return address;

  const mapped = /^::ffff:(\d{1,3}(?:\.\d{1,3}){3})$/i.exec(address);
  if (mapped) return mapped[1];

  const prefix = firstFourGroups(address.toLowerCase());
  return prefix ? `${prefix.join(":")}::/64` : address;
}

const GROUP = /^[0-9a-f]{1,4}$/;

/** An IPv6 address's first four 16-bit groups, without leading zeros; null when it isn't an IPv6 address. */
function firstFourGroups(address: string): string[] | null {
  const halves = address.split("::");
  if (halves.length > 2) return null;
  const split = (part: string): string[] => (part === "" ? [] : part.split(":"));
  const left = split(halves[0]);
  const right = halves.length === 2 ? split(halves[1]) : [];
  // A dotted IPv4 tail (64:ff9b::192.0.2.7) stands for the last two groups.
  const width = (groups: string[]) => groups.reduce((sum, group) => sum + (group.includes(".") ? 2 : 1), 0);
  const zeros = 8 - width(left) - width(right);
  if (halves.length === 2 ? zeros < 1 : zeros !== 0) return null;

  const groups = [...left, ...Array<string>(halves.length === 2 ? zeros : 0).fill("0"), ...right].slice(0, 4);
  if (!groups.every((group) => GROUP.test(group))) return null;
  return groups.map((group) => parseInt(group, 16).toString(16));
}
