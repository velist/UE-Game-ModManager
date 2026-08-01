
// esbuild prelude shim: esbuild 输出的 __name 在 ESM module 顶层执行时找不到 __defProp，改用 identity
const __name = (fn) => fn;

// src/index.ts
// [安全] 允许跨域访问本 API 的浏览器来源白名单。
// 桌面客户端是原生 HTTP 调用，不受 CORS 约束，不需要出现在这里；
// Worker 自托管的 /reset-password 页面与本 Worker 同源，同样不需要。
// 原实现对**所有**响应（含返回 access_token/refresh_token 的登录响应）发 `*`，
// 等于允许任意网页跨域调用登录接口并读取令牌，已收紧为白名单。
const ALLOWED_ORIGINS = [
  "https://modmanger.com",
  "https://www.modmanger.com"
];
function corsHeaders(request) {
  const headers = new Headers();
  const origin = request?.headers?.get("origin");
  if (origin && ALLOWED_ORIGINS.includes(origin)) {
    headers.set("access-control-allow-origin", origin);
    headers.set("access-control-allow-headers", "content-type, authorization, apikey");
    headers.set("access-control-allow-methods", "GET, POST, OPTIONS");
    headers.set("vary", "Origin");
  }
  return headers;
}
function json(data, init = {}) {
  const headers = new Headers(init.headers);
  headers.set("content-type", "application/json; charset=utf-8");
  return new Response(JSON.stringify(data), { ...init, headers });
}
function bad(code, message, extra) {
  return json({ code, message, ...extra }, { status: code });
}
async function rateLimit(env, key, limit, windowSec) {
  const bucket = `rl:${key}:${Math.floor(Date.now() / (windowSec * 1e3))}`;
  const currentRaw = await env.RATE_LIMIT.get(bucket);
  const current = currentRaw ? parseInt(currentRaw, 10) : 0;
  if (current >= limit) {
    return { allowed: false, remaining: 0 };
  }
  await env.RATE_LIMIT.put(bucket, String(current + 1), { expirationTtl: windowSec });
  return { allowed: true, remaining: limit - (current + 1) };
}
// ─────────────────────────────────────────────────────────────────────────────
// 用量统计（注册数 / 在线数）
//
// 只有两个数字要回答：「有多少人注册过」「此刻有多少人开着」。所以这里只有两张 D1 表、
// 一个上报端点、一页看板，没有事件流、没有会话、没有属性——那是产品分析平台的形状。
//
// 为什么统计和「检查更新」合成同一个请求：
//   1) 应用此前根本没有更新检查能力（/app/update 的 GET 实现零客户端调用方），
//      合并之后这个网络请求对用户有了实际用途，不是纯粹的「我们在统计你」；
//   2) 隐私叙事从「上报你的行为」变成「检查新版本时带了版本号」，这是桌面软件的通行做法；
//   3) 少一个往返、少一处失败面。
//
// 为什么存 D1 不存 KV（两条各自独立成立的否决理由，见
// .claude/audit_reports/2026-07-27-telemetry-options.md）：
//   - 额度：KV 免费档 1000 写/天。1000 台设备各心跳一次就打满，当天之后全部丢失。
//   - 正确性：KV 是最终一致模型，「get → 判断 → put」是坏的读改写（本文件的 rateLimit()
//     就有这个 bug）。限流读错只是放宽了限制；计数读错是**永久性的数字失真**，事后无法修复。
// ─────────────────────────────────────────────────────────────────────────────

/**
 * 设备标识：客户端自己生成的随机 UUID v4。与硬件序列号/MachineGuid/MAC/机器名/邮箱
 * 全都无关联，因此换硬件会变、重装会变（累计数偏高是自觉接受的代价）。
 * 格式不合法一律静默丢弃，连 D1 都不碰——垃圾流量的数据库成本必须是零。
 */
const DEVICE_ID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

/** 账号标识：客户端算好的邮箱哈希（32 位十六进制）。明文邮箱永远不出用户本机。 */
const ACCOUNT_HASH_RE = /^[0-9a-f]{32}$/i;

/**
 * 上报体的硬上限（字节）。已删掉的 /logs 端点正是因为 `await request.text()` 完全不校验
 * 大小、又直接往 KV 里塞，才成为一个纯攻击面。定长 payload 实测约 140 字节，256 已很宽裕。
 */
const TELEMETRY_MAX_BODY = 256;

/** 认定「在线」的时间窗。客户端心跳 15 分钟一次，留 5 分钟余量给网络抖动与调度延迟。 */
const ONLINE_WINDOW_SEC = 20 * 60;

/** 看板趋势线与版本分布的回看天数。 */
const TREND_DAYS = 30;

/**
 * 版本号 / 系统版本这类短字符串的清洗：只留可打印 ASCII 再截断。
 * 客户端本来就只会发 `2.0.5` / `10.0.26200`，这里防的是有人往看板里塞控制字符或超长串。
 */
function clipAscii(value, max) {
  if (typeof value !== "string") return "";
  return value.replace(/[^\x20-\x7E]/g, "").slice(0, max);
}

/**
 * 记一次心跳。**任何失败都不得影响更新检查的结果**，调用方负责兜住异常。
 *
 * 几处刻意的设计：
 * - 只信服务端时间。客户端的系统时钟可以是任意值（改过时区、CMOS 没电、故意伪造），
 *   拿它当 last_seen 会让「在线」窗口彻底失效。
 * - 写入幂等（ON CONFLICT DO UPDATE）：攻击者反复上报同一个 UUID 只会更新一行，
 *   不增加任何存储。要污染数字必须每次换新 UUID，门槛显著更高。
 * - 同一设备 60 秒内的重复上报不落更新（DO UPDATE 上的 WHERE）：挡住单机狂刷，
 *   而正常心跳间隔是 15 分钟，永远够不着这条线。
 * - D1 未绑定时直接返回：忘了配绑定的后果应该是「统计没了」，不是「更新检查挂了」。
 */
