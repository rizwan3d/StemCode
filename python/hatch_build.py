from __future__ import annotations

import os
import platform
import shutil
import subprocess
from pathlib import Path

from hatchling.builders.hooks.plugin.interface import BuildHookInterface


class CustomBuildHook(BuildHookInterface):
    def initialize(self, version: str, build_data: dict[str, object]) -> None:
        project_root = Path(__file__).resolve().parent
        repo_root = project_root.parent
        output_dir = project_root / "stemcode" / "_dotnet"
        stemcode_project = repo_root / "StemCode" / "StemCode.csproj"
        runtime_identifier = os.environ.get("STEMCODE_DOTNET_RUNTIME_ID") or _runtime_identifier()
        bundle_dotnet = os.environ.get("STEMCODE_BUNDLE_DOTNET") == "1"

        if bundle_dotnet:
            build_data["pure_python"] = False
            build_data["tag"] = f"py3-none-{_wheel_platform_tag(runtime_identifier)}"
        elif output_dir.exists():
            shutil.rmtree(output_dir)

        if os.environ.get("STEMCODE_SKIP_DOTNET_PUBLISH"):
            return

        if not bundle_dotnet:
            return

        if not stemcode_project.exists():
            if (output_dir / "StemCode.dll").exists():
                return

            msg = (
                f"Cannot bundle StemCode.dll because {stemcode_project} does not "
                "exist and stemcode/_dotnet/StemCode.dll is missing."
            )
            raise FileNotFoundError(msg)

        if output_dir.exists():
            shutil.rmtree(output_dir)

        command = [
            "dotnet",
            "publish",
            str(stemcode_project),
            "--configuration",
            "Release",
            "--framework",
            "net10.0",
            "--runtime",
            runtime_identifier,
            "--self-contained",
            "false",
            "--output",
            str(output_dir),
        ]
        subprocess.run(command, cwd=repo_root, check=True)

        for symbol_file in output_dir.glob("*.pdb"):
            symbol_file.unlink()


def _runtime_identifier() -> str:
    system = platform.system().lower()
    machine = platform.machine().lower()

    architecture = {
        "amd64": "x64",
        "x86_64": "x64",
        "arm64": "arm64",
        "aarch64": "arm64",
    }.get(machine)
    if architecture is None:
        raise RuntimeError(f"Unsupported CPU architecture for StemCode Python build: {machine}")

    if system == "windows":
        return f"win-{architecture}"
    if system == "linux":
        return f"linux-{architecture}"
    if system == "darwin":
        return f"osx-{architecture}"

    raise RuntimeError(f"Unsupported OS for StemCode Python build: {system}")


def _wheel_platform_tag(runtime_identifier: str) -> str:
    tags = {
        "win-x64": "win_amd64",
        "win-arm64": "win_arm64",
        "linux-x64": "manylinux_2_28_x86_64",
        "linux-arm64": "manylinux_2_28_aarch64",
        "osx-x64": "macosx_11_0_x86_64",
        "osx-arm64": "macosx_11_0_arm64",
    }

    try:
        return tags[runtime_identifier]
    except KeyError as exc:
        raise RuntimeError(f"Unsupported StemCode Python wheel runtime: {runtime_identifier}") from exc
