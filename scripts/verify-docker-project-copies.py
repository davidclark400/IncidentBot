#!/usr/bin/env python3
"""Verify the API Docker build stage contains its complete project graph."""

from __future__ import annotations

import re
import shlex
import sys
from pathlib import Path
from xml.etree import ElementTree


ROOT = Path(__file__).resolve().parents[1]
DOCKERFILE = ROOT / "Dockerfile"
API_PROJECT = ROOT / "src/IncidentBot.Api/IncidentBot.Api.csproj"


def repository_relative(path: Path) -> str:
    return path.resolve().relative_to(ROOT).as_posix()


def referenced_projects(root_project: Path) -> list[Path]:
    projects: set[Path] = set()
    pending = [root_project.resolve()]

    while pending:
        project = pending.pop()
        if project in projects:
            continue
        if not project.is_file():
            raise ValueError(f"Referenced project does not exist: {project}")

        projects.add(project)
        document = ElementTree.parse(project)
        for element in document.iter():
            if element.tag.rsplit("}", 1)[-1] != "ProjectReference":
                continue
            include = element.get("Include")
            if not include:
                continue
            reference = (project.parent / include.replace("\\", "/")).resolve()
            try:
                reference.relative_to(ROOT)
            except ValueError as error:
                raise ValueError(
                    f"Project reference escapes the repository: {reference}"
                ) from error
            pending.append(reference)

    return sorted(projects, key=repository_relative)


def build_stage_lines(lines: list[str]) -> list[tuple[int, str]]:
    stage_start = next(
        (
            index
            for index, line in enumerate(lines)
            if re.match(r"^\s*FROM\s+.+\s+AS\s+build\s*$", line, re.IGNORECASE)
        ),
        None,
    )
    if stage_start is None:
        raise ValueError("Dockerfile has no build stage")

    stage_end = next(
        (
            index
            for index in range(stage_start + 1, len(lines))
            if re.match(r"^\s*FROM\s+", lines[index], re.IGNORECASE)
        ),
        len(lines),
    )
    return list(enumerate(lines[stage_start + 1 : stage_end], stage_start + 1))


def copy_sources(stage: list[tuple[int, str]]) -> list[tuple[int, str]]:
    copies: list[tuple[int, str]] = []
    for index, line in stage:
        if not re.match(r"^\s*COPY\s+", line, re.IGNORECASE):
            continue
        tokens = shlex.split(line)
        arguments = tokens[1:]
        options: list[str] = []
        while arguments and arguments[0].startswith("--"):
            options.append(arguments.pop(0))
        if any(option.startswith("--from=") for option in options):
            continue
        if len(arguments) < 2:
            raise ValueError(f"Unsupported COPY instruction on line {index + 1}: {line}")
        for source in arguments[:-1]:
            copies.append((index, source.removeprefix("./").rstrip("/")))
    return copies


def main() -> int:
    lines = DOCKERFILE.read_text(encoding="utf-8").splitlines()
    stage = build_stage_lines(lines)
    restore_index = next(
        (index for index, line in stage if re.search(r"\bdotnet\s+restore\b", line)),
        None,
    )
    publish_index = next(
        (index for index, line in stage if re.search(r"\bdotnet\s+publish\b", line)),
        None,
    )
    if restore_index is None or publish_index is None:
        raise ValueError("Docker build stage must restore and publish the API")

    copies = copy_sources(stage)
    failures: list[str] = []
    projects = referenced_projects(API_PROJECT)

    for project in projects:
        project_path = repository_relative(project)
        project_directory = repository_relative(project.parent)
        project_copy = any(
            source == project_path and index < restore_index for index, source in copies
        )
        if not project_copy:
            failures.append(f"{project_path} is not copied before dotnet restore")

        source_copy = any(
            restore_index < index < publish_index
            and (ROOT / source).is_dir()
            and (
                project_directory == source
                or project_directory.startswith(f"{source}/")
            )
            for index, source in copies
        )
        if not source_copy:
            failures.append(f"{project_directory} sources are not copied before publish")

    if failures:
        for failure in failures:
            print(f"ERROR: {failure}", file=sys.stderr)
        return 1

    represented = ", ".join(repository_relative(project) for project in projects)
    print(f"Docker build-stage project copies verified: {represented}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, ElementTree.ParseError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(1) from error
