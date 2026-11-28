// Long-song votes, picks dropped for the turn (voted down / couldn't be sent) and osu! songs in Teams.

import { describe, expect, it } from "vitest";
import { Protocol, type MatchInfo, type SongRejectedPacket, type TransferUploadPacket } from "../src/protocol";
import { TestLevel, World } from "./helpers";

const upload = (p: { all<T>(m: string): T[] }) => p.all<TransferUploadPacket>(Protocol.TransferUpload).at(-1)!;

describe("long song vote", () => {
  it("a short song needs no vote", () => {
    const w = new World();
    const lvl = TestLevel.create("Short");
    const a = w.connect("A", [lvl]);
    const b = w.connect("B", [lvl]);
    b.join(a.create());
    expect(a.pickLocal(lvl, 120).ok).toBe(true);
    expect(b.state.songVote).toBeNull();
    expect(b.state.song!.name).toBe("Short");
  });

  it("a long song waits for the others; a yes majority picks it", () => {
    const w = new World();
    const lvl = TestLevel.create("Marathon");
    const a = w.connect("A", [lvl]);
    const b = w.connect("B");
    const c = w.connect("C");
    const id = a.create({ mode: Protocol.ModeMirror, maxPlayers: 4 });
    b.join(id);
    c.join(id);

    expect(a.pickLocal(lvl, 300).ok).toBe(true);
    let s = b.state;
    expect(s.song).toBeNull();
    expect(s.songVote!.song.name).toBe("Marathon");
    expect(s.songVote!.eligible.sort()).toEqual([b.playerId, c.playerId].sort());
    expect(a.all(Protocol.TransferUpload)).toHaveLength(0); // nothing is sent before the OK
    expect(a.voteSong(true).ok).toBe(false); // the picker doesn't vote
    expect(a.pickLocal(lvl, 300).ok).toBe(false); // no second pick meanwhile

    expect(b.voteSong(true).ok).toBe(true);
    expect(c.state.songVote!.yes).toBe(1);
    expect(c.state.songVote!.yourVote).toBe(-1);
    expect(b.state.songVote!.yourVote).toBe(1);
    expect(c.voteSong(false).ok).toBe(true); // 1 - 1: everyone voted, tie = rejected
    s = a.state;
    expect(s.songVote).toBeNull();
    expect(s.song).toBeNull();
    expect(a.latest<SongRejectedPacket>(Protocol.SongRejected)!.hash).toBe(lvl.hash);
    expect(s.blockedHashes).toEqual([lvl.hash]);
    expect(s.turnPlayerId).toBe(a.playerId); // same picker picks again
    expect(s.levels!.find((l) => l.hash === lvl.hash)!.pickable).toBe(false);
    expect(a.pickLocal(lvl, 300).ok).toBe(false);
    expect(b.chatHas("rejected Marathon")).toBe(true);
  });

  it("yes wins and the usual transfer starts", () => {
    const w = new World();
    const lvl = TestLevel.create("Marathon");
    const a = w.connect("A", [lvl]);
    const b = w.connect("B");
    b.join(a.create());
    a.pickLocal(lvl, 260);
    expect(b.voteSong(true).ok).toBe(true);
    expect(b.state.song!.name).toBe("Marathon");
    expect(b.state.song!.duration).toBe(260);
    expect(upload(a)).toBeDefined();
    expect(b.chatHas("accepted")).toBe(true);
  });

  it("the duration a holder reported counts too, and nobody voting in time means yes", () => {
    const w = new World();
    const lvl = TestLevel.create("Long");
    const entry = { ...lvl.entry, duration: 400 };
    const a = w.connect("A");
    const b = w.connect("B");
    w.run((fx) => w.registry.createRoom(a.conn, { name: "r", settings: { maxPlayers: 4 }, levels: [entry] }, fx));
    b.join(a.state.roomId);
    expect(a.pickLocal(lvl, 0).ok).toBe(true);
    expect(b.state.songVote).not.toBeNull();
    w.advance(Protocol.SongVoteMs + 1);
    expect(b.state.songVote).toBeNull();
    expect(b.state.song!.name).toBe("Long");
  });

  it("the picker leaving cancels the vote", () => {
    const w = new World();
    const lvl = TestLevel.create("Long");
    const a = w.connect("A");
    const b = w.connect("B", [lvl]);
    const c = w.connect("C");
    const id = a.create({ maxPlayers: 4, mode: Protocol.ModeMirror });
    b.join(id);
    c.join(id);
    w.run((fx) => w.registry.setSpectate(a.conn, true, fx)); // turn moves to B
    expect(b.pickLocal(lvl, 500).ok).toBe(true);
    expect(c.state.songVote!.eligible).toEqual([c.playerId]);
    b.disconnect();
    expect(c.state.songVote).toBeNull();
    expect(c.chatHas("vote was cancelled")).toBe(true);
  });
});

