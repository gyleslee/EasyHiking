"""Read-only Inno 6.7.3 setup metadata inspection using its published structures.
Does not extract application files or execute the installer.
"""
import json, lzma, pathlib, struct, sys, zlib

def inspect(path):
    path = pathlib.Path(path)
    with path.open('rb') as f:
        prefix = f.read(1024 * 1024)
        i = prefix.index(b'rDlPtS\xcd\xe6\xd7\x7b\x0b\x2a')
        assert struct.unpack_from('<I', prefix, i + 12)[0] == 2
        offset = struct.unpack_from('<q', prefix, i + 40)[0]
        f.seek(offset)
        assert f.read(64).rstrip(b'\0') == b'Inno Setup Setup Data (6.7.0)'
        crc = struct.unpack('<I', f.read(4))[0]
        encryption = f.read(49)
        assert zlib.crc32(encryption) == crc, 'Encryption header CRC'
        assert encryption[0] == 0, 'Encrypted metadata not supported'
        crc = struct.unpack('<I', f.read(4))[0]
        header = f.read(9)
        assert zlib.crc32(header) == crc, 'Block header CRC'
        left, compressed = struct.unpack('<qB', header)
        assert 0 < left < 32 * 1024 * 1024
        chunks = []
        while left:
            crc = struct.unpack('<I', f.read(4))[0]
            left -= 4
            chunk = f.read(min(4096, left))
            assert zlib.crc32(chunk) == crc, 'Data chunk CRC'
            chunks.append(chunk)
            left -= len(chunk)
        data = b''.join(chunks)
    if compressed:
        prop, dictionary = struct.unpack_from('<BI', data)
        lc, remaining = prop % 9, prop // 9
        lp, pb = remaining % 5, remaining // 5
        decoder = lzma.LZMADecompressor(format=lzma.FORMAT_RAW, filters=[{
            'id': lzma.FILTER_LZMA1, 'dict_size': dictionary, 'lc': lc, 'lp': lp, 'pb': pb
        }])
        data = decoder.decompress(data[5:], max_length=32 * 1024 * 1024)
    cursor, strings = 0, []
    for n in range(39 + 4):
        length = struct.unpack_from('<I', data, cursor)[0]
        cursor += 4
        value = data[cursor:cursor + length]
        cursor += length
        strings.append(value.decode('utf-16le') if n < 39 else value)
    cursor += 17 * 4
    win, nt, service_pack = struct.unpack_from('<IIH', data, cursor)
    build, minor, major = struct.unpack('<HBB', struct.pack('<I', nt))
    return {
        'file': str(path), 'bytes': path.stat().st_size,
        'productVersion': strings[9], 'allowedArchitecture': strings[30],
        'minimumWindows': f'{major}.{minor}.{build}', 'servicePack': service_pack,
        'newDiagnosticTextPresent': '此安装包需要64位x64 Windows 10'.encode('utf-16le') in data,
        'offset0': offset, 'metadataCrcValidated': True,
    }

print(json.dumps([inspect(p) for p in sys.argv[1:]], ensure_ascii=False, indent=2))
