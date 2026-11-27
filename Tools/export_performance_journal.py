"""Export a round performance SQLite journal to chronological JSONL without a running server."""

import argparse
from contextlib import closing
from datetime import datetime, timezone
import heapq
import json
from pathlib import Path
import sqlite3
import sys


def timestamp(value):
    try:
        return float(value)
    except ValueError:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return parsed.timestamp()


def records(database, start=None, end=None, kind="all"):
    """A read-only snapshot, streamed with bounded memory even for a week-long round."""
    path = Path(database).resolve()
    with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)) as db:
        db.execute("BEGIN")

        def rows(table):
            columns = "id,utc,run,payload" if table == "samples" else "id,utc,run,kind,line"
            bounds, args = [], []
            if start is not None:
                bounds.append("utc>=?")
                args.append(start)
            if end is not None:
                bounds.append("utc<=?")
                args.append(end)
            where = " WHERE " + " AND ".join(bounds) if bounds else ""
            for row in db.execute(f"SELECT {columns} FROM {table}{where} ORDER BY utc,id", args):
                index, at, run, *payload = row
                data = dict(type="sample" if table == "samples" else "report", id=index,
                            utc=datetime.fromtimestamp(at, timezone.utc).isoformat(), utcSeconds=at, run=run)
                if table == "samples":
                    data["sample"] = json.loads(payload[0])
                else:
                    data.update(kind=payload[0], line=payload[1])
                yield data

        selected = [rows(table) for table in ("samples", "reports") if kind in ("all", table)]
        yield from heapq.merge(*selected, key=lambda row: (row["utcSeconds"], row["type"], row["id"]))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("database", type=Path)
    parser.add_argument("--from", dest="start", type=timestamp, help="UTC ISO timestamp or Unix seconds (inclusive)")
    parser.add_argument("--to", dest="end", type=timestamp, help="UTC ISO timestamp or Unix seconds (inclusive)")
    parser.add_argument("--kind", choices=("all", "samples", "reports"), default="all")
    parser.add_argument("--output", type=Path, help="UTF-8 JSONL file; defaults to stdout")
    args = parser.parse_args()
    if args.start is not None and args.end is not None and args.start > args.end:
        parser.error("--from must be before --to")
    stream = args.output.open("w", encoding="utf-8", newline="\n") if args.output else sys.stdout
    try:
        if not args.output and hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
        for row in records(args.database, args.start, args.end, args.kind):
            stream.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")
    finally:
        if args.output:
            stream.close()


if __name__ == "__main__":
    main()
