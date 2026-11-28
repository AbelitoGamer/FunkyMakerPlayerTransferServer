import { describe, expect, it } from "vitest";
import { avatarUrlOf, displayNameOf, signSession, verifySession, SessionSeconds } from "../src/accounts";
import { Protocol } from "../src/protocol";
import { GuestRoomError, safeAvatar } from "../src/registry";
import { World, identityOf } from "./helpers";

describe("display names", () => {
  it("uses the Discord display name, folded to what the game's font can draw", () => {
    expect(displayNameOf("Abelito", "abelito_gamer")).toBe("Abelito");
    expect(displayNameOf("José Ñandú", "jose")).toBe("Jose Nandu");
    expect(displayNameOf("  Fenix   🔥  ", "fenix")).toBe("Fenix");
  });

  it("falls back to the unique username when nothing drawable is left", () => {
    expect(displayNameOf("🔥🔥🔥", "fenix")).toBe("fenix");
    expect(displayNameOf("( ˘ω˘ )", "sleepy")).toBe("sleepy");
    expect(displayNameOf(null, "abelito_gamer")).toBe("abelito_gamer");
    expect(displayNameOf("", "")).toBe("Player");
  });

  it("caps the name at 32 characters", () => {
    expect(displayNameOf("x".repeat(50), "u")).toHaveLength(32);
  });
});

describe("avatars", () => {
  it("builds the CDN URL, or Discord's default avatar without a hash", () => {
    const id = "80351110224678912";
    expect(avatarUrlOf(id, "8342729096ea3675442027381ff50dfe")).toBe(
      `https://cdn.discordapp.com/avatars/${id}/8342729096ea3675442027381ff50dfe.png?size=64`,
    );
    expect(avatarUrlOf(id, "a_8342729096ea3675442027381ff50dfe")).toContain(".png?size=64");
    expect(avatarUrlOf(id, "")).toBe(`https://cdn.discordapp.com/embed/avatars/${Number((BigInt(id) >> 22n) % 6n)}.png`);
    expect(avatarUrlOf("dev-x", "../../evil")).toBe("https://cdn.discordapp.com/embed/avatars/0.png");
  });

  it("only lets Discord CDN URLs reach the clients", () => {
    expect(safeAvatar("https://cdn.discordapp.com/embed/avatars/1.png")).not.toBe("");
    expect(safeAvatar("https://evil.example/a.png")).toBe("");
    expect(safeAvatar("http://cdn.discordapp.com/embed/avatars/1.png")).toBe("");
  });
});

describe("session tokens", () => {
  const secret = "test-secret-0123456789";
  const now = 1_700_000_000_000;

  it("accepts its own token for 30 days", async () => {
    const t = await signSession(secret, "80351110224678912", now);
    expect(await verifySession(secret, t, now)).toEqual({ ok: true, playerId: "80351110224678912" });
    expect(await verifySession(secret, t, now + (SessionSeconds - 60) * 1000)).toMatchObject({ ok: true });
    expect(await verifySession(secret, t, now + (SessionSeconds + 1) * 1000)).toEqual({ ok: false, reason: "expired" });
  });

  it("refuses tampered tokens and other secrets", async () => {
    const t = await signSession(secret, "123456789", now);
    const [v, , exp, sig] = t.split(".");
    expect(await verifySession(secret, `${v}.987654321.${exp}.${sig}`, now)).toEqual({ ok: false, reason: "invalid" });
    expect(await verifySession(secret, `${v}.123456789.${Number(exp) + 999999}.${sig}`, now)).toEqual({ ok: false, reason: "invalid" });
    expect(await verifySession("another-secret", t, now)).toEqual({ ok: false, reason: "invalid" });
    expect(await verifySession(secret, "garbage", now)).toEqual({ ok: false, reason: "invalid" });
  });
});

describe("guests", () => {
  it("can connect and list rooms, but not create or join them", () => {
    const w = new World();
    const a = w.connect("A");
    const id = a.create();
    const hello = w.run((fx) => w.registry.hello("guest-conn", { protocolVersion: Protocol.Version, playerId: a.playerId, username: "A" }, fx));
    expect(hello.ok).toBe(true);
    expect(hello.guest).toBe(true);
    expect(hello.account).toBeNull();
    expect(w.registry.listRooms()).toHaveLength(1);
    expect(w.run((fx) => w.registry.joinRoom("guest-conn", { roomId: id }, fx)).error).toBe(GuestRoomError);
    expect(w.run((fx) => w.registry.createRoom("guest-conn", {}, fx)).error).toBe(GuestRoomError);
    // The guest's claimed playerId is ignored: A is still alone in the room and not kicked.
    expect(a.state.members).toHaveLength(1);
    expect(w.registry.profileOf("guest-conn")).toBeNull();
  });

  it("logged-in players carry their name and avatar into the room", () => {
    const w = new World();
    const a = w.connect("Abelito");
    a.create();
    expect(a.state.members[0].username).toBe("Abelito");
    expect(a.state.members[0].avatar).toBe(identityOf(a.playerId, "Abelito").avatar);
  });

  it("logging out on the same connection leaves the room", () => {
    const w = new World();
    const a = w.connect("A");
    const b = w.connect("B");
    const id = a.create();
    b.join(id);
    w.run((fx) => w.registry.hello(b.conn, { protocolVersion: Protocol.Version }, fx));
    expect(a.state.members).toHaveLength(1);
  });
});
