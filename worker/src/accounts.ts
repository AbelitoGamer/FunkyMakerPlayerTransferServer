// Discord accounts (protocol 5): the pure parts, no Cloudflare bindings (unit-tested).
//
// A player logs in with Discord once (auth.ts); the server then hands out its own session token,
// "v1.<discord id>.<expiry, unix seconds>.<HMAC-SHA256, base64url>", signed with SESSION_SECRET.
// Every Hello renews it for another 30 days, so a session only ends after 30 days without playing.

import type { AccountInfo } from "./protocol";

export const SessionDays = 30;
export const SessionSeconds = SessionDays * 24 * 3600;
/** A pending login (game waiting for the browser) is dropped after this long. */
export const LoginTtlMs = 10 * 60 * 1000;
/** Name / avatar are fetched again from Discord at most this often. */
export const ProfileRefreshMs = 24 * 3600 * 1000;

/** What the server keeps about an account (Leaderboard Durable Object, table `accounts`). */
export interface StoredAccount {
  /** Discord user id (snowflake). */
  id: string;
  /** Unique Discord username. */
  username: string;
  /** Discord display name (global_name), or "". */
  globalName: string;
  /** Avatar hash, or "" for Discord's default avatar. */
  avatarHash: string;
  colorHue: number;
  /** Discord OAuth tokens (scope identify only), used to refresh the name / avatar once a day. */
  accessToken: string;
  refreshToken: string;
  tokenExpiresAt: number;
  /** When the profile was last fetched from Discord (ms). */
  refreshedAt: number;
  createdAt: number;
  lastSeenAt: number;
}

/** A login started by the game: the browser finishes it, the game polls for it. */
export interface PendingLogin {
  id: string;
  /** Secret only the game knows (the login id is in the browser's address bar). */
  pollKey: string;
  expiresAt: number;
  /** Set when the browser came back: the account, or why it failed. */
  playerId: string;
  error: string;
}

/** Discord's /users/@me (the fields we use). */
export interface DiscordUser {
  id: string;
  username: string;
  global_name?: string | null;
  avatar?: string | null;
}

/**
 * The game's fonts only draw ASCII (32-126): accents are folded ("José" → "Jose"), everything else
 * (emoji, other scripts) dropped. When nothing is left, the unique username (always ASCII) is used.
 */
export function displayNameOf(globalName: string | null | undefined, username: string): string {
  const fold = (s: string) =>
    s
      .normalize("NFKD")
      .replace(/[^\x20-\x7e]/g, "")
      .replace(/\s+/g, " ")
      .trim()
      .slice(0, 32)
      .trim();
  const shown = fold(globalName ?? "");
  // Needs at least one letter or digit, otherwise "( ˘ω˘ )" would become "( )".
  if (/[A-Za-z0-9]/.test(shown)) return shown;
  return fold(username) || "Player";
}

/** 64 px PNG on Discord's CDN (static even for animated avatars); Discord's default avatar when there is none. */
export function avatarUrlOf(id: string, avatarHash: string): string {
  if (avatarHash && /^(a_)?[0-9a-f]{32}$/.test(avatarHash) && /^\d{5,25}$/.test(id))
    return `https://cdn.discordapp.com/avatars/${id}/${avatarHash}.png?size=64`;
  let index = 0;
  if (/^\d{5,25}$/.test(id)) index = Number((BigInt(id) >> 22n) % 6n);
  return `https://cdn.discordapp.com/embed/avatars/${index}.png`;
}

export function accountInfo(a: StoredAccount): AccountInfo {
  return {
    playerId: a.id,
    username: displayNameOf(a.globalName, a.username),
    discordUsername: a.username,
    avatar: avatarUrlOf(a.id, a.avatarHash),
    colorHue: a.colorHue,
  };
}

// ── Session tokens ──────────────────────────────────────────────────────────

const encoder = new TextEncoder();
const keys = new Map<string, Promise<CryptoKey>>();

function hmacKey(secret: string): Promise<CryptoKey> {
  let key = keys.get(secret);
  if (!key) {
    key = crypto.subtle.importKey("raw", encoder.encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign", "verify"]);
    keys.set(secret, key);
  }
  return key;
}

function toBase64Url(bytes: ArrayBuffer): string {
  let s = "";
  for (const b of new Uint8Array(bytes)) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function fromBase64Url(s: string): Uint8Array | null {
  if (!/^[A-Za-z0-9_-]+$/.test(s)) return null;
  try {
    const bin = atob(s.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((s.length + 3) % 4));
    return Uint8Array.from(bin, (c) => c.charCodeAt(0));
  } catch {
    return null;
  }
}

/** A session token for this account, valid SessionDays from nowMs. */
export async function signSession(secret: string, playerId: string, nowMs: number): Promise<string> {
  const body = `v1.${playerId}.${Math.floor(nowMs / 1000) + SessionSeconds}`;
  const sig = await crypto.subtle.sign("HMAC", await hmacKey(secret), encoder.encode(body));
  return `${body}.${toBase64Url(sig)}`;
}

export type SessionCheck = { ok: true; playerId: string } | { ok: false; reason: "invalid" | "expired" };

export async function verifySession(secret: string, token: string, nowMs: number): Promise<SessionCheck> {
  const parts = token.split(".");
  if (parts.length !== 4 || parts[0] !== "v1" || !/^[A-Za-z0-9_-]{1,64}$/.test(parts[1]) || !/^\d{1,12}$/.test(parts[2]))
    return { ok: false, reason: "invalid" };
  const sig = fromBase64Url(parts[3]);
  if (!sig) return { ok: false, reason: "invalid" };
  const body = parts.slice(0, 3).join(".");
  // crypto.subtle.verify compares in constant time.
  const good = await crypto.subtle.verify("HMAC", await hmacKey(secret), sig, encoder.encode(body));
  if (!good) return { ok: false, reason: "invalid" };
  if (Number(parts[2]) * 1000 <= nowMs) return { ok: false, reason: "expired" };
  return { ok: true, playerId: parts[1] };
}

/** Random lower-case hex (crypto-strong). */
export function randomHex(bytes: number): string {
  const b = crypto.getRandomValues(new Uint8Array(bytes));
  return [...b].map((x) => x.toString(16).padStart(2, "0")).join("");
}
