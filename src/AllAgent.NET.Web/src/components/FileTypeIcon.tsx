import iconC from 'material-icon-theme/icons/c.svg?raw'
import iconConsole from 'material-icon-theme/icons/console.svg?raw'
import iconCpp from 'material-icon-theme/icons/cpp.svg?raw'
import iconCsharp from 'material-icon-theme/icons/csharp.svg?raw'
import iconCss from 'material-icon-theme/icons/css.svg?raw'
import iconDocker from 'material-icon-theme/icons/docker.svg?raw'
import iconDocument from 'material-icon-theme/icons/document.svg?raw'
import iconFile from 'material-icon-theme/icons/file.svg?raw'
import iconFolder from 'material-icon-theme/icons/folder.svg?raw'
import iconFolderOpen from 'material-icon-theme/icons/folder-open.svg?raw'
import iconFont from 'material-icon-theme/icons/font.svg?raw'
import iconGit from 'material-icon-theme/icons/git.svg?raw'
import iconGo from 'material-icon-theme/icons/go.svg?raw'
import iconHtml from 'material-icon-theme/icons/html.svg?raw'
import iconImage from 'material-icon-theme/icons/image.svg?raw'
import iconJava from 'material-icon-theme/icons/java.svg?raw'
import iconJavascript from 'material-icon-theme/icons/javascript.svg?raw'
import iconJson from 'material-icon-theme/icons/json.svg?raw'
import iconKotlin from 'material-icon-theme/icons/kotlin.svg?raw'
import iconLicense from 'material-icon-theme/icons/license.svg?raw'
import iconLock from 'material-icon-theme/icons/lock.svg?raw'
import iconMarkdown from 'material-icon-theme/icons/markdown.svg?raw'
import iconNodejs from 'material-icon-theme/icons/nodejs.svg?raw'
import iconNpm from 'material-icon-theme/icons/npm.svg?raw'
import iconPdf from 'material-icon-theme/icons/pdf.svg?raw'
import iconPhp from 'material-icon-theme/icons/php.svg?raw'
import iconPowershell from 'material-icon-theme/icons/powershell.svg?raw'
import iconPython from 'material-icon-theme/icons/python.svg?raw'
import iconReactTs from 'material-icon-theme/icons/react_ts.svg?raw'
import iconReadme from 'material-icon-theme/icons/readme.svg?raw'
import iconRuby from 'material-icon-theme/icons/ruby.svg?raw'
import iconRust from 'material-icon-theme/icons/rust.svg?raw'
import iconSvg from 'material-icon-theme/icons/svg.svg?raw'
import iconTypescript from 'material-icon-theme/icons/typescript.svg?raw'
import iconVideo from 'material-icon-theme/icons/video.svg?raw'
import iconXml from 'material-icon-theme/icons/xml.svg?raw'
import iconYaml from 'material-icon-theme/icons/yaml.svg?raw'
import iconZip from 'material-icon-theme/icons/zip.svg?raw'

// A curated slice of the VSCode Material Icon Theme, imported as raw SVG so the bundle only
// carries the icons the workspace actually renders.
const ICONS: Record<string, string> = {
  c: iconC,
  console: iconConsole,
  cpp: iconCpp,
  csharp: iconCsharp,
  css: iconCss,
  docker: iconDocker,
  document: iconDocument,
  file: iconFile,
  folder: iconFolder,
  'folder-open': iconFolderOpen,
  font: iconFont,
  git: iconGit,
  go: iconGo,
  html: iconHtml,
  image: iconImage,
  java: iconJava,
  javascript: iconJavascript,
  json: iconJson,
  kotlin: iconKotlin,
  license: iconLicense,
  lock: iconLock,
  markdown: iconMarkdown,
  nodejs: iconNodejs,
  npm: iconNpm,
  pdf: iconPdf,
  php: iconPhp,
  powershell: iconPowershell,
  python: iconPython,
  react_ts: iconReactTs,
  readme: iconReadme,
  ruby: iconRuby,
  rust: iconRust,
  svg: iconSvg,
  typescript: iconTypescript,
  video: iconVideo,
  xml: iconXml,
  yaml: iconYaml,
  zip: iconZip,
}

