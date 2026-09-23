export default {
  async fetch(): Promise<Response> {
    return Response.json({ error: "Not found." }, { status: 404 });
  },
} satisfies ExportedHandler<Cloudflare.Env>;
