// SPDX-License-Identifier: LGPL-3.0-or-later
// This project has no @types/node (and gains no dependency for a test): vitest runs on Node, so
// the two imports below resolve at run time and are typed `any` here.
// @ts-expect-error TS2307 — no Node type declarations
import { readdirSync, readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { viewerShots } from './viewerShots'

/**
 * Guards the screenshots of `docs/viewer`: every entry of `viewerShots` has
 * its PNG in `public/viewer/`, the declared size is the file's own (the
 * `width`/`height` attributes reserve the box, so a stale number is a layout
 * shift), and no PNG sits in that folder without an entry (a re-capture that
 * renamed a shot would otherwise ship a dead file). Reads the PNG header
 * (IHDR: width and height, big-endian, at byte 16 and 20).
 */
// Relative to the working directory: `npm test` runs in `web/`.
const dir = 'public/viewer/'
const PNG_SIGNATURE = '89504e470d0a1a0a'

describe('viewer screenshots', () => {
  for (const [name, shot] of Object.entries(viewerShots)) {
    it(`${name}: ${shot.file} exists and has the declared size`, () => {
      const bytes = readFileSync(dir + shot.file)
      expect(bytes.subarray(0, 8).toString('hex')).toBe(PNG_SIGNATURE)
      expect({ width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) }).toEqual({
        width: shot.width,
        height: shot.height,
      })
      expect(shot.alt.length).toBeGreaterThan(40)
    })
  }

  it('has no PNG without an entry, and stays within the weight budget', () => {
    const declared = new Set(Object.values(viewerShots).map((s) => s.file))
    const files = readdirSync(dir).filter((f: string) => f.endsWith('.png'))
    expect(files.filter((f: string) => !declared.has(f))).toEqual([])
    const total = files.reduce((sum: number, f: string) => sum + readFileSync(dir + f).length, 0)
    expect(total).toBeLessThan(1_500_000)
  })
})
