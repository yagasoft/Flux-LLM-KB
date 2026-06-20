from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Migration:
    version: int
    name: str
    path: str
    sql: str


def load_migrations() -> list[Migration]:
    sql_dir = Path(__file__).with_name("sql")
    migrations: list[Migration] = []

    for path in sorted(sql_dir.glob("*.sql")):
        version_text, name = path.stem.split("_", 1)
        migrations.append(
            Migration(
                version=int(version_text),
                name=path.stem,
                path=str(path),
                sql=path.read_text(encoding="utf-8"),
            )
        )

    return migrations

