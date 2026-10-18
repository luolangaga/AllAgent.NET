// Image attachments for the web composer: clipboard and drop payloads arrive as
// browser File objects, so they are normalized here into the host's
// ImageAttachment shape (name + media type + base64) before they reach the bridge.

import type { ImageAttachment } from '@/types'

/** Mirrors the host limits in ImageAttachment so failures are reported before the send. */
export const MAX_IMAGES = 8
export const MAX_IMAGE_BYTES = 4 * 1024 * 1024
/**
 * Total budget for one message. The bridge frame that carries chat.startStream is
 * capped at 16 MB of JSON, and base64 costs ~1.37x the raw bytes, so 8 MB of
 * images stays comfortably inside it.
 */
export const MAX_TOTAL_BYTES = 8 * 1024 * 1024

const MAX_DIMENSION = 2000
const MAX_ENCODED_BYTES = 3_300_000
const MEDIA_TYPES = ['image/png', 'image/jpeg', 'image/gif', 'image/webp']

const EXTENSIONS: Record<string, string> = {
  'image/png': '.png',
  'image/jpeg': '.jpg',
  'image/gif': '.gif',
  'image/webp': '.webp',
}

export function dataUrl(image: ImageAttachment): string {
  return `data:${image.mediaType};base64,${image.base64}`
}

/** Decoded size of a base64 payload without materializing the bytes. */
export function base64Bytes(base64: string): number {
  const padding = base64.endsWith('==') ? 2 : base64.endsWith('=') ? 1 : 0
  return Math.max(0, Math.floor((base64.length * 3) / 4) - padding)
}

export function totalBytes(images: ImageAttachment[]): number {
  return images.reduce((sum, image) => sum + base64Bytes(image.base64), 0)
}

export function formatBytes(bytes: number): string {
  return bytes >= 1024 * 1024
    ? `${(bytes / 1024 / 1024).toFixed(1)} MB`
    : `${Math.max(1, Math.round(bytes / 1024))} KB`
}

export function assertCapacity(images: ImageAttachment[]): void {
  if (images.length > MAX_IMAGES) throw new Error(`每条消息最多 ${MAX_IMAGES} 张图片。`)
  const total = totalBytes(images)
  if (total > MAX_TOTAL_BYTES) throw new Error(`图片总量不能超过 ${formatBytes(MAX_TOTAL_BYTES)}，请先移除一部分再发送。`)
}

/** Image payloads out of a paste or drop event, ignoring text and other file kinds. */
export function imageFilesFrom(data: DataTransfer | null): File[] {
  if (!data) return []
  const files: File[] = []
  for (const item of Array.from(data.items ?? [])) {
    if (item.kind !== 'file' || !item.type.toLowerCase().startsWith('image/')) continue
    const file = item.getAsFile()
    if (file) files.push(file)
  }
  if (!files.length) files.push(...droppedFiles(data).filter(file => file.type.toLowerCase().startsWith('image/')))
  return files
}

export function droppedFiles(data: DataTransfer | null): File[] {
  return data ? Array.from(data.files ?? []) : []
}

/**
 * Normalizes one clipboard or picked image: verified, bounded to 2000 px and
 * re-encoded when needed, so a 4K screenshot stays well inside the bridge frame.
 */
