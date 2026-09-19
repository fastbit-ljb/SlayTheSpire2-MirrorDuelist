"""Minimal Godot 4.x PCK reader/writer.

Reader parses the shipped SlayTheSpire2.pck (Godot 4.5, PCK format v3: directory
at end, offsets relative to file_base). Writer builds mod PCKs in the same v3
layout, unencrypted.
"""
import hashlib
import struct

PACK_HEADER_MAGIC = 0x43504447  # "GDPC" little-endian
PACK_FORMAT_VERSION = 3
PACK_REL_FILEBASE = 1 << 1  # vanilla 4.5 enum: DIR_ENCRYPTED=1<<0, REL_FILEBASE=1<<1


def _pad4(n):
    return (4 - n % 4) % 4


def read_pck(path):
    with open(path, "rb") as f:
        data = f.read()
    magic, version, major, minor, patch = struct.unpack_from("<IIIII", data, 0)
    assert magic == PACK_HEADER_MAGIC, hex(magic)
    if version == 3:
        pack_flags, file_base, dir_offset = struct.unpack_from("<IQQ", data, 20)
        count = struct.unpack_from("<I", data, dir_offset)[0]
        off = dir_offset + 4
        files = {}
        for _ in range(count):
            plen = struct.unpack_from("<I", data, off)[0]
            off += 4
            path = data[off:off + plen].rstrip(b"\0").decode("utf-8")
            off += plen + _pad4(plen)
            foff, fsize = struct.unpack_from("<QQ", data, off)
            off += 16
            md5 = data[off:off + 16]
            off += 16
            flags = struct.unpack_from("<I", data, off)[0]
            off += 4
            files[path] = (foff + file_base, fsize, md5, flags)
        return {"version": version, "engine": f"{major}.{minor}.{patch}",
                "pack_flags": pack_flags, "file_base": file_base,
                "dir_offset": dir_offset, "count": count, "files": files, "data": data}
    raise ValueError(f"unsupported pck version {version}")


def extract(info, path):
    foff, fsize, _, _ = info["files"][path]
    return info["data"][foff:foff + fsize]


def _entry_bytes(path):
    p = path.encode("utf-8")
    padded = p + b"\0" * _pad4(len(p))
    return struct.pack("<I", len(padded)) + padded


def write_pck(out_path, files, engine=(4, 5, 1)):
    """files: dict 'res://path' -> bytes. Unencrypted v3, directory at end."""
    header = bytearray(struct.pack("<IIIII", PACK_HEADER_MAGIC, PACK_FORMAT_VERSION,
                                   engine[0], engine[1], engine[2]))
    header += struct.pack("<I", PACK_REL_FILEBASE)  # pack_flags
    header += bytes(112 - len(header))              # file_base + dir_offset + reserved
    file_base = len(header)                         # payload starts right after the header

    payload = bytearray()
    offsets = []
    for path in sorted(files):
        offsets.append((path, len(payload), len(files[path])))
        payload += files[path]
    data_size = len(payload)

    directory = bytearray(struct.pack("<I", len(files)))
    for path, off, size in offsets:
        p = path.encode("utf-8")
        padded = p + b"\0" * _pad4(len(p))
        directory += struct.pack("<I", len(padded)) + padded
        directory += struct.pack("<QQ", off, size)
        directory += hashlib.md5(files[path]).digest()
        directory += struct.pack("<I", 0)
    directory = bytes(directory)

    dir_offset = len(header) + data_size
    struct.pack_into("<Q", header, 24, file_base)
    struct.pack_into("<Q", header, 32, dir_offset)

    with open(out_path, "wb") as f:
        f.write(bytes(header) + bytes(payload) + directory)
    return out_path


if __name__ == "__main__":
    info = read_pck(r"D:\L\Game\Steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.pck")
    print(f"engine {info['engine']} v{info['version']} flags {info['pack_flags']} files {info['count']} dir@{info['dir_offset']}")
    files = info["files"]
    for probe in ["res://images/relics/akabeko.png", "res://images/relics/akabeko.ctex",
                  "res://images/relics/akabeko.png.import",
                  "res://images/atlases/relic_atlas.sprites/akabeko.tres",
                  "res://images/atlases/relic_outline_atlas.sprites/akabeko.tres",
                  "res://images/powers/missing_power.png.import",
                  "res://localization/eng/relics.json",
                  "res://localization/eng/powers.json"]:
        if probe in files:
            print(f"OK       {probe} size={files[probe][1]}")
        else:
            print(f"MISSING  {probe}")
    import collections
    raw_pngs = [p for p in files if p.endswith(".png")]
    ctexs = [p for p in files if p.endswith(".ctex")]
    print("raw .png:", len(raw_pngs), "| .ctex:", len(ctexs))
