import { handleAdmin } from "./admin";
import { handleFeedback } from "./feedback";
import { handleHouseholdRoutes } from "./households/routes";
import { handleConsent, handleDelete } from "./install";
import { handleReport } from "./report";
import { runRetention } from "./retention";

export default {
  async fetch(request, env): Promise<Response> {
    const url = new URL(request.url);

    if (request.method === "POST" && url.pathname === "/v1/report") {
      return handleReport(request, env);
    }
    if (request.method === "POST" && url.pathname === "/v1/consent") {
      return handleConsent(request, env);
    }
    if (request.method === "POST" && url.pathname === "/v1/delete") {
      return handleDelete(request, env);
    }
    if (request.method === "POST" && url.pathname === "/v1/feedback") {
      return handleFeedback(request, env);
    }
    if (url.pathname.startsWith("/admin/")) {
      return handleAdmin(request, env);
    }

    const household = await handleHouseholdRoutes(request, env);
    if (household) return household;

    return Response.json({ error: "Not found." }, { status: 404 });
  },

  async scheduled(_controller, env, _ctx): Promise<void> {
    await runRetention(env);
  },
} satisfies ExportedHandler<Cloudflare.Env>;
