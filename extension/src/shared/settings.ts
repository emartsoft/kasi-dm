// The extension's one local setting (plan m2-5 decision 4): interception on or off, flipped from the popup. Absent means on.
export const INTERCEPTION_KEY = 'interceptionEnabled';

import type { StorageAreaLike } from './rules-cache';

export async function readInterceptionEnabled(
  area: Pick<StorageAreaLike, 'get'>,
): Promise<boolean> {
  return (await area.get(INTERCEPTION_KEY))[INTERCEPTION_KEY] !== false;
}

export function writeInterceptionEnabled(area: Pick<StorageAreaLike, 'set'>, on: boolean) {
  return area.set({ [INTERCEPTION_KEY]: on });
}