async function recordTelemetry(env, raw) {
  if (!env.DB) return;
  if (typeof raw !== "string" || raw.length === 0 || raw.length > TELEMETRY_MAX_BODY) return;

  let body;
  try {
    body = JSON.parse(raw);
  } catch {
    return;
  }
  if (!body || typeof body !== "object") return;

  const now = Math.floor(Date.now() / 1e3);
  const statements = [];

  const deviceId = typeof body.d === "string" ? body.d.toLowerCase() : "";
  if (DEVICE_ID_RE.test(deviceId)) {
    statements.push(
      env.DB.prepare(
        `INSERT INTO devices (id, first_seen, last_seen, ver, os) VALUES (?1, ?2, ?2, ?3, ?4)
         ON CONFLICT(id) DO UPDATE SET last_seen = ?2, ver = ?3, os = ?4
         WHERE devices.last_seen < ?2 - 60`
      ).bind(deviceId, now, clipAscii(body.v, 16), clipAscii(body.o, 32))
    );
  }

  // 账号哈希与设备是两条独立语句：登录那一刻很可能刚发过心跳（60 秒静默窗口内），
  // 合成一条的话这次注册上报就会连带被丢掉。
  const accountHash = typeof body.a === "string" ? body.a.toLowerCase() : "";
  if (ACCOUNT_HASH_RE.test(accountHash)) {
    statements.push(
      env.DB.prepare(
        `INSERT INTO accounts (hash, first_seen, last_seen) VALUES (?1, ?2, ?2)
         ON CONFLICT(hash) DO UPDATE SET last_seen = ?2`
      ).bind(accountHash, now)
    );
  }

  if (statements.length > 0) await env.DB.batch(statements);
}

/**
 * 更新检查的回答。版本信息走 wrangler.toml 的 [vars]，**不读 KV**：
 * 原实现每次调用读 3 个 KV key，在心跳场景下是 3 倍读放大；而版本号本来就是每次发版
 * 都要改仓库的东西，随 deploy 一起走即可，少一套需要单独维护的运行时配置。
 */
function updateInfo(env) {
  return {
    latest: env.LATEST_VERSION || "2.0.5",
    mandatory: env.UPDATE_MANDATORY === "true",
    notes: env.UPDATE_NOTES || ""
  };
}

/**
 * 定时比较：两串等长且逐字符异或累加，比较耗时与「前几位对不对」无关。
 * 看板 token 是一个长期有效的静态密钥，用 `!==` 比较理论上可被计时侧信道逐位试出来；
 * 这段代码只有 8 行，没有理由留下这个口子。
 */
function timingSafeEqual(a, b) {
  if (typeof a !== "string" || typeof b !== "string" || a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

/** 看板鉴权：token 未配置时一律拒绝——忘了设 secret 的后果不能是「看板变成公开的」。 */
function dashboardAuthorized(request, env) {
  const token = (env.DASH_TOKEN || "").trim();
  if (!token) return false;
  return timingSafeEqual(request.headers.get("authorization") || "", `Bearer ${token}`);
}

async function forwardSupabase(env, path, init) {
  const supabaseUrl = (env.SUPABASE_URL || "").trim().replace(/[\r\n]/g, "");
  const anonKey = (env.SUPABASE_ANON_KEY || "").trim().replace(/[\r\n]/g, "");
  const url = new URL(path, supabaseUrl);
  const headers = new Headers(init.headers);
  if (!headers.has("apikey")) headers.set("apikey", anonKey);
  if (!headers.has("authorization")) headers.set("authorization", `Bearer ${anonKey}`);
  const resp = await fetch(url.toString(), { ...init, headers });
  const text = await resp.text();
  let data;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = text;
  }
  return { status: resp.status, headers: resp.headers, data };
}
async function generateSupabaseLinkOrOtp(env, email, mode, redirect_to) {
  const supabaseUrl = (env.SUPABASE_URL || "").trim().replace(/[\r\n]/g, "");
  const serviceKey = (env.SUPABASE_SERVICE_KEY || "").trim().replace(/[\r\n]/g, "");
  const headers = new Headers({ "content-type": "application/json" });
  headers.set("apikey", serviceKey);
  headers.set("authorization", `Bearer ${serviceKey}`);
  const payload = { email, type: mode === "magiclink" ? "magiclink" : mode === "recovery" ? "recovery" : "signup" };
  if (redirect_to) {
    if (mode === "recovery") {
      payload.redirect_to = redirect_to;
    } else {
      payload.options = { email_redirect_to: redirect_to };
    }
  }
  const resp = await fetch(new URL("/auth/v1/admin/generate_link", supabaseUrl).toString(), { method: "POST", headers, body: JSON.stringify(payload) });
  const text = await resp.text();
  let data;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = text;
  }
  if (!resp.ok) return { ok: false, status: resp.status, data };
  let link = data?.action_link;
  if (link && redirect_to && mode === "recovery") {
    try {
      const url = new URL(link);
      url.searchParams.set("redirect_to", redirect_to);
      link = url.toString();
    } catch {
    }
  }
  const otp = data?.email_otp || data?.hashed_token || data?.token;
  return { ok: true, status: resp.status, data, link, otp };
}
function uuidToInt(uuid) {
  if (!uuid) return 0;
  const cleanUuid = uuid.replace(/-/g, "");
  let hash = 0;
  for (let i = 0; i < cleanUuid.length; i++) {
    const char = cleanUuid.charCodeAt(i);
    hash = (hash << 5) - hash + char;
    hash = hash & 2147483647;
  }
  return hash;
}
async function sendBrevoMail(env, to, subject, html, text) {
  const apiKey = (env.BREVO_API_KEY || "").trim().replace(/[\r\n]/g, "");
  const headers = new Headers();
  headers.set("Content-Type", "application/json");
  headers.set("Accept", "application/json");
  headers.set("api-key", apiKey);
  const body = {
    sender: { name: env.BREVO_FROM_NAME, email: env.BREVO_FROM },
    to: [{ email: to }],
    subject,
    htmlContent: html,
    textContent: text || ""
  };
  const url = "https://api.brevo.com/v3/smtp/email";
  const resp = await fetch(url, { method: "POST", headers, body: JSON.stringify(body) });
  const t = await resp.text();
  let data;
  try {
    data = t ? JSON.parse(t) : null;
  } catch {
    data = t;
  }
  return { ok: resp.ok, status: resp.status, data };
}
function buildBilingualMail(titleCn, titleEn, cn, en) {
  const html = `<!doctype html><html><body style="font-family:Segoe UI,Arial;line-height:1.6">
<h2>${titleCn}</h2><p>${cn}</p><hr/><h3>${titleEn}</h3><p>${en}</p><p style="color:#666">\u2014 \u7231\u9171\u5DE5\u4F5C\u5BA4 / Ai-chan Studio</p></body></html>`;
  const text = `${titleCn}
${cn}

${titleEn}
${en}

\u2014 \u7231\u9171\u5DE5\u4F5C\u5BA4 / Ai-chan Studio`;
  return { html, text };
}
/**
 * 看板页面。**页面本身不含任何数据、也不含 token**：它只是一个空壳，
 * 打开后向用户要一次 token，存进 localStorage，再带 Authorization 头去拉 /admin/stats。
 *
 * 为什么 token 走 Authorization 头而不是 URL query：query 会进 Cloudflare 访问日志、
 * 会被浏览器历史记录下来、还会通过 Referer 泄给页面里任何一个外链。
 * 页面里也没有任何外部资源引用（无 CDN、无字体、无图表库），整页零外链。
 */
