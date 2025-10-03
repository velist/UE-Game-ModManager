export interface Env {
  RATE_LIMIT: KVNamespace;
  SUPABASE_URL: string;
  SUPABASE_ANON_KEY: string;
  SUPABASE_SERVICE_KEY: string;
  BREVO_API_KEY: string;
  BREVO_FROM: string;
  BREVO_FROM_NAME: string;
}

function json(data: unknown, init: ResponseInit = {}) {
  const headers = new Headers(init.headers);
  headers.set('content-type', 'application/json; charset=utf-8');
  headers.set('access-control-allow-origin', '*');
  headers.set('access-control-allow-headers', 'content-type, authorization, apikey');
  headers.set('access-control-allow-methods', 'GET, POST, OPTIONS');
  return new Response(JSON.stringify(data), { ...init, headers });
}

function bad(code: number, message: string, extra?: Record<string, unknown>) {
  return json({ code, message, ...extra }, { status: code });
}

async function rateLimit(env: Env, key: string, limit: number, windowSec: number) {
  const bucket = `rl:${key}:${Math.floor(Date.now() / (windowSec * 1000))}`;
  const currentRaw = await env.RATE_LIMIT.get(bucket);
  const current = currentRaw ? parseInt(currentRaw, 10) : 0;
  if (current >= limit) {
    return { allowed: false, remaining: 0 } as const;
  }
  await env.RATE_LIMIT.put(bucket, String(current + 1), { expirationTtl: windowSec });
  return { allowed: true, remaining: limit - (current + 1) } as const;
}

async function forwardSupabase(env: Env, path: string, init: RequestInit) {
  const supabaseUrl = (env.SUPABASE_URL || '').trim().replace(/[\r\n]/g, '');
  const anonKey = (env.SUPABASE_ANON_KEY || '').trim().replace(/[\r\n]/g, '');
  const url = new URL(path, supabaseUrl);
  const headers = new Headers(init.headers);
  if (!headers.has('apikey')) headers.set('apikey', anonKey);
  if (!headers.has('authorization')) headers.set('authorization', `Bearer ${anonKey}`);
  const resp = await fetch(url.toString(), { ...init, headers });
  const text = await resp.text();
  let data: unknown;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  return { status: resp.status, headers: resp.headers, data } as const;
}

async function generateSupabaseLinkOrOtp(env: Env, email: string, mode: 'magiclink'|'email'|'recovery', redirect_to?: string) {
  const supabaseUrl = (env.SUPABASE_URL || '').trim().replace(/[\r\n]/g, '');
  const serviceKey = (env.SUPABASE_SERVICE_KEY || '').trim().replace(/[\r\n]/g, '');
  const headers = new Headers({ 'content-type': 'application/json' });
  headers.set('apikey', serviceKey);
  headers.set('authorization', `Bearer ${serviceKey}`);
  const payload: Record<string, unknown> = { email, type: mode === 'magiclink' ? 'magiclink' : (mode === 'recovery' ? 'recovery' : 'signup') };
  // ✅ 修复：recovery 类型使用 redirect_to 而不是 options.email_redirect_to
  if (redirect_to) {
    if (mode === 'recovery') {
      payload.redirect_to = redirect_to;
    } else {
      payload.options = { email_redirect_to: redirect_to };
    }
  }
  const resp = await fetch(new URL('/auth/v1/admin/generate_link', supabaseUrl).toString(), { method: 'POST', headers, body: JSON.stringify(payload) });
  const text = await resp.text();
  let data: any; try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!resp.ok) return { ok: false, status: resp.status, data };
  // ✅ 修复：Supabase 直接返回 action_link/email_otp，不在 properties 中
  let link: string | undefined = data?.action_link;
  // ✅ 如果 Supabase 没有正确处理 redirect_to，手动替换链接中的参数
  if (link && redirect_to && mode === 'recovery') {
    try {
      const url = new URL(link);
      url.searchParams.set('redirect_to', redirect_to);
      link = url.toString();
    } catch {}
  }
  const otp: string | undefined = data?.email_otp || data?.hashed_token || data?.token;
  return { ok: true, status: resp.status, data, link, otp };
}