describe("picks that can't be sent", () => {
  it("the only holder failing to send drops the pick for this turn", () => {
    const w = new World();
    const lvl = TestLevel.create("Broken");
    const other = TestLevel.create("Other");
    const a = w.connect("A", [lvl, other]);
    const b = w.connect("B");
    b.join(a.create());
    expect(a.pickLocal(lvl).ok).toBe(true);
    const up = upload(a);
    const res = w.upload(up.transferId, up.token, lvl.bytes, { hash: "0".repeat(64) });
    expect(res.ok).toBe(false);

    const s = b.state;
    expect(s.song).toBeNull();
    expect(s.phase).toBe(Protocol.PhaseLobby);
    expect(s.turnPlayerId).toBe(a.playerId);
    expect(s.blockedHashes).toEqual([lvl.hash]);
    expect(a.latest<SongRejectedPacket>(Protocol.SongRejected)!.reason).toContain("Couldn't send Broken");
    expect(a.pickLocal(lvl).ok).toBe(false);
    expect(a.pickLocal(other).ok).toBe(true);
  });

  it("blocked songs can be picked again in a later turn", () => {
    const w = new World();
    const lvl = TestLevel.create("Broken");
    const shared = TestLevel.create("Shared");
    const a = w.connect("A", [lvl, shared]);
    const b = w.connect("B", [shared]);
    b.join(a.create());
    a.pickLocal(lvl);
    const up = upload(a);
    w.upload(up.transferId, up.token, lvl.bytes, { hash: "0".repeat(64) });
    expect(a.state.blockedHashes).toEqual([lvl.hash]);

    // Play a round with another song: the turn passes to B and the block is gone.
    a.pickLocal(shared);
    b.ready();
    const info = a.latest<MatchInfo>(Protocol.MatchLoading)!;
    a.loaded(info.matchId);
    b.loaded(info.matchId);
    a.finish(info.matchId, 100);
    b.finish(info.matchId, 200);
    const s = b.state;
    expect(s.turnPlayerId).toBe(b.playerId);
    expect(s.blockedHashes).toEqual([]);
    expect(s.levels!.find((l) => l.hash === lvl.hash)!.pickable).toBe(true);
  });
});

describe("osu! in Teams", () => {
  it("both teams play the single chart (side Player) and keep their teams", () => {
    const w = new World();
    const osu = TestLevel.osu("Mania");
    const players = ["A", "B", "C", "D"].map((n) => w.connect(n, [osu]));
    const [a] = players;
    const id = a.create({ mode: Protocol.ModeTeams, maxPlayers: 4 });
    for (const p of players.slice(1)) p.join(id);
    expect(a.pickLocal(osu).ok).toBe(true);
    for (const p of players.slice(1)) expect(p.ready().ok).toBe(true);
    const info = a.latest<MatchInfo>(Protocol.MatchLoading)!;
    expect(info.players.every((p) => p.side === Protocol.SidePlayer)).toBe(true);
    expect(info.players.map((p) => p.team).sort()).toEqual([0, 0, 1, 1]);
  });
});
