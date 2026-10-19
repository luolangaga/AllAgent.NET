// Base64 ↔ bytes for terminal I/O: the bridge is text-only, PTY traffic is raw bytes.
// xterm.js decodes UTF-8 itself, so both directions stay byte-exact.

export function base64ToBytes(base64: string): Uint8Array {
  const binary = atob(base64)
  const bytes = new Uint8Array(binary.length)
  for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index)
  return bytes
}

export function bytesToBase64(bytes: Uint8Array): string {
  let binary = ''
  const chunk = 0x8000
  for (let index = 0; index < bytes.length; index += chunk) {
    binary += String.fromCharCode(...bytes.subarray(index, index + chunk))
  }
  return btoa(binary)
}

export function utf8ToBase64(text: string): string {
  return bytesToBase64(new TextEncoder().encode(text))
}

export function base64ToText(base64: string): string {
  return new TextDecoder().decode(base64ToBytes(base64))
}
