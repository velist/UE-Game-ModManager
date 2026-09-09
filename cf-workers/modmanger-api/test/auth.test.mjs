import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { createRuntime } from "./runtime.mjs";

async function usingRuntime(run, options) {
  const runtime = await createRuntime(options);
  try { await run(runtime); assert.deepEqual(runtime.unexpected, []); }
  finally { await runtime.dispose(); }
}

async function call(runtime, path, body, { method = "POST", ip = "192.0.2.1", token, edge = "api" } = {}) {
  const worker = await runtime.mf.getWorker(edge);
  return worker.fetch(`https://api.example.test${path}`, {
    method,
    headers: { "content-type": "application/json", "cf-connecting-ip": ip, ...(token ? { authorization: token } : {}) },
    ...(method === "GET" ? {} : { body: JSON.stringify(body) })
  });
}

async function issue(runtime, email = "user@example.test", ip = "192.0.2.1") {
  const response = await call(runtime, "/api/auth/otp/request", { email, purpose: "login" }, { ip });
  assert.equal(response.status, 200, await response.clone().text());
  const result = await response.json();
  const mail = runtime.mails.at(-1);
  const code = mail.textContent.match(/验证码：(\d{6})/)[1];
  return { result, mail, code, email: email.trim().toLowerCase() };
}

function verify(runtime, issued, changes = {}, options = {}) {
  return call(runtime, "/api/auth/otp/verify", {
    email: issued.email, purpose: "login", challenge_id: issued.result.challenge_id, code: issued.code, ...changes
  }, options);
}

test("F05: removed mail relay refuses arbitrary anonymous content without an outbound call", async () => usingRuntime(async runtime => {
  for (const path of ["/email/send", "/v1/email/send"]) {
    const response = await call(runtime, path, {
      to: "victim@example.test", subject: "UEModManager malicious content", html: "<h1>attacker</h1>", text: "attacker"
    });
    assert.equal(response.status, 410);
  }
  assert.equal(runtime.outbound.length, 0);
}));

test("OTP mail is server-owned, normalizes the address and reveals only an opaque challenge", async () => usingRuntime(async runtime => {
  const issued = await issue(runtime, "  User@Example.Test  ");
  assert.deepEqual(Object.keys(issued.result).sort(), ["challenge_id", "expires_in", "message", "retry_after", "success"]);
  assert.match(issued.result.challenge_id, /^[0-9a-f]{64}$/);
  assert.ok(issued.result.expires_in > 0 && issued.result.expires_in <= 600);
  assert.equal(issued.result.retry_after, 60);
  assert.equal(issued.mail.to[0].email, "user@example.test");
  assert.match(issued.mail.subject, /登录验证码/);
  assert.ok(issued.mail.htmlContent.includes(issued.code));
  const response = await verify(runtime, issued, { email: " USER@EXAMPLE.TEST " });
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { success: true, verified_email: "user@example.test", purpose: "login" });
}));

test("OTP input cannot choose a body, subject, code, recipient alias or a different action", async () => usingRuntime(async runtime => {
  for (const body of [
    { email: "user@example.test", purpose: "login", subject: "attacker" },
    { email: "user@example.test", purpose: "login", html: "attacker" },
    { email: "user@example.test", purpose: "login", code: "123456" },
    { email: "user@example.test", purpose: "reset" },
    { email: "Name <user@example.test>", purpose: "login" },
    { email: "user@example.test\r\nbcc:other@example.test", purpose: "login" },
    { email: {}, purpose: "login" }
  ]) assert.equal((await call(runtime, "/api/auth/otp/request", body)).status, 400);
  const worker = await runtime.mf.getWorker("api");
  assert.equal((await worker.fetch("https://api.example.test/api/auth/otp/request", { method: "POST", body: "x".repeat(8193) })).status, 413);
  assert.equal(runtime.outbound.length, 0);
}));

