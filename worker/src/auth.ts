// Discord login (protocol 5). The game can't receive the OAuth redirect itself (desktop and Android),
// so the Worker does the whole OAuth2 code flow and the game polls for the result:
//
//   game     POST /api/auth/start                 → { loginId, pollKey, url }
//   game     opens url in the browser             = GET /auth/discord?login=<loginId>
//   browser  → discord.com/oauth2/authorize (scope identify) → GET /auth/discord/callback?code&state
//   worker   exchanges the code, reads /users/@me, saves the account, marks the login done
//   game     POST /api/auth/poll { loginId, pollKey } every 2 s → { status: "ok", sessionToken, account }
//
// The Discord client secret never leaves the Worker; the game only ever holds the server's own
// session token (accounts.ts). Discord tokens are kept to refresh the name / avatar once a day.

import {
  LoginTtlMs,
  ProfileRefreshMs,
  accountInfo,
  avatarUrlOf,
  displayNameOf,
  randomHex,
  signSession,
  verifySession,
  type DiscordUser,
  type StoredAccount,
} from "./accounts";
import { type Env, leaderboardStub } from "./env";
import { type HelloRequest, str } from "./protocol";
import type { SessionIdentity } from "./registry";

const DiscordApi = "https://discord.com/api/v10";
const LoginIdRegex = /^[0-9a-f]{32}$/;
const PollKeyRegex = /^[0-9a-f]{64}$/;

export function authConfigured(env: Env): boolean {
  return !!(env.DISCORD_CLIENT_ID && env.DISCORD_CLIENT_SECRET && env.SESSION_SECRET);
}

const callbackUrl = (url: URL) => `${url.origin}/auth/discord/callback`;

/** Handles the auth routes; null when the path isn't one of them. */
export async function routeAuth(request: Request, env: Env, url: URL, path: string): Promise<Response | null> {
  const method = request.method;
  if (method === "POST" && path === "/api/auth/start") return start(env, url);
  if (method === "POST" && path === "/api/auth/poll") return poll(request, env);
  if (method === "GET" && path === "/auth/discord") return toDiscord(env, url);
  if (method === "GET" && path === "/auth/discord/callback") return callback(env, url);
  return null;
}

async function start(env: Env, url: URL): Promise<Response> {
  if (!authConfigured(env)) return Response.json({ error: "Discord login isn't set up on this server." }, { status: 503 });
  const id = randomHex(16);
  const pollKey = randomHex(32);
  await leaderboardStub(env).createLogin({ id, pollKey, expiresAt: Date.now() + LoginTtlMs, playerId: "", error: "" });
  return Response.json(
    { loginId: id, pollKey, url: `${url.origin}/auth/discord?login=${id}`, expiresIn: LoginTtlMs / 1000, interval: 2 },
    { headers: { "Cache-Control": "no-store" } },
  );
}

async function poll(request: Request, env: Env): Promise<Response> {
  if (!authConfigured(env)) return Response.json({ error: "Discord login isn't set up on this server." }, { status: 503 });
  let body: Record<string, unknown> = {};
  try {
    body = (await request.json()) as Record<string, unknown>;
  } catch {
    /* empty body */
  }
  const id = str(body.loginId);
  const pollKey = str(body.pollKey);
  const noStore = { headers: { "Cache-Control": "no-store" } };
  if (!LoginIdRegex.test(id) || !PollKeyRegex.test(pollKey))
    return Response.json({ status: "error", error: "Invalid login." }, noStore);
  const lb = leaderboardStub(env);
  const res = await lb.pollLogin(id, pollKey);
  if (res.status !== "ok") return Response.json({ status: res.status, error: res.error }, noStore);
  const account = await lb.account(res.playerId);
  if (!account) return Response.json({ status: "error", error: "The account couldn't be saved. Try again." }, noStore);
  const sessionToken = await signSession(env.SESSION_SECRET!, account.id, Date.now());
  return Response.json({ status: "ok", sessionToken, account: accountInfo(account) }, noStore);
}

async function toDiscord(env: Env, url: URL): Promise<Response> {
  if (!authConfigured(env)) return page("Discord login isn't set up on this server.", false);
  const id = url.searchParams.get("login") ?? "";
  if (!LoginIdRegex.test(id) || !(await leaderboardStub(env).login(id)))
    return page("This login link expired. Start the login again from the game.", false);
  const q = new URLSearchParams({
    response_type: "code",
    client_id: env.DISCORD_CLIENT_ID!,
    scope: "identify",
    state: id,
    redirect_uri: callbackUrl(url),
    // Skip the consent screen for players who already authorized the app.
    prompt: "none",
  });
  return Response.redirect(`https://discord.com/oauth2/authorize?${q}`, 302);
}

async function callback(env: Env, url: URL): Promise<Response> {
  if (!authConfigured(env)) return page("Discord login isn't set up on this server.", false);
  const id = url.searchParams.get("state") ?? "";
  const lb = leaderboardStub(env);
  if (!LoginIdRegex.test(id) || !(await lb.login(id)))
    return page("This login expired. Start the login again from the game.", false);

  const error = url.searchParams.get("error");
  if (error) {
    await lb.finishLogin(id, "", error === "access_denied" ? "The login was cancelled on Discord." : `Discord said: ${error}`);
    return page("Login cancelled. You can close this tab.", false);
  }
  const code = url.searchParams.get("code") ?? "";
  if (!code) return page("Discord didn't send a login code. Try again from the game.", false);

  try {
    const tokens = await discordToken(env, { grant_type: "authorization_code", code, redirect_uri: callbackUrl(url) });
    const user = await discordUser(tokens.accessToken);
    const account = await lb.upsertDiscordAccount(user, tokens);
    if (!(await lb.finishLogin(id, account.id, ""))) return page("This login was already used. Start it again from the game.", false);
    const name = displayNameOf(account.globalName, account.username);
    return page(`Logged in as ${name}. You can close this tab and go back to the game.`, true, avatarUrlOf(account.id, account.avatarHash));
  } catch (e) {
    console.log(`[Auth] callback failed: ${(e as Error).message}`);
    await lb.finishLogin(id, "", "Discord login failed. Try again.");
    return page("The login failed. Go back to the game and try again.", false);
  }
}