export async function prepareImage(file: File): Promise<ImageAttachment> {
  const declared = file.type.toLowerCase()
  if (declared && !MEDIA_TYPES.includes(declared)) throw new Error(`${file.name || '该文件'}不是支持的图片格式，请使用 PNG、JPEG、GIF 或 WebP。`)
  const bytes = new Uint8Array(await file.arrayBuffer())
  const mediaType = sniffMediaType(bytes) ?? declared
  if (!mediaType) throw new Error('无法识别的图片格式，请使用 PNG、JPEG、GIF 或 WebP。')
  if (bytes.length > MAX_IMAGE_BYTES) throw new Error(`${file.name || '图片'} 超过 ${formatBytes(MAX_IMAGE_BYTES)}。`)

  const decoded = await decodeImage(bytes, mediaType)
  try {
    const scale = Math.min(1, MAX_DIMENSION / Math.max(decoded.width, decoded.height))
    if (scale === 1 && bytes.length <= MAX_ENCODED_BYTES) {
      return { name: named(file.name, mediaType), mediaType, base64: toBase64(bytes) }
    }
    let width = Math.max(1, Math.round(decoded.width * scale))
    let height = Math.max(1, Math.round(decoded.height * scale))
    for (let attempt = 0; ; attempt++) {
      const canvas = render(decoded.source, width, height)
      const png = await encode(canvas, 'image/png')
      if (png && png.size <= MAX_ENCODED_BYTES) return await toAttachment(file.name, png, 'image/png')
      const jpeg = await encode(canvas, 'image/jpeg', attempt >= 3 ? 0.7 : 0.86)
      if (jpeg && jpeg.size <= MAX_ENCODED_BYTES) return await toAttachment(file.name, jpeg, 'image/jpeg')
      if (attempt >= 5 || (width <= 2 && height <= 2)) throw new Error('图片过大，无法压缩到传输限制内，请先裁剪或缩小。')
      width = Math.max(1, Math.round(width * 0.75))
      height = Math.max(1, Math.round(height * 0.75))
    }
  } finally {
    decoded.release?.()
  }
}

interface Decoded {
  source: CanvasImageSource
  width: number
  height: number
  release?: () => void
}

async function decodeImage(bytes: Uint8Array, mediaType: string): Promise<Decoded> {
  const blob = new Blob([bytes as BlobPart], { type: mediaType })
  if (typeof createImageBitmap === 'function') {
    try {
      const bitmap = await createImageBitmap(blob)
      return { source: bitmap, width: bitmap.width, height: bitmap.height, release: () => bitmap.close() }
    } catch { /* fall through to the <img> decoder */ }
  }
  const url = URL.createObjectURL(blob)
  const image = new Image()
  try {
    await new Promise<void>((resolve, reject) => {
      image.onload = () => resolve()
      image.onerror = () => reject(new Error('图片数据损坏或不完整，请重新导出后再试。'))
      image.src = url
    })
  } catch (error) {
    URL.revokeObjectURL(url)
    throw error
  }
  return { source: image, width: image.naturalWidth, height: image.naturalHeight, release: () => URL.revokeObjectURL(url) }
}

function render(source: CanvasImageSource, width: number, height: number): HTMLCanvasElement {
  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  const context = canvas.getContext('2d')
  if (!context) throw new Error('当前浏览器不支持图片缩放，请改用较小的截图。')
  context.drawImage(source, 0, 0, width, height)
  return canvas
}

function encode(canvas: HTMLCanvasElement, type: string, quality?: number): Promise<Blob | null> {
  return new Promise(resolve => canvas.toBlob(blob => resolve(blob), type, quality))
}

async function toAttachment(name: string, blob: Blob, mediaType: string): Promise<ImageAttachment> {
  return { name: named(name, mediaType), mediaType, base64: toBase64(new Uint8Array(await blob.arrayBuffer())) }
}

/** Keeps the human-readable stem and corrects the extension to the encoded format. */
function named(name: string, mediaType: string): string {
  const extension = EXTENSIONS[mediaType] ?? ''
  const stem = (name || 'image').replace(/\.[a-z0-9]+$/i, '').slice(0, 80)
  return `${stem || 'image'}${extension}`
}

function sniffMediaType(bytes: Uint8Array): string | null {
  const ascii = (start: number, length: number) => String.fromCharCode(...bytes.subarray(start, start + length))
  if (bytes.length >= 8 && bytes[0] === 137 && ascii(1, 3) === 'PNG') return 'image/png'
  if (bytes.length >= 3 && bytes[0] === 255 && bytes[1] === 216 && bytes[2] === 255) return 'image/jpeg'
  if (bytes.length >= 6 && (ascii(0, 6) === 'GIF87a' || ascii(0, 6) === 'GIF89a')) return 'image/gif'
  if (bytes.length >= 12 && ascii(0, 4) === 'RIFF' && ascii(8, 4) === 'WEBP') return 'image/webp'
  return null
}

/** Chunked so multi-megabyte payloads never overflow the argument stack. */
function toBase64(bytes: Uint8Array): string {
  let binary = ''
  const chunk = 0x8000
  for (let offset = 0; offset < bytes.length; offset += chunk) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + chunk))
  }
  return btoa(binary)
}