// 将 UUID 字符串转换为整数（用于客户端期望 int 类型的 id）
// 使用哈希算法确保返回值在 int32 范围内 (0 到 2147483647)
function uuidToInt(uuid: string): number {
  if (!uuid) return 0;
  // 移除连字符
  const cleanUuid = uuid.replace(/-/g, '');
  // 使用简单哈希算法
  let hash = 0;
  for (let i = 0; i < cleanUuid.length; i++) {
    const char = cleanUuid.charCodeAt(i);
    hash = ((hash << 5) - hash) + char;
    // 确保在正数 int32 范围内 (0x7FFFFFFF = 2147483647)
    hash = hash & 0x7FFFFFFF;
  }
  return hash;
}

async function sendBrevoMail(env: Env, to: string, subject: string, html: string, text?: string) {
  // 清理API Key，去除可能的不可见字符（换行、回车、空格等）
  const apiKey = (env.BREVO_API_KEY || '').trim().replace(/[\r\n]/g, '');

  const headers = new Headers();
  headers.set('Content-Type', 'application/json');
  headers.set('Accept', 'application/json');
  headers.set('api-key', apiKey);

  const body = {
    sender: { name: env.BREVO_FROM_NAME, email: env.BREVO_FROM },
    to: [{ email: to }],
    subject,
    htmlContent: html,
    textContent: text || ''
  };

  // 硬编码URL，避免拼接问题
  const url = 'https://api.brevo.com/v3/smtp/email';
  const resp = await fetch(url, { method: 'POST', headers, body: JSON.stringify(body) });
  const t = await resp.text();
  let data: unknown; try { data = t ? JSON.parse(t) : null; } catch { data = t; }
  return { ok: resp.ok, status: resp.status, data };
}

function buildBilingualMail(titleCn: string, titleEn: string, cn: string, en: string) {
  const html = `<!doctype html><html><body style="font-family:Segoe UI,Arial;line-height:1.6">\n<h2>${titleCn}</h2><p>${cn}</p><hr/><h3>${titleEn}</h3><p>${en}</p><p style="color:#666">— 爱酱工作室 / Ai-chan Studio</p></body></html>`;
  const text = `${titleCn}\n${cn}\n\n${titleEn}\n${en}\n\n— 爱酱工作室 / Ai-chan Studio`;
  return { html, text };
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname.startsWith('/v1/')) url.pathname = url.pathname.substring(3);
    else if (url.pathname === '/v1') url.pathname = '/';

    if (request.method === 'OPTIONS') return json({ ok: true });
    if (url.pathname === '/test/env' && request.method === 'GET') {
      const safe = (v?: string) => v ? v.length : 0;
      return json({
        supabaseUrlLen: safe(env.SUPABASE_URL as any),
        anonKeyLen: safe(env.SUPABASE_ANON_KEY as any),
        serviceKeyLen: safe(env.SUPABASE_SERVICE_KEY as any),
        brevoKeyLen: safe(env.BREVO_API_KEY as any),
        fromSet: !!env.BREVO_FROM,
        fromNameSet: !!env.BREVO_FROM_NAME
      });
    }

    if (url.pathname === '/test/generate' && request.method === 'GET') {
      const email = url.searchParams.get('email') || 'test@example.com';
      const redirect = url.searchParams.get('redirect') || undefined;
      try {
        const r = await generateSupabaseLinkOrOtp(env, email, 'recovery', redirect);
        return json({ ok: r.ok, status: r.status, link: r.link, data: r.data, redirect_used: redirect });
      } catch (e) {
        return json({ ok: false, error: String(e), stack: (e as Error).stack }, { status: 500 });
      }
    }


        if (url.pathname === '/test/brevo' && request.method === 'GET') {
      try {
        const headers = new Headers({ 'api-key': env.BREVO_API_KEY });
        const resp = await fetch('https://api.brevo.com/v3/account', { headers });
        const text = await resp.text();
        return json({ code: resp.status, data: text });
      } catch (e) {
        return json({ code: 502, message: String(e) }, { status: 502 });
      }
    }
