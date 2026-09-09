import { loginCodeMail, sendBrevoMail } from "./mail.js";

const OTP_TTL_MS = 10 * 60 * 1000;
const OTP_RESEND_MS = 60 * 1000;
const OTP_ATTEMPTS = 5;

export class SecurityUnavailable extends Error {}

export function authJson(data, status = 200, headers = {}) {
  return new Response(JSON.stringify(data), {
    status, headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store", ...headers }
  });
}

export function normalizeEmail(value) {
  if (typeof value !== "string") return null;
  const email = value.trim().toLowerCase();
  // Match the desktop contract; reject display names, control characters,
  // embedded addresses and oversized inputs before any upstream request.
  return email.length <= 254 && /^[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?\.[a-z]{2,63}$/.test(email)
    ? email : null;
}

export async function sha256(value) {
  const bytes = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return Array.from(new Uint8Array(bytes), byte => byte.toString(16).padStart(2, "0")).join("");
}

export async function securityObject(env, name) {
  if (!env.SECURITY_STATE) throw new SecurityUnavailable("security state binding is missing");
  // Do not put raw addresses or IPs into object names or logs.
  const id = env.SECURITY_STATE.idFromName(await sha256(name));
  return env.SECURITY_STATE.get(id);
}

export async function rateLimit(env, key, limit, windowSec) {
  try {
    const object = await securityObject(env, `rate:${key}`);
    const response = await object.fetch("https://security.internal/rate", {
      method: "POST", body: JSON.stringify({ limit, window_sec: windowSec })
    });
    if (!response.ok) throw new Error("rate limiter rejected its configuration");
    const result = await response.json();
    if (typeof result.allowed !== "boolean" || !Number.isInteger(result.retry_after)) throw new Error("invalid limiter response");
    return result;
  } catch (error) {
    if (error instanceof SecurityUnavailable) throw error;
    throw new SecurityUnavailable("rate limiter is unavailable");
  }
}

export function rateLimitResponse(result) {
  return authJson({ success: false, message: "请求过于频繁，请稍后重试", retry_after: result.retry_after }, 429,
    { "retry-after": String(Math.max(1, result.retry_after)) });
}

export async function checkMailLimits(request, env, email) {
  const ip = request.headers.get("cf-connecting-ip") || "unknown";
  for (const [key, limit, seconds] of [
    [`mail:ip:${ip}`, 10, 60], [`mail:address:${email}`, 6, 3600], ["mail:global", 300, 3600]
  ]) {
    const result = await rateLimit(env, key, limit, seconds);
    if (!result.allowed) return rateLimitResponse(result);
  }
  return null;
}

function randomCode() {
  const limit = Math.floor(0x100000000 / 1000000) * 1000000;
  const value = new Uint32Array(1);
  do { crypto.getRandomValues(value); } while (value[0] >= limit);
  return String(value[0] % 1000000).padStart(6, "0");
}

function randomChallenge() {
  return Array.from(crypto.getRandomValues(new Uint8Array(32)), byte => byte.toString(16).padStart(2, "0")).join("");
}

function equalHashes(left, right) {
  if (left.length !== right.length) return false;
  let diff = 0;
  for (let i = 0; i < left.length; i++) diff |= left.charCodeAt(i) ^ right.charCodeAt(i);
  return diff === 0;
}

function invalidCode() {
  return authJson({ success: false, message: "验证码错误、已使用或已过期，请重新获取" }, 401);
}

/**
 * One SQLite Durable Object per rate-limit subject / email + action. No
 * process-local lock or KV read/modify/write is used. transactionSync commits
 * the decision and increment/consumption atomically, across Worker instances,
 * concurrent requests and object eviction. This class has no public route.
 */
export class AuthState {
  constructor(state, env) {
    this.state = state;
    this.env = env;
    this.sql = state.storage.sql;
    this.sql.exec(`CREATE TABLE IF NOT EXISTS rate_limit (
      singleton INTEGER PRIMARY KEY CHECK(singleton = 1), bucket INTEGER NOT NULL,
      count INTEGER NOT NULL, end_at INTEGER NOT NULL, window_ms INTEGER NOT NULL)`);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS otp (
      singleton INTEGER PRIMARY KEY CHECK(singleton = 1), email TEXT NOT NULL, purpose TEXT NOT NULL,
      challenge_id TEXT NOT NULL, code_hash TEXT NOT NULL, expires_at INTEGER NOT NULL,
      send_after INTEGER NOT NULL, attempts INTEGER NOT NULL, active INTEGER NOT NULL)`);
  }

  async fetch(request) {
    if (request.method !== "POST") return authJson({ message: "method not allowed" }, 405);
    const body = await request.json().catch(() => null);
    if (!body || typeof body !== "object" || Array.isArray(body)) return authJson({ message: "invalid request" }, 400);
    const path = new URL(request.url).pathname;
    if (path === "/rate") return this.consumeRate(body);
    const email = normalizeEmail(body.email);
    if (!email || body.purpose !== "login") return authJson({ message: "invalid action or email" }, 400);
    if (path === "/otp/request") return this.requestOtp(email, body.purpose);
    if (path === "/otp/verify") return this.verifyOtp(email, body);
    return authJson({ message: "not found" }, 404);
  }

  async consumeRate(body) {
    const { limit, window_sec: windowSec } = body;
    if (!Number.isInteger(limit) || limit < 1 || limit > 10000 ||
        !Number.isInteger(windowSec) || windowSec < 1 || windowSec > 86400) {
      return authJson({ message: "invalid rate policy" }, 400);
    }
    const now = Date.now();
    const windowMs = windowSec * 1000;
    const bucket = Math.floor(now / windowMs);
    const endAt = (bucket + 1) * windowMs;
    const result = this.state.storage.transactionSync(() => {
      const previous = this.sql.exec("SELECT * FROM rate_limit WHERE singleton = 1").toArray()[0];
      const count = previous?.bucket === bucket && previous?.window_ms === windowMs ? previous.count : 0;
      const allowed = count < limit;
      if (allowed) {
        this.sql.exec(`INSERT INTO rate_limit VALUES (1, ?, ?, ?, ?)
          ON CONFLICT(singleton) DO UPDATE SET bucket = excluded.bucket, count = excluded.count,
          end_at = excluded.end_at, window_ms = excluded.window_ms`, bucket, count + 1, endAt, windowMs);
      }
      return { allowed, remaining: Math.max(0, limit - count - (allowed ? 1 : 0)),
        retry_after: allowed ? 0 : Math.max(1, Math.ceil((endAt - now) / 1000)) };
    });
    await this.state.storage.setAlarm(endAt);
    return authJson(result);
  }

  async requestOtp(email, purpose) {
    const code = randomCode();
    const challenge = randomChallenge();
    const hash = await sha256(`${challenge}\n${email}\n${purpose}\n${code}`);
    const now = Date.now();
    const expiresAt = now + OTP_TTL_MS;
    const retryAfter = this.state.storage.transactionSync(() => {
      const previous = this.sql.exec("SELECT send_after FROM otp WHERE singleton = 1").toArray()[0];
      if (previous?.send_after > now) return Math.ceil((previous.send_after - now) / 1000);
      this.sql.exec(`INSERT INTO otp VALUES (1, ?, ?, ?, ?, ?, ?, 0, 0)
        ON CONFLICT(singleton) DO UPDATE SET email = excluded.email, purpose = excluded.purpose,
        challenge_id = excluded.challenge_id, code_hash = excluded.code_hash, expires_at = excluded.expires_at,
        send_after = excluded.send_after, attempts = 0, active = 0`,
        email, purpose, challenge, hash, expiresAt, now + OTP_RESEND_MS);
      return 0;
    });
    if (retryAfter > 0) return rateLimitResponse({ retry_after: retryAfter });
    await this.state.storage.setAlarm(expiresAt);
    try {
      const mail = loginCodeMail(code);
      const sent = await sendBrevoMail(this.env, email, mail.subject, mail.html, mail.text);
      if (!sent.ok) throw new Error(`mail upstream status ${sent.status}`);
      const active = this.state.storage.transactionSync(() => {
        this.sql.exec("UPDATE otp SET active = 1 WHERE challenge_id = ? AND expires_at > ?", challenge, Date.now());
        return this.sql.exec("SELECT active FROM otp WHERE challenge_id = ?", challenge).toArray()[0]?.active === 1;
      });
      if (!active) return authJson({ success: false, message: "验证码已过期，请重新获取" }, 409);
      return authJson({ success: true, message: "验证码已发送，请查收邮件", challenge_id: challenge,
        expires_in: Math.max(1, Math.ceil((expiresAt - Date.now()) / 1000)), retry_after: 60 });
    } catch (error) {
      // Keep the send cooldown even when the mail provider is down; do not
      // allow retries to turn an outage into unlimited upstream calls.
      this.sql.exec("UPDATE otp SET active = -1, code_hash = '' WHERE challenge_id = ?", challenge);
      console.error("[otp/request] delivery failed", error instanceof Error ? error.message : "upstream unavailable");
      return authJson({ success: false, message: "邮件发送失败，请稍后重试", retry_after: 60 }, 502,
        { "retry-after": "60" });
    }
  }

  async verifyOtp(email, body) {
    const { challenge_id: challenge, code, purpose } = body;
    if (typeof challenge !== "string" || !/^[0-9a-f]{64}$/.test(challenge) ||
        typeof code !== "string" || !/^\d{6}$/.test(code)) return invalidCode();
    const hash = await sha256(`${challenge}\n${email}\n${purpose}\n${code}`);
    return this.state.storage.transactionSync(() => {
      const record = this.sql.exec("SELECT * FROM otp WHERE singleton = 1").toArray()[0];
      if (!record || record.active !== 1 || record.email !== email || record.purpose !== purpose ||
          record.challenge_id !== challenge || record.expires_at <= Date.now() || record.attempts >= OTP_ATTEMPTS) return invalidCode();
      if (!equalHashes(record.code_hash, hash)) {
        const attempts = record.attempts + 1;
        this.sql.exec("UPDATE otp SET attempts = ?, active = ?, code_hash = ? WHERE singleton = 1",
          attempts, attempts >= OTP_ATTEMPTS ? -1 : 1, attempts >= OTP_ATTEMPTS ? "" : record.code_hash);
        return invalidCode();
      }
      // Preserve the resend cooldown, but consume the secret in the same
      // transaction as success so simultaneous verification can succeed once.
      this.sql.exec("UPDATE otp SET active = 2, code_hash = '' WHERE singleton = 1");
      return authJson({ success: true, verified_email: record.email, purpose: record.purpose });
    });
  }

  async alarm() {
    const now = Date.now();
    const next = this.state.storage.transactionSync(() => {
      this.sql.exec("DELETE FROM rate_limit WHERE end_at <= ?", now);
      this.sql.exec("DELETE FROM otp WHERE expires_at <= ?", now);
      const rate = this.sql.exec("SELECT end_at FROM rate_limit").toArray()[0]?.end_at;
      const otp = this.sql.exec("SELECT expires_at FROM otp").toArray()[0]?.expires_at;
      return Math.min(rate ?? Infinity, otp ?? Infinity);
    });
    if (Number.isFinite(next)) await this.state.storage.setAlarm(next);
  }
}
