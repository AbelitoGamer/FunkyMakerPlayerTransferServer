// Wire types shared with the game client (FunkyMakerPlayer/Multiplayer/Rooms/RoomProtocol.cs).
// Port of src/FunkyMakerTransferServer/Protocol.cs. Field names are camelCase on the wire (what
// SignalR's JSON protocol sent); incoming messages are accepted in any casing (see normalizeKeys).

export const Protocol = {
  /**
   * 5 = Discord accounts: Hello carries a session token (without one the connection is a guest, who
   *     can't create or join rooms); every player packet carries an avatar URL.
   * 4 = Cloudflare Workers server: plain WebSocket envelope instead of SignalR (see hub.ts),
   *     chunked uploads as R2 multipart parts, X-File-Hash on packed uploads.
   * 3 = osu!mania songs. 2 = room system. 1 = old pair-transfer server.
   */
  Version: 5,

  KindFnf: "fnf",
  KindOsu: "osu",

  SongTypesFnf: "fnf",
  SongTypesOsu: "osu",
  SongTypesBoth: "both",

  VariantFull: "full",
  VariantNoVideo: "novideo",
  VariantChartOnly: "chartonly",

  // Server → client events
  RoomState: "RoomState",
  Chat: "Chat",
  Kicked: "Kicked",
  TransferUpload: "TransferUpload",
  TransferDownload: "TransferDownload",
  TransferFailed: "TransferFailed",
  /** A pick was dropped (the lobby voted no, or the level couldn't be sent): the picker picks again. */
  SongRejected: "SongRejected",
  MatchLoading: "MatchLoading",
  MatchStart: "MatchStart",
  MatchResults: "MatchResults",
  PlayerState: "PlayerState",
  PlayerHit: "PlayerHit",
  PlayerKeyPress: "PlayerKeyPress",
  Pong: "Pong",

  ModeVersus: "versus",
  ModeTeams: "teams",
  ModeMirror: "mirror",

  SelectTurns: "turns",
  SelectHost: "host",

  SourceOfficial: "official",
  SourceLocal: "local",

  PhaseLobby: "lobby",
  PhaseSongSelected: "selected",
  PhaseLoading: "loading",
  PhasePlaying: "playing",

  StatusNotReady: "notready",
  StatusReady: "ready",
  StatusPlaying: "playing",
  StatusSpectating: "spectating",
  StatusFinished: "finished",
  StatusLeftMatch: "leftmatch",

  LevelMissing: "missing",
  LevelDownloading: "downloading",
  LevelReady: "ready",
  LevelFailed: "failed",

  SidePlayer: 0,
  SideEnemy: 1,

  TokenHeader: "X-Transfer-Token",
  /** SHA-256 (lower-case hex) of the uploaded file; required for packed variants (osu! .osz, chartonly). */
  FileHashHeader: "X-File-Hash",

  MinPlayers: 2,
  MaxPlayers: 8,
  HardMaxPlayers: 32,
  MaxRoomNameLength: 32,
  MaxChatLength: 200,
  /** Songs at least this long (seconds) need the lobby's OK (a vote) before they are played. */
  LongSongSeconds: 240,
  /** How long a song vote stays open (ms); members who didn't vote are left out of the count. */
  SongVoteMs: 30_000,
} as const;

// ── Session ────────────────────────────────────────────────────────────────

export interface HelloRequest {
  playerId: string;
  username: string;
  colorHue: number;
  clientVersion: string;
  protocolVersion: number;
  osuSkipVideo: boolean;
  fnfChartOnly: boolean;
  /** Session token from the Discord login; empty = guest. (playerId / username are ignored since protocol 5.) */
  sessionToken: string;
}

/** The Discord account a session is logged in as. */
export interface AccountInfo {
  /** Discord user id (= the PlayerId everywhere). */
  playerId: string;
  /** Shown name: the Discord display name, or the unique username when the display name has nothing the game can draw. */
  username: string;
  /** Unique Discord username (lower-case). */
  discordUsername: string;
  /** Avatar PNG URL (cdn.discordapp.com). */
  avatar: string;
  colorHue: number;
}

