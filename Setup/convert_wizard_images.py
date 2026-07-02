from pathlib import Path
import sys

try:
    from PIL import Image
except ImportError:
    sys.exit("Pillow is required. Install it with: python -m pip install Pillow")


ROOT = Path(__file__).resolve().parent / "wizard-images"


def convert_rgb(source_name: str, target_name: str, size: tuple[int, int]) -> None:
    source = ROOT / source_name
    target = ROOT / target_name
    if not source.exists():
        print(f"skip missing {source}")
        return

    Image.open(source).convert("RGB").resize(size, Image.Resampling.LANCZOS).save(target)
    print(f"wrote {target.name}")


def convert_logo() -> None:
    source = ROOT / "aichan-logo.png"
    target = ROOT / "small.bmp"
    if not source.exists():
        print(f"skip missing {source}")
        return

    logo = Image.open(source).convert("RGBA")
    canvas = Image.new("RGBA", (110, 110), (250, 250, 250, 255))
    bounds = logo.getbbox()
    if bounds:
        logo = logo.crop(bounds)
    logo.thumbnail((86, 86), Image.Resampling.LANCZOS)
    canvas.alpha_composite(logo, ((110 - logo.width) // 2, (110 - logo.height) // 2))
    canvas.convert("RGB").save(target)
    print(f"wrote {target.name}")


def main() -> int:
    convert_rgb("banner_raw.png", "banner.bmp", (384, 772))
    convert_rgb("step1_import_raw.png", "step1.bmp", (1024, 614))
    convert_rgb("step2_deploy_raw.png", "step2.bmp", (1024, 614))
    convert_rgb("step3_check_raw.png", "step3.bmp", (1024, 614))
    convert_logo()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
