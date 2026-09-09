import { Miniflare, Log, LogLevel } from "miniflare";
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";

const workerPath = fileURLToPath(new URL("../src/index.js", import.meta.url));
const fixtureWorkerPath = fileURLToPath(new URL("./state-fixture.js", import.meta.url));
const response = (body, status = 200) => new Response(body === null ? null : JSON.stringify(body), {
  status, headers: { "content-type": "application/json" }
});

/** All outbound fetches are intercepted here. There is no network fallback. */
export async function createRuntime(options = {}) {
  const outbound = [];
  const mails = [];
  const unexpected = [];
  const sessions = new Map();
  const mode = { failMail: false, failAuth: false, confirmationRequired: false };
  let sequence = 0;
  const user = email => ({
    id: "a46c5a04-426a-4e1e-becb-5c119ce151d0", email,
    user_metadata: { username: "Fixture User" }, email_confirmed_at: "2026-01-01T00:00:00Z",
    created_at: "2026-01-01T00:00:00Z", updated_at: "2026-09-01T00:00:00Z"
  });
  const upstream = async request => {
    const url = new URL(request.url);
    const raw = request.method === "GET" ? "" : await request.text();
    const body = raw ? JSON.parse(raw) : null;
    outbound.push({ url: url.toString(), method: request.method, body,
      authorization: request.headers.get("authorization"), apikey: request.headers.get("apikey") });
    if (url.origin === "https://api.brevo.com" && url.pathname === "/v3/smtp/email") {
      mails.push(body);
      return mode.failMail ? response({ message: "fixture mail failure" }, 503) : response({ messageId: "fixture-mail" }, 201);
    }
    if (url.origin === "https://supabase.example.test") {
      if (mode.failAuth) return response({ message: "fixture upstream unavailable" }, 503);
      if (url.pathname === "/auth/v1/token" && url.search === "?grant_type=password") {
        if (body.password === "wrong-password") return response({ message: "invalid credentials" }, 400);
        const token = `fixture-access-token-${++sequence}`;
        const account = user(body.email);
        sessions.set(`Bearer ${token}`, account);
        return response({ access_token: token, refresh_token: "fixture-refresh-token", expires_in: 3600, user: account });
      }
      if (url.pathname === "/auth/v1/signup") {
        const account = { ...user(body.email), user_metadata: body.data };
        if (mode.confirmationRequired || body.email.startsWith("confirm")) {
          account.email_confirmed_at = null;
          return response(account);
        }
        return response({ access_token: "fixture-signup-token", expires_in: 3600, user: account });
      }
      if (url.pathname === "/auth/v1/user") {
        const account = sessions.get(request.headers.get("authorization"));
        return account ? response(account) : response({ message: "invalid token" }, 401);
      }
      if (url.pathname === "/auth/v1/logout" && url.search === "?scope=local") {
        return sessions.delete(request.headers.get("authorization")) ? response(null, 204) : response({ message: "invalid token" }, 401);
      }
      if (["/auth/v1/recover", "/auth/v1/otp"].includes(url.pathname)) return response({});
      if (url.pathname === "/auth/v1/admin/generate_link") return response({
        action_link: "https://supabase.example.test/auth/v1/verify?token=fixture-recovery-token",
        email_otp: "987654"
      });
    }
    unexpected.push(url.toString());
    throw new Error(`Unexpected outbound request blocked: ${url.origin}${url.pathname}`);
  };
  const bindings = {
    SUPABASE_URL: "https://supabase.example.test", SUPABASE_ANON_KEY: "fixture-anon-key",
    SUPABASE_SERVICE_KEY: "fixture-service-key", BREVO_API_KEY: "fixture-brevo-key",
    BREVO_FROM: "noreply@example.test", BREVO_FROM_NAME: "UEModManager"
  };
  const className = options.stateFixture ? "StateFixture" : "AuthState";
  const core = {
    modules: true, scriptPath: options.stateFixture ? fixtureWorkerPath : workerPath,
    modulesRules: [{ type: "ESModule", include: ["**/*.js"] }],
    compatibilityDate: "2025-10-02", bindings, outboundService: upstream,
    durableObjects: options.noBinding ? {} : { SECURITY_STATE: { className, scriptName: "api", useSQLite: true } }
  };
  const frontScript = `export default { async fetch(request, env) {
    if (new URL(request.url).pathname.startsWith("/__fixture/")) return env.MAILBOX.fetch(request);
    return env.API.fetch(request);
  } };`;
  const mf = new Miniflare({
    host: "127.0.0.1", port: options.port ?? 0, log: new Log(LogLevel.ERROR),
    durableObjectsPersist: options.persist ?? false,
    workers: [
      { name: "fixture-front", modules: true, script: frontScript, compatibilityDate: "2025-10-02", outboundService: upstream,
        serviceBindings: { API: "api", MAILBOX: async request => {
          const url = new URL(request.url);
          if (url.pathname === "/__fixture/mail") {
            const email = url.searchParams.get("email")?.trim().toLowerCase();
            return response(mails.filter(mail => !email || mail.to[0].email === email));
          }
          if (url.pathname === "/__fixture/outbound") return response(outbound);
          return response({ message: "fixture route not found" }, 404);
        } } },
      { name: "api", ...core },
      { name: "edge-b", ...core }
    ]
  });
  await mf.ready;
  return { mf, outbound, mails, unexpected, mode,
    async object(name) {
      const ns = await mf.getDurableObjectNamespace("SECURITY_STATE", "api");
      const key = createHash("sha256").update(name).digest("hex");
      return ns.get(ns.idFromName(key));
    },
    async dispose() { await mf.dispose(); }
  };
}

if (process.argv.includes("--serve")) {
  const runtime = await createRuntime();
  console.log(JSON.stringify({ url: (await runtime.mf.ready).toString(), mailbox: "/__fixture/mail?email=user@example.test" }));
  for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, async () => { await runtime.dispose(); process.exit(0); });
}
