from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import venv
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Install the StemCode Python wheel and verify load_stemcode() through NuGet download."
    )
    parser.add_argument("--wheel", type=Path, help="Path to the stemcode-sdk wheel to install.")
    parser.add_argument("--package", default="StemCode", help="NuGet package ID to download.")
    parser.add_argument("--version", help="NuGet package version. Defaults to the latest published version.")
    parser.add_argument(
        "--source",
        default="https://api.nuget.org/v3-flatcontainer",
        help="NuGet flat-container source URL.",
    )
    parser.add_argument("--timeout", type=int, default=300, help="Seconds to allow for load_stemcode().")
    parser.add_argument("--keep", action="store_true", help="Keep the temporary venv/cache for inspection.")
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parents[2]
    python_root = repo_root / "python"
    wheel = args.wheel or _latest_wheel(python_root / "dist")
    version = args.version or _latest_nuget_version(args.package, args.source)

    temp_root = Path(tempfile.mkdtemp(prefix="stemcode-nuget-runtime-"))
    venv_dir = temp_root / "venv"
    cache_dir = temp_root / "cache"

    print(f"wheel: {wheel}")
    print(f"nuget: {args.package} {version}")
    print(f"cache: {cache_dir}")

    try:
        venv.EnvBuilder(with_pip=True, clear=True).create(venv_dir)
        python = _venv_python(venv_dir)

        subprocess.run(
            [str(python), "-m", "pip", "install", "--disable-pip-version-check", str(wheel)],
            check=True,
        )

        env = os.environ.copy()
        env.update(
            {
                "PYTHONNOUSERSITE": "1",
                "STEMCODE_NUGET_PACKAGE": args.package,
                "STEMCODE_NUGET_VERSION": version,
                "STEMCODE_NUGET_SOURCE": args.source,
                "STEMCODE_DOTNET_CACHE": str(cache_dir),
                "STEMCODE_NUGET_VERBOSE": "1",
            }
        )
        env.pop("STEMCODE_DOTNET_PATH", None)

        smoke_code = (
            "from pathlib import Path\n"
            "from stemcode import load_stemcode\n"
            "load_stemcode()\n"
            "print('load_stemcode: ok')\n"
        )
        subprocess.run(
            [str(python), "-c", smoke_code],
            cwd=temp_root,
            env=env,
            check=True,
            timeout=args.timeout,
        )

        cached_assembly = next(cache_dir.rglob("StemCode.dll"), None)
        if cached_assembly is None:
            raise RuntimeError("StemCode.dll was not found in the NuGet cache after load_stemcode().")

        print(f"cached assembly: {cached_assembly}")
        return 0
    finally:
        if args.keep:
            print(f"kept: {temp_root}")
        else:
            shutil.rmtree(temp_root, ignore_errors=True)


def _latest_wheel(dist_dir: Path) -> Path:
    wheels = sorted(
        dist_dir.glob("stemcode_sdk-*-py3-none-any.whl"),
        key=lambda path: path.stat().st_mtime,
        reverse=True,
    )
    if not wheels:
        raise FileNotFoundError(
            f"No pure Python stemcode-sdk wheel found in {dist_dir}. Run `python -m build` first."
        )

    return wheels[0]


def _latest_nuget_version(package: str, source: str) -> str:
    url = f"{source.rstrip('/')}/{package.lower()}/index.json"
    request = urllib.request.Request(url, headers={"User-Agent": "stemcode-sdk-python-test"})
    with urllib.request.urlopen(request, timeout=60) as response:
        payload = json.loads(response.read().decode("utf-8"))

    versions = payload.get("versions") or []
    if not versions:
        raise RuntimeError(f"No versions found for NuGet package {package}.")

    return str(versions[-1])


def _venv_python(venv_dir: Path) -> Path:
    if sys.platform == "win32":
        return venv_dir / "Scripts" / "python.exe"

    return venv_dir / "bin" / "python"


if __name__ == "__main__":
    raise SystemExit(main())