export interface HelloResult {
  ok: boolean;
  error: string;
  protocolVersion: number;
  serverVersion: string;
  maxUploadBytes: number;
  maxRoomPlayers: number;
  supportsChunkedUpload: boolean;
  supportsFnfChartOnly: boolean;
  uploadChunkBytes: number;
  /** True for a guest (no or refused token): leaderboards and the room list only, no rooms. */
  guest: boolean;
  account: AccountInfo | null;
  /** Renewed session token (valid 30 days from now): the client saves it. Empty for guests. */
  sessionToken: string;
  /** Why the token was refused ("expired" / "invalid"); empty otherwise. */
  authError: string;
}

export interface OpResult {
  ok: boolean;
  error: string;
  roomId: string;
}

export const OpResult = {
  success: (roomId = ""): OpResult => ({ ok: true, error: "", roomId }),
  fail: (error: string): OpResult => ({ ok: false, error, roomId: "" }),
};

export interface PongPacket {
  clientTimestamp: number;
  serverTimestamp: number;
  ping: number;
}

// ── Rooms ──────────────────────────────────────────────────────────────────

export interface LevelEntry {
  hash: string;
  name: string;
  fileName: string;
  size: number;
  stars: number;
  kind: string;
  setId: string;
  setName: string;
  difficulty: string;
  sizeNoVideo: number;
  /** Song length in seconds (end of the last note), 0 = unknown. */
  duration: number;
}

export interface RoomSettings {
  mode: string;
  songSelection: string;
  password: string;
  maxPlayers: number;
  allowGuestLocalLevels: boolean;
  songTypes: string;
}

export interface CreateRoomRequest {
  name: string;
  settings: Partial<RoomSettings>;
  levels: Partial<LevelEntry>[];
}

export interface JoinRoomRequest {
  roomId: string;
  password: string;
  levels: Partial<LevelEntry>[];
}

export interface RoomPlayerSummary {
  id: string;
  username: string;
  colorHue: number;
  avatar: string;
}

export interface RoomSummary {
  id: string;
  name: string;
  hasPassword: boolean;
  mode: string;
  phase: string;
  songName: string;
  songStars: number;
  hostName: string;
  players: RoomPlayerSummary[];
  playerCount: number;
  maxPlayers: number;
}

export interface SongInfo {
  source: string;
  hash: string;
  name: string;
  fileName: string;
  size: number;
  officialId: string;
  stars: number;
  pickedBy: string;
  kind: string;
  setId: string;
  setName: string;
  difficulty: string;
  sizeNoVideo: number;
  /** Seconds, 0 = unknown. */
  duration: number;
}

export interface RoomMember {
  id: string;
  username: string;
  colorHue: number;
  avatar: string;
  isHost: boolean;
  status: string;
  spectate: boolean;
  levelStatus: string;
  levelProgress: number;
  uploadProgress: number;
  vote: number;
}

export interface RoomLevel {
  hash: string;
  name: string;
  fileName: string;
  size: number;
  stars: number;
  youHave: boolean;
  holders: string[];
  pickable: boolean;
  kind: string;
  setId: string;
  setName: string;
  difficulty: string;
  sizeNoVideo: number;
  duration: number;
}

export interface MatchAssignment {
  playerId: string;
  side: number;
  team: number;
}

export interface MatchInfo {
  matchId: string;
  mode: string;
  song: SongInfo;
  players: MatchAssignment[];
  startTimestamp: number;
  mirrorSide: number;
  loaded: string[];
}

export interface MatchStartPacket {
  matchId: string;
  startTimestamp: number;
}

export interface PlayerResult {
  playerId: string;
  username: string;
  colorHue: number;
  avatar: string;
  side: number;
  team: number;
  score: number;
  accuracy: number;
  misses: number;
  sicks: number;
  goods: number;
  bads: number;
  shits: number;
  maxCombo: number;
  place: number;
  disconnected: boolean;
}