function dashboardHtml() {
  return `<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<meta name="robots" content="noindex,nofollow"/>
<title>UEModManager 用量</title>
<style>
:root{color-scheme:dark;--bg:#0d1117;--card:#161b22;--line:#30363d;--txt:#e6edf3;--dim:#8b949e;--acc:#58a6ff;--ok:#3fb950}
*{box-sizing:border-box}
body{margin:0;padding:32px 20px;background:var(--bg);color:var(--txt);
     font-family:"Segoe UI","PingFang SC","Microsoft YaHei",sans-serif}
.wrap{max-width:860px;margin:0 auto}
h1{font-size:20px;font-weight:600;margin:0 0 4px}
.sub{color:var(--dim);font-size:13px;margin:0 0 28px}
.row{display:flex;gap:16px;flex-wrap:wrap;margin-bottom:28px}
.card{flex:1 1 200px;background:var(--card);border:1px solid var(--line);border-radius:10px;padding:20px 22px}
.num{font-size:40px;font-weight:700;line-height:1.1;font-variant-numeric:tabular-nums}
.lbl{color:var(--dim);font-size:13px;margin-top:6px}
.online .num{color:var(--ok)}
.panel{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:20px 22px;margin-bottom:20px}
.panel h2{font-size:14px;font-weight:600;margin:0 0 4px;color:var(--dim)}
.legend{display:flex;gap:18px;font-size:12px;color:var(--dim);margin-bottom:10px}
.legend b{font-weight:600}
.legend s{display:inline-block;width:14px;height:2px;vertical-align:middle;margin-right:5px}
svg{width:100%;height:150px;display:block}
table{width:100%;border-collapse:collapse;font-size:13px}
td{padding:6px 0;border-bottom:1px solid var(--line)}
td:last-child{text-align:right;color:var(--dim);font-variant-numeric:tabular-nums}
tr:last-child td{border-bottom:0}
.bar{display:flex;align-items:center;gap:10px}
.bar i{display:block;height:6px;border-radius:3px;background:var(--acc);min-width:2px}
#gate{display:none;background:var(--card);border:1px solid var(--line);border-radius:10px;padding:22px}
#gate input{width:100%;padding:10px 12px;margin:12px 0;background:var(--bg);color:var(--txt);
            border:1px solid var(--line);border-radius:6px;font-size:14px}
#gate button{padding:9px 20px;background:var(--acc);color:#0d1117;border:0;border-radius:6px;
             font-size:14px;font-weight:600;cursor:pointer}
.err{color:#f85149;font-size:13px;margin-top:10px;min-height:18px}
.foot{color:var(--dim);font-size:12px;margin-top:24px;line-height:1.7}
</style>
</head>
<body>
<div class="wrap">
  <h1>UEModManager 用量</h1>
  <p class="sub" id="sub">加载中…</p>

  <div id="gate">
    <div>请输入看板口令（DASH_TOKEN）</div>
    <input id="tok" type="password" autocomplete="off" placeholder="token"/>
    <button id="go">打开</button>
    <div class="err" id="err"></div>
  </div>

  <div id="main" style="display:none">
    <div class="row">
      <div class="card"><div class="num" id="nAcc">-</div><div class="lbl">注册数（去重邮箱）</div></div>
      <div class="card online"><div class="num" id="nOn">-</div><div class="lbl">在线（最近 20 分钟）</div></div>
      <div class="card"><div class="num" id="nDev">-</div><div class="lbl">累计设备</div></div>
    </div>
    <div class="panel">
      <h2>每日新增（近 ${TREND_DAYS} 天）</h2>
      <div class="legend">
        <span><s style="background:#58a6ff"></s>新增设备</span>
        <span><s style="background:#3fb950"></s>新增注册</span>
        <span>峰值 <b id="peak">-</b></span>
      </div>
      <svg id="chart" viewBox="0 0 600 150" preserveAspectRatio="none"></svg>
    </div>
    <div class="panel">
      <h2>版本分布（近 ${TREND_DAYS} 天活跃设备）</h2>
      <table id="vers"></table>
    </div>
    <p class="foot">
      累计设备数偏高：设备标识是客户端随机生成的 UUID，重装系统或换机会产生新 ID。<br/>
      在线数只反映“此刻有多少人恰好开着窗口”，对用完即关的工具类应用，绝对值几乎没有意义。<br/>
      关闭了统计开关的用户不在以上任何一个数字里。
    </p>
  </div>
</div>
<script>
const K='uemm_dash_token';
const $=id=>document.getElementById(id);
function esc(s){return String(s).replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));}

function chart(trend){
  const W=600,H=150,P=8;
  if(!trend.length){$('chart').innerHTML='';$('peak').textContent='0';return;}
  const max=Math.max(1,...trend.map(p=>Math.max(p.devices,p.accounts)));
  $('peak').textContent=max;
  const x=i=>trend.length<2?W/2:P+i*(W-2*P)/(trend.length-1);
  const y=v=>H-P-v*(H-2*P)/max;
  const line=(key,color)=>{
    const d=trend.map((p,i)=>(i?'L':'M')+x(i).toFixed(1)+' '+y(p[key]).toFixed(1)).join(' ');
    return '<path d="'+d+'" fill="none" stroke="'+color+'" stroke-width="2" stroke-linejoin="round"/>';
  };
  // 图例与峰值放在 SVG 外的 HTML 里：画在 SVG 顶部会被折线穿过去，
  // 而 preserveAspectRatio="none" 下文字还会被横向拉伸变形。
  $('chart').innerHTML=line('devices','#58a6ff')+line('accounts','#3fb950');
}

function render(d){
  $('nAcc').textContent=d.accounts;
  $('nOn').textContent=d.online;
  $('nDev').textContent=d.devices;
  $('sub').textContent='服务端时间 '+d.now;
  chart(d.trend||[]);
  const top=Math.max(1,...(d.versions||[]).map(v=>v.n));
  $('vers').innerHTML=(d.versions||[]).map(v=>
    '<tr><td><div class="bar"><i style="width:'+(v.n/top*160).toFixed(0)+'px"></i>'
    +esc(v.ver||'未知')+'</div></td><td>'+v.n+'</td></tr>').join('')
    ||'<tr><td>暂无数据</td><td></td></tr>';
  $('gate').style.display='none';
  $('main').style.display='block';
}

async function load(token){
  const r=await fetch('/admin/stats',{headers:{authorization:'Bearer '+token}});
  if(r.status===401){throw new Error('口令不对');}
  if(!r.ok){throw new Error('服务端错误 '+r.status);}
  const j=await r.json();
  localStorage.setItem(K,token);
  render(j.data);
}

$('go').onclick=()=>{
  const t=$('tok').value.trim();
  if(!t){$('err').textContent='请输入口令';return;}
  load(t).catch(e=>{$('err').textContent=e.message;});
};
$('tok').onkeydown=e=>{if(e.key==='Enter')$('go').click();};

const saved=localStorage.getItem(K);
if(saved){
  load(saved).catch(()=>{localStorage.removeItem(K);$('sub').textContent='';$('gate').style.display='block';});
}else{
  $('sub').textContent='';
  $('gate').style.display='block';
}
</script>
</body>
</html>`;
}

