"""Generate the firmware ID for each PlatformIO build, including uploads."""

from datetime import datetime, timezone
import json
from pathlib import Path
import subprocess

Import("env")  # Provided by PlatformIO.


def git_label(project_dir):
    try:
        revision = subprocess.check_output(
            ["git", "rev-parse", "--short=12", "HEAD"],
            cwd=project_dir, stderr=subprocess.DEVNULL, text=True,
        ).strip()
        changes = subprocess.check_output(
            ["git", "status", "--porcelain"],
            cwd=project_dir, stderr=subprocess.DEVNULL,
        )
    except (OSError, subprocess.CalledProcessError):
        return "unknown"
    return revision + ("+dirty" if changes else "")


build_dir = Path(env.subst("$BUILD_DIR"))
project_dir = env.subst("$PROJECT_DIR")
timestamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
version = f"v0.1.0-{timestamp}"
identity = f"VW ECU Reader firmware: {version} ({env['PIOENV']}, git {git_label(project_dir)})"
build_dir.mkdir(parents=True, exist_ok=True)
(build_dir / "vw_firmware_version.h").write_text(
    f"#pragma once\n#define VW_FIRMWARE_ID {json.dumps(identity)}\n", encoding="ascii"
)
env.Append(CPPPATH=[str(build_dir)])
print(identity)
