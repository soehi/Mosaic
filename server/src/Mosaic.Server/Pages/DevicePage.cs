namespace Mosaic.Server.Pages;

/// <summary>
/// Built-in browser page for the device flow. It is deliberately dependency-free
/// (no external scripts, fonts or images) so the deployment needs no web assets.
/// </summary>
public static class DevicePage
{
    public const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Mosaic · 授权设备</title>
<style>
  :root {
    color-scheme: dark;
    --bg: #0e1015;
    --card: #171a22;
    --card-2: #1d212c;
    --line: #2a2f3d;
    --text: #eef0f6;
    --muted: #9aa1b4;
    --accent: #5b8cff;
    --accent-2: #7aa2ff;
    --danger: #ff7a7a;
    --ok: #58d68d;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
    padding: 32px 16px;
    background: radial-gradient(1200px 700px at 15% -10%, #1b2440 0%, transparent 60%),
                radial-gradient(900px 600px at 110% 10%, #1a2a33 0%, transparent 55%), var(--bg);
    color: var(--text);
    font: 15px/1.6 "Segoe UI", "Microsoft YaHei", system-ui, sans-serif;
  }
  .shell { width: 100%; max-width: 460px; }
  .brand { display: flex; align-items: center; gap: 12px; margin-bottom: 18px; padding-left: 4px; }
  .mark {
    width: 38px; height: 38px; border-radius: 11px;
    background: linear-gradient(140deg, var(--accent), #8f6bff);
    display: grid; place-items: center; font-weight: 700; font-size: 18px;
    box-shadow: 0 10px 24px -10px rgba(91,140,255,.85);
  }
  .brand b { font-size: 16px; letter-spacing: .4px; }
  .brand span { display: block; color: var(--muted); font-size: 12px; }
  .card {
    background: linear-gradient(180deg, var(--card), var(--card-2));
    border: 1px solid var(--line); border-radius: 18px; padding: 26px;
    box-shadow: 0 30px 70px -40px rgba(0,0,0,.9);
  }
  h1 { font-size: 20px; margin: 0 0 6px; }
  .sub { color: var(--muted); font-size: 13px; margin: 0 0 20px; }
  label { display: block; font-size: 12px; color: var(--muted); margin: 14px 0 6px; }
  input {
    width: 100%; padding: 11px 13px; border-radius: 10px; border: 1px solid var(--line);
    background: #10131a; color: var(--text); font: inherit; outline: none;
    transition: border-color .15s, box-shadow .15s;
  }
  input:focus { border-color: var(--accent); box-shadow: 0 0 0 3px rgba(91,140,255,.18); }
  .code-input {
    text-transform: uppercase; letter-spacing: 6px; font-weight: 700; font-size: 22px;
    text-align: center; font-family: "Cascadia Mono", "Consolas", monospace;
  }
  .row { display: flex; gap: 10px; }
  .row > * { flex: 1; }
  button {
    border: 0; border-radius: 10px; padding: 12px 16px; font: inherit; font-weight: 600;
    cursor: pointer; transition: transform .08s, filter .15s, opacity .15s;
  }
  button:active { transform: translateY(1px); }
  button:disabled { opacity: .5; cursor: default; }
  .primary { background: linear-gradient(140deg, var(--accent), #6f8cff); color: #fff; width: 100%; margin-top: 18px; }
  .ghost { background: #222736; color: var(--text); border: 1px solid var(--line); }
  .danger { background: transparent; color: var(--danger); border: 1px solid #4a2b31; }
  .tabs { display: flex; gap: 6px; background: #10131a; padding: 4px; border-radius: 12px; margin-bottom: 6px; }
  .tabs button { flex: 1; background: transparent; color: var(--muted); padding: 8px; border-radius: 8px; font-size: 13px; }
  .tabs button.active { background: #262c3b; color: var(--text); }
  .message { min-height: 20px; margin: 14px 0 0; font-size: 13px; color: var(--muted); }
  .message.error { color: var(--danger); }
  .message.ok { color: var(--ok); }
  .account { margin: 16px 0 0; padding: 12px 14px; border-radius: 10px; background: #10131a; border: 1px solid var(--line); font-size: 13px; color: var(--muted); }
  .account b { color: var(--text); }
  .actions { display: flex; gap: 10px; margin-top: 18px; }
  .actions .primary { margin-top: 0; }
  .link { background: none; border: 0; color: var(--accent-2); padding: 8px 0 0; font-size: 13px; cursor: pointer; }
  .hint { color: var(--muted); font-size: 12px; margin-top: 10px; }
  .done { text-align: center; padding: 8px 0 4px; }
  .done .tick {
    width: 56px; height: 56px; margin: 0 auto 16px; border-radius: 50%;
    background: rgba(88,214,141,.14); color: var(--ok); display: grid; place-items: center;
    font-size: 28px; border: 1px solid rgba(88,214,141,.4);
  }
  [hidden] { display: none !important; }
</style>
</head>
<body>
<div class="shell">
  <div class="brand">
    <div class="mark">M</div>
    <div><b>Mosaic</b><span>在浏览器中授权桌面客户端</span></div>
  </div>

  <div class="card">
    <div id="authView">
      <h1>授权桌面客户端</h1>
      <p class="sub">登录你的 Mosaic 账号，然后确认授权。授权码只会显示在你的桌面应用窗口里。</p>

      <label for="codeInput">授权码</label>
      <input id="codeInput" class="code-input" maxlength="9" placeholder="XXXX-XXXX" autocomplete="off" spellcheck="false">

      <div class="tabs" role="tablist">
        <button id="tabLogin" class="active" type="button">登录</button>
        <button id="tabRegister" type="button">创建账号</button>
      </div>

      <form id="loginForm" autocomplete="on">
        <label for="loginEmail">邮箱</label>
        <input id="loginEmail" type="email" autocomplete="username" placeholder="you@example.com">
        <label for="loginPassword">密码</label>
        <input id="loginPassword" type="password" autocomplete="current-password" placeholder="至少 10 个字符">
        <button class="primary" id="loginButton" type="submit">登录并继续</button>
        <button class="link" id="useCodeLogin" type="button">使用邮箱验证码登录</button>
      </form>

      <form id="codeLoginForm" hidden>
        <label for="codeLoginEmail">邮箱</label>
        <input id="codeLoginEmail" type="email" autocomplete="username" placeholder="you@example.com">
        <label for="codeLoginCode">邮箱验证码</label>
        <div class="row">
          <input id="codeLoginCode" inputmode="numeric" maxlength="6" autocomplete="one-time-code" placeholder="6 位数字">
          <button class="ghost" id="sendLoginCode" type="button">获取验证码</button>
        </div>
        <button class="primary" id="codeLoginButton" type="submit">登录并继续</button>
        <button class="link" id="usePasswordLogin" type="button">使用密码登录</button>
      </form>

      <form id="registerForm" hidden>
        <label for="registerEmail">邮箱</label>
        <input id="registerEmail" type="email" autocomplete="username" placeholder="you@example.com">
        <label for="registerCode">邮箱验证码</label>
        <div class="row">
          <input id="registerCode" inputmode="numeric" maxlength="6" autocomplete="one-time-code" placeholder="6 位数字">
          <button class="ghost" id="sendRegisterCode" type="button">获取验证码</button>
        </div>
        <label for="registerPassword">设置密码</label>
        <input id="registerPassword" type="password" autocomplete="new-password" placeholder="至少 10 个字符">
        <label for="registerConfirm">确认密码</label>
        <input id="registerConfirm" type="password" autocomplete="new-password" placeholder="再次输入密码">
        <button class="primary" id="registerButton" type="submit">创建账号并继续</button>
      </form>

      <p id="message" class="message" aria-live="polite"></p>
    </div>

    <div id="approveView" hidden>
      <h1>确认授权</h1>
      <p class="sub">确认后，该桌面客户端会获得你的账号访问权限，可在设置中随时退出登录。</p>
      <div class="account">正在授权的账号：<b id="accountEmail">-</b></div>
      <div class="account">授权码：<b id="approveCode">-</b></div>
      <div class="actions">
        <button class="primary" id="approveButton" type="button">确认授权</button>
        <button class="danger" id="denyButton" type="button">拒绝</button>
      </div>
      <p id="approveMessage" class="message" aria-live="polite"></p>
      <button class="link" id="switchAccount" type="button">使用其他账号</button>
    </div>

    <div id="doneView" hidden>
      <div class="done">
        <div class="tick" id="doneTick">✓</div>
        <h1 id="doneTitle">授权完成</h1>
        <p class="sub" id="doneText">现在可以返回桌面应用继续使用。</p>
      </div>
    </div>
  </div>

  <p class="hint">此页面由你连接的自建服务端提供，不会加载任何第三方资源。</p>
</div>

<script>
(() => {
  "use strict";
  const state = { token: null, email: null, code: "" };
  const $ = (id) => document.getElementById(id);
  const params = new URLSearchParams(location.search);
  const initialCode = (params.get("code") || "").toUpperCase();
  if (initialCode) { $("codeInput").value = initialCode; state.code = initialCode; }

  function showMessage(text, kind) {
    const el = $("message");
    el.textContent = text || "";
    el.className = "message" + (kind ? " " + kind : "");
  }

  function currentCode() {
    const value = ($("codeInput").value || "").trim().toUpperCase();
    state.code = value;
    return value;
  }

  async function request(path, body, token) {
    const headers = { "content-type": "application/json" };
    if (token) headers.authorization = "Bearer " + token;
    const response = await fetch(path, { method: "POST", headers, body: JSON.stringify(body) });
    let data = null;
    const text = await response.text();
    if (text) { try { data = JSON.parse(text); } catch { data = null; } }
    if (!response.ok) {
      const message = data && data.message ? data.message : "请求失败（" + response.status + "）";
      const error = new Error(message);
      error.retryAfter = response.headers.get("retry-after");
      throw error;
    }
    return data;
  }

  function setBusy(button, busy, label) {
    if (!button.dataset.label) button.dataset.label = button.textContent;
    button.disabled = busy;
    button.textContent = busy ? (label || "请稍候…") : button.dataset.label;
  }

  function countdown(button, seconds) {
    let left = seconds;
    button.disabled = true;
    const label = button.dataset.label || button.textContent;
    const tick = () => {
      if (left <= 0) { clearInterval(timer); button.disabled = false; button.textContent = label; return; }
      button.textContent = left + " 秒后重发";
      left -= 1;
    };
    const timer = setInterval(tick, 1000);
    tick();
  }

  async function sendCode(purpose, emailInput, button) {
    const email = (emailInput.value || "").trim();
    if (!email) { showMessage("请先填写邮箱。", "error"); return; }
    setBusy(button, true, "发送中…");
    try {
      const data = await request("/v1/auth/email-code", { email, purpose });
      const wait = data && data.resendAfterSeconds ? data.resendAfterSeconds : 60;
      countdown(button, wait);
      showMessage("验证码已发送，请检查邮箱。", "ok");
    } catch (error) {
      setBusy(button, false);
      showMessage(error.message, "error");
    }
  }

  function enterApprove(session) {
    state.token = session.accessToken;
    state.email = session.user.email;
    $("accountEmail").textContent = session.user.email;
    $("approveCode").textContent = currentCode() || "未填写";
    $("authView").hidden = true;
    $("approveView").hidden = false;
    if (!currentCode()) { showApproveMessage("请先填写桌面应用上显示的授权码。", "error"); }
  }

  function showApproveMessage(text, kind) {
    const el = $("approveMessage");
    el.textContent = text || "";
    el.className = "message" + (kind ? " " + kind : "");
  }

  function switchTab(mode) {
    $("tabLogin").classList.toggle("active", mode === "login");
    $("tabRegister").classList.toggle("active", mode === "register");
    $("loginForm").hidden = mode !== "login";
    $("codeLoginForm").hidden = true;
    $("registerForm").hidden = mode !== "register";
    showMessage("");
  }

  $("tabLogin").addEventListener("click", () => switchTab("login"));
  $("tabRegister").addEventListener("click", () => switchTab("register"));
  $("useCodeLogin").addEventListener("click", () => {
    $("loginForm").hidden = true; $("codeLoginForm").hidden = false;
  });
  $("usePasswordLogin").addEventListener("click", () => {
    $("loginForm").hidden = false; $("codeLoginForm").hidden = true;
  });

  $("sendRegisterCode").addEventListener("click", () =>
    sendCode("register", $("registerEmail"), $("sendRegisterCode")));
  $("sendLoginCode").addEventListener("click", () =>
    sendCode("login", $("codeLoginEmail"), $("sendLoginCode")));

  $("loginForm").addEventListener("submit", async (event) => {
    event.preventDefault();
    const button = $("loginButton");
    setBusy(button, true);
    try {
      const session = await request("/v1/auth/login", {
        email: $("loginEmail").value, password: $("loginPassword").value, deviceLabel: "浏览器",
      });
      showMessage("");
      enterApprove(session);
    } catch (error) { showMessage(error.message, "error"); }
    finally { setBusy(button, false); }
  });

  $("codeLoginForm").addEventListener("submit", async (event) => {
    event.preventDefault();
    const button = $("codeLoginButton");
    setBusy(button, true);
    try {
      const session = await request("/v1/auth/login/code", {
        email: $("codeLoginEmail").value, code: $("codeLoginCode").value, deviceLabel: "浏览器",
      });
      showMessage("");
      enterApprove(session);
    } catch (error) { showMessage(error.message, "error"); }
    finally { setBusy(button, false); }
  });

  $("registerForm").addEventListener("submit", async (event) => {
    event.preventDefault();
    if ($("registerPassword").value !== $("registerConfirm").value) {
      showMessage("两次输入的密码不一致。", "error");
      return;
    }
    const button = $("registerButton");
    setBusy(button, true);
    try {
      const session = await request("/v1/auth/register", {
        email: $("registerEmail").value,
        code: $("registerCode").value,
        password: $("registerPassword").value,
        deviceLabel: "浏览器",
      });
      showMessage("");
      enterApprove(session);
    } catch (error) { showMessage(error.message, "error"); }
    finally { setBusy(button, false); }
  });

  $("approveButton").addEventListener("click", async () => {
    const code = currentCode();
    if (!code) { showApproveMessage("请先填写授权码。", "error"); $("codeInput").focus(); return; }
    setBusy($("approveButton"), true);
    try {
      await request("/v1/device/approve", { userCode: code }, state.token);
      $("approveView").hidden = true;
      $("doneView").hidden = false;
      $("doneTick").textContent = "✓";
      $("doneTitle").textContent = "授权完成";
      $("doneText").textContent = "现在可以返回桌面应用继续使用。";
    } catch (error) {
      showApproveMessage(error.message, "error");
    } finally { setBusy($("approveButton"), false); }
  });

  $("denyButton").addEventListener("click", async () => {
    const code = currentCode();
    if (!code) { showApproveMessage("请先填写授权码。", "error"); return; }
    setBusy($("denyButton"), true);
    try {
      await request("/v1/device/deny", { userCode: code }, state.token);
      $("approveView").hidden = true;
      $("doneView").hidden = false;
      $("doneTick").textContent = "×";
      $("doneTitle").textContent = "已拒绝授权";
      $("doneText").textContent = "桌面客户端不会获得你的账号访问权限。";
    } catch (error) {
      showApproveMessage(error.message, "error");
    } finally { setBusy($("denyButton"), false); }
  });

  $("switchAccount").addEventListener("click", () => {
    state.token = null; state.email = null;
    $("approveView").hidden = true;
    $("authView").hidden = false;
    $("loginPassword").value = "";
    showMessage("");
  });

  $("codeInput").addEventListener("input", (event) => {
    const raw = event.target.value.toUpperCase().replace(/[^A-Z0-9]/g, "").slice(0, 8);
    event.target.value = raw.length > 4 ? raw.slice(0, 4) + "-" + raw.slice(4) : raw;
    state.code = event.target.value;
    if (state.token) $("approveCode").textContent = currentCode() || "未填写";
  });
})();
</script>
</body>
</html>
""";
}
