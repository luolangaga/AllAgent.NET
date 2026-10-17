# Bundled guest Python libraries

The `.py` files are unchanged CPython 3.14.0 standard-library sources from
https://github.com/python/cpython/tree/v3.14.0/Lib. The bundled Hyperlight
Python 0.7.0 guest runs CPython 3.14.0 and already supplies the native
`binascii`, `_struct`, and `_codecs` implementations these sources use.

`GuestPythonLibraries` embeds these sources in the worker assembly and loads
missing modules into the isolated guest before each user program. Existing
modules in a custom guest are kept. UTF-16 codec lookup uses the standard
`encodings` package and codec implementations, without replacing `str.encode`
or calling a host conversion tool. Guest snapshot restore discards mutations
between requests. No host filesystem mount or host package import is added.

The full upstream license is retained in `LICENSE` and copied on build/publish.
