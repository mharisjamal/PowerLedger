import { handleReport } from "./report";

export default {
  async fetch(request, env): Promise<Response> {
    const url = new URL(request.url);

    if (request.method === "POST" && url.pathname === "/v1/report") {
      return handleReport(request, env);
    }

    return Response.json({ error: "Not found." }, { status: 404 });
  },
} satisfies ExportedHandler<Cloudflare.Env>;
