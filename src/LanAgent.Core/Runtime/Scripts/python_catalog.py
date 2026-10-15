import importlib.metadata as metadata, importlib.util, json, pathlib, re, sys

root = pathlib.Path(sys.argv[1])
defaults = {'requests', 'beautifulsoup4', 'pillow', 'pyyaml', 'numpy', 'pandas', 'openpyxl', 'python-docx', 'pypdf'}
known = {'requests': {'requests': 'requests'}, 'beautifulsoup4': {'bs4': 'bs4', 'BeautifulSoup': 'bs4:BeautifulSoup'},
         'pillow': {'PIL': 'PIL', 'Image': 'PIL.Image'}, 'pyyaml': {'yaml': 'yaml'},
         'numpy': {'np': 'numpy', 'numpy': 'numpy'}, 'pandas': {'pd': 'pandas', 'pandas': 'pandas'},
         'openpyxl': {'openpyxl': 'openpyxl'}, 'python-docx': {'docx': 'docx', 'Document': 'docx:Document'},
         'pypdf': {'pypdf': 'pypdf', 'PdfReader': 'pypdf:PdfReader', 'PdfWriter': 'pypdf:PdfWriter'}}
packages = []
aliases = {'Path': 'pathlib:Path', 'json': 'json', 'os': 'os', 'sys': 'sys', 're': 're', 'math': 'math', 'datetime': 'datetime', 'subprocess': 'subprocess'}
for distribution in metadata.distributions():
    installed_files = distribution.files
    # An incomplete installation can leave dist-info behind. Do not advertise it.
    if not installed_files or any(not distribution.locate_file(file).is_file()
                                  for file in installed_files if file.suffix not in {'.pyc', '.pyo'}):
        continue
    name = distribution.metadata.get('Name', '')
    if not name:
        continue
    key = re.sub(r'[-_.]+', '-', name).lower()
    if not re.fullmatch(r'[a-z0-9][a-z0-9-]*', key):
        continue
    module_aliases = known.get(key, {})
    if not module_aliases:
        modules = set((distribution.read_text('top_level.txt') or '').splitlines())
        for file in distribution.files or []:
            if file.suffix == '.py' and file.parts:
                modules.add(file.parts[0].removesuffix('.py'))
        for module in sorted(modules):
            if module.isidentifier() and not module.startswith('_') and module not in aliases and module not in {'call_tool', 'print', 'exec', 'compile', 'input'}:
                module_aliases[module] = module
    module_aliases = {alias: target for alias, target in module_aliases.items()
                      if importlib.util.find_spec(target.partition(':')[0].split('.')[0]) is not None}
    if not module_aliases:
        continue
    for alias, target in module_aliases.items():
        aliases.setdefault(alias, target)
    urls = distribution.metadata.get_all('Project-URL') or []
    document = None
    if key not in defaults and key not in {'pip', 'setuptools', 'wheel'}:
        folder = root / 'python' / 'docs' / key
        folder.mkdir(parents=True, exist_ok=True)
        document = 'python/docs/' + key + '/README.md'
        description = distribution.metadata.get_payload() or ''
        text = '# ' + name + ' ' + distribution.version + '\n\n' + '\n'.join(urls) + '\n\n' + str(description)
        if not description:
            text += '\nThe installed wheel contains no usage documentation. Consult its official documentation links; do not guess its API.\n'
        (root / document).write_text(text, encoding='utf-8')
    packages.append({'name': name, 'version': distribution.version, 'aliases': module_aliases, 'document': document, 'urls': urls, 'summary': distribution.metadata.get('Summary', '')})
packages.sort(key=lambda item: item['name'].lower())
registry = {'python': sys.executable, 'packages': packages, 'aliases': aliases}
(root / 'python' / 'libraries.json.tmp').write_text(json.dumps(registry, ensure_ascii=False, indent=2), encoding='utf-8')
(root / 'python' / 'libraries.json.tmp').replace(root / 'python' / 'libraries.json')
lines = ['# LanAgent installed host Python libraries', '',
         'Generated from installed packages in the shared host environment. These packages are not automatically importable in the Hyperlight guest, including full-access mode.', '',
         'Host Python executable: ' + json.dumps(sys.executable, ensure_ascii=False), '',
         'Use explicit imports in host scripts launched through call_tool("run_shell", exe=<host Python executable>, args=["-c", <script>]). Host command permissions still apply. Import names below are references, not automatically injected globals.', '',
         'The guest separately bundles base64, struct and UTF-16 codecs for pure in-memory transformations; these do not require pip installation.', '',
         '| Package | Version | Import names / suggested aliases | Documentation |', '| --- | --- | --- | --- |']
for package in packages:
    lines.append('| ' + package['name'] + ' | ' + package['version'] + ' | ' + ', '.join(package['aliases']) + ' | ' + (package['document'] or 'Built-in / no extra documentation') + ' |')
(root / 'pip.md.tmp').write_text('\n'.join(lines) + '\n', encoding='utf-8')
(root / 'pip.md.tmp').replace(root / 'pip.md')
print(json.dumps({'packages': len(packages), 'python': sys.executable}, ensure_ascii=False))
