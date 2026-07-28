from pathlib import Path
import imageio_ffmpeg
import subprocess
import sys

root = Path(r"C:\Users\asabbour\Git\agentweaver\.worktrees\demo-recording-plans\demo-plans\recordings")
segments = [
    root / "seg1a-create-define.webm",
    root / "seg1b-confirm-plan.webm",
    root / "seg2-board-review.webm",
    root / "seg3-approve-merge.webm",
]
missing = [str(p) for p in segments if not p.exists()]
if missing:
    print(f"Missing segments: {missing}", file=sys.stderr)
    sys.exit(1)

manifest = root / "segments.txt"
manifest.write_text("".join(f"file '{p.as_posix()}'\n" for p in segments), encoding="utf-8")
output = root / "blueprint-to-shipped-fix-final.webm"
ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
cmd = [ffmpeg, "-y", "-f", "concat", "-safe", "0", "-i", str(manifest), "-c", "copy", str(output)]
subprocess.run(cmd, check=True)
print(output)
