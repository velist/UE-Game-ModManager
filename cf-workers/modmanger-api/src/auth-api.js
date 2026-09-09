import { SecurityUnavailable, authJson, checkMailLimits, normalizeEmail, rateLimit, rateLimitResponse, securityObject } from "./auth-security.js";

class InvalidRequest extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

async function readBody(request) {
  const reader = request.body?.getReader();
  if (!reader) throw new InvalidRequest(400, "请求内容不能为空");
  const chunks = [];
  let length = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      length += value.byteLength;
      if (length > 8192) {
        await reader.cancel();
        throw new InvalidRequest(413, "请求内容过大");
      }
      chunks.push(value);
    }
  } finally { reader.releaseLock(); }
  const buffer = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) { buffer.set(chunk, offset); offset += chunk.byteLength; }
  let body;
  try { body = JSON.parse(new TextDecoder().decode(buffer)); }
  catch { throw new InvalidRequest(400, "请求必须为 JSON 对象"); }
  if (!body || typeof body !== "object" || Array.isArray(body)) throw new InvalidRequest(400, "请求必须为 JSON 对象");
  return body;
}

function requireFields(body, allowed) {
  if (Object.keys(body).some(key => !allowed.includes(key))) throw new InvalidRequest(400, "请求包含不支持的字段");
}

function requireEmail(value) {
  const email = normalizeEmail(value);
  if (!email) throw new InvalidRequest(400, "邮箱格式无效");
  return email;
}

function requirePassword(value, register = false) {
  if (typeof value !== "string" || value.length < (register ? 6 : 1) || value.length > 1024) {
    throw new InvalidRequest(400, register ? "密码至少需要 6 个字符" : "密码不能为空");
  }
  return value;
}

function bearer(request) {
  const value = request.headers.get("authorization") || "";
  return /^Bearer [^\s]{1,8192}$/i.test(value) ? value : null;
}

export async function forwardSupabase(env, path, init) {
  const base = (env.SUPABASE_URL || "").trim().replace(/[\r\n]/g, "");
  const key = (env.SUPABASE_ANON_KEY || "").trim().replace(/[\r\n]/g, "");
  if (!base || !key) throw new Error("authentication upstream is not configured");
  const url = new URL(path, base);
  if (url.protocol !== "https:") throw new Error("authentication upstream requires HTTPS");
  const headers = new Headers(init.headers);
  headers.set("apikey", key);
  if (!headers.has("authorization")) headers.set("authorization", `Bearer ${key}`);
  const response = await fetch(url.toString(), { ...init, headers });
  const raw = await response.text();
  let data;
  try { data = raw ? JSON.parse(raw) : null; } catch { data = null; }
  return { status: response.status, headers: response.headers, data };
}

function uuidToInt(uuid) {
  let hash = 0;
  for (const char of uuid.replace(/-/g, "")) hash = ((hash << 5) - hash + char.charCodeAt(0)) & 0x7fffffff;
  return hash;
}

function cloudUser(user) {
  if (!user || typeof user.id !== "string" || !normalizeEmail(user.email)) return null;
  const email = normalizeEmail(user.email);
  const now = new Date().toISOString();
  const name = user.user_metadata?.username || email.split("@")[0];
  return {
    id: uuidToInt(user.id), email, username: name, display_name: name, avatar: null,
    is_active: true, is_verified: Boolean(user.email_confirmed_at), created_at: user.created_at || now,
    updated_at: user.updated_at || now, last_login_at: now, subscription_type: "free", subscription_expires_at: null
  };
}

function upstreamError(status, message = "认证失败，请检查账户信息") {
  return authJson({ success: false, valid: false, message: status >= 500 ? "认证服务暂时不可用，请稍后重试" : message },
    status >= 500 ? 502 : status === 429 ? 429 : 401);
}

const AUTH_METHODS = new Map([
  ["/api/auth/login", "POST"], ["/api/auth/register", "POST"], ["/api/auth/logout", "POST"],
  ["/api/auth/validate", "GET"], ["/api/auth/otp/request", "POST"], ["/api/auth/otp/verify", "POST"],
  ["/email/send", "POST"]
]);

