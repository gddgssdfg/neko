"""Push the current master branch and tags to GitHub and Gitee.

Run from any directory: python tools/sync_remotes.py
No credentials are stored here; Git uses the configured credential helper.
All destinations are checked before the first real push. No force-pushes.
"""
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DESTINATIONS = ("origin", "github")

def run(*args):
    return subprocess.run(["git", *args], cwd=ROOT, check=True)

def main():
    branch = subprocess.check_output(["git", "branch", "--show-current"], cwd=ROOT, text=True).strip()
    if branch != "master":
        raise SystemExit("Switch to master before publishing; no branches were pushed.")
    run("diff-index", "--quiet", "HEAD", "--")
    for destination in DESTINATIONS:
        run("push", "--dry-run", destination, "HEAD:refs/heads/master", "--tags")
    for destination in DESTINATIONS:
        run("push", destination, "HEAD:refs/heads/master", "--tags")
    print("GitHub and Gitee pushes completed. Releases and assets are managed separately.")

if __name__ == "__main__":
    main()
