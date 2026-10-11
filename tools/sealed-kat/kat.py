# An independent implementation of the sealed format, version 1, written from
# docs/design/18-encryption.md, to cross-check the C# known answer.
import hashlib, hmac, struct
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

def hkdf_extract(salt, ikm):
    return hmac.new(salt, ikm, hashlib.sha256).digest()

def hkdf_expand(prk, info, length):
    out, block, counter = b"", b"", 1
    while len(out) < length:
        block = hmac.new(prk, block + info + bytes([counter]), hashlib.sha256).digest()
        out += block
        counter += 1
    return out[:length]

dk = bytes([7]) * 32
wrapped = bytes([9]) * 32
key_id = b"kat"
frame_log2 = 12
object_id = bytes([3]) * 16
binding = bytes([4]) * 32
context = b"ctx"
salt = bytes([5]) * 32
plaintext = bytes(((i * 31) + (i // 251)) & 0xFF for i in range(10_000))

head = (struct.pack("<HB", 1, frame_log2) + object_id + binding
        + bytes([len(key_id)]) + key_id + bytes([len(context)]) + context
        + struct.pack("<H", len(wrapped)) + wrapped + salt)
digest = hashlib.sha256(head).digest()
prk = hkdf_extract(salt, dk)
info = b"vorticity/sealed/v1" + digest + struct.pack("<Iq", 0, 0)
key = hkdf_expand(prk, b"key" + info, 32)
commitment = hkdf_expand(prk, b"commit" + info, 32)
descriptor = head + commitment

out = b"VXSEALED" + struct.pack("<i", len(descriptor)) + descriptor
header_length = len(out)
frame = 1 << frame_log2
aes = AESGCM(key)
chunks = [plaintext[i:i + frame] for i in range(0, len(plaintext), frame)] or [b""]
for i, chunk in enumerate(chunks):
    nonce = struct.pack("<qI", i, 1 if i == len(chunks) - 1 else 0)
    out += aes.encrypt(nonce, chunk, None)
trailer_length = len(descriptor) + 4 + 16 + 8
out += descriptor + struct.pack("<Iqq", 1, header_length, len(plaintext)) + struct.pack("<I", trailer_length) + b"VXSE"
print(hashlib.sha256(out).hexdigest().upper())