export async function handleAuthApi(request, env, path) {
  const method = AUTH_METHODS.get(path);
  if (!method) return null;
  if (path === "/email/send") {
    // Drain only the bounded legacy request before returning. Leaving a POST
    // unread resets the next .NET HTTP/1.1 keep-alive request in workerd.
    // Invalid or oversized bodies still cannot reactivate this retired route.
    try { await readBody(request); } catch { }
    return authJson({ success: false, message: "此邮件接口已停用，请升级客户端使用验证码登录" }, 410);
  }
  if (request.method !== method) return authJson({ success: false, message: "method not allowed" }, 405, { allow: method });
  try {
    const ip = request.headers.get("cf-connecting-ip") || "unknown";
    if (path === "/api/auth/otp/request" || path === "/api/auth/otp/verify") {
      const verify = path.endsWith("/verify");
      const body = await readBody(request);
      requireFields(body, verify ? ["email", "purpose", "challenge_id", "code"] : ["email", "purpose"]);
      const email = requireEmail(body.email);
      if (body.purpose !== "login") throw new InvalidRequest(400, "不支持的验证码用途");
      if (verify) {
        const limit = await rateLimit(env, `otp:verify:${ip}`, 30, 60);
        if (!limit.allowed) return rateLimitResponse(limit);
      } else {
        const limited = await checkMailLimits(request, env, email);
        if (limited) return limited;
      }
      const object = await securityObject(env, `otp:${email}:${body.purpose}`);
      return await object.fetch(`https://security.internal/otp/${verify ? "verify" : "request"}`, {
        method: "POST", body: JSON.stringify({ ...body, email })
      });
    }

    if (path === "/api/auth/login" || path === "/api/auth/register") {
      const register = path.endsWith("/register");
      const body = await readBody(request);
      requireFields(body, register ? ["email", "password", "username"] : ["email", "password"]);
      const email = requireEmail(body.email);
      const password = requirePassword(body.password, register);
      const username = body.username === undefined ? email.split("@")[0] : body.username;
      if (register && (typeof username !== "string" || username.length > 100 || /[\x00-\x1f\x7f]/.test(username))) {
        throw new InvalidRequest(400, "用户名无效");
      }
      const limit = await rateLimit(env, `${register ? "register" : "login"}:${ip}`, register ? 5 : 10, 60);
      if (!limit.allowed) return rateLimitResponse(limit);
      if (register) {
        const limited = await checkMailLimits(request, env, email);
        if (limited) return limited;
      }
      const response = await forwardSupabase(env, register ? "/auth/v1/signup" : "/auth/v1/token?grant_type=password", {
        method: "POST", headers: { "content-type": "application/json" },
        body: JSON.stringify(register ? { email, password, data: { username } } : { email, password })
      });
      if (response.status < 200 || response.status >= 300) return upstreamError(response.status,
        register ? "无法注册，请检查账户信息或使用已有账户登录" : "邮箱或密码错误");
      const data = response.data;
      const user = cloudUser(data?.user || (register ? data : null));
      if (!user) return upstreamError(502);
      if (register) {
        const verificationRequired = !data.access_token;
        return authJson({ success: true, user_id: user.id, verification_required: verificationRequired,
          verification_email_sent: verificationRequired,
          message: verificationRequired ? "注册请求已受理，请查收验证邮件后登录" : "注册成功" });
      }
      if (typeof data.access_token !== "string" || !data.access_token || user.email !== email) return upstreamError(502);
      return authJson({ success: true, message: "登录成功", access_token: data.access_token,
        refresh_token: data.refresh_token || null, token_type: "Bearer", expires_in: data.expires_in || 3600, user });
    }

    const token = bearer(request);
    if (!token) return upstreamError(401, "缺少有效的登录令牌");
    const limit = await rateLimit(env, `session:${ip}`, 60, 60);
    if (!limit.allowed) return rateLimitResponse(limit);
    const logout = path.endsWith("/logout");
    const response = await forwardSupabase(env, logout ? "/auth/v1/logout?scope=local" : "/auth/v1/user", {
      method: logout ? "POST" : "GET", headers: { authorization: token }
    });
    if (response.status < 200 || response.status >= 300) return upstreamError(response.status, "登录会话已失效");
    if (logout) return authJson({ success: true, message: "已退出当前云端会话" });
    const user = cloudUser(response.data);
    if (!user) return upstreamError(502);
    return authJson({ valid: true, user });
  } catch (error) {
    if (error instanceof InvalidRequest) return authJson({ success: false, message: error.message }, error.status);
    // The boundary maps security-state failures to 503 (fail closed).
    if (error instanceof SecurityUnavailable) throw error;
    console.error("[auth] request failed", path);
    return upstreamError(502);
  }
}