// ── Hello ───────────────────────────────────────────────────────────────────

export interface ResolvedSession {
  identity: SessionIdentity | null;
  /** Account behind the identity (null for guests / dev logins). */
  account: StoredAccount | null;
  /** "expired" / "invalid" when a token was sent but refused. */
  authError: string;
}

/** Who a Hello is from: a valid session token → its account; otherwise a guest (or the dev login). */
export async function resolveHello(env: Env, request: Partial<HelloRequest>): Promise<ResolvedSession> {
  const token = str(request.sessionToken).trim();
  const colorHue = Math.min(360, Math.max(0, Math.trunc(Number(request.colorHue) || 0)));
  if (token && env.SESSION_SECRET) {
    const check = await verifySession(env.SESSION_SECRET, token, Date.now());
    if (check.ok) {
      const account = await leaderboardStub(env).touchAccount(check.playerId, colorHue);
      if (account) {
        const info = accountInfo(account);
        return {
          identity: { playerId: info.playerId, username: info.username, discordUsername: info.discordUsername, avatar: info.avatar },
          account,
          authError: "",
        };
      }
      return { identity: null, account: null, authError: "invalid" };
    }
    return { identity: null, account: null, authError: check.reason };
  }
  if (env.DEV_AUTH === "true") {
    const id = str(request.playerId).replace(/[^A-Za-z0-9_-]/g, "").slice(0, 40);
    if (id) {
      const name = str(request.username).trim() || "Dev";
      return { identity: { playerId: `dev-${id}`, username: name, discordUsername: name.toLowerCase(), avatar: "" }, account: null, authError: "" };
    }
  }
  return { identity: null, account: null, authError: token ? "invalid" : "" };
}

/** Fetches the name / avatar from Discord again when they're over a day old (best effort, in the background). */
export async function refreshProfileIfStale(env: Env, account: StoredAccount): Promise<void> {
  if (!authConfigured(env) || !account.refreshToken || Date.now() - account.refreshedAt < ProfileRefreshMs) return;
  const lb = leaderboardStub(env);
  try {
    let { accessToken, refreshToken, tokenExpiresAt } = account;
    if (Date.now() > tokenExpiresAt - 60_000) ({ accessToken, refreshToken, tokenExpiresAt } = await discordToken(env, { grant_type: "refresh_token", refresh_token: refreshToken }));
    await lb.upsertDiscordAccount(await discordUser(accessToken), { accessToken, refreshToken, tokenExpiresAt });
  } catch (e) {
    // Revoked / Discord down: keep the old name and avatar, try again tomorrow.
    console.log(`[Auth] profile refresh for ${account.id} failed: ${(e as Error).message}`);
    const fresh = await lb.account(account.id);
    if (fresh) {
      fresh.refreshedAt = Date.now();
      await lb.saveAccount(fresh);
    }
  }
}

// ── Discord API ─────────────────────────────────────────────────────────────

async function discordToken(
  env: Env,
  grant: Record<string, string>,
): Promise<{ accessToken: string; refreshToken: string; tokenExpiresAt: number }> {
  const res = await fetch(`${DiscordApi}/oauth2/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ client_id: env.DISCORD_CLIENT_ID!, client_secret: env.DISCORD_CLIENT_SECRET!, ...grant }),
  });
  if (!res.ok) throw new Error(`token ${res.status}: ${(await res.text()).slice(0, 200)}`);
  const json = (await res.json()) as { access_token?: string; refresh_token?: string; expires_in?: number };
  if (!json.access_token) throw new Error("token: no access_token");
  return {
    accessToken: json.access_token,
    refreshToken: json.refresh_token ?? "",
    tokenExpiresAt: Date.now() + (json.expires_in ?? 0) * 1000,
  };
}

async function discordUser(accessToken: string): Promise<DiscordUser> {
  const res = await fetch(`${DiscordApi}/users/@me`, { headers: { Authorization: `Bearer ${accessToken}` } });
  if (!res.ok) throw new Error(`users/@me ${res.status}`);
  const u = (await res.json()) as DiscordUser;
  if (!u || !/^\d{5,25}$/.test(str(u.id))) throw new Error("users/@me: no id");
  return u;
}

// ── Browser page ────────────────────────────────────────────────────────────

const escapeHtml = (s: string) => s.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);

function page(message: string, ok: boolean, avatar = ""): Response {
  const img = avatar ? `<img src="${escapeHtml(avatar)}" width="64" height="64" alt="">` : "";
  const html = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>FunkyMaker login</title>
<style>
  body{margin:0;min-height:100vh;display:grid;place-items:center;background:#16161d;color:#eee;font:16px/1.5 system-ui,sans-serif}
  main{max-width:420px;padding:32px;text-align:center}
  h1{font-size:20px;margin:12px 0;color:${ok ? "#7ee08a" : "#ff8a7a"}}
  img{border-radius:50%}
</style></head>
<body><main>${img}<h1>${ok ? "Done" : "Something went wrong"}</h1><p>${escapeHtml(message)}</p></main></body></html>`;
  return new Response(html, { status: ok ? 200 : 400, headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" } });
}
