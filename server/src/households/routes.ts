import { type MemberRow, verifySigned } from "./auth";
import { handleGetBatches, handlePostBatch, readBatch } from "./batches";
import {
  handleAddMember,
  handleCreateHousehold,
  handleGetKey,
  handleListMembers,
  handlePostKeys,
  handleRemoveMember,
  readSmall,
} from "./households";
import { handleGetSlot, handlePutSlot, MEETING_SLOTS } from "./meetings";

type Params = string[];
type Handler = (request: Request, env: Cloudflare.Env, params: Params) => Promise<Response>;

interface Route {
  method: string;
  path: RegExp;
  handle: Handler;
}

const HID = "([0-9a-f]{32})";
const DEVICE = "([0-9a-f]{32})";
const MEETING = `^/v1/meetings/([0-9a-f]{32})/(${MEETING_SLOTS.join("|")})$`;

/** A route signed by a current member of the household in the path: reads the body (16 KB at most unless `read` says
 * otherwise), checks the signature and membership, then acts. */
function asMember(
  act: (env: Cloudflare.Env, member: MemberRow, body: Uint8Array, params: Params, request: Request) => Promise<Response>,
  read: (request: Request) => Promise<Uint8Array | Response> = readSmall,
): Handler {
  return async (request, env, params) => {
    const body = await read(request);
    if (body instanceof Response) return body;
    const member = await verifySigned(request, env, body, { household: params[0] });
    if (member instanceof Response) return member;
    return act(env, member, body, params, request);
  };
}

const ROUTES: Route[] = [
  { method: "POST", path: /^\/v1\/households$/, handle: (request, env) => handleCreateHousehold(request, env) },
  {
    method: "GET",
    path: new RegExp(`^/v1/households/${HID}/members$`),
    handle: asMember((env, member) => handleListMembers(env, member)),
  },
  {
    method: "POST",
    path: new RegExp(`^/v1/households/${HID}/members$`),
    handle: asMember((env, member, body) => handleAddMember(env, member, body)),
  },
  {
    method: "DELETE",
    path: new RegExp(`^/v1/households/${HID}/members/${DEVICE}$`),
    handle: asMember((env, member, _body, params) => handleRemoveMember(env, member, params[1])),
  },
  {
    method: "POST",
    path: new RegExp(`^/v1/households/${HID}/keys$`),
    handle: asMember((env, member, body) => handlePostKeys(env, member, body)),
  },
  {
    method: "GET",
    path: new RegExp(`^/v1/households/${HID}/keys/([0-9]{1,10})$`),
    handle: asMember((env, member, _body, params) => handleGetKey(env, member, Number(params[1]))),
  },
  {
    method: "POST",
    path: new RegExp(`^/v1/households/${HID}/batches$`),
    handle: asMember((env, member, body) => handlePostBatch(env, member, body), readBatch),
  },
  {
    method: "GET",
    path: new RegExp(`^/v1/households/${HID}/batches$`),
    handle: asMember((env, member, _body, _params, request) => handleGetBatches(env, member, new URL(request.url))),
  },
  {
    method: "PUT",
    path: new RegExp(MEETING),
    handle: (request, env, params) => handlePutSlot(request, env, params[0], params[1]),
  },
  {
    method: "GET",
    path: new RegExp(MEETING),
    handle: (request, env, params) => handleGetSlot(request, env, params[0], params[1]),
  },
];

/** The households routes (households design §5 to §8), or null when the request is for none of them. */
export async function handleHouseholdRoutes(request: Request, env: Cloudflare.Env): Promise<Response | null> {
  const { pathname } = new URL(request.url);
  for (const route of ROUTES) {
    if (route.method !== request.method) continue;
    const match = route.path.exec(pathname);
    if (match) return route.handle(request, env, match.slice(1));
  }
  return null;
}
