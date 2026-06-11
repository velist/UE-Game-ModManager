"""Extract the most recent base64 PNG image from the JSONL transcript."""
import json, base64, sys, os

src = r"C:\Users\a\.claude\projects\D--modmangerpd---\da176a71-5d62-46ec-9b82-598708c9ee9d.jsonl"
dst = r"D:\modmangerpd\测试\Setup\wizard-images\aichan-logo.png"

candidates = []
with open(src, "rb") as f:
    for ln, line in enumerate(f, 1):
        try:
            obj = json.loads(line.decode("utf-8"))
        except Exception:
            continue
        msg = obj.get("message", {}) or {}
        content = msg.get("content")
        if not isinstance(content, list):
            continue
        for c in content:
            if isinstance(c, dict) and c.get("type") == "image":
                src_obj = c.get("source", {}) or {}
                if src_obj.get("type") == "base64" and src_obj.get("media_type", "").startswith("image/"):
                    data = src_obj.get("data", "")
                    candidates.append((ln, src_obj["media_type"], len(data), data))

print(f"Found {len(candidates)} embedded images.")
for ln, mt, ln_len, _ in candidates[-5:]:
    print(f"  line {ln}: {mt}, {ln_len} chars b64 ({ln_len*3//4} bytes)")

if not candidates:
    sys.exit("No images found.")

ln, mt, _, data = max(candidates, key=lambda c: c[2])
raw = base64.b64decode(data)
os.makedirs(os.path.dirname(dst), exist_ok=True)
ext = "jpg" if mt == "image/jpeg" else "png"
dst_real = dst.rsplit(".", 1)[0] + "." + ext
with open(dst_real, "wb") as f:
    f.write(raw)
print(f"\nSaved largest image (line {ln}, {mt}, {len(raw)} bytes) -> {dst_real}")
