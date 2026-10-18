import { describe, expect, it, vi } from 'vitest'
import { MAX_IMAGE_BYTES, MAX_TOTAL_BYTES, assertCapacity, base64Bytes, dataUrl, formatBytes, imageFilesFrom, prepareImage, totalBytes } from '../src/lib/attachments'
import type { ImageAttachment } from '../src/types'

const Png = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4//8/AAX+Av4N70a4AAAAAElFTkSuQmCC'

function attachment(base64: string, name = 'pixel.png', mediaType = 'image/png'): ImageAttachment {
  return { name, mediaType, base64 }
}

/** jsdom's File predates Blob.arrayBuffer(), which every browser has. */
function fileFrom(bytes: Uint8Array, name: string, type: string): File {
  const created = new File([bytes as unknown as BlobPart], name, { type })
  Object.defineProperty(created, 'arrayBuffer', { value: async () => bytes.slice().buffer })
  return created
}

describe('attachment budget', () => {
  it('measures base64 payloads without decoding them', () => {
    expect(base64Bytes(Png)).toBe(Buffer.from(Png, 'base64').length)
    expect(base64Bytes('')).toBe(0)
    expect(base64Bytes(Buffer.from('hello!').toString('base64'))).toBe(6)
    expect(totalBytes([attachment(Png), attachment(Png)])).toBe(Buffer.from(Png, 'base64').length * 2)
    expect(formatBytes(4096)).toBe('4 KB')
    expect(formatBytes(2 * 1024 * 1024)).toBe('2.0 MB')
  })

  it('rejects batches the host would refuse', () => {
    expect(() => assertCapacity(Array.from({ length: 9 }, () => attachment(Png)))).toThrow(/最多 8 张/)
    const heavy = attachment(Buffer.alloc(MAX_TOTAL_BYTES + 1).toString('base64'))
    expect(() => assertCapacity([heavy])).toThrow(/图片总量/)
    expect(() => assertCapacity([attachment(Png)])).not.toThrow()
  })

  it('builds data URLs for the thumbnails', () => {
    expect(dataUrl(attachment(Png))).toBe(`data:image/png;base64,${Png}`)
  })
})

describe('image sources', () => {
  it('collects image files out of a paste or drop payload', () => {
    const image = new File(['x'], 'shot.png', { type: 'image/png' })
    const text = new File(['x'], 'notes.txt', { type: 'text/plain' })
    const data = {
      items: [
        { kind: 'string', type: 'text/plain', getAsFile: () => null },
        { kind: 'file', type: 'image/png', getAsFile: () => image },
      ],
      files: [image, text],
    } as unknown as DataTransfer
    expect(imageFilesFrom(data)).toEqual([image])

    // A drop that exposes only `files` still yields its images.
    const dropped = { items: [], files: [text, image] } as unknown as DataTransfer
    expect(imageFilesFrom(dropped)).toEqual([image])
    expect(imageFilesFrom(null)).toEqual([])
  })

  it('refuses payloads the host cannot decode', async () => {
    await expect(prepareImage(fileFrom(new TextEncoder().encode('x'), 'notes.txt', 'text/plain'))).rejects.toThrow(/不是支持的图片格式/)
    await expect(prepareImage(fileFrom(new Uint8Array(MAX_IMAGE_BYTES + 1), 'huge.png', 'image/png'))).rejects.toThrow(/超过/)
  })

  it('believes the bytes over the file name and keeps small images untouched', async () => {
    // A PNG-named screenshot that is really a JPEG: the host validates content
    // against the declared media type, so the sniffed type must win.
    const jpeg = new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46, 0x49, 0x46, 0x00, 0x01, 0x00, 0x00])
    vi.stubGlobal('createImageBitmap', async () => ({ width: 8, height: 8, close: () => {} }))
    try {
      const prepared = await prepareImage(fileFrom(jpeg, 'shot.png', 'image/png'))
      expect(prepared.mediaType).toBe('image/jpeg')
      expect(prepared.name).toBe('shot.jpg')
      expect(prepared.base64).toBe(Buffer.from(jpeg).toString('base64'))
    } finally { vi.unstubAllGlobals() }
  })
})
