/* Generated from docs/protocol/kasi-messages.schema.json. Do not edit. */

/**
 * One message between the Kasi browser extension (through the native host) and the Kasi application. See docs/protocol/README.md.
 */
export type KasiProtocolMessage =
  | Hello
  | Rules
  | AddDownload
  | AddBatch
  | MediaDetected
  | CaptureMedia
  | RefreshLink
  | LinkRefreshed
  | OpenSettings
  | Ack
  | Error;
export type Id = string;
export type ShortText = string;
/**
 * @maxItems 1000
 */
export type TextList = string[];
export type DownloadUrl = string;
export type WebUrl = string;
/**
 * @maxItems 200
 */
export type Cookies = Cookie[];
export type MediaKind = 'hls' | 'dash' | 'direct';

/**
 * Extension to Kasi: the version handshake. protocolMax must not be below protocolMin (checked by the C# parser).
 */
export interface Hello {
  v: 1;
  id: Id;
  type: 'hello';
  client: 'chrome' | 'edge' | 'firefox';
  extensionVersion: ShortText;
  protocolMin: number;
  protocolMax: number;
}
/**
 * Kasi to extension: interception rules, bypass key, enabled flag.
 */
export interface Rules {
  v: 1;
  id: Id;
  type: 'rules';
  enabled: boolean;
  extensions: TextList;
  mimeTypes: TextList;
  excludedSites: TextList;
  bypassKey: 'alt' | 'ctrl' | 'shift' | 'none';
  ackTimeoutMs: number;
}
/**
 * Extension to Kasi: one intercepted or context-menu download.
 */
export interface AddDownload {
  v: 1;
  id: Id;
  type: 'addDownload';
  url: DownloadUrl;
  finalUrl: DownloadUrl;
  referrer?: WebUrl;
  filename: string;
  mime: string;
  userAgent: string;
  cookies: Cookies;
}
/**
 * A cookie the browser holds for the download. The value is a secret and is never logged.
 */
export interface Cookie {
  name: string;
  value: string;
  domain: string;
  path: string;
  secure: boolean;
  httpOnly: boolean;
}
/**
 * Extension to Kasi: the links chosen in the picker.
 */
export interface AddBatch {
  v: 1;
  id: Id;
  type: 'addBatch';
  referrer: WebUrl;
  /**
   * @minItems 1
   * @maxItems 100000
   */
  links: [Link, ...Link[]];
  cookies: Cookies;
  userAgent: string;
}
export interface Link {
  url: DownloadUrl;
  filename?: string;
}
/**
 * Extension to Kasi: a manifest or direct media file found in a tab.
 */
export interface MediaDetected {
  v: 1;
  id: Id;
  type: 'mediaDetected';
  tabId: number;
  pageUrl: WebUrl;
  kind: MediaKind;
  url: WebUrl;
  title?: string;
}
/**
 * Extension to Kasi: the user chose a stream from the popup or overlay.
 */
export interface CaptureMedia {
  v: 1;
  id: Id;
  type: 'captureMedia';
  tabId: number;
  url: WebUrl;
  kind: MediaKind;
  cookies: Cookies;
  referrer: WebUrl;
  userAgent: string;
}
/**
 * Kasi to extension: open the referrer page and watch for a matching download.
 */
export interface RefreshLink {
  v: 1;
  id: Id;
  type: 'refreshLink';
  jobId: Id;
  referrerUrl: WebUrl;
  matchHint?: string;
}
/**
 * Extension to Kasi: the fresh URL and cookies for a paused job.
 */
export interface LinkRefreshed {
  v: 1;
  id: Id;
  type: 'linkRefreshed';
  jobId: Id;
  url: DownloadUrl;
  cookies: Cookies;
}
/**
 * Extension to Kasi: show Kasi's settings window. section defaults to browser. Answered with ack.
 */
export interface OpenSettings {
  v: 1;
  id: Id;
  type: 'openSettings';
  section?: 'general' | 'browser';
}
/**
 * The result for a correlation id (both directions).
 */
export interface Ack {
  v: 1;
  id: Id;
  type: 'ack';
  ok: true;
  detail?: string;
}
/**
 * A failure for a correlation id (both directions). The detail never carries the offending input.
 */
export interface Error {
  v: 1;
  id: Id;
  type: 'error';
  code:
    | 'INVALID_MESSAGE'
    | 'UNSUPPORTED_VERSION'
    | 'APP_NOT_RUNNING'
    | 'APP_NOT_INSTALLED'
    | 'INTERNAL';
  detail?: string;
}