if (url.pathname === '/health') {
      return json({ ok: true, time: new Date().toISOString() });
    }

    // 密码重置页面
    if (url.pathname === '/reset-password' && request.method === 'GET') {
      const html = `<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>重置密码 - UEModManager</title>
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
            <h1>🔑 重置密码</h1>
            <p class="subtitle">请输入您的新密码 (v2.5)</p>
            <div id="message" class="message"></div>
            <div id="loadingDiv" class="loading"><div class="spinner"></div><p>正在验证链接...</p></div>
            <form id="resetForm" style="display:none;">
                <div class="form-group">
                    <label for="password">新密码</label>
                    <input type="password" id="password" required minlength="6" placeholder="至少6个字符" />
                    <p class="hint">密码长度至少6个字符</p>
                </div>
                <div class="form-group">
                    <label for="confirmPassword">确认密码</label>
                    <input type="password" id="confirmPassword" required placeholder="再次输入新密码" />
                </div>
                <button type="submit" class="btn" id="submitBtn">重置密码</button>
            </form>
        </div>
    </div>
    <script>
        const SUPABASE_URL='https://oiatqeymovnyubrnlmlu.supabase.co';
        const SUPABASE_ANON_KEY='eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im9pYXRxZXltb3ZueXVicm5sbWx1Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTQzMjM0MzYsImV4cCI6MjA2OTg5OTQzNn0.U-3p0SEVNOQUV4lYFWRiOfVmxgNSbMRWx0mE0DXZYuM';
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
                showMessage(\`❌ \${errorDesc||error}\`,'error');
                return false;
            }
            if(type==='recovery'||(accessToken&&!error)){
                loadingDiv.className='loading';
                form.style.display='block';
                return true;
            }
            loadingDiv.className='loading';
            showMessage('❌ 无效的重置链接，请重新申请密码重置','error');
            return false;
        }
        form.addEventListener('submit',async(e)=>{
            e.preventDefault();
            hideMessage();
            const password=document.getElementById('password').value;
            const confirmPassword=document.getElementById('confirmPassword').value;
            if(password.length<6){showMessage('密码长度至少6个字符','error');return;}
            if(password!==confirmPassword){showMessage('两次输入的密码不一致','error');return;}
            submitBtn.disabled=true;
            submitBtn.textContent='正在重置...';

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

                // 如果有 access_token，先尝试刷新它
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

                // 如果没有 authToken，尝试验证 recovery token
                if(!authToken && type && token){
                    const verifyResp=await fetch(\`\${SUPABASE_URL}/auth/v1/verify\`,{
                        method:'POST',
                        headers:{'Content-Type':'application/json','apikey':SUPABASE_ANON_KEY},
                        body:JSON.stringify({type:type,token:token})
                    });
                    if(!verifyResp.ok){
                        const error=await verifyResp.json();
                        throw new Error(error.msg||error.message||'链接已过期或无效，请重新申请密码重置');
                    }
                    const session=await verifyResp.json();
                    authToken=session.access_token;
                    console.log('Recovery token verified successfully');
                }

                if(!authToken){
                    throw new Error('无法获取有效的认证令牌，请重新申请密码重置');
                }

                // 更新密码
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
                        throw new Error('认证失败，链接可能已过期。请重新申请密码重置');
                    }
                    if(updateResp.status===422||updateResp.status===400){
                        const errorMsg=updateResult.msg||updateResult.message||updateResult.error_description||'';
                        if(errorMsg.includes('same as the old')||errorMsg.includes('different from')){
                            throw new Error('新密码不能与旧密码相同，请使用不同的密码');
                        }
                        throw new Error(errorMsg||'密码格式不符合要求');
                    }
                    throw new Error(updateResult.msg||updateResult.message||updateResult.error_description||'密码更新失败');
                }

                // 检查响应体中是否有错误（某些情况下 Supabase 返回 200 但包含错误）
                if(updateResult.error||updateResult.error_description){
                    const errorMsg=updateResult.error_description||updateResult.error;
                    console.error('Password update error in response:', errorMsg);
                    if(errorMsg.includes('same as the old')||errorMsg.includes('different from')){
                        throw new Error('新密码不能与旧密码相同，请使用不同的密码');
                    }
                    throw new Error(errorMsg);
                }

                showMessage('✅ 密码重置成功！您可以使用新密码登录 UEModManager 了。','success');
                form.style.display='none';
            }catch(error){
                showMessage(\`❌ \${error.message}\`,'error');
                submitBtn.disabled=false;
                submitBtn.textContent='重置密码';
            }
        });
        console.log('=== Page Load v2.4 (Fixed API Key) ===');
        console.log('URL:', window.location.href);
        console.log('Hash:', window.location.hash);
        console.log('Search:', window.location.search);
        loadingDiv.className='loading show';
        verifyToken();
    </script>
</body>
</html>`;
      return new Response(html, {
        headers: {
          'content-type': 'text/html; charset=utf-8',
          'cache-control': 'no-cache, no-store, must-revalidate',
          'pragma': 'no-cache',
          'expires': '0'
        }
      });
    }

    if (url.pathname === '/app/update' && request.method === 'GET') {
      const latest = (await env.RATE_LIMIT.get('config:latest')) || '1.7.37';
      const mandatory = (await env.RATE_LIMIT.get('config:mandatory')) === 'true';
      const notes = (await env.RATE_LIMIT.get('config:notes')) || '';
      return json({ code: 200, data: { latest, mandatory, notes } });
    }

    // 密码登录 (旧端点，保留兼容性)
    if (url.pathname === '/auth/password' && request.method === 'POST') {
      const body = await request.json().catch(() => ({}));
      const { email, password } = body as Record<string, string>;
      if (!email || !password) return bad(400, 'email/password required');
      const ip = request.headers.get('cf-connecting-ip') || '0.0.0.0';
      const rl = await rateLimit(env, `pw:${ip}`, 10, 60);
      if (!rl.allowed) return bad(429, 'too many requests');
      try {
        const res = await forwardSupabase(env, '/auth/v1/token?grant_type=password', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email, password }) });
        return json({ code: res.status, data: res.data }, { status: res.status });
      } catch (e) {
        return json({ code: 502, message: String(e) }, { status: 502 });
      }
    }

    // CloudAuthService 使用的登录端点
    if (url.pathname === '/api/auth/login' && request.method === 'POST') {
      const body = await request.json().catch(() => ({}));
      const { email, password } = body as Record<string, string>;
      if (!email || !password) return bad(400, 'email/password required');
      const ip = request.headers.get('cf-connecting-ip') || '0.0.0.0';
      const rl = await rateLimit(env, `login:${ip}`, 10, 60);
      if (!rl.allowed) return bad(429, 'too many requests');

      try {
        // 使用 Supabase 进行认证
        const res = await forwardSupabase(env, '/auth/v1/token?grant_type=password', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ email, password })
        });

        if (res.status === 200 && res.data) {
          const data = res.data as any;
          // 转换为 CloudAuthService 期望的格式 (snake_case)
          return json({
            success: true,
            message: '登录成功',
            access_token: data.access_token,
            refresh_token: data.refresh_token || null,
            token_type: 'Bearer',
            expires_in: data.expires_in || 3600,
            user: {
              id: uuidToInt(data.user?.id),  // 将 UUID 转换为 int
              email: data.user?.email || email,
              username: data.user?.user_metadata?.username || email.split('@')[0],
              display_name: data.user?.user_metadata?.username || email.split('@')[0],
              avatar: null,
              is_active: true,
              is_verified: data.user?.email_confirmed_at ? true : false,
              created_at: data.user?.created_at || new Date().toISOString(),
              updated_at: data.user?.updated_at || new Date().toISOString(),
              last_login_at: new Date().toISOString(),
              subscription_type: 'free',
              subscription_expires_at: null
            }
          });
        } else {
          return json({
            success: false,
            message: '邮箱或密码错误'
          }, { status: 401 });
        }
      } catch (e) {
        return json({
          success: false,
          message: '登录失败: ' + String(e)
        }, { status: 500 });
      }
    }

    // 发送验证码/魔法链接
    if (url.pathname === '/auth/otp/send' && request.method === 'POST') {
      const body = await request.json().catch(() => ({}));
      const { email, type = 'email', redirect_to, channel = 'auto' } = body as Record<string, string>;
      if (!email) return bad(400, 'email required');
      const ip = request.headers.get('cf-connecting-ip') || '0.0.0.0';
      const rl1 = await rateLimit(env, `otp_ip:${ip}`, 10, 60);
      const rl2 = await rateLimit(env, `otp_em:${email.toLowerCase()}`, 6, 300);
      if (!rl1.allowed || !rl2.allowed) return bad(429, 'too many requests');

      const preferBrevo = channel === 'brevo';
      if (!preferBrevo) {
        try {
          const supabaseUrl = (env.SUPABASE_URL || '').trim().replace(/[\r\n]/g, '');
          const serviceKey = (env.SUPABASE_SERVICE_KEY || '').trim().replace(/[\r\n]/g, '');
          const headers = new Headers({ 'content-type': 'application/json' });
          headers.set('apikey', serviceKey);
          headers.set('authorization', `Bearer ${serviceKey}`);
          const payload: Record<string, unknown> = { email, type, create_user: true };
          if (type === 'magiclink' && redirect_to) payload.options = { email_redirect_to: redirect_to };
          const resp = await fetch(new URL('/auth/v1/otp', supabaseUrl).toString(), { method: 'POST', headers, body: JSON.stringify(payload) });
          const text = await resp.text(); let data: unknown; try { data = text ? JSON.parse(text) : null; } catch { data = text; }
          if (resp.ok) return json({ code: 200, data, channelUsed: 'supabase' });
          if (!(resp.status === 429 || resp.status >= 500)) return json({ code: resp.status, data, channelUsed: 'supabase' }, { status: resp.status });
        } catch (e) { /* fallback */ }
      }

      try {
        const gen = await generateSupabaseLinkOrOtp(env, email, type === 'magiclink' ? 'magiclink' : 'email', redirect_to);
        if (!gen.ok) return json({ code: 502, message: 'generate_link_failed', data: gen.data }, { status: 502 });
        let subject = 'UEModManager 登录';
        let cn = '', en = '';
        if (type === 'magiclink' && gen.link) {
          cn = `点击下方链接登录：<br/><a href="${gen.link}">${gen.link}</a><br/>该链接短期有效，请尽快使用。`;
          en = `Click the link below to sign in:<br/><a href="${gen.link}">${gen.link}</a><br/>The link will expire shortly.`;
          subject = 'UEModManager 登录链接 / Magic Link';
        } else if (gen.otp) {
          cn = `您的验证码为：<b>${gen.otp}</b>（15 分钟内有效）。如非本人操作，请忽略本邮件。`;
          en = `Your verification code: <b>${gen.otp}</b> (valid for ~15 minutes). If you didn't request this, please ignore.`;
          subject = 'UEModManager 验证码 / Verification Code';
        } else {
          cn = '生成验证码/链接失败，请稍后重试。';
          en = 'Failed to generate OTP/Link, please try again later.';
        }
        const mail = buildBilingualMail('登录验证', 'Sign-in Verification', cn, en);
        const sent = await sendBrevoMail(env, email, subject, mail.html, mail.text);
        if (!sent.ok) return json({ code: 200, data: { link: gen.link, note: 'brevo_failed' }, channelUsed: 'link_only' });
        return json({ code: 200, data: { ok: true }, channelUsed: 'brevo' });
      } catch (e) {
        return json({ code: 502, message: String(e), channelUsed: 'brevo_exception' }, { status: 502 });
      }
    }

    // 忘记密码（带兜底）- 全路径确保返回link_only而不是502
    if (url.pathname === '/auth/reset' && request.method === 'POST') {
      const body = await request.json().catch(() => ({}));
      const { email, redirect_to } = body as Record<string, string>;
      if (!email) return bad(400, 'email required');
      const ip = request.headers.get('cf-connecting-ip') || '0.0.0.0';
      const rl = await rateLimit(env, `reset:${ip}`, 5, 60);
      if (!rl.allowed) return bad(429, 'too many requests');

      const supabaseUrl = (env.SUPABASE_URL || '').trim().replace(/[\r\n]/g, '');
      const serviceKey = (env.SUPABASE_SERVICE_KEY || '').trim().replace(/[\r\n]/g, '');
      const headers = new Headers({ 'content-type': 'application/json' });
      headers.set('apikey', serviceKey);
      headers.set('authorization', `Bearer ${serviceKey}`);

      // 第一层：尝试Supabase内置的recover（可能失败）
      try {
        const payload: Record<string, unknown> = { email };
        if (redirect_to) payload.options = { email_redirect_to: redirect_to };
        const resp = await fetch(new URL('/auth/v1/recover', supabaseUrl).toString(), { method: 'POST', headers, body: JSON.stringify(payload) });
        const text = await resp.text(); let data: unknown; try { data = text ? JSON.parse(text) : null; } catch { data = text; }
        if (resp.ok) return json({ code: 200, data, channelUsed: 'supabase' });
      } catch (e) { /* fallback to generate_link */ }

      // 第二层：生成链接 + 尝试发Brevo邮件
      let recoveryLink: string | undefined;
      let layer2Error: string | undefined;
      try {
        const gen = await generateSupabaseLinkOrOtp(env, email, 'recovery', redirect_to);
        if (gen.ok && gen.link) {
          recoveryLink = gen.link;
          // 尝试发送邮件
          try {
            const cn = `点击下方链接重置密码：<br/><a href="${gen.link}">${gen.link}</a>`;
            const en = `Click to reset your password:<br/><a href="${gen.link}">${gen.link}</a>`;
            const mail = buildBilingualMail('重置密码', 'Reset Password', cn, en);
            const sent = await sendBrevoMail(env, email, 'UEModManager 重置密码 / Reset Password', mail.html, mail.text);
            if (sent.ok) {
              return json({ code: 200, data: { ok: true }, channelUsed: 'brevo' });
            }
          } catch (brevoErr) {
            // Brevo发送失败，但链接已生成，返回link_only
          }
          // Brevo失败，但有链接，返回link_only
          return json({ code: 200, data: { link: gen.link, note: 'brevo_failed' }, channelUsed: 'link_only' });
        } else {
          layer2Error = `gen.ok=${gen.ok}, status=${gen.status}, data=${JSON.stringify(gen.data)}`;
        }
      } catch (e) {
        layer2Error = String(e);
      }

      // 第三层：最后的兜底 - 再次尝试生成链接
      let layer3Error: string | undefined;
      try {
        const gen3 = await generateSupabaseLinkOrOtp(env, email, 'recovery', redirect_to);
        if (gen3.ok && gen3.link) {
          return json({ code: 200, data: { link: gen3.link, note: 'final_fallback' }, channelUsed: 'link_only' });
        } else {
          layer3Error = `gen3.ok=${gen3.ok}, status=${gen3.status}, data=${JSON.stringify(gen3.data)}`;
        }
      } catch (finalErr) {
        layer3Error = String(finalErr);
      }

      // 最终失败：无法生成链接 - 返回详细错误用于诊断
      return json({
        code: 500,
        message: 'unable_to_generate_recovery_link',
        channelUsed: 'failed',
        debug: { layer2: layer2Error, layer3: layer3Error }
      }, { status: 500 });
    }

    // 日志上报（KV 兜底）
    if (url.pathname === '/logs' && request.method === 'POST') {
      const ip = request.headers.get('cf-connecting-ip') || '0.0.0.0';
      const rl = await rateLimit(env, `logs:${ip}`, 30, 60);
      if (!rl.allowed) return bad(429, 'too many requests');
      const now = new Date();
      const y = now.getUTCFullYear();
      const m = String(now.getUTCMonth() + 1).padStart(2, '0');
      const d = String(now.getUTCDate()).padStart(2, '0');
      const hh = String(now.getUTCHours()).padStart(2, '0');
      const ts = now.toISOString().replace(/[:.]/g, '-');
      const q = url.searchParams.get('source') || 'client';
      const key = `${y}/${m}/${d}/${hh}/${ts}_${ip}_${Math.random().toString(36).slice(2, 8)}_${q}.log`;
      const contentType = request.headers.get('content-type') || '';
      let payload: string;
      if (contentType.includes('application/json')) {
        const obj = await request.json().catch(() => ({}));
        payload = JSON.stringify(obj);
      } else {
        payload = await request.text();
      }
      await env.RATE_LIMIT.put(`logs:${key}`, payload, { expirationTtl: 60 * 60 * 24 * 30 });
      return json({ code: 200, key });
    }

    return bad(404, 'not found');
  }
};
