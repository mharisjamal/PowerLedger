import {
  handleApprove,
  handleAskToJoin,
  handleCommit,
  handleDeleteAccount,
  handleDenyRequest,
  handleGetRecovery,
  handleLink,
  handleListRequests,
  handleOwnRequests,
  handlePutRecovery,
  handleRecover,
  handleRequestNonce,
  handleReveal,
  handleSignout,
} from "./account";
import { checkMember, checkSession, finishMember, finishSession, type MemberRow, type SessionRow } from "./auth";
import { handleGetBatches, handlePostBatch, readBatch } from "./batches";
import {
  handleAddMember,
  handleCreateHousehold,
  handleGetKey,
  handleListMembers,
  handlePostKeys,
  handleRemoveMember,
  readMedium,
  readSmall,
} from "./households";
import { overAddressLimit } from "./http";
import { handleGetSlot, handlePutSlot, MEETING_SLOTS } from "./meetings";
import { handleSignin } from "./signin";

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

/**
 * A route signed by a current member of the household in the path. Behind the address limit; the headers and the
 * member row are checked before the body is read (16 KB at most unless `read` says otherwise), then the signature, and
 * only then does it act.
 */
function asMember(
  act: (env: Cloudflare.Env, member: MemberRow, body: Uint8Array, params: Params, request: Request) => Promise<Response>,
  read: (request: Request) => Promise<Uint8Array | Response> = readSmall,
): Handler {
  return async (request, env, params) => {
    const limited = await overAddressLimit(request, env);
    if (limited) return limited;
    const check = await checkMember(request, env, params[0]);
    if (check instanceof Response) return check;
    const body = await read(request);
    if (body instanceof Response) return body;
    const member = await finishMember(request, env, check, body);
    if (member instanceof Response) return member;
    return act(env, member, body, params, request);
  };
}

/** An account route (N2): behind the address limit, a current session checked before the body is read, and the request
 * signed by that session's PC. */
function asSession(
  act: (env: Cloudflare.Env, session: SessionRow, body: Uint8Array) => Promise<Response>,
  read: (request: Request) => Promise<Uint8Array | Response> = readSmall,
): Handler {
  return async (request, env) => {
    const limited = await overAddressLimit(request, env);
    if (limited) return limited;
    const check = await checkSession(request, env);
    if (check instanceof Response) return check;
    const body = await read(request);
    if (body instanceof Response) return body;
    const session = await finishSession(request, env, check, body);
    if (session instanceof Response) return session;
    return act(env, session, body);
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
  { method: "POST", path: /^\/v1\/auth\/signin$/, handle: (request, env) => handleSignin(request, env) },
  { method: "POST", path: /^\/v1\/account\/household$/, handle: asSession(handleLink) },
  { method: "POST", path: /^\/v1\/account\/requests$/, handle: asSession((env, session) => handleAskToJoin(env, session)) },
  { method: "GET", path: /^\/v1\/account\/requests$/, handle: asSession((env, session) => handleOwnRequests(env, session)) },
  { method: "POST", path: /^\/v1\/account\/requests\/nonce$/, handle: asSession(handleRequestNonce) },
  { method: "PUT", path: /^\/v1\/account\/recovery$/, handle: asSession(handlePutRecovery, readMedium) },
  { method: "GET", path: /^\/v1\/account\/recovery$/, handle: asSession((env, session) => handleGetRecovery(env, session)) },
  { method: "POST", path: /^\/v1\/account\/recover$/, handle: asSession(handleRecover) },
  { method: "POST", path: /^\/v1\/auth\/signout$/, handle: (request, env) => handleSignout(request, env) },
  { method: "DELETE", path: /^\/v1\/account$/, handle: asSession((env, session) => handleDeleteAccount(env, session)) },
  {
    method: "GET",
    path: new RegExp(`^/v1/households/${HID}/requests$`),
    handle: asMember((env, member) => handleListRequests(env, member)),
  },
  {
    method: "POST",
    path: new RegExp(`^/v1/households/${HID}/requests/${DEVICE}/approve$`),
    handle: asMember((env, member, body, params) => handleApprove(env, member, params[1], body), readMedium),
  },
  {
    method: "POST",
    path: new RegExp(`^/v1/households/${HID}/requests/${DEVICE}/commit$`),
    handle: asMember((env, member, body, params) => handleCommit(env, member, params[1], body)),
  },
  {
    method: "POST",
    path: new RegExp(`^/v1/households/${HID}/requests/${DEVICE}/reveal$`),
    handle: asMember((env, member, body, params) => handleReveal(env, member, params[1], body)),
  },
  {
    method: "DELETE",
    path: new RegExp(`^/v1/households/${HID}/requests/${DEVICE}$`),
    handle: asMember((env, member, _body, params) => handleDenyRequest(env, member, params[1])),
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