test("OTP challenges are bound to email, purpose and challenge ID; success is single-use", async () => usingRuntime(async runtime => {
  const issued = await issue(runtime);
  assert.equal((await verify(runtime, issued, { email: "other@example.test" })).status, 401);
  assert.equal((await verify(runtime, issued, { purpose: "reset" })).status, 400);
  assert.equal((await verify(runtime, issued, { challenge_id: "0".repeat(64) })).status, 401);
  assert.equal((await verify(runtime, issued)).status, 200);
  assert.equal((await verify(runtime, issued)).status, 401);
}));

test("twenty simultaneous verifies through two Worker instances consume a challenge only once", async () => usingRuntime(async runtime => {
  const issued = await issue(runtime);
  const responses = await Promise.all(Array.from({ length: 20 }, (_, index) =>
    verify(runtime, issued, {}, { edge: index % 2 ? "edge-b" : "api" })));
  assert.equal(responses.filter(response => response.status === 200).length, 1);
  assert.equal(responses.filter(response => response.status === 401).length, 19);
}));

test("five incorrect guesses exhaust the challenge even when the sixth guess is correct", async () => usingRuntime(async runtime => {
  const issued = await issue(runtime);
  const wrongCode = issued.code === "000000" ? "000001" : "000000";
  for (let attempt = 0; attempt < 5; attempt++) assert.equal((await verify(runtime, issued, { code: wrongCode })).status, 401);
  assert.equal((await verify(runtime, issued)).status, 401);
}));

test("expired OTPs are rejected by the persisted server clock check", async () => usingRuntime(async runtime => {
  const issued = await issue(runtime, "expired@example.test");
  const object = await runtime.object("otp:expired@example.test:login");
  await object.fetch("https://security.internal/test/expire", { method: "POST" });
  assert.equal((await verify(runtime, issued)).status, 401);
}, { stateFixture: true }));

test("resend is rate-limited across address case; an allowed resend invalidates the previous code", async () => usingRuntime(async runtime => {
  const old = await issue(runtime);
  const limited = await call(runtime, "/api/auth/otp/request", { email: " USER@EXAMPLE.TEST ", purpose: "login" });
  assert.equal(limited.status, 429);
  assert.ok(Number(limited.headers.get("retry-after")) > 0);
  assert.equal(runtime.mails.length, 1);
  const object = await runtime.object("otp:user@example.test:login");
  await object.fetch("https://security.internal/test/allow-resend", { method: "POST" });
  const current = await issue(runtime);
  assert.notEqual(current.result.challenge_id, old.result.challenge_id);
  assert.equal((await verify(runtime, old)).status, 401);
  assert.equal((await verify(runtime, current)).status, 200);
}, { stateFixture: true }));

test("parallel send requests reserve one mail and failures expose no challenge or code", async () => usingRuntime(async runtime => {
  runtime.mode.failMail = true;
  const responses = await Promise.all(Array.from({ length: 5 }, (_, index) =>
    call(runtime, "/api/auth/otp/request", { email: "failed@example.test", purpose: "login" }, { edge: index % 2 ? "api" : "edge-b" })));
  assert.equal(responses.filter(response => response.status === 502).length, 1);
  assert.equal(responses.filter(response => response.status === 429).length, 4);
  assert.equal(runtime.mails.length, 1);
  for (const response of responses) {
    const body = await response.json();
    assert.equal(body.challenge_id, undefined);
    assert.equal(body.code, undefined);
    assert.equal(body.success, false);
  }
}));

test("F06: twenty simultaneous password logins across independent Workers allow exactly ten", async () => usingRuntime(async runtime => {
  // Keep this regression inside one fixed minute; the intended window policy
  // permits a new allowance at the next boundary.
  const remainingMs = 60000 - Date.now() % 60000;
  if (remainingMs < 2000) await new Promise(resolve => setTimeout(resolve, remainingMs + 10));
  const responses = await Promise.all(Array.from({ length: 20 }, (_, index) =>
    call(runtime, "/api/auth/login", { email: "user@example.test", password: "fixture-password" },
      { edge: index % 2 ? "edge-b" : "api" })));
  assert.equal(responses.filter(response => response.status === 200).length, 10);
  assert.equal(responses.filter(response => response.status === 429).length, 10);
  assert.equal(runtime.outbound.filter(request => request.url.includes("/auth/v1/token")).length, 10);
  for (const limited of responses.filter(response => response.status === 429)) assert.ok(Number(limited.headers.get("retry-after")) > 0);
}));

