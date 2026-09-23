from __future__ import annotations

import os
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

        if os.environ.get("STEMCODE_SKIP_DOTNET_PUBLISH"):
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
            "--self-contained",
            "false",
            "--output",
            str(output_dir),
        ]
        subprocess.run(command, cwd=repo_root, check=True)