var index_default = {
  async fetch(request, env) {
    // CORS 统一在边界处按白名单附加，业务分支不再各自设置跨域头。
    const resp = await handleRequest(request, env);
    const extra = corsHeaders(request);
    if (![...extra.keys()].length) return resp;
    const merged = new Headers(resp.headers);
    for (const [k, v] of extra) merged.set(k, v);
    return new Response(resp.body, { status: resp.status, statusText: resp.statusText, headers: merged });
  }
};
async function handleRequest(request, env) {
  {
    const url = new URL(request.url);
    if (url.pathname.startsWith("/v1/")) url.pathname = url.pathname.substring(3);
    else if (url.pathname === "/v1") url.pathname = "/";
    if (request.method === "OPTIONS") return json({ ok: true });
    // [安全] /test/env、/test/generate、/test/brevo 三个调试端点已于 2026-07-26 移除。
    // /test/generate 曾以 SERVICE_KEY 生成任意邮箱的 recovery 链接并直接回传，构成未认证的任意账户接管；
    // /test/brevo 泄露第三方账户信息并可被刷配额；/test/env 泄露密钥配置指纹。
    // 如需恢复联调能力，务必：仅在非生产环境注册 + 要求 Authorization 头 + 绝不回传 link/otp/stack。
    if (url.pathname === "/health") {
      return json({ ok: true, time: (/* @__PURE__ */ new Date()).toISOString() });
    }
    if (url.pathname === "/reset-password" && request.method === "GET") {
      const html = `<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>\u91CD\u7F6E\u5BC6\u7801 - UEModManager</title>
    <style>
        :root {
            color-scheme: light;
            font-family: "Helvetica Neue", "PingFang SC", "Microsoft YaHei", sans-serif;
            --bg-gradient: linear-gradient(135deg, #f7f8fa, #eef2f7);
            --card-bg: rgba(255,255,255,0.92);
            --card-border: rgba(200,204,210,0.65);
            --primary: #0b84ff;
            --success: #28a745;
            --error: #dc3545;
            --text-strong: #0f172a;
            --text-secondary: #4e586a;
        }
        * { box-sizing: border-box; }
        body {
            margin: 0;
            background: var(--bg-gradient);
            color: var(--text-strong);
            -webkit-font-smoothing: antialiased;
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 20px;
        }
        .container { width: 100%; max-width: 480px; }
        .card {
            background: var(--card-bg);
            border: 1px solid var(--card-border);
            border-radius: 24px;
            padding: 40px;
            backdrop-filter: blur(12px);
            box-shadow: 0 24px 45px rgba(15, 30, 60, 0.08);
        }
        h1 { font-size: 2rem; margin: 0 0 12px 0; font-weight: 700; text-align: center; }
        .subtitle { text-align: center; color: var(--text-secondary); margin: 0 0 32px 0; }
        .form-group { margin-bottom: 24px; }
        label { display: block; margin-bottom: 8px; font-weight: 600; color: var(--text-strong); }
        input[type="password"] {
            width: 100%; padding: 12px 16px; border: 2px solid var(--card-border);
            border-radius: 12px; font-size: 1rem; font-family: inherit; transition: border-color 0.2s;
        }
        input[type="password"]:focus { outline: none; border-color: var(--primary); }
        .btn {
            width: 100%; padding: 14px; background: var(--primary); color: white; border: none;
            border-radius: 12px; font-size: 1.05rem; font-weight: 600; cursor: pointer;
            transition: opacity 0.2s, transform 0.1s;
        }
        .btn:hover:not(:disabled) { opacity: 0.9; }
        .btn:active:not(:disabled) { transform: scale(0.98); }
        .btn:disabled { opacity: 0.5; cursor: not-allowed; }
        .message {
            padding: 16px; border-radius: 12px; margin-bottom: 24px; display: none; line-height: 1.5;
        }
        .message.show { display: block; }
        .message.success { background: rgba(40, 167, 69, 0.1); border: 1px solid var(--success); color: var(--success); }
        .message.error { background: rgba(220, 53, 69, 0.1); border: 1px solid var(--error); color: var(--error); }
        .hint { font-size: 0.9rem; color: var(--text-secondary); margin-top: 8px; }
        .loading { display: none; text-align: center; padding: 24px; }
        .loading.show { display: block; }
        .spinner {
            border: 3px solid rgba(11, 132, 255, 0.1); border-top-color: var(--primary);
            border-radius: 50%; width: 40px; height: 40px; animation: spin 0.8s linear infinite;
            margin: 0 auto 16px;
        }
        @keyframes spin { to { transform: rotate(360deg); } }
    </style>
</head>
<body>
    <div class="container">
        <div class="card">
            <h1>\u{1F511} \u91CD\u7F6E\u5BC6\u7801</h1>
            <p class="subtitle">\u8BF7\u8F93\u5165\u60A8\u7684\u65B0\u5BC6\u7801 (v2.5)</p>
            <div id="message" class="message"></div>
            <div id="loadingDiv" class="loading"><div class="spinner"></div><p>\u6B63\u5728\u9A8C\u8BC1\u94FE\u63A5...</p></div>
            <form id="resetForm" style="display:none;">
                <div class="form-group">
                    <label for="password">\u65B0\u5BC6\u7801</label>
                    <input type="password" id="password" required minlength="6" placeholder="\u81F3\u5C116\u4E2A\u5B57\u7B26" />
                    <p class="hint">\u5BC6\u7801\u957F\u5EA6\u81F3\u5C116\u4E2A\u5B57\u7B26</p>
                </div>
                <div class="form-group">
                    <label for="confirmPassword">\u786E\u8BA4\u5BC6\u7801</label>
                    <input type="password" id="confirmPassword" required placeholder="\u518D\u6B21\u8F93\u5165\u65B0\u5BC6\u7801" />
                </div>
                <button type="submit" class="btn" id="submitBtn">\u91CD\u7F6E\u5BC6\u7801</button>
            </form>
        </div>
    </div>
    <script>
        const SUPABASE_URL='${(env.SUPABASE_URL || "").trim().replace(/[\r\n]/g, "")}';
        const SUPABASE_ANON_KEY='${(env.SUPABASE_ANON_KEY || "").trim().replace(/[\r\n]/g, "")}';
        const urlParams=new URLSearchParams(window.location.search);
        const hashParams=new URLSearchParams(window.location.hash.substring(1));
        const form=document.getElementById('resetForm');
        const loadingDiv=document.getElementById('loadingDiv');
        const messageDiv=document.getElementById('message');
        const submitBtn=document.getElementById('submitBtn');
        function showMessage(text,type){messageDiv.textContent=text;messageDiv.className='message show '+type;}
        function hideMessage(){messageDiv.className='message';}
        function getParam(key){return hashParams.get(key)||urlParams.get(key);}
        async function verifyToken(){
            const type=getParam('type');
            const token=getParam('token_hash')||getParam('token');
            const accessToken=getParam('access_token');
            const error=getParam('error');
            const errorDesc=getParam('error_description');
            if(error){
                loadingDiv.className='loading';
                showMessage(\`\u274C \${errorDesc||error}\`,'error');
                return false;
            }
            if(type==='recovery'||(accessToken&&!error)){
                loadingDiv.className='loading';
                form.style.display='block';
                return true;
            }
            loadingDiv.className='loading';
            showMessage('\u274C \u65E0\u6548\u7684\u91CD\u7F6E\u94FE\u63A5\uFF0C\u8BF7\u91CD\u65B0\u7533\u8BF7\u5BC6\u7801\u91CD\u7F6E','error');
            return false;
        }
        form.addEventListener('submit',async(e)=>{
            e.preventDefault();
            hideMessage();
            const password=document.getElementById('password').value;
            const confirmPassword=document.getElementById('confirmPassword').value;
            if(password.length<6){showMessage('\u5BC6\u7801\u957F\u5EA6\u81F3\u5C116\u4E2A\u5B57\u7B26','error');return;}
            if(password!==confirmPassword){showMessage('\u4E24\u6B21\u8F93\u5165\u7684\u5BC6\u7801\u4E0D\u4E00\u81F4','error');return;}
            submitBtn.disabled=true;
            submitBtn.textContent='\u6B63\u5728\u91CD\u7F6E...';

            console.log('=== Password Reset Debug ===');
            console.log('URL:', window.location.href);
            console.log('Hash:', window.location.hash);
            console.log('Search:', window.location.search);

            try{
                const accessToken=getParam('access_token');
                const type=getParam('type');
                const token=getParam('token_hash')||getParam('token');
                let authToken=accessToken;

                console.log('Params:', {accessToken: accessToken?.substring(0,10)+'...', type, token: token?.substring(0,10)+'...'});

                // \u5982\u679C\u6709 access_token\uFF0C\u5148\u5C1D\u8BD5\u5237\u65B0\u5B83
                if(accessToken){
                    const refreshToken=getParam('refresh_token');
                    if(refreshToken){
                        try{
                            const refreshResp=await fetch(\`\${SUPABASE_URL}/auth/v1/token?grant_type=refresh_token\`,{
                                method:'POST',
                                headers:{'Content-Type':'application/json','apikey':SUPABASE_ANON_KEY},
                                body:JSON.stringify({refresh_token:refreshToken})
                            });
                            if(refreshResp.ok){
                                const newSession=await refreshResp.json();
                                authToken=newSession.access_token;
                                console.log('Token refreshed successfully');
                            }
                        }catch(e){
                            console.log('Failed to refresh token, using original');
                        }
                    }
                }

                // \u5982\u679C\u6CA1\u6709 authToken\uFF0C\u5C1D\u8BD5\u9A8C\u8BC1 recovery token
                if(!authToken && type && token){
                    const verifyResp=await fetch(\`\${SUPABASE_URL}/auth/v1/verify\`,{
                        method:'POST',
                        headers:{'Content-Type':'application/json','apikey':SUPABASE_ANON_KEY},
                        body:JSON.stringify({type:type,token:token})
                    });
                    if(!verifyResp.ok){
                        const error=await verifyResp.json();
                        throw new Error(error.msg||error.message||'\u94FE\u63A5\u5DF2\u8FC7\u671F\u6216\u65E0\u6548\uFF0C\u8BF7\u91CD\u65B0\u7533\u8BF7\u5BC6\u7801\u91CD\u7F6E');
                    }
                    const session=await verifyResp.json();
                    authToken=session.access_token;
                    console.log('Recovery token verified successfully');
                }

                if(!authToken){
                    throw new Error('\u65E0\u6CD5\u83B7\u53D6\u6709\u6548\u7684\u8BA4\u8BC1\u4EE4\u724C\uFF0C\u8BF7\u91CD\u65B0\u7533\u8BF7\u5BC6\u7801\u91CD\u7F6E');
                }

                // \u66F4\u65B0\u5BC6\u7801
                const updateResp=await fetch(\`\${SUPABASE_URL}/auth/v1/user\`,{
                    method:'PUT',
                    headers:{'Content-Type':'application/json','apikey':SUPABASE_ANON_KEY,'Authorization':\`Bearer \${authToken}\`},
                    body:JSON.stringify({password})
                });

                const updateResult=await updateResp.json();
                console.log('Password update response:', updateResp.status, updateResult);

                if(!updateResp.ok){
                    console.error('Password update failed:', updateResult);
                    if(updateResp.status===401){
                        throw new Error('\u8BA4\u8BC1\u5931\u8D25\uFF0C\u94FE\u63A5\u53EF\u80FD\u5DF2\u8FC7\u671F\u3002\u8BF7\u91CD\u65B0\u7533\u8BF7\u5BC6\u7801\u91CD\u7F6E');
                    }
                    if(updateResp.status===422||updateResp.status===400){
                        const errorMsg=updateResult.msg||updateResult.message||updateResult.error_description||'';
                        if(errorMsg.includes('same as the old')||errorMsg.includes('different from')){
                            throw new Error('\u65B0\u5BC6\u7801\u4E0D\u80FD\u4E0E\u65E7\u5BC6\u7801\u76F8\u540C\uFF0C\u8BF7\u4F7F\u7528\u4E0D\u540C\u7684\u5BC6\u7801');
                        }
                        throw new Error(errorMsg||'\u5BC6\u7801\u683C\u5F0F\u4E0D\u7B26\u5408\u8981\u6C42');
                    }
                    throw new Error(updateResult.msg||updateResult.message||updateResult.error_description||'\u5BC6\u7801\u66F4\u65B0\u5931\u8D25');
                }

                // \u68C0\u67E5\u54CD\u5E94\u4F53\u4E2D\u662F\u5426\u6709\u9519\u8BEF\uFF08\u67D0\u4E9B\u60C5\u51B5\u4E0B Supabase \u8FD4\u56DE 200 \u4F46\u5305\u542B\u9519\u8BEF\uFF09
                if(updateResult.error||updateResult.error_description){
                    const errorMsg=updateResult.error_description||updateResult.error;
                    console.error('Password update error in response:', errorMsg);
                    if(errorMsg.includes('same as the old')||errorMsg.includes('different from')){
                        throw new Error('\u65B0\u5BC6\u7801\u4E0D\u80FD\u4E0E\u65E7\u5BC6\u7801\u76F8\u540C\uFF0C\u8BF7\u4F7F\u7528\u4E0D\u540C\u7684\u5BC6\u7801');
                    }
                    throw new Error(errorMsg);
                }

                showMessage('\u2705 \u5BC6\u7801\u91CD\u7F6E\u6210\u529F\uFF01\u60A8\u53EF\u4EE5\u4F7F\u7528\u65B0\u5BC6\u7801\u767B\u5F55 UEModManager \u4E86\u3002','success');
                form.style.display='none';
            }catch(error){
                showMessage(\`\u274C \${error.message}\`,'error');
                submitBtn.disabled=false;
                submitBtn.textContent='\u91CD\u7F6E\u5BC6\u7801';
            }
        });
        console.log('=== Page Load v2.4 (Fixed API Key) ===');
        console.log('URL:', window.location.href);
        console.log('Hash:', window.location.hash);
        console.log('Search:', window.location.search);
        loadingDiv.className='loading show';
        verifyToken();
    <\/script>
</body>
</html>`;
      return new Response(html, {
        headers: {
          "content-type": "text/html; charset=utf-8",
          "cache-control": "no-cache, no-store, must-revalidate",
          "pragma": "no-cache",
          "expires": "0"
        }
      });
    }
    if (url.pathname === "/app/update" && request.method === "GET") {
      // 遗留兼容路径：全仓没有任何客户端调用它（POST 才是现在的调用方），保留只为不制造
      // 破坏性变更。原实现每次读 3 个 KV key，已改为读 [vars]，与 POST 用同一份版本信息。
      return json({ code: 200, data: updateInfo(env) });
    }
    if (url.pathname === "/app/update" && request.method === "POST") {
      // 一个请求同时办两件事：客户端问「有没有新版本」，服务端顺手记下
      // 「这台设备今天还活着、跑的什么版本」（以及可选的账号哈希）。
      //
      // body 先按定长上限截断再解析：格式垃圾流量的数据库成本必须是零。
      const raw = await request.text().catch(() => "");
      try {
        await recordTelemetry(env, raw);
      } catch (e) {
        // 统计写失败**绝不**影响更新检查的结果——用户是来问「有没有新版本」的，
        // 我们自己的统计出问题不该让他看到一个错误。
        console.error("[app/update] telemetry write failed:", String(e));
      }
      // 校验失败、解析失败、数据库异常，一律返回与成功**完全相同**的响应体。
      // 回传错误细节等于免费告诉攻击者校验规则长什么样（审计 P2-3 的教训）。
      return json({ code: 200, data: updateInfo(env) });
    }
    if (url.pathname === "/admin" && request.method === "GET") {
      // 空壳页面，不含数据也不含 token，因此不需要鉴权。
      return new Response(dashboardHtml(), {
        headers: {
          "content-type": "text/html; charset=utf-8",
          "cache-control": "no-store",
          // 页面零外链（无 CDN、无字体、无图表库），CSP 收到最紧
          "content-security-policy": "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'",
          "x-robots-tag": "noindex, nofollow"
        }
      });
    }
    if (url.pathname === "/admin/stats" && request.method === "GET") {
      if (!dashboardAuthorized(request, env)) return bad(401, "unauthorized");
      const now = Math.floor(Date.now() / 1e3);
      if (!env.DB) {
        // D1 没绑定时给一个空但结构完整的响应，让看板能明确显示「零」而不是白屏报错。
        return json({ code: 200, data: { accounts: 0, online: 0, devices: 0, trend: [], versions: [], now: new Date(now * 1e3).toISOString() } });
      }
      const since = now - TREND_DAYS * 86400;
      const rs = await env.DB.batch([
        env.DB.prepare(`SELECT COUNT(*) AS n FROM accounts`),
        env.DB.prepare(`SELECT COUNT(*) AS n FROM devices WHERE last_seen > ?1`).bind(now - ONLINE_WINDOW_SEC),
        env.DB.prepare(`SELECT COUNT(*) AS n FROM devices`),
        // 趋势线用「每日新增」而不是「每日活跃」：前者只需按 first_seen 分组，零额外存储；
        // 后者要为每个 (设备, 日期) 存一行，还得配一套过期清理。两个数字的看板不值这个复杂度。
        env.DB.prepare(
          `SELECT date(first_seen, 'unixepoch') AS d, COUNT(*) AS n FROM devices
           WHERE first_seen > ?1 GROUP BY d ORDER BY d`
        ).bind(since),
        env.DB.prepare(
          `SELECT date(first_seen, 'unixepoch') AS d, COUNT(*) AS n FROM accounts
           WHERE first_seen > ?1 GROUP BY d ORDER BY d`
        ).bind(since),
        // 版本分布是三个指标里唯一能直接改变代码决策的（什么时候能删旧迁移代码、
        // 要不要推强制更新），而它与上面几条共用同一张表，边际成本为零。
        env.DB.prepare(
          `SELECT ver, COUNT(*) AS n FROM devices WHERE last_seen > ?1
           GROUP BY ver ORDER BY n DESC LIMIT 8`
        ).bind(since)
      ]);
      const first = (r) => (r.results && r.results[0] && r.results[0].n) || 0;
      const byDay = new Map();
      const put = (rows, key) => {
        for (const row of rows.results || []) {
          const slot = byDay.get(row.d) || { d: row.d, devices: 0, accounts: 0 };
          slot[key] = row.n;
          byDay.set(row.d, slot);
        }
      };
      put(rs[3], "devices");
      put(rs[4], "accounts");
      return json({
        code: 200,
        data: {
          accounts: first(rs[0]),
          online: first(rs[1]),
          devices: first(rs[2]),
          trend: [...byDay.values()].sort((a, b) => a.d < b.d ? -1 : 1),
          versions: rs[5].results || [],
          now: new Date(now * 1e3).toISOString()
        }
      });
    }
    if (url.pathname === "/auth/password" && request.method === "POST") {
      const body = await request.json().catch(() => ({}));
      const { email, password } = body;
      if (!email || !password) return bad(400, "email/password required");
      const ip = request.headers.get("cf-connecting-ip") || "0.0.0.0";
      const rl = await rateLimit(env, `pw:${ip}`, 10, 60);
      if (!rl.allowed) return bad(429, "too many requests");
      try {
        const res = await forwardSupabase(env, "/auth/v1/token?grant_type=password", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ email, password }) });
        return json({ code: res.status, data: res.data }, { status: res.status });
      } catch (e) {
        console.error("[auth/password] upstream error:", String(e));
        return json({ code: 502, message: "认证服务暂时不可用，请稍后重试" }, { status: 502 });
      }
    }
    if (url.pathname === "/api/auth/login" && request.method === "POST") {
      const body = await request.json().catch(() => ({}));
      const { email, password } = body;
      if (!email || !password) return bad(400, "email/password required");
      const ip = request.headers.get("cf-connecting-ip") || "0.0.0.0";
      const rl = await rateLimit(env, `login:${ip}`, 10, 60);
      if (!rl.allowed) return bad(429, "too many requests");
      try {
        const res = await forwardSupabase(env, "/auth/v1/token?grant_type=password", {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ email, password })
        });
        if (res.status === 200 && res.data) {
          const data = res.data;
          return json({
            success: true,
            message: "\u767B\u5F55\u6210\u529F",
            access_token: data.access_token,
            refresh_token: data.refresh_token || null,
            token_type: "Bearer",
            expires_in: data.expires_in || 3600,
            user: {
              id: uuidToInt(data.user?.id),
              // 将 UUID 转换为 int
              email: data.user?.email || email,
              username: data.user?.user_metadata?.username || email.split("@")[0],
              display_name: data.user?.user_metadata?.username || email.split("@")[0],
              avatar: null,
              is_active: true,
              is_verified: data.user?.email_confirmed_at ? true : false,
              created_at: data.user?.created_at || (/* @__PURE__ */ new Date()).toISOString(),
              updated_at: data.user?.updated_at || (/* @__PURE__ */ new Date()).toISOString(),
              last_login_at: (/* @__PURE__ */ new Date()).toISOString(),
              subscription_type: "free",
              subscription_expires_at: null
            }
          });
        } else {
          return json({
            success: false,
            message: "\u90AE\u7BB1\u6216\u5BC6\u7801\u9519\u8BEF"
          }, { status: 401 });
        }
      } catch (e) {
        // [\u5B89\u5168] \u4E0D\u628A\u4E0A\u6E38\u5F02\u5E38\u7EC6\u8282\u56DE\u4F20\u5BA2\u6237\u7AEF\uFF0C\u53EA\u8FDB\u670D\u52A1\u7AEF\u65E5\u5FD7\u3002
        console.error("[api/auth/login] upstream error:", String(e));
        return json({
          success: false,
          message: "\u767B\u5F55\u5931\u8D25\uFF0C\u8BF7\u7A0D\u540E\u91CD\u8BD5"
        }, { status: 500 });
      }
    }
    if (url.pathname === "/auth/otp/send" && request.method === "POST") {
      const body = await request.json().catch(() => ({}));
      const { email, type = "email", redirect_to, channel = "auto" } = body;
      if (!email) return bad(400, "email required");
      const ip = request.headers.get("cf-connecting-ip") || "0.0.0.0";
      const rl1 = await rateLimit(env, `otp_ip:${ip}`, 10, 60);
      const rl2 = await rateLimit(env, `otp_em:${email.toLowerCase()}`, 6, 300);
      if (!rl1.allowed || !rl2.allowed) return bad(429, "too many requests");
      const preferBrevo = channel === "brevo";
      if (!preferBrevo) {
        try {
          const supabaseUrl = (env.SUPABASE_URL || "").trim().replace(/[\r\n]/g, "");
          const serviceKey = (env.SUPABASE_SERVICE_KEY || "").trim().replace(/[\r\n]/g, "");
          const headers = new Headers({ "content-type": "application/json" });
          headers.set("apikey", serviceKey);
          headers.set("authorization", `Bearer ${serviceKey}`);
          const payload = { email, type, create_user: true };
          if (type === "magiclink" && redirect_to) payload.options = { email_redirect_to: redirect_to };
          const resp = await fetch(new URL("/auth/v1/otp", supabaseUrl).toString(), { method: "POST", headers, body: JSON.stringify(payload) });
          const text = await resp.text();
          let data;
          try {
            data = text ? JSON.parse(text) : null;
          } catch {
            data = text;
          }
          if (resp.ok) return json({ code: 200, data, channelUsed: "supabase" });
          if (!(resp.status === 429 || resp.status >= 500)) return json({ code: resp.status, data, channelUsed: "supabase" }, { status: resp.status });
        } catch (e) {
        }
      }
      try {
        const gen = await generateSupabaseLinkOrOtp(env, email, type === "magiclink" ? "magiclink" : "email", redirect_to);
        if (!gen.ok) {
          console.error("[auth/otp/send] generate_link failed:", gen.status, JSON.stringify(gen.data));
          return json({ code: 502, message: "generate_link_failed" }, { status: 502 });
        }
        let subject = "UEModManager \u767B\u5F55";
        let cn = "", en = "";
        if (type === "magiclink" && gen.link) {
          cn = `\u70B9\u51FB\u4E0B\u65B9\u94FE\u63A5\u767B\u5F55\uFF1A<br/><a href="${gen.link}">${gen.link}</a><br/>\u8BE5\u94FE\u63A5\u77ED\u671F\u6709\u6548\uFF0C\u8BF7\u5C3D\u5FEB\u4F7F\u7528\u3002`;
          en = `Click the link below to sign in:<br/><a href="${gen.link}">${gen.link}</a><br/>The link will expire shortly.`;
          subject = "UEModManager \u767B\u5F55\u94FE\u63A5 / Magic Link";
        } else if (gen.otp) {
          cn = `\u60A8\u7684\u9A8C\u8BC1\u7801\u4E3A\uFF1A<b>${gen.otp}</b>\uFF0815 \u5206\u949F\u5185\u6709\u6548\uFF09\u3002\u5982\u975E\u672C\u4EBA\u64CD\u4F5C\uFF0C\u8BF7\u5FFD\u7565\u672C\u90AE\u4EF6\u3002`;
          en = `Your verification code: <b>${gen.otp}</b> (valid for ~15 minutes). If you didn't request this, please ignore.`;
          subject = "UEModManager \u9A8C\u8BC1\u7801 / Verification Code";
        } else {
          cn = "\u751F\u6210\u9A8C\u8BC1\u7801/\u94FE\u63A5\u5931\u8D25\uFF0C\u8BF7\u7A0D\u540E\u91CD\u8BD5\u3002";
          en = "Failed to generate OTP/Link, please try again later.";
        }
        const mail = buildBilingualMail("\u767B\u5F55\u9A8C\u8BC1", "Sign-in Verification", cn, en);
        const sent = await sendBrevoMail(env, email, subject, mail.html, mail.text);
        if (!sent.ok) {
          // [\u5B89\u5168] \u4E0E /auth/reset \u540C\u7406\uFF1A\u53D1\u4FE1\u5931\u8D25\u65F6**\u7EDD\u4E0D**\u628A magic link / OTP \u56DE\u4F20\u7ED9\u8BF7\u6C42\u65B9\u3002
          // \u539F\u5B9E\u73B0\u4F1A\u628A type=magiclink \u751F\u6210\u7684\u767B\u5F55\u94FE\u63A5\u76F4\u63A5\u8FD4\u56DE\uFF0C\u7B49\u540C\u4E8E\u4EFB\u610F\u8D26\u6237\u63A5\u7BA1\u3002
          console.error("[auth/otp/send] brevo send failed, link withheld");
          return json({ code: 502, message: "\u9A8C\u8BC1\u7801\u53D1\u9001\u5931\u8D25\uFF0C\u8BF7\u7A0D\u540E\u91CD\u8BD5" }, { status: 502 });
        }
        return json({ code: 200, data: { ok: true }, channelUsed: "brevo" });
      } catch (e) {
        console.error("[auth/otp/send] error:", String(e));
        return json({ code: 502, message: "\u9A8C\u8BC1\u7801\u53D1\u9001\u5931\u8D25\uFF0C\u8BF7\u7A0D\u540E\u91CD\u8BD5" }, { status: 502 });
      }
    }
    if (url.pathname === "/auth/reset" && request.method === "POST") {
      const body = await request.json().catch(() => ({}));
      const { email, redirect_to } = body;
      if (!email) return bad(400, "email required");
      const ip = request.headers.get("cf-connecting-ip") || "0.0.0.0";
      const rl = await rateLimit(env, `reset:${ip}`, 5, 60);
      if (!rl.allowed) return bad(429, "too many requests");
      const supabaseUrl = (env.SUPABASE_URL || "").trim().replace(/[\r\n]/g, "");
      const serviceKey = (env.SUPABASE_SERVICE_KEY || "").trim().replace(/[\r\n]/g, "");
      const headers = new Headers({ "content-type": "application/json" });
      headers.set("apikey", serviceKey);
      headers.set("authorization", `Bearer ${serviceKey}`);
      try {
        const payload = { email };
        if (redirect_to) payload.options = { email_redirect_to: redirect_to };
        const resp = await fetch(new URL("/auth/v1/recover", supabaseUrl).toString(), { method: "POST", headers, body: JSON.stringify(payload) });
        const text = await resp.text();
        let data;
        try {
          data = text ? JSON.parse(text) : null;
        } catch {
          data = text;
        }
        if (resp.ok) return json({ code: 200, data, channelUsed: "supabase" });
      } catch (e) {
      }
      let layer2Error;
      try {
        const gen = await generateSupabaseLinkOrOtp(env, email, "recovery", redirect_to);
        if (gen.ok && gen.link) {
          try {
            const cn = `\u70B9\u51FB\u4E0B\u65B9\u94FE\u63A5\u91CD\u7F6E\u5BC6\u7801\uFF1A<br/><a href="${gen.link}">${gen.link}</a>`;
            const en = `Click to reset your password:<br/><a href="${gen.link}">${gen.link}</a>`;
            const mail = buildBilingualMail("\u91CD\u7F6E\u5BC6\u7801", "Reset Password", cn, en);
            const sent = await sendBrevoMail(env, email, "UEModManager \u91CD\u7F6E\u5BC6\u7801 / Reset Password", mail.html, mail.text);
            if (sent.ok) {
              return json({ code: 200, data: { ok: true }, channelUsed: "brevo" });
            }
          } catch (brevoErr) {
          }
          // [安全] 邮件发送失败时**绝不**把 recovery link 回传给请求方：
          // 重置链接的唯一安全前提是"只有邮箱所有者能看到它"，回传即等于任意账户接管。
          console.error("[auth/reset] brevo send failed, link withheld");
          return json({ code: 502, message: "邮件发送失败，请稍后重试" }, { status: 502 });
        } else {
          layer2Error = `gen.ok=${gen.ok}, status=${gen.status}`;
        }
      } catch (e) {
        layer2Error = String(e);
      }
      // [安全] 原第三层"final_fallback"会无条件把 recovery link 放进响应体，已移除。
      // 它本身也无意义：只是用完全相同的参数重试一次 generate_link。
      // 内部错误细节只进服务端日志，不回传给调用方（避免泄露 Supabase 响应结构）。
      console.error("[auth/reset] unable to generate recovery link:", layer2Error);
      return json({
        code: 500,
        message: "无法发送重置邮件，请稍后重试",
        channelUsed: "failed"
      }, { status: 500 });
    }
    // [安全] /logs 端点已于 2026-07-30 移除。
    //
    // 核实结论：全仓（*.cs / *.ps1 / *.xaml / *.json，排除 bin/obj/.git）对 "/logs" 的引用
    // **只有本文件自己**，零客户端调用方——它从上线起就没有承接过任何一条日志。
    //
    // 而它同时具备三个问题（审计 P2-4）：未认证；`await request.text()` 完全不校验 body 大小，
    // 而 KV 单值上限是 25MB；写进的还是 RATE_LIMIT 这个与限流计数器共用的 namespace，
    // 运维上没法给日志和限流分别设过期策略。也就是说，任何人都可以用它把限流用的
    // KV namespace 塞满 25MB × N 条。零业务价值 + 纯攻击面 = 删。
    //
    // 需要客户端日志时，正确做法是本机诊断包（DiagnosticExportService 已经在做这件事），
    // 由用户主动导出，而不是让服务端开一个匿名可写的存储口。
    if (url.pathname === "/email/send" && request.method === "POST") {
      const ip = request.headers.get("cf-connecting-ip") || "0.0.0.0";
      const rl1 = await rateLimit(env, `mail_ip:${ip}`, 12, 60);
      if (!rl1.allowed) return bad(429, "too many requests");
      const body = await request.json().catch(() => ({}));
      const { to, subject, html, text } = body;
      if (!to || !subject || !html) return bad(400, "to, subject, html required");
      if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(to)) return bad(400, "invalid email");
      if (!/UEModManager|爱酱/i.test(subject)) return bad(403, "subject brand prefix required");
      if (html.length > 50000) return bad(413, "html too large");
      const rl2 = await rateLimit(env, `mail_em:${to.toLowerCase()}`, 6, 300);
      if (!rl2.allowed) return bad(429, "rate limited for this address");
      const sent = await sendBrevoMail(env, to, subject, html, text || "");
      if (!sent.ok) {
        // [安全] 不回传 Brevo 原始错误体（含账户/配额等内部信息）。
        console.error("[email/send] brevo failed:", sent.status, JSON.stringify(sent.data));
        return json({ code: 502, message: "send_failed", brevoStatus: sent.status }, { status: 502 });
      }
      return json({ code: 200, data: { ok: true }, channelUsed: "brevo" });
    }
    return bad(404, "not found");
  }
}
export {
  index_default as default
};
//# sourceMappingURL=index.js.map