test("legacy password route shares the same limit and cannot double the allowance", async () => usingRuntime(async runtime => {
  const responses = await Promise.all(Array.from({ length: 20 }, (_, index) =>
    call(runtime, index % 2 ? "/auth/password" : "/api/auth/login", { email: "user@example.test", password: "fixture-password" })));
  assert.equal(responses.filter(response => response.status === 200).length, 10);
  assert.equal(responses.filter(response => response.status === 429).length, 10);
}));

test("rate counter advances at a window boundary instead of permanently locking its subject", async () => usingRuntime(async runtime => {
  const object = await runtime.object("rate:window-test");
  const consume = async () => (await object.fetch("https://security.internal/rate", {
    method: "POST", body: JSON.stringify({ limit: 1, window_sec: 60 })
  })).json();
  assert.equal((await consume()).allowed, true);
  const limited = await consume();
  assert.equal(limited.allowed, false);
  assert.ok(limited.retry_after > 0 && limited.retry_after <= 60);
  await object.fetch("https://security.internal/test/previous-window", { method: "POST" });
  assert.equal((await consume()).allowed, true);
}, { stateFixture: true }));

test("SQLite counters and pending OTPs survive complete workerd shutdown and restart", async () => {
  const directory = await mkdtemp(join(tmpdir(), "uemodmanager-worker-test-"));
  let runtime;
  try {
    runtime = await createRuntime({ persist: directory });
    let object = await runtime.object("rate:persistent-test");
    const consume = target => target.fetch("https://security.internal/rate", {
      method: "POST", body: JSON.stringify({ limit: 1, window_sec: 86400 })
    }).then(response => response.json());
    assert.equal((await consume(object)).allowed, true);
    const issued = await issue(runtime);
    await runtime.dispose();
    runtime = await createRuntime({ persist: directory });
    object = await runtime.object("rate:persistent-test");
    assert.equal((await consume(object)).allowed, false);
    assert.equal((await verify(runtime, issued)).status, 200);
    assert.equal((await verify(runtime, issued)).status, 401);
    assert.equal(runtime.outbound.length, 0);
  } finally {
    if (runtime) await runtime.dispose();
    assert.equal(dirname(resolve(directory)), resolve(tmpdir()));
    assert.ok(directory.includes("uemodmanager-worker-test-"));
    await rm(directory, { recursive: true, force: true });
  }
});

test("missing security binding fails closed for every public auth and mail action", async () => usingRuntime(async runtime => {
  const routes = [
    ["/api/auth/login", { email: "user@example.test", password: "fixture-password" }],
    ["/api/auth/register", { email: "user@example.test", password: "fixture-password" }],
    ["/api/auth/otp/request", { email: "user@example.test", purpose: "login" }],
    ["/api/auth/otp/verify", { email: "user@example.test", purpose: "login", challenge_id: "0".repeat(64), code: "123456" }],
    ["/auth/password", { email: "user@example.test", password: "fixture-password" }],
    ["/auth/otp/send", { email: "user@example.test" }], ["/auth/reset", { email: "user@example.test" }]
  ];
  for (const [path, body] of routes) assert.equal((await call(runtime, path, body)).status, 503, path);
  assert.equal((await call(runtime, "/api/auth/logout", {}, { token: "Bearer fixture" })).status, 503);
  assert.equal((await call(runtime, "/api/auth/validate", null, { method: "GET", token: "Bearer fixture" })).status, 503);
  assert.equal(runtime.outbound.length, 0);
}, { noBinding: true }));