const EXTENSIONS: Record<string, string> = {
  ts: 'typescript', mts: 'typescript', cts: 'typescript',
  tsx: 'react_ts', jsx: 'react_ts',
  js: 'javascript', mjs: 'javascript', cjs: 'javascript',
  cs: 'csharp', csx: 'csharp',
  py: 'python', pyi: 'python', pyw: 'python',
  ps1: 'powershell', psm1: 'powershell', psd1: 'powershell',
  go: 'go', rs: 'rust', java: 'java', kt: 'kotlin', kts: 'kotlin',
  c: 'c', h: 'c', cpp: 'cpp', cc: 'cpp', cxx: 'cpp', hpp: 'cpp', hh: 'cpp',
  php: 'php', rb: 'ruby', rake: 'ruby',
  json: 'json', jsonc: 'json', json5: 'json',
  yaml: 'yaml', yml: 'yaml', toml: 'yaml', ini: 'yaml', conf: 'yaml', config: 'yaml',
  css: 'css', scss: 'css', sass: 'css', less: 'css', postcss: 'css',
  html: 'html', htm: 'html', vue: 'html', svelte: 'html', astro: 'html',
  md: 'markdown', markdown: 'markdown', mdx: 'markdown',
  xml: 'xml', xaml: 'xml', csproj: 'xml', fsproj: 'xml', vbproj: 'xml', props: 'xml', targets: 'xml', sln: 'xml', slnx: 'xml', svg: 'svg',
  png: 'image', jpg: 'image', jpeg: 'image', gif: 'image', webp: 'image', bmp: 'image', ico: 'image', avif: 'image', tiff: 'image',
  mp4: 'video', webm: 'video', mov: 'video', avi: 'video', mkv: 'video',
  pdf: 'pdf',
  zip: 'zip', gz: 'zip', tar: 'zip', '7z': 'zip', rar: 'zip',
  ttf: 'font', otf: 'font', woff: 'font', woff2: 'font', eot: 'font',
  sh: 'console', bash: 'console', zsh: 'console', fish: 'console', bat: 'console', cmd: 'console',
  txt: 'document', log: 'document', csv: 'document', rst: 'document', adoc: 'document',
  lock: 'lock',
}

const FILENAMES: Record<string, string> = {
  'package.json': 'npm',
  'package-lock.json': 'npm',
  'npm-shrinkwrap.json': 'npm',
  'node_modules': 'nodejs',
  'tsconfig.json': 'typescript',
  'jsconfig.json': 'javascript',
  'dockerfile': 'docker',
  'docker-compose.yml': 'docker',
  '.gitignore': 'git',
  '.gitattributes': 'git',
  '.gitmodules': 'git',
  'go.mod': 'go',
  'go.sum': 'go',
  'cargo.toml': 'rust',
  'cargo.lock': 'rust',
  'requirements.txt': 'python',
  'pyproject.toml': 'python',
  'pipfile': 'python',
  'composer.json': 'php',
  'gemfile': 'ruby',
}

function iconKey(name: string): string {
  const lower = name.toLowerCase()
  if (FILENAMES[lower]) return FILENAMES[lower]
  if (lower.startsWith('readme')) return 'readme'
  if (lower.startsWith('license') || lower.startsWith('licence')) return 'license'
  if (lower.startsWith('dockerfile')) return 'docker'
  const dot = lower.lastIndexOf('.')
  const extension = dot > 0 ? lower.slice(dot + 1) : lower
  return EXTENSIONS[extension] ?? 'file'
}

/**
 * VSCode-style file type icon for the workspace tree, the Git change list and commit details.
 * Directories use the folder / folder-open glyphs.
 */
export function FileTypeIcon({ name, directory = false, open = false, size = 14, className }: {
  name: string
  directory?: boolean
  open?: boolean
  size?: number
  className?: string
}) {
  const svg = directory ? (open ? ICONS['folder-open'] : ICONS.folder) : ICONS[iconKey(name)] ?? ICONS.file
  return <span
    aria-hidden
    className={`file-type-icon${className ? ` ${className}` : ''}`}
    dangerouslySetInnerHTML={{ __html: svg }}
    style={{ width: size, height: size, flexBasis: size }}
  />
}
