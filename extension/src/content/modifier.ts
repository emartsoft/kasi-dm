// Reports clicks made while Alt, Ctrl or Shift is held, so the background can honour the bypass key (KASI-BRW-003).
// Plain clicks send nothing. Untrusted (script-made) events are ignored: a page cannot fake a bypass.
import { MODIFIER_KIND, type ModifierMessage } from '../shared/bypass';

export interface ModifierEventLike {
  type: string;
  isTrusted: boolean;
  altKey: boolean;
  ctrlKey: boolean;
  shiftKey: boolean;
  key?: string;
  target: unknown;
}

interface AnchorLike {
  href?: unknown;
}

const POINTER_EVENTS = ['pointerdown', 'click', 'auxclick', 'keydown'];

function anchorHref(target: unknown): string | undefined {
  const element = target as { closest?: (selector: string) => AnchorLike | null } | null;
  const href = element?.closest?.('a[href]')?.href;
  return typeof href === 'string' && href !== '' ? href : undefined;
}

export function toModifierMessage(event: ModifierEventLike): ModifierMessage | undefined {
  if (!event.isTrusted) return undefined;
  if (event.type === 'keydown' && event.key !== 'Enter') return undefined;
  if (!event.altKey && !event.ctrlKey && !event.shiftKey) return undefined;
  const mods = { alt: event.altKey, ctrl: event.ctrlKey, shift: event.shiftKey };
  const href = anchorHref(event.target);
  return href === undefined ? { kind: MODIFIER_KIND, mods } : { kind: MODIFIER_KIND, mods, href };
}

export interface EventRoot {
  addEventListener(
    type: string,
    listener: (event: never) => void,
    options: { capture: boolean },
  ): void;
}

export function installModifierReporter(
  root: EventRoot,
  send: (message: ModifierMessage) => void,
): void {
  for (const type of POINTER_EVENTS) {
    root.addEventListener(
      type,
      (event: ModifierEventLike) => {
        const message = toModifierMessage(event);
        if (message) send(message);
      },
      { capture: true },
    );
  }
}
