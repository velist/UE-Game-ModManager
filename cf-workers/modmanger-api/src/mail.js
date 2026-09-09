// The only mail transport. Recipients/content must be chosen by an auth action;
// this function is never exposed as an HTTP relay.
export async function sendBrevoMail(env, to, subject, html, text) {
  const apiKey = (env.BREVO_API_KEY || "").trim().replace(/[\r\n]/g, "");
  const from = (env.BREVO_FROM || env.BREVO_FROM_EMAIL || "").trim();
  if (!apiKey || !from) throw new Error("mail service is not configured");
  const response = await fetch("https://api.brevo.com/v3/smtp/email", {
    method: "POST",
    headers: { "content-type": "application/json", accept: "application/json", "api-key": apiKey },
    body: JSON.stringify({
      sender: { name: env.BREVO_FROM_NAME || "UEModManager", email: from },
      to: [{ email: to }], subject, htmlContent: html, textContent: text || ""
    })
  });
  const raw = await response.text();
  let data;
  try { data = raw ? JSON.parse(raw) : null; } catch { data = null; }
  return { ok: response.ok, status: response.status, data };
}

export function loginCodeMail(code) {
  // No caller-controlled values are interpolated. A request only selects the
  // fixed login action and the address which will own its challenge.
  return {
    subject: "【UEModManager】邮箱登录验证码 / Sign-in code",
    html: `<!doctype html><html><body style="font-family:Segoe UI,Arial;line-height:1.7">
<h2>UEModManager 邮箱登录</h2><p>您的验证码是：</p>
<p style="font-size:36px;font-weight:bold;letter-spacing:6px">${code}</p>
<p>验证码 10 分钟内有效，仅用于登录。请勿转发。如非本人操作，请忽略此邮件。</p>
<hr><p>Your sign-in code is <b>${code}</b>. It expires in 10 minutes.
Do not share this code. If you did not request it, ignore this email.</p>
<p>爱酱工作室 / UEModManager</p></body></html>`,
    text: `UEModManager 登录验证码：${code}\n10 分钟内有效，仅用于登录。请勿转发。如非本人操作，请忽略。\nYour sign-in code: ${code}. Valid for 10 minutes. Do not share it.`
  };
}