test("F07: register/login/validate/logout implement the desktop snake_case contract", async () => usingRuntime(async runtime => {
  const register = await call(runtime, "/api/auth/register", { email: " User@Example.Test ", password: "fixture-password", username: "Example" });
  assert.equal(register.status, 200);
  const registered = await register.json();
  assert.equal(registered.success, true);
  assert.equal(registered.verification_required, false);
  assert.ok(Number.isInteger(registered.user_id) && registered.user_id >= 0 && registered.user_id <= 0x7fffffff);
  const signup = runtime.outbound.find(request => request.url.endsWith("/auth/v1/signup"));
  assert.deepEqual(signup.body, { email: "user@example.test", password: "fixture-password", data: { username: "Example" } });
  assert.equal(signup.apikey, "fixture-anon-key");
  const login = await call(runtime, "/v1/api/auth/login", { email: "USER@EXAMPLE.TEST", password: "fixture-password" });
  const signedIn = await login.json();
  assert.equal(login.status, 200);
  assert.equal(signedIn.success, true);
  assert.equal(signedIn.user.id, registered.user_id);
  assert.equal(signedIn.user.email, "user@example.test");
  assert.equal(signedIn.expires_in, 3600);
  assert.equal(login.headers.get("cache-control"), "no-store");
  const token = `Bearer ${signedIn.access_token}`;
  const validate = await call(runtime, "/api/auth/validate", null, { method: "GET", token });
  assert.equal(validate.status, 200);
  assert.equal((await validate.json()).valid, true);
  const logout = await call(runtime, "/api/auth/logout", null, { token });
  assert.equal(logout.status, 200);
  assert.equal((await logout.json()).success, true);
  assert.equal((await call(runtime, "/api/auth/validate", null, { method: "GET", token })).status, 401);
  assert.ok(runtime.outbound.some(request => request.url.endsWith("/auth/v1/logout?scope=local") && request.authorization === token));
}));

test("registration requiring email confirmation is reported as pending verification", async () => usingRuntime(async runtime => {
  runtime.mode.confirmationRequired = true;
  const response = await call(runtime, "/api/auth/register", { email: "confirm@example.test", password: "fixture-password" });
  const body = await response.json();
  assert.equal(response.status, 200);
  assert.equal(body.success, true);
  assert.equal(body.verification_required, true);
  assert.equal(body.verification_email_sent, true);
  assert.equal(body.access_token, undefined);
}));

test("auth routes reject absent/invalid bearer, methods and malformed credentials", async () => usingRuntime(async runtime => {
  for (const path of ["/api/auth/logout", "/api/auth/validate"]) {
    const method = path.endsWith("validate") ? "GET" : "POST";
    assert.equal((await call(runtime, path, null, { method })).status, 401);
    assert.equal((await call(runtime, path, null, { method, token: "Bearer invalid" })).status, 401);
  }
  assert.equal((await call(runtime, "/api/auth/register", {}, { method: "GET" })).status, 405);
  assert.equal((await call(runtime, "/api/auth/login", { email: {}, password: "fixture-password" })).status, 400);
  assert.equal((await call(runtime, "/api/auth/login", { email: "user@example.test", password: "wrong-password" })).status, 401);
}));

test("upstream failures are sanitized; legacy reset still works without returning a recovery secret", async () => usingRuntime(async runtime => {
  const reset = await call(runtime, "/auth/reset", { email: "RESET@EXAMPLE.TEST", redirect_to: "https://modmanger.com/reset" });
  assert.equal(reset.status, 200);
  assert.equal(runtime.outbound.at(-1).body.email, "reset@example.test");
  assert.ok(!(await reset.text()).includes("fixture-recovery-token"));
  runtime.mode.failAuth = true;
  const failure = await call(runtime, "/api/auth/login", { email: "user@example.test", password: "fixture-password" });
  assert.equal(failure.status, 502);
  const body = await failure.text();
  assert.ok(!body.includes("fixture") && !body.includes("supabase.example.test"));
}));
