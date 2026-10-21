import type { AddDownload, Cookie } from '../types/protocol';

export interface DownloadFields {
  url: string;
  finalUrl?: string;
  referrer?: string;
  filename?: string;
  mime?: string;
}

const MAX_NAME = 255;

// A cut keeps the extension, which is what the rules and Kasi's category guess use.
function cut(name: string): string {
  if (name.length <= MAX_NAME) return name;
  const dot = name.lastIndexOf('.');
  const extension = dot > 0 && name.length - dot <= 32 ? name.slice(dot) : '';
  return name.slice(0, MAX_NAME - extension.length) + extension;
}

// Only a hint: Kasi's engine resolves the real name from the response (plan 2.6).
export function fileNameHint(fields: DownloadFields): string {
  const named = fields.filename?.split(/[\\/]/).pop();
  if (named) return cut(named);
  try {
    const segment = new URL(fields.finalUrl || fields.url).pathname.split('/').pop() ?? '';
    const decoded = decodeURIComponent(segment);
    if (decoded) return cut(decoded);
  } catch {
    // fall through to the default
  }
  return 'download';
}

function webReferrer(referrer: string | undefined): string | undefined {
  if (!referrer || referrer.length > 8192) return undefined;
  try {
    const { protocol } = new URL(referrer);
    return protocol === 'http:' || protocol === 'https:' ? referrer : undefined;
  } catch {
    return undefined;
  }
}

export function buildAddDownload(
  id: string,
  fields: DownloadFields,
  userAgent: string,
  cookies: Cookie[],
): AddDownload {
  const message: AddDownload = {
    v: 1,
    id,
    type: 'addDownload',
    url: fields.url,
    finalUrl: fields.finalUrl || fields.url,
    filename: fileNameHint(fields),
    mime: (fields.mime || 'application/octet-stream').slice(0, 255),
    userAgent: userAgent.slice(0, 1024),
    cookies,
  };
  const referrer = webReferrer(fields.referrer);
  if (referrer) message.referrer = referrer;
  return message;
}
