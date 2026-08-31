#!/usr/bin/env python3
"""Work out the next release from the Conventional Commits since the last tag.

Bumping follows the usual rules: a `!` marker or a `BREAKING CHANGE:` footer is a
major, `feat` is a minor, `fix`/`perf`/`revert` are a patch, and everything else
(docs, chore, ci, ...) is not worth a release on its own. Prints the decision as
GitHub Actions outputs and writes the generated changelog to a file so the
workflow never has to escape multi-line text.
"""

import argparse
import os
import re
import subprocess
import sys

import yaml

RECORD = "\x1e"
FIELD = "\x1f"

# type -> (changelog heading, bump level)
TYPES = {
    "feat": ("Features", "minor"),
    "fix": ("Bug Fixes", "patch"),
    "perf": ("Performance", "patch"),
    "revert": ("Reverts", "patch"),
    "refactor": ("Refactoring", None),
    "docs": ("Documentation", None),
    "test": ("Tests", None),
    "build": ("Build System", None),
    "ci": ("CI", None),
    "chore": ("Chores", None),
    "style": ("Styling", None),
}

RANK = {None: 0, "patch": 1, "minor": 2, "major": 3}

HEADER = re.compile(r"^(?P<type>[a-zA-Z]+)(?:\((?P<scope>[^)]*)\))?(?P<breaking>!)?: (?P<subject>.+)$")


def run(*args: str) -> str:
    # Only newlines are trimmed: Python treats the \x1c-\x1f separators below as whitespace.
    return subprocess.run(args, check=True, capture_output=True, text=True).stdout.strip("\n")


def last_tag() -> str | None:
    tags = [t for t in run("git", "tag", "--list", "v*", "--sort=-v:refname").splitlines() if t]
    return tags[0] if tags else None


def commits_since(tag: str | None) -> list[tuple[str, str, str]]:
    fmt = f"%H{FIELD}%s{FIELD}%b{RECORD}"
    rng = f"{tag}..HEAD" if tag else "HEAD"
    raw = run("git", "log", rng, f"--pretty=format:{fmt}")
    out = []
    for record in raw.split(RECORD):
        record = record.strip("\n")
        if not record:
            continue
        parts = (record.split(FIELD, 2) + ["", ""])[:3]
        out.append((parts[0], parts[1], parts[2]))
    return out


def parse(subject: str, body: str) -> tuple[str | None, str, str, bool]:
    """Returns (type, scope, subject text, breaking) — type is None if not conventional."""
    breaking = bool(re.search(r"^BREAKING[ -]CHANGE:", body, re.MULTILINE))
    match = HEADER.match(subject)
    if not match:
        return None, "", subject, breaking
    if match.group("breaking"):
        breaking = True
    return match.group("type").lower(), match.group("scope") or "", match.group("subject"), breaking


def bump_version(version: str, level: str) -> str:
    major, minor, patch = (int(part) for part in version.split(".")[:3])
    if level == "major":
        return f"{major + 1}.0.0"
    if level == "minor":
        return f"{major}.{minor + 1}.0"
    return f"{major}.{minor}.{patch + 1}"


def emit(name: str, value: str) -> None:
    print(f"{name}={value}")
    path = os.environ.get("GITHUB_OUTPUT")
    if path:
        with open(path, "a", encoding="utf-8") as handle:
            handle.write(f"{name}={value}\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--build-yaml", default="build.yaml")
    parser.add_argument("--changelog-file", default="CHANGELOG_FRAGMENT.md")
    parser.add_argument("--force-bump", default="auto", choices=["auto", "patch", "minor", "major"],
                        help="release even without a releasable commit, at this level")
    parser.add_argument("--repo-url", default="")
    args = parser.parse_args()

    tag = last_tag()
    if tag:
        current = tag.lstrip("v")
    else:
        with open(args.build_yaml, encoding="utf-8") as handle:
            current = str(yaml.safe_load(handle).get("version", "0.0.0"))
    current = ".".join((current.split(".") + ["0", "0", "0"])[:3])

    commits = commits_since(tag)
    level = None
    sections: dict[str, list[str]] = {}
    breaking_notes: list[str] = []

    for sha, subject, body in commits:
        ctype, scope, text, breaking = parse(subject, body)
        if ctype is None:
            continue
        heading, commit_level = TYPES.get(ctype, (None, None))
        if breaking:
            commit_level = "major"
        if RANK[commit_level] > RANK[level]:
            level = commit_level

        entry = f"{scope}: {text}" if scope else text
        if args.repo_url:
            entry += f" ([{sha[:7]}]({args.repo_url}/commit/{sha}))"
        else:
            entry += f" ({sha[:7]})"
        if breaking:
            breaking_notes.append(entry)
        if heading and commit_level is not None:
            sections.setdefault(heading, []).append(entry)

    if args.force_bump != "auto":
        level = args.force_bump if RANK[args.force_bump] > RANK[level] else level

    if level is None:
        emit("release", "false")
        emit("reason", f"no releasable commits since {tag or 'the start of history'}")
        return 0

    version = bump_version(current, level)

    lines: list[str] = []
    if breaking_notes:
        lines.append("### ⚠ BREAKING CHANGES")
        lines += [f"- {note}" for note in breaking_notes]
        lines.append("")
    for heading in ("Features", "Bug Fixes", "Performance", "Reverts"):
        if sections.get(heading):
            lines.append(f"### {heading}")
            lines += [f"- {entry}" for entry in sections[heading]]
            lines.append("")
    if not lines:
        lines = [f"Manual {level} release."]

    with open(args.changelog_file, "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines).strip() + "\n")

    emit("release", "true")
    emit("previous_tag", tag or "")
    emit("bump", level)
    emit("version", version)
    emit("version4", f"{version}.0")
    emit("tag", f"v{version}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