export interface RoundResult {
  matchId: string;
  mode: string;
  song: SongInfo;
  players: PlayerResult[];
  teamScores: number[];
  winningTeam: number;
  finishedAt: number;
}

export interface ChatMessage {
  playerId: string;
  username: string;
  colorHue: number;
  /** Sender's avatar URL ("" for system messages). */
  avatar: string;
  text: string;
  system: boolean;
  timestamp: number;
}

export interface RoomStatePacket {
  roomId: string;
  name: string;
  settings: RoomSettings;
  hasPassword: boolean;
  phase: string;
  members: RoomMember[];
  hostId: string;
  youId: string;
  turnPlayerId: string;
  song: SongInfo | null;
  match: MatchInfo | null;
  startBlocker: string;
  levels: RoomLevel[] | null;
  history: RoundResult[] | null;
  /** A long song waiting for the lobby's vote, or null. */
  songVote: SongVoteInfo | null;
  /** Songs that can't be picked again this turn (rejected by the lobby or couldn't be sent). */
  blockedHashes: string[];
}

export interface SongVoteInfo {
  song: SongInfo;
  yes: number;
  no: number;
  /** Players asked to vote (everybody playing except the picker, when the vote started). */
  eligible: string[];
  /** Who already voted. */
  voted: string[];
  /** Server time (ms) the vote closes. */
  endsAt: number;
  /** Your own vote: 1 yes, 0 no, -1 none. */
  yourVote: number;
}

export interface SongRejectedPacket {
  hash: string;
  name: string;
  reason: string;
  pickedBy: string;
}

// ── Round flow ─────────────────────────────────────────────────────────────

export interface SongSelection {
  source: string;
  hash: string;
  name: string;
  officialId: string;
  stars: number;
  /** Song length in seconds as the picker's game measured it (0 = unknown). */
  duration: number;
}

export interface LevelStatusUpdate {
  hash: string;
  state: string;
  progress: number;
}

export interface TransferUploadPacket {
  transferId: string;
  token: string;
  hash: string;
  fileName: string;
  size: number;
  kind: string;
  variant: string;
  maxSize: number;
}

export interface TransferDownloadPacket {
  transferId: string;
  token: string;
  hash: string;
  name: string;
  fileName: string;
  size: number;
  kind: string;
  variant: string;
  fileHash: string;
}

export interface TransferFailedPacket {
  hash: string;
  reason: string;
}

export interface KickedPacket {
  reason: string;
}

// ── In-match relay ─────────────────────────────────────────────────────────

export interface PlayerStatePacket {
  playerId: string;
  score: number;
  combo: number;
  maxCombo: number;
  accuracy: number;
  sicks: number;
  goods: number;
  bads: number;
  shits: number;
  misses: number;
  songTime: number;
  left: boolean;
}

export interface FinishMatchRequest {
  matchId: string;
  score: number;
  accuracy: number;
  misses: number;
  sicks: number;
  goods: number;
  bads: number;
  shits: number;
  maxCombo: number;
  quit: boolean;
}

// ── Helpers ────────────────────────────────────────────────────────────────

/** Lower-cases the first letter of every object key (PascalCase C# clients → camelCase), recursively. */
export function normalizeKeys(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(normalizeKeys);
  if (value !== null && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      const key = k.length > 0 ? k[0].toLowerCase() + k.slice(1) : k;
      out[key] = normalizeKeys(v);
    }
    return out;
  }
  return value;
}

export const str = (v: unknown, fallback = ""): string => (typeof v === "string" ? v : fallback);
export const num = (v: unknown, fallback = 0): number => {
  const n = typeof v === "number" ? v : typeof v === "string" && v.trim() !== "" ? Number(v) : NaN;
  return Number.isFinite(n) ? n : fallback;
};
export const int = (v: unknown, fallback = 0): number => Math.trunc(num(v, fallback));
export const bool = (v: unknown, fallback = false): boolean => (typeof v === "boolean" ? v : fallback);
